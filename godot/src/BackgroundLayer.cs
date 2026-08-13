using Godot;
using System;
using System.Collections.Generic;

namespace OpenTyrianVR;

/// <summary>
/// Renders the game's three scrolling background map layers from the exported
/// tile data (ABI v8) in place of the legacy framebuffer background, which is
/// suppressed natively via ConfigFlags.SuppressBackground.
///
/// Every map layer scroll-interpolates between simulation ticks. Terrain-paint
/// enemy records receive the identical sub-tick offset as their underlying
/// layer, preserving pixel lock while removing the 35 Hz ground judder.
///
/// Each layer is one continuous quad over the authored -24..288 side lanes;
/// sprites use a nearly identical independent clip. The fragment
/// shader resolves frame pixel -> map tile -> shape pixel -> palette, with a
/// seam-aware bilinear blend in post-palette RGB for anti-aliasing.
/// </summary>
public unsafe partial class BackgroundLayer : Node3D
{
    private const float LaneWidth = 1.0f, LaneHeight = 0.625f;
    private const int PlayW = 312, PlayH = 184;
    private const int AtlasCols = 8;  // 8x9 grid of 24x28 shapes

    // Full authored terrain boundary. One continuous shader sample removes
    // the old join between the playable centre and its side columns.
    private const float CanvasX0 = PlayfieldGeometry.TerrainMinX, CanvasY0 = PlayfieldGeometry.MinY;
    private const float CanvasW = PlayfieldGeometry.TerrainWidth, CanvasH = PlayfieldGeometry.Height;

    // Lane-local Z per layer, chosen from the layer's over mode each tick.
    // Coplanar layers hug the lane overlay (sub-pixel offsets: ~0.1 mm of
    // head parallax against 3.1 mm/px); layers that legacy draws over
    // entities get real diorama height.  Layer 1 over==1 (e.g. Tyrian-1
    // clouds, drawn over the ground objects) sits above the terrain paint;
    // layer 2 (e.g. floating platforms) sits above the clouds.  Both stay
    // below the player band (0.04): legacy draws the player, shots, and
    // top enemies after every background layer, so nothing map-based may
    // ever cover the ship.
    public const float GroundZ = -0.0008f;
    public const float PlatformZ = 0.030f;
    public const float CloudLowZ = 0.020f;
    public const float CloudHighZ = 0.025f;
    public const float MaxCloudZ = PlatformZ - 0.001f;

    // Water-cloud split (SAVARA): clouds baked into the water ground art
    // re-render on their own plane between the ground and the real cloud
    // layers, while the ground pass darkens those pixels in place (the
    // baked spot becomes the cloud's shadow on the water).  Armed per
    // level only when the ground art reads as water-with-clouds.
    public const float WaterCloudZ = 0.014f;

    // Editor-adjustable, persisted as classes["water-clouds"].
    public float WaterCloudHeight { get; private set; } = WaterCloudZ;
    public bool WaterCloudsArmed => _cloudActive;

    public void SetWaterCloudHeight(float h)
    {
        // A cloud is never allowed to cross the aerial-platform plane.
        // This also constrains old editor saves that predate semantic roles.
        WaterCloudHeight = Mathf.Clamp(h, -0.005f, MaxCloudZ);
        if (_cloudQuad != null)
        {
            Vector3 p = _cloudQuad.Position;
            _cloudQuad.Position = new Vector3(p.X, p.Y, WaterCloudHeight);
        }
    }
    private static float RawLayerHeight(int layer, byte overMode) => layer switch
    {
        0 => GroundZ,
        1 => overMode == 1 ? CloudLowZ : -0.0004f,
        _ => overMode == 2 ? CloudHighZ : PlatformZ,
    };

    // Draw-order events may change OverMode without changing the map art.
    // Once this epoch proves a layer is cloud art, retain its cloud plane as
    // well as its alpha; reverting only the Z put translucent clouds beneath
    // the ground after the first order transition on Quest.
    private float LayerHeight(int layer, byte overMode)
    {
        if (_cloudLayer[layer])
            return layer == 1 ? CloudLowZ : CloudHighZ;
        return RawLayerHeight(layer, overMode);
    }

    private OtyrNative.BackgroundMap _map;  // fetch scratch (57 KB)
    private uint _mapEpoch;

    private readonly MeshInstance3D[] _quads = new MeshInstance3D[OtyrNative.BgLayerCount];
    private readonly ShaderMaterial[] _materials = new ShaderMaterial[OtyrNative.BgLayerCount];
    private readonly MeshInstance3D[] _castShadowQuads = new MeshInstance3D[OtyrNative.BgLayerCount];
    private readonly ShaderMaterial[] _castShadowMaterials = new ShaderMaterial[OtyrNative.BgLayerCount];
    private MeshInstance3D _platformCloudShadowQuad = null!;
    private ShaderMaterial _platformCloudShadowMaterial = null!;
    private readonly ImageTexture[] _tilemapTex = new ImageTexture[OtyrNative.BgLayerCount];
    private readonly ImageTexture[] _atlasTex = new ImageTexture[OtyrNative.BgLayerCount];
    private readonly Vector2I[] _mapSize = new Vector2I[OtyrNative.BgLayerCount];
    private readonly byte[][] _tilesCpu = new byte[OtyrNative.BgLayerCount][];
    private readonly byte[][] _atlasCpu = new byte[OtyrNative.BgLayerCount][];
    // True only when every map cell is present and every pixel of every
    // referenced shape is opaque. The legacy renderer composites its layers
    // into one framebuffer; a non-blended opaque later layer therefore
    // replaces the earlier one completely, regardless of its semantic 3D
    // height. Keeping both exposed otherwise-unused base art in ALE/TIME WAR.
    private readonly bool[] _fullyOpaqueMap = new bool[OtyrNative.BgLayerCount];
    private readonly bool[][] _opaqueShape = new bool[OtyrNative.BgLayerCount][];
    // Entity-shadow materials use the same live map data to reject fragments
    // whose selected receiving plane is transparent at that pixel. This keeps
    // a shadow centred on a platform from floating across its holes.
    private readonly List<ShaderMaterial> _shadowReceiverMaterials = new();
    private readonly List<ShaderMaterial> _groundOcclusionMaterials = new();

    private readonly OtyrNative.BackgroundDraw[] _currDraw = new OtyrNative.BackgroundDraw[OtyrNative.BgLayerCount];
    private readonly OtyrNative.BackgroundDraw[] _prevDraw = new OtyrNative.BackgroundDraw[OtyrNative.BgLayerCount];
    // Continuous presentation anchors for legacy delayed scroll. A section
    // can intentionally advance one pixel every N simulation ticks; drawing
    // the literal 0,0,1 sequence is a visible judder in a 90 Hz headset.
    private readonly float[] _scrollRate = new float[OtyrNative.BgLayerCount];
    private readonly Vector2[] _scrollAnchor = new Vector2[OtyrNative.BgLayerCount];
    private readonly uint[] _scrollAnchorTick = new uint[OtyrNative.BgLayerCount];
    private uint _renderTick;
    // Cloud identity belongs to the map art, not its current legacy draw
    // order. Events can change OverMode while retaining the same map epoch;
    // once a layer is observed in a cloud role, keep its translucent treatment
    // until FetchMaps installs different art.
    private readonly bool[] _cloudLayer = new bool[OtyrNative.BgLayerCount];
    private readonly bool[] _cloudOrderChangeLogged = new bool[OtyrNative.BgLayerCount];

    // Storm (host-rendered water smoothie): palette hue row, -1 off.
    private int _stormHue = -1;
    private uint _stormTick;

    /// <summary>Frame.StormWater passthrough: 0 off, else 0x10 | hue.</summary>
    public void SetStorm(byte code)
    {
        int hue = code == 0 ? -1 : (code & 0x0f) << 4;
        if (hue == _stormHue)
            return;
        _stormHue = hue;
        _materials[0].SetShaderParameter("storm_hue", hue);
    }

    // Water-cloud split state (see WaterCloudZ).
    private MeshInstance3D _cloudQuad = null!;
    private ShaderMaterial _cloudMaterial = null!;
    private bool _cloudMaskPending;
    private bool _cloudActive;
    private ulong _palSettleHash;
    private int _palSettleTicks;
    private int _palSettleWaited;

    // A scroll step larger than this between ticks is a map jump (wrap
    // event); snap instead of interpolating across it.
    private const float TeleportGuardPx = 56f;

    private readonly ImageTexture _palette;
    private const float VirtualSunShadowXPerMeter = 100f;
    private const float VirtualSunShadowYPerMeter = 250f;
    private const float VirtualShadowLift = 0.00035f;
    public int CastShadowLayerCount { get; private set; }
    public int ElevatedReceiverLayerCount { get; private set; }
    /// <summary>The legacy over=1 pass paints layer 1 after both ground-enemy
    /// bands. When that pass is a solid non-blended cover, those records must
    /// not be reintroduced by the host at authored 3D heights.</summary>
    public bool OpaqueLayer1CoversGroundEnemies { get; private set; }

    public BackgroundLayer(ImageTexture palette)
    {
        _palette = palette;
    }

    public override void _Ready()
    {
        _map.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.BackgroundMap>();

        var shader = new Shader
        {
            Code = """
                shader_type spatial;
                // Elevated translucent geometry must still own depth. Without
                // this, later sprite materials painted ground objects over
                // clouds and platform-under objects over aerial platforms.
                render_mode unshaded, cull_disabled, depth_draw_always;

                uniform sampler2D tilemap : filter_nearest;
                uniform sampler2D atlas : filter_nearest;
                uniform sampler2D palette : source_color, filter_nearest;
                uniform ivec2 map_size;
                uniform vec2 origin_px;   // map tile (0,0) position, play-region px
                uniform vec2 quad_px0;    // quad top-left in play-region px (E1 canvas)
                uniform vec2 quad_size_px = vec2(312.0, 184.0);
                uniform float alpha_mul;  // 1.0, or 0.55 for the legacy blend variant
                // 1: darken everything -- the in-place shadow copy of a
                // lifted water-cloud layer.
                uniform int cloud_mode = 0;
                // Storm (water smoothie, SAVARA V): blue-row pixels smear
                // downward with a flowing waver and recolor into hue row
                // storm_hue -- the legacy water_filter, minus its frame
                // feedback (time-scrolling the waver stands in for the
                // cascade).  0 = off.
                uniform int storm_hue = -1;
                uniform float storm_time = 0.0;

                // Palette index at one integer map pixel; -1 = transparent.
                int map_index(ivec2 mp) {
                    if (mp.x < 0 || mp.y < 0)
                        return -1;
                    ivec2 tile = mp / ivec2(24, 28);
                    if (tile.x >= map_size.x || tile.y >= map_size.y)
                        return -1;
                    int idx = int(texelFetch(tilemap, tile, 0).r * 255.0 + 0.5);
                    if (idx > 200)  // 0xff = empty cell
                        return -1;
                    ivec2 ap = ivec2((idx % 8) * 24, (idx / 8) * 28) + (mp - tile * ivec2(24, 28));
                    int pi = int(texelFetch(atlas, ap, 0).r * 255.0 + 0.5);
                    return pi == 0 ? -1 : pi;  // palette 0 = transparent
                }

                // Palette RGB at one integer map pixel; a = coverage.
                vec4 sample_map(ivec2 mp) {
                    int pi = map_index(mp);
                    if (pi < 0)
                        return vec4(0.0);
                    if (storm_hue >= 0 && (pi & 0x30) != 0) {
                        // Legacy waver: abs(((w>>10)&7)-4)-1 over the pixel
                        // index; time scroll makes the smear flow downward.
                        int w = mp.y * 320 + mp.x + int(storm_time * 96.0);
                        int value = pi & 0x0f;
                        int taps = 1;
                        for (int i = 1; i <= 3; i++) {
                            int waver = abs(((w >> 10) & 7) - 4) - 1 + ((w >> (3 + i)) & 1);
                            int pj = map_index(mp + ivec2(waver, i));
                            if (pj >= 0) { value += pj & 0x0f; ++taps; }
                        }
                        pi = storm_hue | (value / taps);
                    }
                    vec3 rgb = texture(palette, vec2((float(pi) + 0.5) / 256.0, 0.5)).rgb;
                    if (cloud_mode == 1)
                        rgb *= 0.45;
                    return vec4(rgb, 1.0);
                }

                void fragment() {
                    vec2 frame_px = quad_px0 + UV * quad_size_px;
                    vec2 mpf = frame_px - origin_px;

                    // Anti-aliased point sampling: snap to texel centers except
                    // in a fwidth-wide band at texel seams (the lane shader's
                    // trick), then blend the 4 neighbors in post-palette RGB.
                    vec2 seam = floor(mpf + 0.5);
                    vec2 dudv = max(fwidth(mpf), vec2(1e-4));
                    vec2 px = seam + clamp((mpf - seam) / dudv, -0.5, 0.5);

                    vec2 base = px - 0.5;
                    ivec2 i0 = ivec2(floor(base));
                    vec2 w = base - vec2(i0);

                    vec4 c = mix(
                        mix(sample_map(i0), sample_map(i0 + ivec2(1, 0)), w.x),
                        mix(sample_map(i0 + ivec2(0, 1)), sample_map(i0 + ivec2(1, 1)), w.x),
                        w.y);

                    if (c.a < 0.004)
                        discard;
                    ALBEDO = c.rgb / c.a;   // un-premultiply the coverage blend
                    ALPHA = c.a * alpha_mul;
                }
                """,
        };

        // Palette-safe virtual-sun shadows for elevated map geometry. This
        // samples only the caster's opaque tile silhouette and multiplies the
        // already-rendered receiver, so the original indexed colors remain
        // unchanged outside the cast shape.
        var castShadowShader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, blend_mul, depth_draw_never;

                uniform sampler2D tilemap : filter_nearest;
                uniform sampler2D atlas : filter_nearest;
                uniform ivec2 map_size;
                uniform vec2 origin_px;
                uniform vec2 quad_px0;
                uniform vec2 quad_size_px = vec2(312.0, 184.0);
                uniform vec2 shadow_offset_px;
                uniform vec4 clip_rect_px;
                uniform float strength = 0.78;
                uniform sampler2D receiver_tilemap : filter_nearest;
                uniform sampler2D receiver_atlas : filter_nearest;
                uniform ivec2 receiver_map_size;
                uniform vec2 receiver_origin_px;
                uniform int receiver_required = 0;

                bool covered(ivec2 mp) {
                    if (mp.x < 0 || mp.y < 0)
                        return false;
                    ivec2 tile = mp / ivec2(24, 28);
                    if (tile.x >= map_size.x || tile.y >= map_size.y)
                        return false;
                    int idx = int(texelFetch(tilemap, tile, 0).r * 255.0 + 0.5);
                    if (idx > 200)
                        return false;
                    ivec2 ap = ivec2((idx % 8) * 24, (idx / 8) * 28) +
                                (mp - tile * ivec2(24, 28));
                    return int(texelFetch(atlas, ap, 0).r * 255.0 + 0.5) != 0;
                }

                bool receiver_covered(vec2 frame_px) {
                    ivec2 mp = ivec2(floor(frame_px - receiver_origin_px));
                    if (mp.x < 0 || mp.y < 0)
                        return false;
                    ivec2 tile = mp / ivec2(24, 28);
                    if (tile.x >= receiver_map_size.x || tile.y >= receiver_map_size.y)
                        return false;
                    int idx = int(texelFetch(receiver_tilemap, tile, 0).r * 255.0 + 0.5);
                    if (idx > 200)
                        return false;
                    ivec2 ap = ivec2((idx % 8) * 24, (idx / 8) * 28) +
                                (mp - tile * ivec2(24, 28));
                    return int(texelFetch(receiver_atlas, ap, 0).r * 255.0 + 0.5) != 0;
                }

                void fragment() {
                    // The quad covers the RECEIVER area. Sample the caster
                    // backwards through the sun offset so map art that is
                    // still just above the screen can cast its complete
                    // leading shadow into view.
                    vec2 projected_px = quad_px0 + UV * quad_size_px;
                    if (projected_px.x < clip_rect_px.x || projected_px.x >= clip_rect_px.z ||
                        projected_px.y < clip_rect_px.y || projected_px.y >= clip_rect_px.w)
                        discard;
                    vec2 caster_px = projected_px - shadow_offset_px;
                    if (!covered(ivec2(floor(caster_px - origin_px))))
                        discard;
                    if (receiver_required != 0 && !receiver_covered(projected_px))
                        discard;
                    ALBEDO = vec3(strength);
                }
                """,
        };

        // The cropped de-parallax playfield in lane-local coordinates (the
        // lane maps the full 320x200 frame; play area publishes at -24).
        QuadMesh LayerMesh(int l) => new QuadMesh
        {
            Size = new Vector2(CanvasW / 320f * LaneWidth, CanvasH / 200f * LaneHeight),
        };
        float LayerCenterX(int l) => ((CanvasX0 + CanvasW / 2f) / 320f - 0.5f) * LaneWidth;
        float centerY = (0.5f - (CanvasY0 + CanvasH / 2f) / 200f) * LaneHeight;

        for (int l = 0; l < OtyrNative.BgLayerCount; l++)
        {
            // Explicit transparent order: terrain/map shadows, color sprites,
            // clouds, platforms, entity shadows, text. Color sprites write
            // their real depth before translucent geometry: clouds blend over
            // ground objects but fail depth against flying objects; platforms
            // similarly cover platform-under art but not platform objects.
            _materials[l] = new ShaderMaterial { Shader = shader };
            _materials[l].SetShaderParameter("palette", _palette);
            _materials[l].SetShaderParameter("alpha_mul", 1.0f);
            _materials[l].SetShaderParameter("quad_px0", new Vector2(CanvasX0, CanvasY0));
            _materials[l].SetShaderParameter("quad_size_px", new Vector2(CanvasW, CanvasH));

            _quads[l] = new MeshInstance3D
            {
                Name = $"BgLayer{l}",
                Mesh = LayerMesh(l),
                MaterialOverride = _materials[l],
                Position = new Vector3(LayerCenterX(l), centerY, LayerHeight(l, 0)),
                Visible = false,
            };
            AddChild(_quads[l]);

            _castShadowMaterials[l] = new ShaderMaterial
            {
                Shader = castShadowShader,
                // Ground/coplanar art paints at -20..-18, then its shadow,
                // then elevated clouds and platforms. A positive priority
                // painted the multiply silhouette on top of its own caster.
                RenderPriority = -10,
            };
            _castShadowMaterials[l].SetShaderParameter("quad_px0", new Vector2(CanvasX0, CanvasY0));
            _castShadowMaterials[l].SetShaderParameter("quad_size_px", new Vector2(CanvasW, CanvasH));
            _castShadowMaterials[l].SetShaderParameter("clip_rect_px",
                new Vector4(CanvasX0, CanvasY0, CanvasX0 + CanvasW, CanvasY0 + CanvasH));
            _castShadowQuads[l] = new MeshInstance3D
            {
                Name = $"BgCastShadow{l}",
                Mesh = LayerMesh(l),
                MaterialOverride = _castShadowMaterials[l],
                Position = new Vector3(LayerCenterX(l), centerY, GroundZ + VirtualShadowLift),
                Visible = false,
            };
            AddChild(_castShadowQuads[l]);
        }

        // A second platform-shadow pass lands only on opaque cloud pixels.
        // The normal layer-2 pass remains on the ground, while this one sits
        // just above the cloud plane and is masked by layer 1's live map art.
        _platformCloudShadowMaterial = new ShaderMaterial
        {
            Shader = castShadowShader,
            RenderPriority = 4,
        };
        _platformCloudShadowMaterial.SetShaderParameter("quad_px0", new Vector2(CanvasX0, CanvasY0));
        _platformCloudShadowMaterial.SetShaderParameter("quad_size_px", new Vector2(CanvasW, CanvasH));
        _platformCloudShadowMaterial.SetShaderParameter("clip_rect_px",
            new Vector4(CanvasX0, CanvasY0, CanvasX0 + CanvasW, CanvasY0 + CanvasH));
        _platformCloudShadowMaterial.SetShaderParameter("receiver_required", 1);
        _platformCloudShadowMaterial.SetShaderParameter("strength", 0.74f);
        _platformCloudShadowQuad = new MeshInstance3D
        {
            Name = "PlatformCloudShadow",
            Mesh = LayerMesh(2),
            MaterialOverride = _platformCloudShadowMaterial,
            Position = new Vector3(LayerCenterX(2), centerY, WaterCloudHeight + VirtualShadowLift),
            Visible = false,
        };
        AddChild(_platformCloudShadowQuad);

        // Water-cloud pass: when a level's COPLANAR layer 1 is dominantly
        // cloud art (SAVARA: clouds baked over the water/land, legacy-drawn
        // under every entity), this quad re-renders that whole layer at
        // WaterCloudZ while layer 1's own quad darkens into the in-place
        // shadow.  Hidden until a level's art arms the split.
        _cloudMaterial = new ShaderMaterial { Shader = shader };
        _cloudMaterial.SetShaderParameter("palette", _palette);
        _cloudMaterial.SetShaderParameter("alpha_mul", 0.82f);
        _cloudMaterial.SetShaderParameter("quad_px0", new Vector2(CanvasX0, CanvasY0));
        _cloudMaterial.SetShaderParameter("quad_size_px", new Vector2(CanvasW, CanvasH));
        _cloudMaterial.RenderPriority = 3;  // after color sprites, before platforms
        _cloudQuad = new MeshInstance3D
        {
            Name = "WaterClouds",
            Mesh = LayerMesh(1),
            MaterialOverride = _cloudMaterial,
            Position = new Vector3(LayerCenterX(1), centerY, WaterCloudHeight),
            Visible = false,
        };
        AddChild(_cloudQuad);

        // Opaque backing behind the cropped play region: where the suppressed
        // legacy frame and the tile layers are all transparent there, the
        // board reads as black (erased HUD rects, astral events) instead of
        // see-through. Menus draw the frame opaque, so they never need it.
        AddChild(new MeshInstance3D
        {
            Name = "Backing",
            Mesh = new QuadMesh { Size = new Vector2(CanvasW / 320f * LaneWidth, CanvasH / 200f * LaneHeight) },
            Position = new Vector3(((CanvasX0 + CanvasW / 2f) / 320f - 0.5f) * LaneWidth,
                                   (0.5f - (CanvasY0 + CanvasH / 2f) / 200f) * LaneHeight, -0.0012f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0f, 0f, 0f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        });
    }

    /// <summary>Called by SnapshotLayer when a new gameplay tick's snapshot
    /// arrives; refreshes map textures on epoch change and latches the
    /// per-layer scroll state.</summary>
    public void OnSnapshot(ulong session, in OtyrNative.Snapshot snapshot)
    {
        if (snapshot.SheetEpoch != _mapEpoch)
        {
            _mapEpoch = snapshot.SheetEpoch;
            Array.Clear(_scrollAnchorTick);
            FetchMaps(session);
        }
        _stormTick = snapshot.LevelTick;
        _renderTick = snapshot.LevelTick;
        // E2 de-parallax: rebase each layer's origin to its fixed offset.
        // Deltas are PER TICK and pair with that tick's draw record: every
        // layer lerps prev->curr origins per render frame, and
        // rebasing the prev draw with the CURRENT delta made the clouds
        // wobble during strafes (user-caught).
        for (int l = 0; l < OtyrNative.BgLayerCount; l++)
        {
            _prevParallax[l] = _layerParallax[l];
            _layerParallax[l] = snapshot.LayerParallax(l);
        }

        for (int l = 0; l < OtyrNative.BgLayerCount; l++)
        {
            _prevDraw[l] = _currDraw[l];
            _currDraw[l] = snapshot.Background(l);
            if (_prevDraw[l].Drawn == 0 && _currDraw[l].Drawn != 0)
                GD.Print($"OpenTyrianVR: background layer {l} appeared " +
                         $"tick={snapshot.LevelTick} over={_currDraw[l].OverMode}");
            _quads[l].Visible = _currDraw[l].Drawn != 0;

            int rateNumerator = _currDraw[l].ScrollRateRatio >> 4;
            int rateDenominator = Math.Max(_currDraw[l].ScrollRateRatio & 0x0f, 1);
            float rate = rateNumerator / (float)rateDenominator;
            Vector2 rawOrigin = Origin(l, _currDraw[l]);
            bool resetScroll = _scrollAnchorTick[l] == 0 ||
                snapshot.LevelTick < _scrollAnchorTick[l] || _prevDraw[l].Drawn == 0 ||
                Mathf.Abs(rate - _scrollRate[l]) > 0.001f;
            if (!resetScroll && rate > 0f)
            {
                float expectedY = _scrollAnchor[l].Y +
                    (snapshot.LevelTick - _scrollAnchorTick[l]) * rate;
                // Events/map jumps may replace the underlying map position.
                // Quantized delayed motion itself stays within one pixel.
                resetScroll = Mathf.Abs(rawOrigin.Y - expectedY) > 1.1f;
            }
            if (resetScroll)
            {
                _scrollAnchor[l] = rawOrigin;
                _scrollAnchorTick[l] = snapshot.LevelTick;
            }
            _scrollRate[l] = rate;
            if (l == 0 && rate > 0f)
                GroundScrollDy = rate;

            float rawZ = RawLayerHeight(l, _currDraw[l].OverMode);
            // Cloud-height layers get an extra transparency nudge on top of
            // the legacy blend flag (user-tuned, 2026-07-12: the kept
            // translucent look, a tad lighter).
            bool cloudHeight = rawZ > 0.001f && Mathf.Abs(rawZ - PlatformZ) > 0.0005f;
            if (!_cloudLayer[l] && cloudHeight)
            {
                _cloudLayer[l] = true;
                GD.Print($"OpenTyrianVR: background layer {l} cloud identity latched " +
                         $"(epoch {_mapEpoch}, over {_currDraw[l].OverMode})");
            }
            else if (_cloudLayer[l] && !cloudHeight && !_cloudOrderChangeLogged[l])
            {
                _cloudOrderChangeLogged[l] = true;
                GD.Print($"OpenTyrianVR: background layer {l} retaining cloud height/alpha " +
                         $"across draw-order change " +
                         $"(epoch {_mapEpoch}, over {_currDraw[l].OverMode})");
            }
            float z = LayerHeight(l, _currDraw[l].OverMode);
            // Authored fully opaque covers are not translucent merely because
            // their legacy draw order maps to the cloud-height band. TIME WAR
            // uses over=1 for a solid blue cover, not for see-through clouds.
            bool translucentCloud = _cloudLayer[l] && !_fullyOpaqueMap[l];
            float alpha = (_currDraw[l].Blend != 0 ? 0.55f : 1.0f) * (translucentCloud ? 0.82f : 1.0f);
            _materials[l].SetShaderParameter("alpha_mul", alpha);
            Vector3 position = _quads[l].Position;
            if (position.Z != z)
                _quads[l].Position = new Vector3(position.X, position.Y, z);

            // See the construction-time ordering note. Ground remains first;
            // cloud geometry follows color sprites, and platforms follow it.
            _materials[l].RenderPriority = _cloudLayer[l] ? 3
                : z > 0.001f ? 4
                : -20 + l;

            // OnRender owns all origin updates so map layers and their
            // terrain-attached entity records share one interpolation phase.
        }

        // Water-cloud split: classify once the level palette has settled
        // (fetch can land mid fade-in, which blinds a brightness test),
        // then mirror layer 1's tick-locked origin onto the lifted pass.
        if (_cloudMaskPending)
            TryClassifyWaterClouds();
        _cloudQuad.Visible = _cloudActive && _currDraw[1].Drawn != 0;
        if (_cloudQuad.Visible)
            _cloudMaterial.SetShaderParameter("origin_px", Origin(1, _currDraw[1]));
        UpdateShadowReceiverState();
        // No in-place copy while lifted: the darkened-shadow look reads
        // wrong over land (user call, 2026-07-12).  Clouds cast nothing
        // until the real directional-sun shadow pass.
        if (_cloudActive)
            _quads[1].Visible = false;

        // Preserve the legacy framebuffer result for opaque overlays. ALE is
        // authored with a green layer 0 beneath a solid blue layer 1; TIME WAR
        // similarly has unused structures beneath its elevated solid cover.
        // A legacy blend still needs the lower layer and is explicitly exempt.
        bool opaqueCover = _currDraw[1].Drawn != 0 && _currDraw[1].Blend == 0 &&
            (_fullyOpaqueMap[1] || CoversTerrainOpaque(1));
        if (opaqueCover)
            _quads[0].Visible = false;
        OpaqueLayer1CoversGroundEnemies = _currDraw[1].Drawn != 0 &&
            _currDraw[1].Blend == 0 && _currDraw[1].OverMode == 1;
    }

    // Classify layer 0's atlas pixels: cloud art is bright and desaturated,
    // water is saturated blue.  SAVARA bakes its clouds into COPLANAR
    // layer 1 (legacy draws it over the water but under every entity), so
    // the split lifts that WHOLE layer when its art is dominantly cloud
    // and layer 0 is water-dominant -- no per-pixel masking.
    private void TryClassifyWaterClouds()
    {
        byte[]? atlas = _atlasCpu[0];
        byte[]? overlay = _atlasCpu[1];
        if (atlas == null || overlay == null || _currDraw[1].Drawn == 0)
            return;
        // Only a coplanar overlay needs the baked-water-cloud split. An
        // elevated layer 1 already floats, but its OverMode may change later
        // in the same map epoch. Keep classification pending so that later
        // coplanar state is still examined instead of permanently falling
        // back to opaque rendering.
        if (LayerHeight(1, _currDraw[1].OverMode) > 0.001f)
            return;
        Image pal = _palette.GetImage();
        // Wait for the fade-in to FINISH, not just clear a brightness bar:
        // a 70%-faded palette kept enough cloud brightness to half-detect
        // clouds while crushing the water's blue-over-red margin to zero
        // (SAVARA read water 0.1% and disarmed).  Settled = the palette
        // hash holds still for 3 ticks; a bounded wait falls back to the
        // brightness test alone in case some level animates its palette.
        float peak = 0f;
        ulong hash = 14695981039346656037UL;
        for (int i = 16; i < 256; i += 8)
        {
            Color c = pal.GetPixel(i, 0);
            peak = Mathf.Max(peak, c.Luminance);
            hash = (hash ^ (ulong)c.ToRgba32()) * 1099511628211UL;
        }
        if (peak < 0.55f)
            return;
        if (hash != _palSettleHash)
        {
            _palSettleHash = hash;
            _palSettleTicks = 0;
            ++_palSettleWaited;
            return;
        }
        if (++_palSettleTicks < 3 && ++_palSettleWaited < 400)
            return;
        _cloudMaskPending = false;

        var isCloud = new bool[256];
        var isWater = new bool[256];
        for (int i = 1; i < 256; i++)
        {
            Color c = pal.GetPixel(i, 0);
            float mx = Mathf.Max(c.R, Mathf.Max(c.G, c.B));
            float mn = Mathf.Min(c.R, Mathf.Min(c.G, c.B));
            float sat = mx <= 0f ? 0f : (mx - mn) / mx;
            // Clouds are bright, desaturated, and COOL (white to bluish) --
            // over land too.  The coolness test keeps warm-bright terrain
            // (tan dirt speckle, rock highlights) out of the mask.
            isCloud[i] = mx > 0.55f && sat < 0.35f && c.B >= c.R - 0.01f;
            isWater[i] = c.B > mx - 0.02f && c.B > c.R + 0.08f && mx > 0.15f;
        }

        long waterOpaque = 0, water = 0;
        foreach (byte pi in atlas)
        {
            if (pi == 0)
                continue;
            ++waterOpaque;
            if (isWater[pi]) ++water;
        }
        long overlayOpaque = 0, cloud = 0;
        foreach (byte pi in overlay)
        {
            if (pi == 0)
                continue;
            ++overlayOpaque;
            if (isCloud[pi]) ++cloud;
        }
        float cloudFrac = overlayOpaque > 0 ? cloud / (float)overlayOpaque : 0f;
        float waterFrac = waterOpaque > 0 ? water / (float)waterOpaque : 0f;
        // Whole-layer lift wants the overlay to be dominantly cloud; the
        // bright-pixel test undercounts cloud EDGES, so SAVARA's all-cloud
        // overlay reads 48.7% -- the bar sits at 0.35 and the layer-0
        // water gate carries the false-positive load.
        _cloudActive = cloudFrac > 0.35f && waterFrac > 0.15f;
        GD.Print($"OpenTyrianVR: water-cloud split {(_cloudActive ? "ARMED" : "off")} " +
                 $"(overlay cloud {cloudFrac:P1}, ground water {waterFrac:P1}, epoch {_mapEpoch})");
        if (!_cloudActive)
        {
            _materials[1].SetShaderParameter("cloud_mode", 0);
            return;
        }

        // Layer 1's own quad hides (see OnSnapshot); the cloud quad
        // re-renders the same layer lifted.
        _cloudMaterial.SetShaderParameter("tilemap", _tilemapTex[1]);
        _cloudMaterial.SetShaderParameter("atlas", _atlasTex[1]);
        _cloudMaterial.SetShaderParameter("map_size", _mapSize[1]);
    }

    // Sub-tick scroll offset per layer (interpolated origin minus this
    // tick's origin): quads riding an elevated layer add this so they move
    // with the smooth-scrolled tiles instead of stepping against them.
    private readonly Vector2[] _subTickPx = new Vector2[OtyrNative.BgLayerCount];

    /// <summary>Called every render frame with the tick interpolation phase;
    /// smooth-scrolls every map layer.</summary>
    public void OnRender(float t)
    {
        // Storm flow: advance the waver in sub-tick time so the smear runs
        // smoothly between ticks (and freezes with the sim on pause).
        if (_stormHue >= 0)
            _materials[0].SetShaderParameter("storm_time", _stormTick + t);

        CastShadowLayerCount = 0;
        ElevatedReceiverLayerCount = 0;
        _platformCloudShadowQuad.Visible = false;
        for (int l = 0; l < OtyrNative.BgLayerCount; l++)
        {
            _subTickPx[l] = Vector2.Zero;
            if (_currDraw[l].Drawn == 0)
            {
                _castShadowQuads[l].Visible = false;
                continue;
            }

            Vector2 curr = Origin(l, _currDraw[l]);
            Vector2 origin = curr;
            if (_scrollRate[l] > 0f && _scrollAnchorTick[l] != 0)
            {
                // Present the interval ending at this snapshot, matching the
                // entity prev->curr interpolation latency.
                float steps = _renderTick > _scrollAnchorTick[l]
                    ? _renderTick - _scrollAnchorTick[l] - 1f + t : 0f;
                origin.Y = _scrollAnchor[l].Y + Mathf.Max(steps, 0f) * _scrollRate[l];
            }
            else if (_prevDraw[l].Drawn != 0)
            {
                Vector2 prev = Origin(l, _prevDraw[l], prev: true);
                if (prev.DistanceTo(curr) <= TeleportGuardPx)
                    origin = prev.Lerp(curr, t);
            }
            _materials[l].SetShaderParameter("origin_px", origin);
            if (l == 1 && _cloudQuad.Visible)
                _cloudMaterial.SetShaderParameter("origin_px", origin);
            _castShadowMaterials[l].SetShaderParameter("origin_px", origin);
            if (l > 0)
                foreach (ShaderMaterial material in _shadowReceiverMaterials)
                    material.SetShaderParameter($"receiver_origin_{l}", origin);

            float casterZ = l == 1 && _cloudActive
                ? WaterCloudHeight : LayerHeight(l, _currDraw[l].OverMode);
            bool casts = l > 0 && casterZ > GroundZ + 0.002f;
            if (l > 0 && PresentedHeight(l) > 0.001f)
                ElevatedReceiverLayerCount++;
            _castShadowQuads[l].Visible = casts;
            if (casts)
            {
                CastShadowLayerCount++;
                float gap = casterZ - GroundZ;
                float dxPx = gap * VirtualSunShadowXPerMeter;
                float dyPx = gap * VirtualSunShadowYPerMeter;
                Vector3 basePosition = _quads[l].Position;
                _castShadowQuads[l].Position = new Vector3(
                    basePosition.X,
                    basePosition.Y,
                    GroundZ + VirtualShadowLift);
                _castShadowMaterials[l].SetShaderParameter("shadow_offset_px", new Vector2(dxPx, dyPx));
                bool cloudCaster = (l == 1 && _cloudActive) || _cloudLayer[l];
                _castShadowMaterials[l].SetShaderParameter("strength", cloudCaster ? 0.84f : 0.74f);
            }
            _subTickPx[l] = origin - curr;
        }

        float cloudZ = PresentedHeight(1);
        float platformZ = PresentedHeight(2);
        bool platformOverCloud = _currDraw[1].Drawn != 0 && _currDraw[2].Drawn != 0 &&
            cloudZ > GroundZ + 0.002f &&
            platformZ > cloudZ + 0.002f &&
            Mathf.Abs(platformZ - PlatformZ) <= 0.0005f;
        _platformCloudShadowQuad.Visible = platformOverCloud;
        if (platformOverCloud)
        {
            CastShadowLayerCount++;
            Vector2 casterOrigin = Origin(2, _currDraw[2]) + _subTickPx[2];
            Vector2 receiverOrigin = Origin(1, _currDraw[1]) + _subTickPx[1];
            float gap = platformZ - cloudZ;
            _platformCloudShadowMaterial.SetShaderParameter("origin_px", casterOrigin);
            _platformCloudShadowMaterial.SetShaderParameter("receiver_origin_px", receiverOrigin);
            _platformCloudShadowMaterial.SetShaderParameter("shadow_offset_px", new Vector2(
                gap * VirtualSunShadowXPerMeter,
                gap * VirtualSunShadowYPerMeter));
            Vector3 receiverPosition = _quads[1].Position;
            _platformCloudShadowQuad.Position = new Vector3(
                receiverPosition.X, receiverPosition.Y, cloudZ + VirtualShadowLift);
        }
    }

    /// <summary>Sub-tick scroll offset of the elevated layer sitting at (or,
    /// with a wider tolerance, near) the given lane height; zero when no
    /// layer matches. Authored-height statics riding just above/below a
    /// platform pass a wide tolerance to glue to it.</summary>
    public Vector2 SubTickOffsetAt(float z, float tolerance = 0.0005f)
    {
        for (int l = 0; l < OtyrNative.BgLayerCount; l++)
        {
            if (_currDraw[l].Drawn == 0)
                continue;
            if (Mathf.Abs(LayerHeight(l, _currDraw[l].OverMode) - z) < tolerance)
                return _subTickPx[l];
        }
        return Vector2.Zero;
    }

    /// <summary>Height of the topmost elevated map layer whose art covers the
    /// given play-region pixel this tick, or 0 when only the ground is
    /// beneath it.  With includeClouds false, only layer 2 (platforms,
    /// raised roads) counts: statics RIDE platforms, but a static under a
    /// cloud stays on the ground beneath it, covered -- exactly the legacy
    /// draw order.  Shadows pass true: they fall on clouds too.</summary>
    // A layer's height as PRESENTED: an armed water-cloud overlay reads at
    // its lifted plane for picking, naming, and shadow-landing, while its
    // own quad stays coplanar as the shadow copy.
    private float PresentedHeight(int l)
    {
        float z = LayerHeight(l, _currDraw[l].OverMode);
        if (l == 1 && _cloudActive && z <= 0.001f)
            return WaterCloudHeight;
        return z;
    }

    public float SurfaceZAt(Vector2 framePx, bool includeClouds = false)
    {
        return SurfaceZAt(framePx, includeClouds, out _);
    }

    /// <summary>Surface-height query with the actual receiving map layer.
    /// Layer zero denotes the ground fallback; one and two identify an
    /// opaque elevated receiver suitable for per-fragment shadow masking.</summary>
    public float SurfaceZAt(Vector2 framePx, bool includeClouds, out int receiverLayer)
    {
        receiverLayer = 0;
        for (int l = OtyrNative.BgLayerCount - 1; l >= 1; l--)
        {
            if (_currDraw[l].Drawn == 0)
                continue;
            float z = PresentedHeight(l);
            if (z <= 0.001f)
                continue;  // coplanar layers are not ridable surfaces
            // Ridability is the layer's ROLE, not its index: on SAVARA the
            // CLOUD layer is layer 2 (over mode 2), and the old index rule
            // bounced surface-following ground objects up to cloud height
            // whenever a cloud drifted over them.  Only the platform plane
            // is ridable; cloud-height layers count for shadows alone.
            bool platform = Mathf.Abs(z - PlatformZ) <= 0.0005f;
            if (!platform && !includeClouds)
                continue;
            if (OpaqueAt(l, framePx))
            {
                receiverLayer = l;
                return z;
            }
        }
        return 0f;
    }

    /// <summary>Connect an entity-shadow material to the current background
    /// receiver maps. Resources update at map epochs; draw state and origins
    /// update with the presentation so interpolation remains pixel-locked.</summary>
    public void RegisterShadowReceiverMaterial(ShaderMaterial material)
    {
        _shadowReceiverMaterials.Add(material);
        for (int l = 1; l < OtyrNative.BgLayerCount; l++)
        {
            if (_tilemapTex[l] != null)
                material.SetShaderParameter($"receiver_tilemap_{l}", _tilemapTex[l]);
            if (_atlasTex[l] != null)
                material.SetShaderParameter($"receiver_atlas_{l}", _atlasTex[l]);
            material.SetShaderParameter($"receiver_map_size_{l}", _mapSize[l]);
            material.SetShaderParameter($"receiver_origin_{l}",
                _currDraw[l].Drawn != 0 ? Origin(l, _currDraw[l]) : Vector2.Zero);
            material.SetShaderParameter($"receiver_drawn_{l}",
                _currDraw[l].Drawn != 0 && PresentedHeight(l) > 0.001f ? 1 : 0);
        }
    }

    /// <summary>Connect a ground-entity material to layer 1's exact opacity
    /// mask. Native background2over=1 paints this map after both ground enemy
    /// bands, so the host must discard only the covered sprite fragments.</summary>
    public void RegisterGroundOcclusionMaterial(ShaderMaterial material)
    {
        _groundOcclusionMaterials.Add(material);
        if (_tilemapTex[1] != null)
            material.SetShaderParameter("occluder_tilemap", _tilemapTex[1]);
        if (_atlasTex[1] != null)
            material.SetShaderParameter("occluder_atlas", _atlasTex[1]);
        material.SetShaderParameter("occluder_map_size", _mapSize[1]);
        UpdateGroundOcclusionMaterial(material);
    }

    private void UpdateGroundOcclusionMaterial(ShaderMaterial material)
    {
        bool enabled = _currDraw[1].Drawn != 0 && _currDraw[1].Blend == 0 &&
            _currDraw[1].OverMode == 1;
        material.SetShaderParameter("occluder_enabled", enabled ? 1 : 0);
        material.SetShaderParameter("occluder_origin", enabled
            ? Origin(1, _currDraw[1]) + _subTickPx[1] : Vector2.Zero);
    }

    private void UpdateShadowReceiverState()
    {
        foreach (ShaderMaterial material in _shadowReceiverMaterials)
            for (int l = 1; l < OtyrNative.BgLayerCount; l++)
                material.SetShaderParameter($"receiver_drawn_{l}",
                    _currDraw[l].Drawn != 0 && PresentedHeight(l) > 0.001f ? 1 : 0);
        foreach (ShaderMaterial material in _groundOcclusionMaterials)
            UpdateGroundOcclusionMaterial(material);
    }

    // Pixel-granular art test: a PLACED tile can still be transparent at
    // the queried pixel (sparse decoration art).  Tile-granular banding
    // hoisted ground statics under such tiles to platform height -- they
    // floated misaligned above their own baked terrain.
    private bool OpaqueAt(int l, Vector2 framePx)
    {
        if (_tilesCpu[l] == null)
            return false;
        Vector2 mp = framePx - Origin(l, _currDraw[l]);
        int tx = (int)Mathf.Floor(mp.X / OtyrNative.BgTileW);
        int ty = (int)Mathf.Floor(mp.Y / OtyrNative.BgTileH);
        if (tx < 0 || ty < 0 || tx >= _mapSize[l].X || ty >= _mapSize[l].Y)
            return false;
        byte shape = _tilesCpu[l][ty * _mapSize[l].X + tx];
        if (shape == OtyrNative.BgTileEmpty)
            return false;
        if (_atlasCpu[l] == null)
            return true;
        int px = (int)mp.X - tx * OtyrNative.BgTileW;
        int py = (int)mp.Y - ty * OtyrNative.BgTileH;
        int ax = (shape % AtlasCols) * OtyrNative.BgTileW + px;
        int ay = (shape / AtlasCols) * OtyrNative.BgTileH + py;
        return _atlasCpu[l][ay * AtlasCols * OtyrNative.BgTileW + ax] != 0;
    }

    // A map can contain transparent cells outside the current draw while its
    // complete visible window is nevertheless a solid cover (TIME WAR). Use
    // whole-shape opacity over the terrain crop, avoiding a 57k-pixel scan on
    // every snapshot while remaining conservative at partial edge tiles.
    private bool CoversTerrainOpaque(int layer)
    {
        byte[]? tiles = _tilesCpu[layer];
        bool[]? shapes = _opaqueShape[layer];
        int width = _mapSize[layer].X;
        int height = _mapSize[layer].Y;
        if (tiles == null || shapes == null || width <= 0 || height <= 0)
            return false;

        Vector2 origin = Origin(layer, _currDraw[layer]);
        int minTx = (int)Mathf.Floor((PlayfieldGeometry.TerrainMinX - origin.X) / OtyrNative.BgTileW);
        int maxTx = (int)Mathf.Floor((PlayfieldGeometry.TerrainMaxX - 0.001f - origin.X) / OtyrNative.BgTileW);
        int minTy = (int)Mathf.Floor((PlayfieldGeometry.MinY - origin.Y) / OtyrNative.BgTileH);
        int maxTy = (int)Mathf.Floor((PlayfieldGeometry.MaxY - 0.001f - origin.Y) / OtyrNative.BgTileH);
        if (minTx < 0 || minTy < 0 || maxTx >= width || maxTy >= height)
            return false;

        for (int ty = minTy; ty <= maxTy; ++ty)
            for (int tx = minTx; tx <= maxTx; ++tx)
            {
                byte shape = tiles[ty * width + tx];
                if (shape == OtyrNative.BgTileEmpty || shape >= shapes.Length || !shapes[shape])
                    return false;
            }
        return true;
    }

    /// <summary>Editor: pick the topmost layer whose art is opaque where
    /// the (local-space) ray crosses its plane.  Returns the layer index
    /// and its current height.</summary>
    public bool TryPickLayer(Vector3 localOrigin, Vector3 localDir, out int layer, out float z)
    {
        layer = -1;
        z = 0f;
        if (Mathf.Abs(localDir.Z) < 1e-5f)
            return false;
        float best = float.NegativeInfinity;
        for (int l = 0; l < OtyrNative.BgLayerCount; l++)
        {
            if (_currDraw[l].Drawn == 0)
                continue;
            float lz = PresentedHeight(l);
            float t = (lz - localOrigin.Z) / localDir.Z;
            if (t <= 0f)
                continue;
            Vector3 hit = localOrigin + localDir * t;
            var playPx = new Vector2((hit.X / LaneWidth + 0.5f) * 320f,
                                     (0.5f - hit.Y / LaneHeight) * 200f);
            if (playPx.X < CanvasX0 || playPx.X >= CanvasX0 + CanvasW ||
                playPx.Y < CanvasY0 || playPx.Y >= CanvasY0 + CanvasH)
                continue;
            if (lz > best && OpaqueAt(l, playPx))
            {
                best = lz;
                layer = l;
                z = lz;
            }
        }
        return layer >= 0;
    }

    /// <summary>Editor: name a layer by its current role.</summary>
    public string LayerName(int l)
    {
        float z = PresentedHeight(l);
        if (l == 0) return "ground layer";
        if (l == 1 && _cloudActive) return "water clouds (layer 1)";
        if (z <= 0.001f) return $"layer {l} (coplanar/terrain)";
        return Mathf.Abs(z - PlatformZ) <= 0.0005f ? $"platforms (layer {l})" : $"clouds (layer {l})";
    }

    // Whole-layer selection highlight (editor): a faint additive quad the
    // size of the play region hovering just over the layer's plane.
    private MeshInstance3D? _layerHighlight;

    public void EditorHighlightLayer(int l)
    {
        if (l < 0)
        {
            if (_layerHighlight != null)
                _layerHighlight.Visible = false;
            return;
        }
        if (_layerHighlight == null)
        {
            _layerHighlight = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(PlayW / 320f * LaneWidth, PlayH / 200f * LaneHeight) },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                    AlbedoColor = new Color(1f, 0.85f, 0.25f, 0.10f),
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    RenderPriority = 10,
                },
            };
            AddChild(_layerHighlight);
        }
        Vector3 basePos = _quads[l].Position;
        // An armed water-cloud overlay is SELECTED at its lifted plane,
        // even though its own quad stays coplanar as the shadow copy.
        _layerHighlight.Position = new Vector3(basePos.X, basePos.Y, PresentedHeight(l) + 0.002f);
        _layerHighlight.Visible = true;
    }

    /// <summary>Play-region position of map tile (0,0) for a draw record: the
    /// record pins map cell (row0, col0) at frame (x, y); draw x is in
    /// pre-composite coordinates (play area publishes shifted -24).</summary>
    /// <summary>Ground layer's down-screen scroll (px per tick).</summary>
    public float GroundScrollDy { get; private set; } = 1f;

    // E2: per-layer parallax delta this tick and last tick (drawn minus
    // fixed offset) -- each draw record rebases with ITS OWN tick's delta.
    private readonly int[] _layerParallax = new int[OtyrNative.BgLayerCount];
    private readonly int[] _prevParallax = new int[OtyrNative.BgLayerCount];

    private Vector2 Origin(int layer, in OtyrNative.BackgroundDraw draw, bool prev = false)
    {
        int width = Math.Max((int)_mapSize[layer].X, 1);
        int row0 = (int)Math.Floor(draw.TileOffset / (double)width);
        int col0 = draw.TileOffset - row0 * width;
        // Subtracting the parallax delta pins the layer at its fixed
        // mid-swing offset (E2); enemy records rebase by the same deltas,
        // so terrain-glued art stays glued.
        int parallax = prev ? _prevParallax[layer] : _layerParallax[layer];
        return new Vector2(draw.X - 24 - col0 * 24 - parallax, draw.Y - row0 * 28);
    }

    private void FetchMaps(ulong session)
    {
        Array.Clear(_cloudLayer, 0, _cloudLayer.Length);
        Array.Clear(_cloudOrderChangeLogged, 0, _cloudOrderChangeLogged.Length);
        for (uint l = 0; l < OtyrNative.BgLayerCount; l++)
        {
            int rc;
            fixed (OtyrNative.BackgroundMap* mapPtr = &_map)
                rc = OtyrNative.GetBackgroundMap(session, l, mapPtr, _map.StructSize);
            if (rc != OtyrNative.Ok || _map.Width == 0)
                continue;

            _mapSize[l] = new Vector2I(_map.Width, _map.Height);
            _materials[l].SetShaderParameter("map_size", _mapSize[l]);
            _castShadowMaterials[l].SetShaderParameter("map_size", _mapSize[l]);
            if (l == 2)
                _platformCloudShadowMaterial.SetShaderParameter("map_size", _mapSize[l]);
            else if (l == 1)
                _platformCloudShadowMaterial.SetShaderParameter("receiver_map_size", _mapSize[l]);
            foreach (ShaderMaterial material in _shadowReceiverMaterials)
                material.SetShaderParameter($"receiver_map_size_{l}", _mapSize[l]);
            if (l == 1)
                foreach (ShaderMaterial material in _groundOcclusionMaterials)
                    material.SetShaderParameter("occluder_map_size", _mapSize[l]);

            var tiles = new byte[_map.Width * _map.Height];
            fixed (OtyrNative.BackgroundMap* map = &_map)
            {
                for (int i = 0; i < tiles.Length; i++)
                    tiles[i] = map->Tiles[i];
            }
            _tilesCpu[l] = tiles;  // kept for surface-height queries
            var tilemapImage = Image.CreateFromData(_map.Width, _map.Height, false, Image.Format.R8, tiles);
            _tilemapTex[l] = ImageTexture.CreateFromImage(tilemapImage);
            _materials[l].SetShaderParameter("tilemap", _tilemapTex[l]);
            _castShadowMaterials[l].SetShaderParameter("tilemap", _tilemapTex[l]);
            if (l == 2)
                _platformCloudShadowMaterial.SetShaderParameter("tilemap", _tilemapTex[l]);
            else if (l == 1)
                _platformCloudShadowMaterial.SetShaderParameter("receiver_tilemap", _tilemapTex[l]);
            foreach (ShaderMaterial material in _shadowReceiverMaterials)
                material.SetShaderParameter($"receiver_tilemap_{l}", _tilemapTex[l]);
            if (l == 1)
                foreach (ShaderMaterial material in _groundOcclusionMaterials)
                    material.SetShaderParameter("occluder_tilemap", _tilemapTex[l]);

            int atlasRows = (OtyrNative.BgShapeMax + AtlasCols - 1) / AtlasCols;
            int atlasW = AtlasCols * OtyrNative.BgTileW;
            int atlasH = atlasRows * OtyrNative.BgTileH;
            var atlas = new byte[atlasW * atlasH];
            fixed (OtyrNative.BackgroundMap* map = &_map)
            {
                for (int s = 0; s < _map.ShapeCount; s++)
                {
                    int originX = (s % AtlasCols) * OtyrNative.BgTileW;
                    int originY = (s / AtlasCols) * OtyrNative.BgTileH;
                    byte* src = map->Shapes + s * OtyrNative.BgTileW * OtyrNative.BgTileH;
                    for (int y = 0; y < OtyrNative.BgTileH; y++)
                        for (int x = 0; x < OtyrNative.BgTileW; x++)
                            atlas[(originY + y) * atlasW + originX + x] = src[y * OtyrNative.BgTileW + x];
                }
            }
            _atlasCpu[l] = atlas;  // kept for pixel-granular surface queries

            // A full-map test is deliberately conservative: if any cell or
            // any referenced shape pixel is transparent, retain the lower
            // plane. This cannot hide authored detail on partially overlaid
            // maps, while recognizing ALE's uniform opaque blue cover.
            var opaqueShape = new bool[_map.ShapeCount];
            Array.Fill(opaqueShape, true);
            for (int s = 0; s < _map.ShapeCount; s++)
            {
                int originX = (s % AtlasCols) * OtyrNative.BgTileW;
                int originY = (s / AtlasCols) * OtyrNative.BgTileH;
                for (int y = 0; y < OtyrNative.BgTileH && opaqueShape[s]; y++)
                    for (int x = 0; x < OtyrNative.BgTileW; x++)
                        if (atlas[(originY + y) * atlasW + originX + x] == 0)
                        {
                            opaqueShape[s] = false;
                            break;
                        }
            }
            _opaqueShape[l] = opaqueShape;
            bool fullyOpaque = tiles.Length != 0;
            foreach (byte tile in tiles)
                if (tile == OtyrNative.BgTileEmpty || tile >= opaqueShape.Length || !opaqueShape[tile])
                {
                    fullyOpaque = false;
                    break;
                }
            _fullyOpaqueMap[l] = fullyOpaque;
            var atlasImage = Image.CreateFromData(atlasW, atlasH, false, Image.Format.R8, atlas);
            _atlasTex[l] = ImageTexture.CreateFromImage(atlasImage);
            _materials[l].SetShaderParameter("atlas", _atlasTex[l]);
            _castShadowMaterials[l].SetShaderParameter("atlas", _atlasTex[l]);
            if (l == 2)
                _platformCloudShadowMaterial.SetShaderParameter("atlas", _atlasTex[l]);
            else if (l == 1)
                _platformCloudShadowMaterial.SetShaderParameter("receiver_atlas", _atlasTex[l]);
            foreach (ShaderMaterial material in _shadowReceiverMaterials)
                material.SetShaderParameter($"receiver_atlas_{l}", _atlasTex[l]);
            if (l == 1)
                foreach (ShaderMaterial material in _groundOcclusionMaterials)
                    material.SetShaderParameter("occluder_atlas", _atlasTex[l]);
        }
        // New level art: re-decide the water-cloud split against it.
        _cloudMaskPending = true;
        _cloudActive = false;
        _palSettleHash = 0;
        _palSettleTicks = 0;
        _palSettleWaited = 0;
        _materials[1].SetShaderParameter("cloud_mode", 0);
        GD.Print($"OpenTyrianVR: background maps refreshed (epoch {_mapEpoch})");
    }
}
