using Godot;
using System;

namespace OpenTyrianVR;

/// <summary>
/// Phase 3 vertical slice: renders the native presentation snapshot as
/// palette-shaded sprite quads floating over the lane at semantic height
/// bands (ENTITY_TAXONOMY.md). One MultiMesh per sprite sheet; cell atlases
/// are fetched whenever the sheet epoch changes, and colors resolve through
/// the live palette texture so fades and flashes track the legacy frame.
/// </summary>
public unsafe partial class SnapshotLayer : Node3D
{
    private static readonly bool DumpAtlases = false;  // debug: writes user://atlas_N.png on fetch

    private const float LaneWidth = 1.0f, LaneHeight = 0.625f;
    private const float PxToMeters = LaneWidth / 320f;
    private const int AtlasCellsPerRow = 32;  // 32x32 grid of 12x14 cells
    // Visible playfield after removing parallax: the legacy 264x184 window
    // widened by 24 px on each side, with no hidden vertical spawn aprons.
    private const float CropX0 = PlayfieldGeometry.MinX, CropY0 = PlayfieldGeometry.MinY;
    private const float CropX1 = PlayfieldGeometry.MaxX, CropY1 = PlayfieldGeometry.MaxY;

    // Lane-local Z (out of the board) per category -- the diorama height bands.
    // Every hazard band sits ABOVE the elevated map layers (clouds 0.02,
    // platforms 0.03): anything that can collide with the player must never
    // hide under scenery.  Genuinely grounded units float a little as a
    // consequence -- accepted until Stage B authored heights.
    private static readonly float[] BandHeight =
    {
        0.055f,  // EnemySky (mid band)
        0.036f,  // EnemyGroundA (above platforms, below the player)
        0.085f,  // EnemyTop (high band)
        0.036f,  // EnemyGroundB
        0.041f,  // EnemyShot (the player layer carries all projectiles)
        0.041f,  // PlayerShot
        0.040f,  // Player
        -0.0002f, // Shadow (fallback; normally surface-following, see AddCell)
        0.040f,  // Sidekick
        0.050f,  // Explosion
        0.050f,  // Superpixel
        0.090f,  // Text (in-play overlay text/HUD icons; proud of everything)
    };

    // Baked structures and shadows render as DECALS: positioned at exactly
    // the terrain/platform plane (identical screen position at any head
    // angle -- true zero parallax, so their transparent pixels composite
    // the destroyed-state art baked in the tiles), with the legacy paint
    // order encoded as a per-record DEPTH-only bias (statics beat the
    // tiles, self-shadows tuck under their owners, late player/shot
    // shadows cross structure art).  Ground-band statics decal the ground
    // plane (beneath passing clouds, matching legacy draw order); top-band
    // statics decal the platform plane.

    // Draw-order bias within a tick: later records sit imperceptibly higher,
    // reproducing legacy layering without z-fighting.
    private const float OrderBias = 0.00001f;

    // Same-band ENEMIES stack by screen Y instead: lower on screen draws on
    // top (painter's order).  Record order is slot order, and JE_newEnemy
    // reuses the lowest freed slot, so a segmented ship's sections draw in
    // whatever order slots happened to free up -- legacy has no stable rule
    // (user-hit both directions on the object-30 ship column).  Y spread is
    // 0.0008 max over 200 px (inside the tightest 0.0015 band gap); the
    // record tiebreak stays under one Y-pixel step (4e-6).
    private static float EnemyOrderBias(float screenY, long index)
        => (screenY * 4f + (index & 255) * 0.002f) * 0.000001f;

    // Height-editor sessions render flat/single-view, where the in-shader
    // decal depth bias is reliable and the VR geometric lift only adds
    // oblique-view parallax against the baked art.
    private static readonly bool FlatEditorMode =
        System.Environment.GetEnvironmentVariable("OTYR_HEIGHT_EDITOR") == "1";

    private OtyrNative.Snapshot _snapshot;
    private OtyrNative.Snapshot _incomingSnapshot;
    private OtyrNative.SpriteSheet _sheet;
    private uint _sheetEpoch;
    private uint _lastRenderedSnapshot;
    private ulong _snapshotArrivalUsec;
    private double _snapshotPeriod = 0.02875;  // nominal 35 Hz tick
    private uint _cellLevelTick;
    private uint _pairTickGap = 1;

    public int CellCount => _cellCount;
    public int VisibleInstanceCount { get; private set; }
    public uint LastTickGap { get; private set; } = 1;
    public uint MaxTickGap { get; private set; } = 1;
    public ulong SkippedTicksTotal { get; private set; }
    public int RigidAssemblyCount { get; private set; }
    public int SeamGuardCellCount { get; private set; }

    // Height-editor presentation history. The native simulation remains the
    // authority; complete published snapshots are cheap enough to retain for
    // a short scrub timeline. Main pauses the game while this is active, so
    // the selected historical object remains available for picking/editing.
    private sealed class EditorHistoryFrame
    {
        public OtyrNative.Snapshot Snapshot;
        public uint[] Palette = null!;
    }
    private const int EditorHistoryCapacity = 35 * 30;
    private readonly System.Collections.Generic.List<EditorHistoryFrame> _editorHistory = new(EditorHistoryCapacity);
    private int _editorHistoryCursor = -1;  // -1 = live
    public bool EditorHistoryActive => _editorHistoryCursor >= 0;
    public float EditorHistorySecondsBack => !EditorHistoryActive || _editorHistory.Count == 0
        ? 0f
        : (_editorHistory[^1].Snapshot.LevelTick - _editorHistory[_editorHistoryCursor].Snapshot.LevelTick) / 35f;
    public float EditorHistorySecondsAvailable => _editorHistory.Count < 2
        ? 0f
        : (_editorHistory[^1].Snapshot.LevelTick - _editorHistory[0].Snapshot.LevelTick) / 35f;

    // Layers 0..SheetCount-1 are sprite sheets; then the glow layer
    // (superpixel debris as small palette-colored quads), the old-table
    // layer (variable-size OPTION_SHAPES blend shots), and one
    // multiplicative shadow layer per sheet (legacy darken blits halve the
    // brightness of whatever is beneath, keeping its hue).
    private const int GlowLayer = OtyrNative.SheetCount;
    private const int OldLayer = OtyrNative.SheetCount + 1;
    private const int ShadowLayerBase = OtyrNative.SheetCount + 2;
    // Text layers (v13): glyphs/HUD icons proud of the playfield; the color
    // layer hue/value-shades old-table glyphs, the shadow layer is the
    // multiplicative glyph drop shadow.
    private const int TextLayer = ShadowLayerBase + OtyrNative.SheetCount;
    private const int TextShadowLayer = TextLayer + 1;
    private const int LayerCount = TextShadowLayer + 1;
    private const int OldAtlasSlotsPerRow = 16;  // grid of 64x64 slots
    private const int OldAtlasSlots = OtyrNative.OldTableSlots * OtyrNative.OldSpriteMax;
    private const int OldAtlasRows = (OldAtlasSlots + OldAtlasSlotsPerRow - 1) / OldAtlasSlotsPerRow;

    private readonly MultiMesh[] _multiMesh = new MultiMesh[LayerCount];
    private readonly ImageTexture[] _atlas = new ImageTexture[OtyrNative.SheetCount];
    private ImageTexture _oldAtlas = null!;
    private OtyrNative.OldSprite _oldSprite;  // fetch scratch
    private readonly Vector2I[] _oldSize = new Vector2I[OldAtlasSlots];
    private ImageTexture _paletteTexture = null!;
    private Image _paletteImage = null!;
    private readonly byte[] _paletteRgba = new byte[256 * 4];
    private readonly int[] _instanceCount = new int[LayerCount];
    private readonly System.Collections.Generic.List<ShaderMaterial> _clipMaterials = new();

    private void RegisterClipMaterial(ShaderMaterial material)
    {
        material.SetShaderParameter("clip_rect_px", new Vector4(CropX0, CropY0, CropX1, CropY1));
        _clipMaterials.Add(material);
    }

    private void UpdateClipTransforms()
    {
        Transform3D worldToPlayfield = GlobalTransform.AffineInverse();
        foreach (ShaderMaterial material in _clipMaterials)
            material.SetShaderParameter("world_to_playfield", worldToPlayfield);
    }

    /// <summary>Set before adding to the tree: render the background map
    /// layers in 3D (pair with ConfigFlags.SuppressBackground).</summary>
    public bool EnableBackground;
    private BackgroundLayer? _background;

    public override void _Ready()
    {
        _snapshot.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.Snapshot>();
        _incomingSnapshot.StructSize = _snapshot.StructSize;
        _sheet.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.SpriteSheet>();
        LoadHoverHeights();
        LoadHeightSemantics();

        _paletteImage = Image.CreateEmpty(256, 1, false, Image.Format.Rgba8);
        _paletteTexture = ImageTexture.CreateFromImage(_paletteImage);

        if (EnableBackground)
        {
            _background = new BackgroundLayer(_paletteTexture) { Name = "BackgroundLayer" };
            AddChild(_background);
            if (_classHeights.TryGetValue("water-clouds", out float wc))
                _background.SetWaterCloudHeight(wc);
        }

        var shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, depth_draw_always;

                uniform sampler2D atlas : filter_nearest;
                uniform sampler2D palette : source_color, filter_nearest;
                uniform mat4 world_to_playfield;
                uniform vec4 clip_rect_px; // left, top, right, bottom

                // FLAT: per-instance integers must arrive bit-exact.
                // Smooth varyings interpolate (a*w0+b*w1+c*w2) even when all
                // vertices agree, and 8.0 arriving as 7.9998 flipped every
                // power-of-two flag decode (2x2 bit lost -> one cell
                // stretched; phantom blend -> ghost ship; phantom filter ->
                // solid hue blocks).  Pipeline-dependent, so flat desktop
                // runs could pass while the headset broke.
                varying flat float cell;
                varying flat float v_flags;
                varying flat float v_filter;
                varying flat float v_decal;
                varying vec2 v_play_px;

                void vertex() {
                    cell = INSTANCE_CUSTOM.x;
                    v_flags = INSTANCE_CUSTOM.y;
                    v_filter = INSTANCE_CUSTOM.z;
                    v_decal = INSTANCE_CUSTOM.w;
                    // Host-only flag 256 marks a joined composite. Expand
                    // half a pixel total for conservative stereo coverage.
                    bool seam = floor(v_flags / 256.0) >= 1.0;
                    bool big = mod(floor(v_flags / 8.0), 2.0) >= 1.0;
                    if (seam) {
                        vec2 size_px = big ? vec2(24.0, 28.0) : vec2(12.0, 14.0);
                        VERTEX.xy *= (size_px + vec2(0.5)) / size_px;
                    }
                    vec3 p = (world_to_playfield * MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    v_play_px = vec2((p.x + 0.5) * 320.0, (0.5 - p.y / 0.625) * 200.0);
                }

                void fragment() {
                    if (v_play_px.x < clip_rect_px.x || v_play_px.x >= clip_rect_px.z ||
                        v_play_px.y < clip_rect_px.y || v_play_px.y >= clip_rect_px.w)
                        discard;
                    // Terrain decals sit at EXACTLY the tile plane (zero
                    // head parallax, so transparent art pixels composite
                    // the baked tiles beneath); a depth-only bias encodes
                    // the paint order against the tiles and each other.
                    DEPTH = FRAGCOORD.z + (v_decal > 0.0 ? 0.00001 + v_decal * 0.00002 : 0.0);
                    // Rounded decode (see the text layer): custom data can
                    // arrive a hair under the integer and wrap the atlas
                    // origin at column boundaries.
                    float cid = floor(cell + 0.5);
                    bool seam = floor(v_flags / 256.0) >= 1.0;
                    bool dbg_big = mod(floor(v_flags / 8.0), 2.0) >= 1.0;
                    vec2 size_px = dbg_big ? vec2(24.0, 28.0) : vec2(12.0, 14.0);
                    // Preserve the original pixel scale. Only the expanded
                    // quarter-pixel rim repeats the sprite's edge sample.
                    vec2 uv0 = seam
                        ? clamp((UV * (size_px + vec2(0.5)) - vec2(0.25)) / size_px,
                                vec2(0.0), vec2(1.0))
                        : UV;
                    // 2x2 sprites (flag bit 8): one quad; pick the legacy
                    // cell (+0/+1/+19/+20) by UV quadrant.
                    vec2 dbg_q = vec2(0.0);
                    if (dbg_big) {
                        // MSAA edge fragments get UVs extrapolated a hair
                        // outside [0,1]; fract() would wrap them to the FAR
                        // edge of the sub-cell (opaque mid-sprite art -> the
                        // hairline dashes off the quad's top/left edges), so
                        // clamp instead of wrapping.
                        vec2 h = uv0 * 2.0;
                        vec2 q = clamp(floor(h), vec2(0.0), vec2(1.0));
                        dbg_q = q;
                        cid += q.x + q.y * 19.0;
                        uv0 = clamp(h - q, vec2(0.0), vec2(1.0));
                    }
                    // Half-texel inset keeps edge fragments inside this cell
                    // (no atlas bleeding from neighboring cells).
                    vec2 cell_origin_px = vec2(mod(cid, 32.0) * 12.0, floor(cid / 32.0) * 14.0);
                    vec2 cell_px = clamp(uv0 * vec2(12.0, 14.0), vec2(0.5), vec2(11.5, 13.5));
                    vec2 uv = (cell_origin_px + cell_px) / vec2(384.0, 448.0);
                    vec2 s = texture(atlas, uv).rg;
                    if (s.g < 0.5)  // opacity plane: index 0 is real black
                        discard;
                    float idx = floor(s.r * 255.0 + 0.5);
                    // Hit-flash / ice tint: exact legacy hue swap,
                    // out = (idx & 0x0f) | filter.
                    if (mod(v_flags, 2.0) >= 1.0)
                        idx = mod(idx, 16.0) + v_filter;
                    ALBEDO = texture(palette, vec2((idx + 0.5) / 256.0, 0.5)).rgb;
                    // Legacy blend variants (transparent explosions,
                    // invulnerable ship) approximate as 55% alpha.
                    ALPHA = mod(floor(v_flags / 2.0), 2.0) >= 1.0 ? 0.55 : 1.0;
                }
                """,
        };

        var quad = new QuadMesh
        {
            Size = new Vector2(OtyrNative.SheetCellW * PxToMeters, OtyrNative.SheetCellH * PxToMeters),
        };

        for (int id = 0; id < OtyrNative.SheetCount; id++)
        {
            var atlasImage = Image.CreateEmpty(
                AtlasCellsPerRow * OtyrNative.SheetCellW, AtlasCellsPerRow * OtyrNative.SheetCellH,
                false, Image.Format.Rg8);  // r = palette index, g = opacity
            _atlas[id] = ImageTexture.CreateFromImage(atlasImage);

            // Explicit transparent-pass ordering (the distance-sort roulette
            // has bitten repeatedly): tile layers 0/+5 (BackgroundLayer),
            // shadows 1, color sprites 2, in-play text 4.
            var material = new ShaderMaterial { Shader = shader, RenderPriority = 2 };
            material.SetShaderParameter("atlas", _atlas[id]);
            material.SetShaderParameter("palette", _paletteTexture);
            RegisterClipMaterial(material);

            _multiMesh[id] = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseCustomData = true,
                Mesh = quad,
                InstanceCount = OtyrNative.SnapshotSpriteMax,
                VisibleInstanceCount = 0,
            };

            AddChild(new MultiMeshInstance3D
            {
                Name = $"Sheet{id}",
                Multimesh = _multiMesh[id],
                MaterialOverride = material,
            });
        }

        // Glow layer: superpixel debris as small palette-colored quads.
        var glowShader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled;

                uniform sampler2D palette : source_color, filter_nearest;
                uniform mat4 world_to_playfield;
                uniform vec4 clip_rect_px;

                varying flat float pal_index;  // flat: see the sprite shader
                varying vec2 v_play_px;

                void vertex() {
                    pal_index = INSTANCE_CUSTOM.x;
                    vec3 p = (world_to_playfield * MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    v_play_px = vec2((p.x + 0.5) * 320.0, (0.5 - p.y / 0.625) * 200.0);
                }

                void fragment() {
                    if (v_play_px.x < clip_rect_px.x || v_play_px.x >= clip_rect_px.z ||
                        v_play_px.y < clip_rect_px.y || v_play_px.y >= clip_rect_px.w)
                        discard;
                    ALBEDO = texture(palette, vec2((pal_index + 0.5) / 256.0, 0.5)).rgb;
                }
                """,
        };
        var glowMaterial = new ShaderMaterial { Shader = glowShader, RenderPriority = 2 };
        glowMaterial.SetShaderParameter("palette", _paletteTexture);
        RegisterClipMaterial(glowMaterial);

        _multiMesh[GlowLayer] = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = new Vector2(2f * PxToMeters, 2f * PxToMeters) },
            InstanceCount = OtyrNative.SnapshotSpriteMax,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D
        {
            Name = "GlowLayer",
            Multimesh = _multiMesh[GlowLayer],
            MaterialOverride = glowMaterial,
        });

        // Old-table layer: variable-size OPTION_SHAPES sprites drawn with the
        // legacy 50/50 value blend, approximated as 55% alpha.  Unit-pixel
        // quads scaled per instance; custom data = (sprite index, w, h).
        var oldShader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, depth_prepass_alpha;

                uniform sampler2D atlas : filter_nearest;
                uniform sampler2D palette : source_color, filter_nearest;
                uniform mat4 world_to_playfield;
                uniform vec4 clip_rect_px;

                varying flat vec3 slot_wh;  // flat: see the sprite shader
                varying vec2 v_play_px;

                void vertex() {
                    slot_wh = INSTANCE_CUSTOM.xyz;
                    vec3 p = (world_to_playfield * MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    v_play_px = vec2((p.x + 0.5) * 320.0, (0.5 - p.y / 0.625) * 200.0);
                }

                void fragment() {
                    if (v_play_px.x < clip_rect_px.x || v_play_px.x >= clip_rect_px.z ||
                        v_play_px.y < clip_rect_px.y || v_play_px.y >= clip_rect_px.w)
                        discard;
                    // Rounded slot decode: see the text shader.
                    float slot = floor(slot_wh.x + 0.5);
                    vec2 wh = slot_wh.yz;
                    vec2 origin_px = vec2(mod(slot, 16.0), floor(slot / 16.0)) * 64.0;
                    vec2 px = clamp(UV * wh, vec2(0.5), wh - 0.5);
                    vec2 s = texture(atlas, (origin_px + px) / vec2(1024.0, 2432.0)).rg;
                    if (s.g < 0.5)
                        discard;
                    float idx = floor(s.r * 255.0 + 0.5);
                    ALBEDO = texture(palette, vec2((idx + 0.5) / 256.0, 0.5)).rgb;
                    ALPHA = 0.55;
                }
                """,
        };
        var oldMaterial = new ShaderMaterial { Shader = oldShader, RenderPriority = 2 };
        _oldAtlas = ImageTexture.CreateFromImage(Image.CreateEmpty(
            OldAtlasSlotsPerRow * OtyrNative.OldSpriteWMax,
            OldAtlasRows * OtyrNative.OldSpriteHMax, false, Image.Format.Rg8));
        oldMaterial.SetShaderParameter("atlas", _oldAtlas);
        oldMaterial.SetShaderParameter("palette", _paletteTexture);
        RegisterClipMaterial(oldMaterial);

        _multiMesh[OldLayer] = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = new Vector2(PxToMeters, PxToMeters) },
            InstanceCount = 128,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D
        {
            Name = "OldTableLayer",
            Multimesh = _multiMesh[OldLayer],
            MaterialOverride = oldMaterial,
        });

        // Shadow layers: legacy darken blits (shadows, iced enemies) halve
        // the value of whatever lies beneath while keeping its hue -- a
        // multiplicative quad using the sprite cell as a coverage mask.
        var shadowShader = new Shader
        {
            Code = """
                shader_type spatial;
                // No depth WRITE (depth_draw_never): shadows draw before the
                // color sprites and multiply only what is beneath them; a
                // written shadow depth blocked the caster's own repaint at
                // grazing angles (the shadow floor point is genuinely nearer
                // than the elevated ship there).
                render_mode unshaded, cull_disabled, blend_mul, depth_draw_never;

                uniform sampler2D atlas : filter_nearest;
                uniform mat4 world_to_playfield;
                uniform vec4 clip_rect_px;
                uniform sampler2D receiver_tilemap_1 : filter_nearest;
                uniform sampler2D receiver_atlas_1 : filter_nearest;
                uniform ivec2 receiver_map_size_1;
                uniform vec2 receiver_origin_1;
                uniform int receiver_drawn_1 = 0;
                uniform sampler2D receiver_tilemap_2 : filter_nearest;
                uniform sampler2D receiver_atlas_2 : filter_nearest;
                uniform ivec2 receiver_map_size_2;
                uniform vec2 receiver_origin_2;
                uniform int receiver_drawn_2 = 0;

                // FLAT: see the sprite shader.
                varying flat float cell;
                varying flat float v_flags;
                varying flat float v_decal;
                varying flat float v_strength;
                varying vec2 v_play_px;

                bool receiver_covered_1(vec2 frame_px) {
                    if (receiver_drawn_1 == 0)
                        return false;
                    ivec2 mp = ivec2(floor(frame_px - receiver_origin_1));
                    if (mp.x < 0 || mp.y < 0)
                        return false;
                    ivec2 tile = mp / ivec2(24, 28);
                    if (tile.x >= receiver_map_size_1.x || tile.y >= receiver_map_size_1.y)
                        return false;
                    int idx = int(texelFetch(receiver_tilemap_1, tile, 0).r * 255.0 + 0.5);
                    if (idx > 200)
                        return false;
                    ivec2 ap = ivec2((idx % 8) * 24, (idx / 8) * 28) +
                                (mp - tile * ivec2(24, 28));
                    return int(texelFetch(receiver_atlas_1, ap, 0).r * 255.0 + 0.5) != 0;
                }

                bool receiver_covered_2(vec2 frame_px) {
                    if (receiver_drawn_2 == 0)
                        return false;
                    ivec2 mp = ivec2(floor(frame_px - receiver_origin_2));
                    if (mp.x < 0 || mp.y < 0)
                        return false;
                    ivec2 tile = mp / ivec2(24, 28);
                    if (tile.x >= receiver_map_size_2.x || tile.y >= receiver_map_size_2.y)
                        return false;
                    int idx = int(texelFetch(receiver_tilemap_2, tile, 0).r * 255.0 + 0.5);
                    if (idx > 200)
                        return false;
                    ivec2 ap = ivec2((idx % 8) * 24, (idx / 8) * 28) +
                                (mp - tile * ivec2(24, 28));
                    return int(texelFetch(receiver_atlas_2, ap, 0).r * 255.0 + 0.5) != 0;
                }

                int top_receiver(vec2 frame_px) {
                    if (receiver_covered_2(frame_px))
                        return 2;
                    if (receiver_covered_1(frame_px))
                        return 1;
                    return 0;
                }

                void vertex() {
                    cell = INSTANCE_CUSTOM.x;
                    v_flags = INSTANCE_CUSTOM.y;
                    v_decal = INSTANCE_CUSTOM.w;
                    v_strength = INSTANCE_CUSTOM.z;
                    bool seam = floor(v_flags / 256.0) >= 1.0;
                    bool big = mod(floor(v_flags / 8.0), 2.0) >= 1.0;
                    if (seam) {
                        vec2 size_px = big ? vec2(24.0, 28.0) : vec2(12.0, 14.0);
                        VERTEX.xy *= (size_px + vec2(0.5)) / size_px;
                    }
                    vec3 p = (world_to_playfield * MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    v_play_px = vec2((p.x + 0.5) * 320.0, (0.5 - p.y / 0.625) * 200.0);
                }

                void fragment() {
                    if (v_play_px.x < clip_rect_px.x || v_play_px.x >= clip_rect_px.z ||
                        v_play_px.y < clip_rect_px.y || v_play_px.y >= clip_rect_px.w)
                        discard;
                    // No DEPTH write (see render_mode note); paint order vs
                    // the statics on the same plane is real geometry now
                    // (the decal lift folds decalOrder into z).
                    float cid = floor(cell + 0.5);
                    bool seam = floor(v_flags / 256.0) >= 1.0;
                    bool big = mod(floor(v_flags / 8.0), 2.0) >= 1.0;
                    vec2 size_px = big ? vec2(24.0, 28.0) : vec2(12.0, 14.0);
                    vec2 uv0 = seam
                        ? clamp((UV * (size_px + vec2(0.5)) - vec2(0.25)) / size_px,
                                vec2(0.0), vec2(1.0))
                        : UV;
                    if (big) {  // 2x2 quad
                        // Clamp, not fract: see the sprite shader (MSAA edge
                        // extrapolation must not wrap to the far sub-cell edge).
                        vec2 h = uv0 * 2.0;
                        vec2 q = clamp(floor(h), vec2(0.0), vec2(1.0));
                        cid += q.x + q.y * 19.0;
                        uv0 = clamp(h - q, vec2(0.0), vec2(1.0));
                    }
                    vec2 cell_origin_px = vec2(mod(cid, 32.0) * 12.0, floor(cid / 32.0) * 14.0);
                    vec2 cell_px = clamp(uv0 * vec2(12.0, 14.0), vec2(0.5), vec2(11.5, 13.5));
                    if (texture(atlas, (cell_origin_px + cell_px) / vec2(384.0, 448.0)).g < 0.5)
                        discard;
                    // Generated shadows encode the centre-selected receiver
                    // in custom-data W. Validate every covered fragment
                    // against the live map art so transparent holes do not
                    // acquire a floating multiplicative silhouette.
                    if (v_decal <= -2.0) {
                        int expected_receiver = int(round(-v_decal - 2.0));
                        if (top_receiver(v_play_px) != expected_receiver)
                            discard;
                    }
                    // Generated virtual-sun shadows pass their multiplier in
                    // custom-data Z; legacy darken effects retain 0.5.
                    float strength = v_decal < -1.0 ? v_strength / 255.0 : 0.5;
                    ALBEDO = vec3(strength);
                }
                """,
        };
        for (int id = 0; id < OtyrNative.SheetCount; id++)
        {
            var shadowMaterial = new ShaderMaterial { Shader = shadowShader, RenderPriority = 1 };
            shadowMaterial.SetShaderParameter("atlas", _atlas[id]);
            RegisterClipMaterial(shadowMaterial);
            _background?.RegisterShadowReceiverMaterial(shadowMaterial);

            _multiMesh[ShadowLayerBase + id] = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseCustomData = true,
                Mesh = quad,
                InstanceCount = 256,
                VisibleInstanceCount = 0,
            };
            AddChild(new MultiMeshInstance3D
            {
                Name = $"Shadow{id}",
                Multimesh = _multiMesh[ShadowLayerBase + id],
                MaterialOverride = shadowMaterial,
            });
        }

        // Text layers: in-play overlay glyphs proud of the playfield.  The
        // color layer reproduces the legacy hue/value shading per glyph
        // pixel (custom data = slot, packed w/h, mode + hue*4, value byte);
        // the shadow layer is the multiplicative glyph drop shadow.
        var textShader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, depth_draw_always;

                uniform sampler2D atlas : filter_nearest;
                uniform sampler2D palette : source_color, filter_nearest;
                uniform mat4 world_to_playfield;
                uniform vec4 clip_rect_px;

                varying flat vec4 v_data;  // slot, w + h*65, mode + hue*4, value byte
                varying vec2 v_play_px;

                void vertex() {
                    v_data = INSTANCE_CUSTOM;
                    vec3 p = (world_to_playfield * MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    v_play_px = vec2((p.x + 0.5) * 320.0, (0.5 - p.y / 0.625) * 200.0);
                }

                void fragment() {
                    if (v_play_px.x < clip_rect_px.x || v_play_px.x >= clip_rect_px.z ||
                        v_play_px.y < clip_rect_px.y || v_play_px.y >= clip_rect_px.w)
                        discard;
                    // Round before decode: instance custom data can arrive a
                    // hair under the integer, and at exact multiples of the
                    // row width the mod/floor pair wraps to the wrong slot
                    // (column-0 glyphs sampled empty atlas space).
                    float slot = floor(v_data.x + 0.5);
                    float whp = floor(v_data.y + 0.5);
                    vec2 wh = vec2(mod(whp, 65.0), floor(whp / 65.0));
                    vec2 origin_px = vec2(mod(slot, 16.0), floor(slot / 16.0)) * 64.0;
                    vec2 px = clamp(UV * wh, vec2(0.5), wh - 0.5);
                    vec2 s = texture(atlas, (origin_px + px) / vec2(1024.0, 2432.0)).rg;
                    if (s.g < 0.5) {
                        discard;
                    } else {
                    int mode = int(round(mod(v_data.z, 4.0)));
                    int hue = int(round(floor(v_data.z / 4.0)));
                    int val = int(round(v_data.w));
                    if (val > 127) val -= 256;  // signed value shift
                    int low = int(round(s.r * 255.0)) & 15;
                    int idx = 0;  // mode 3: solid black (outline passes)
                    if (mode == 0) {
                        // blit_sprite_hv_unsafe: value wraps into the hue
                        // bits (the legacy bright-pixel sparkle is real).
                        idx = ((hue << 4) | ((low + val) & 255)) & 255;
                    } else if (mode >= 1) {
                        // blit_sprite_hv / _hv_blend: clamped value nibble.
                        int t = (low + val) & 255;
                        if (t > 15) t = t >= 31 ? 0 : 15;
                        idx = (hue << 4) | t;
                    }
                    ALBEDO = texture(palette, vec2((float(idx) + 0.5) / 256.0, 0.5)).rgb;
                    // Mode 2 (dest 50/50 blend) approximates as half alpha.
                    ALPHA = mode == 2 ? 0.5 : 1.0;
                    }
                }
                """,
        };
        var textMaterial = new ShaderMaterial { Shader = textShader, RenderPriority = 4 };
        textMaterial.SetShaderParameter("atlas", _oldAtlas);
        textMaterial.SetShaderParameter("palette", _paletteTexture);
        RegisterClipMaterial(textMaterial);

        _multiMesh[TextLayer] = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = new Vector2(PxToMeters, PxToMeters) },
            InstanceCount = OtyrNative.SnapshotSpriteMax,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D
        {
            Name = "TextLayer",
            Multimesh = _multiMesh[TextLayer],
            MaterialOverride = textMaterial,
        });

        var textShadowShader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, blend_mul, depth_draw_always;

                uniform sampler2D atlas : filter_nearest;
                uniform mat4 world_to_playfield;
                uniform vec4 clip_rect_px;

                varying flat vec4 v_data;  // flat: see the sprite shader
                varying vec2 v_play_px;

                void vertex() {
                    v_data = INSTANCE_CUSTOM;
                    vec3 p = (world_to_playfield * MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    v_play_px = vec2((p.x + 0.5) * 320.0, (0.5 - p.y / 0.625) * 200.0);
                }

                void fragment() {
                    if (v_play_px.x < clip_rect_px.x || v_play_px.x >= clip_rect_px.z ||
                        v_play_px.y < clip_rect_px.y || v_play_px.y >= clip_rect_px.w)
                        discard;
                    // Same rounded decode as the text color layer.
                    float slot = floor(v_data.x + 0.5);
                    float whp = floor(v_data.y + 0.5);
                    vec2 wh = vec2(mod(whp, 65.0), floor(whp / 65.0));
                    vec2 origin_px = vec2(mod(slot, 16.0), floor(slot / 16.0)) * 64.0;
                    vec2 px = clamp(UV * wh, vec2(0.5), wh - 0.5);
                    if (texture(atlas, (origin_px + px) / vec2(1024.0, 2432.0)).g < 0.5)
                        discard;
                    ALBEDO = vec3(0.5);  // halve brightness, keep hue
                }
                """,
        };
        var textShadowMaterial = new ShaderMaterial { Shader = textShadowShader, RenderPriority = 4 };
        textShadowMaterial.SetShaderParameter("atlas", _oldAtlas);
        RegisterClipMaterial(textShadowMaterial);

        _multiMesh[TextShadowLayer] = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = new Vector2(PxToMeters, PxToMeters) },
            InstanceCount = OtyrNative.SnapshotSpriteMax,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D
        {
            Name = "TextShadowLayer",
            Multimesh = _multiMesh[TextShadowLayer],
            MaterialOverride = textShadowMaterial,
        });
    }

    /// <summary>Polls for a new snapshot and updates the sprite quads.
    /// paletteArgb is the live 0xAARRGGBB palette from the legacy frame.</summary>
    public void Poll(ulong session, uint[] paletteArgb)
    {
        int rc;
        fixed (OtyrNative.Snapshot* snapshotPtr = &_incomingSnapshot)
            rc = OtyrNative.GetSnapshot(session, snapshotPtr, _incomingSnapshot.StructSize, 0);
        if (rc == OtyrNative.Ok && _incomingSnapshot.SnapshotNumber != _lastRenderedSnapshot)
        {
            _lastRenderedSnapshot = _incomingSnapshot.SnapshotNumber;

            if (FlatEditorMode)
                RecordEditorHistory(in _incomingSnapshot, paletteArgb);

            // A pause/menu publication can arrive just after rewind starts.
            // Keep ingesting its cursor, but do not replace the selected
            // historical presentation.
            if (!EditorHistoryActive)
                ApplySnapshot(session, in _incomingSnapshot, paletteArgb, historical: false);

            // Smoothed snapshot period for interpolation pacing.
            ulong now = Time.GetTicksUsec();
            if (_snapshotArrivalUsec != 0)
            {
                double dt = (now - _snapshotArrivalUsec) / 1_000_000.0;
                if (dt > 0.001 && dt < 0.25)
                    _snapshotPeriod = _snapshotPeriod * 0.8 + dt * 0.2;
            }
            _snapshotArrivalUsec = now;
        }

        WriteTransforms();
    }

    private void RecordEditorHistory(in OtyrNative.Snapshot snapshot, uint[] paletteArgb)
    {
        if (_editorHistory.Count > 0)
        {
            OtyrNative.Snapshot previous = _editorHistory[^1].Snapshot;
            // Never scrub across a level reset or sprite-bank epoch: the
            // native atlas/map API exposes only the current epoch's assets.
            if (snapshot.Episode != previous.Episode || snapshot.LevelTick < previous.LevelTick ||
                snapshot.SheetEpoch != previous.SheetEpoch)
            {
                _editorHistory.Clear();
                _editorHistoryCursor = -1;
            }
        }
        _editorHistory.Add(new EditorHistoryFrame
        {
            Snapshot = snapshot,
            Palette = (uint[])paletteArgb.Clone(),
        });
        if (_editorHistory.Count > EditorHistoryCapacity)
        {
            _editorHistory.RemoveAt(0);
            if (_editorHistoryCursor > 0)
                --_editorHistoryCursor;
        }
    }

    private void ApplySnapshot(ulong session, in OtyrNative.Snapshot snapshot,
                               uint[] paletteArgb, bool historical)
    {
        _snapshot = snapshot;
        if (_snapshot.SheetEpoch != _sheetEpoch)
        {
            _sheetEpoch = _snapshot.SheetEpoch;
            FetchAtlases(session);
        }
        UpdatePalette(paletteArgb);
        foreach (ushort t in _editorGroundTemp)
            _editorHeights.Remove(t);
        _editorGroundTemp.Clear();
        if (historical)
        {
            // Scrubbing is exact-frame inspection, not animated playback.
            _cellCount = 0;
            _prevCellCount = 0;
            _cellLevelTick = 0;
            _snapshotArrivalUsec = 0;
        }
        BuildSprites();
        _background?.OnSnapshot(session, in _snapshot);
    }

    /// <summary>Move the editor timeline by simulation ticks. Negative enters
    /// history; moving through the newest retained frame returns to live.</summary>
    public bool EditorStepHistory(ulong session, int deltaTicks)
    {
        if (!FlatEditorMode || _editorHistory.Count < 2 || deltaTicks == 0)
            return false;
        int cursor = EditorHistoryActive ? _editorHistoryCursor : _editorHistory.Count - 1;
        uint current = _editorHistory[cursor].Snapshot.LevelTick;
        long target = (long)current + deltaTicks;
        if (deltaTicks < 0)
        {
            while (cursor > 0 && _editorHistory[cursor - 1].Snapshot.LevelTick >= target)
                --cursor;
        }
        else
        {
            uint newest = _editorHistory[^1].Snapshot.LevelTick;
            if (target >= newest)
            {
                EditorResumeHistory(session);
                return true;
            }
            while (cursor + 1 < _editorHistory.Count &&
                   _editorHistory[cursor + 1].Snapshot.LevelTick <= target)
                ++cursor;
        }
        if (cursor == _editorHistoryCursor)
            return false;
        _editorHistoryCursor = cursor;
        EditorHistoryFrame frame = _editorHistory[cursor];
        ApplySnapshot(session, in frame.Snapshot, frame.Palette, historical: true);
        return true;
    }

    public void EditorResumeHistory(ulong session)
    {
        if (!EditorHistoryActive || _editorHistory.Count == 0)
            return;
        _editorHistoryCursor = -1;
        EditorHistoryFrame frame = _editorHistory[^1];
        ApplySnapshot(session, in frame.Snapshot, frame.Palette, historical: true);
    }

    public void EditorClearHistory()
    {
        _editorHistory.Clear();
        _editorHistoryCursor = -1;
    }

    private void FetchAtlases(ulong session)
    {
        int atlasW = AtlasCellsPerRow * OtyrNative.SheetCellW;
        int atlasH = AtlasCellsPerRow * OtyrNative.SheetCellH;
        var pixels = new byte[atlasW * atlasH * 2];  // rg: index, opacity

        for (uint id = 0; id < OtyrNative.SheetCount; id++)
        {
            int rc;
            fixed (OtyrNative.SpriteSheet* sheetPtr = &_sheet)
                rc = OtyrNative.GetSpriteSheet(session, id, sheetPtr, _sheet.StructSize);
            if (rc != OtyrNative.Ok)
                continue;

            Array.Clear(pixels);
            fixed (OtyrNative.SpriteSheet* sheet = &_sheet)
            {
                for (int cell = 0; cell < (int)_sheet.CellCount; cell++)
                {
                    int originX = (cell % AtlasCellsPerRow) * OtyrNative.SheetCellW;
                    int originY = (cell / AtlasCellsPerRow) * OtyrNative.SheetCellH;
                    byte* src = sheet->Pixels + cell * OtyrNative.SheetCellW * OtyrNative.SheetCellH;
                    byte* opa = sheet->Opacity + cell * OtyrNative.SheetCellW * OtyrNative.SheetCellH;

                    for (int y = 0; y < OtyrNative.SheetCellH; y++)
                        for (int x = 0; x < OtyrNative.SheetCellW; x++)
                        {
                            int at = ((originY + y) * atlasW + originX + x) * 2;
                            int st = y * OtyrNative.SheetCellW + x;
                            pixels[at] = src[st];
                            pixels[at + 1] = opa[st] != 0 ? (byte)255 : (byte)0;
                        }
                }
            }

            var image = Image.CreateFromData(atlasW, atlasH, false, Image.Format.Rg8, pixels);
            _atlas[id].Update(image);

            if (DumpAtlases)
                image.SavePng($"user://atlas_{id}_epoch{_sheetEpoch}.png");
        }

        FetchOldAtlas(session);
        GD.Print($"OpenTyrianVR: sprite atlases refreshed (epoch {_sheetEpoch})");
    }

    private void FetchOldAtlas(ulong session)
    {
        int atlasW = OldAtlasSlotsPerRow * OtyrNative.OldSpriteWMax;
        int atlasH = OldAtlasRows * OtyrNative.OldSpriteHMax;
        var pixels = new byte[atlasW * atlasH * 2];  // rg: index, opacity
        _oldSprite.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.OldSprite>();

        // Slot order matches AddOldCell/AddTextCell: OPTION_SHAPES first
        // (blend shots keep their v9 slots), then the three font tables.
        ReadOnlySpan<uint> tables = stackalloc uint[]
        {
            OtyrNative.OldTableOption, OtyrNative.OldTableFontBig,
            OtyrNative.OldTableFontSmall, OtyrNative.OldTableFontTiny,
        };

        for (int t = 0; t < tables.Length; t++)
        for (uint i = 0; i < OtyrNative.OldSpriteMax; i++)
        {
            int slot = t * OtyrNative.OldSpriteMax + (int)i;
            int rc;
            fixed (OtyrNative.OldSprite* ptr = &_oldSprite)
                rc = OtyrNative.GetOldSprite(session, tables[t], i, ptr, _oldSprite.StructSize);
            _oldSize[slot] = rc == OtyrNative.Ok
                ? new Vector2I(_oldSprite.Width, _oldSprite.Height)
                : Vector2I.Zero;
            if (_oldSize[slot] == Vector2I.Zero)
                continue;

            int originX = (slot % OldAtlasSlotsPerRow) * OtyrNative.OldSpriteWMax;
            int originY = (slot / OldAtlasSlotsPerRow) * OtyrNative.OldSpriteHMax;
            fixed (OtyrNative.OldSprite* spr = &_oldSprite)
            {
                for (int y = 0; y < _oldSprite.Height; y++)
                    for (int x = 0; x < _oldSprite.Width; x++)
                    {
                        int at = ((originY + y) * atlasW + originX + x) * 2;
                        int st = y * OtyrNative.OldSpriteWMax + x;
                        pixels[at] = spr->Pixels[st];
                        pixels[at + 1] = spr->Opacity[st] != 0 ? (byte)255 : (byte)0;
                    }
            }
        }

        var oldImage = Image.CreateFromData(atlasW, atlasH, false, Image.Format.Rg8, pixels);
        _oldAtlas.Update(oldImage);
        if (DumpAtlases)
            oldImage.SavePng($"user://old_atlas_epoch{_sheetEpoch}.png");
    }

    private void UpdatePalette(uint[] paletteArgb)
    {
        for (int i = 0; i < 256; i++)
        {
            uint argb = paletteArgb[i];
            _paletteRgba[i * 4 + 0] = (byte)(argb >> 16);
            _paletteRgba[i * 4 + 1] = (byte)(argb >> 8);
            _paletteRgba[i * 4 + 2] = (byte)argb;
            _paletteRgba[i * 4 + 3] = 0xff;
        }
        _paletteImage.SetData(256, 1, false, Image.Format.Rgba8, _paletteRgba);
        _paletteTexture.Update(_paletteImage);
    }

    private struct RenderCell
    {
        public int SheetId;
        public int CellIndex;      // 0-based atlas cell
        public byte Flags, FilterColor;
        public float Z;            // lane-local height incl. order bias
        public float DecalOrder;   // > 0: terrain decal; depth-only paint order
        public float Aux0, Aux1;   // text layers: mode + hue*4, value byte
        public Vector2 CurrPx;     // cell center, frame pixels
        public Vector2 PrevPx;     // previous-tick center (== CurrPx if new)
        public bool HasPrev;
        public ushort EntityType;  // enemies: eDat index (height editor)
        public byte AssemblyId;    // enemies: native linknum; 0 = standalone
        public byte Category;
        public int CastFrom;       // >=0: virtual-sun shadow of this cell
        public bool SeamGuard;     // connected composite: conservative shared edge
    }

    private RenderCell[] _cells = new RenderCell[OtyrNative.SnapshotSpriteMax * 4];
    private int _cellCount;
    private int _textOrder;  // per-tick sequence for text cells (Z stacking)
    private RenderCell[] _prevCells = new RenderCell[OtyrNative.SnapshotSpriteMax * 4];
    private int _prevCellCount;
    // Pairing: source id of each cell; cells pair to last tick's nearest
    // same-source, same-layer cell.  (Emission-order pairing broke when the
    // legacy renderer skipped off-screen cell rows: entities entering or
    // leaving the screen edges shifted the order, cells lerped across the
    // sprite, and entities visibly dissolved.)
    private ushort[] _cellSource = new ushort[OtyrNative.SnapshotSpriteMax * 4];
    private ushort[] _prevCellSource = new ushort[OtyrNative.SnapshotSpriteMax * 4];
    private readonly int[] _assemblyComponent = new int[OtyrNative.SnapshotSpriteMax * 4];
    private readonly float[] _assemblyMotionX = new float[OtyrNative.SnapshotSpriteMax * 4];
    private readonly float[] _assemblyMotionY = new float[OtyrNative.SnapshotSpriteMax * 4];
    private readonly System.Collections.Generic.Dictionary<ushort, (int Start, int Count)> _prevRuns = new();

    private const float PairRadiusPx = 16f;
    private const float RigidPairRadiusPerTickPx = 32f;
    private const uint MaxInterpolatedTickGap = 4;

    private void BuildSprites()
    {
        if (_snapshot.Episode != 0 && _snapshot.Episode != _lastLoggedEpisode)
        {
            _lastLoggedEpisode = _snapshot.Episode;
            int classified = _snapshot.Episode < _semanticCountByEpisode.Length
                ? _semanticCountByEpisode[_snapshot.Episode] : 0;
            GD.Print($"OpenTyrianVR: height semantics episode {_snapshot.Episode}: " +
                     $"{classified} classified; Episode-1 fine heights " +
                     $"{(_snapshot.Episode == 1 ? "enabled" : "disabled")}");
        }
        // The host exposes the newest snapshot rather than a queue.  A slow
        // render frame can therefore skip one or more 35 Hz publications.
        // Scale rigid multi-cell pairing by the actual tick gap; after a very
        // large stall, snap the complete object instead of risking a recycled
        // source id stretching across the scene.
        uint nextTick = _snapshot.LevelTick;
        _pairTickGap = _cellLevelTick != 0 && nextTick > _cellLevelTick
            ? nextTick - _cellLevelTick : 1;
        LastTickGap = _pairTickGap;
        MaxTickGap = Math.Max(MaxTickGap, _pairTickGap);
        if (_pairTickGap > 1)
            SkippedTicksTotal += _pairTickGap - 1;
        _cellLevelTick = nextTick;

        // Rotate current -> previous.
        (_prevCells, _cells) = (_cells, _prevCells);
        (_prevCellSource, _cellSource) = (_cellSource, _prevCellSource);
        _prevCellCount = _cellCount;
        _cellCount = 0;
        _textOrder = 0;

        // Same-source cells are emitted contiguously; index the runs.
        _pairRunSource = OtyrNative.NoSource;
        _pairRunOrdinal = 0;
        _prevRuns.Clear();
        for (int i = 0; i < _prevCellCount;)
        {
            ushort source = _prevCellSource[i];
            int start = i;
            while (i < _prevCellCount && _prevCellSource[i] == source)
                i++;
            if (source != OtyrNative.NoSource)
                _prevRuns.TryAdd(source, (start, i - start));
        }
        _surfaceBySource.Clear();
        _groundAssemblySources.Clear();

        fixed (OtyrNative.Snapshot* snapshot = &_snapshot)
        {
            var sprites = (OtyrNative.SnapshotSprite*)snapshot->SpritesRaw;

            // First establish CONNECTED assembly semantics before emitting
            // any cells. Link numbers are reusable group ids, so spatially
            // separate formations with the same number must not share a
            // surface. This is the tank-boss case: classify the connected
            // machine once, not each turret/body independently.
            for (uint i = 0; i < snapshot->SpriteCount; i++)
            {
                ref readonly var sprite = ref sprites[i];
                _spriteAssemblyParent[i] = (int)i;
                bool valid = sprite.AssemblyId != 0 && sprite.SourceId != OtyrNative.NoSource &&
                    sprite.Category <= (byte)OtyrNative.Category.EnemyGroundB && sprite.Index != 0;
                _spriteAssemblyValid[i] = valid;
                if (!valid)
                    continue;
                bool big = sprite.Kind == 1;
                float cx = sprite.X + (big ? OtyrNative.SheetCellW : OtyrNative.SheetCellW / 2f);
                float cy = sprite.Y + (big ? OtyrNative.SheetCellH : OtyrNative.SheetCellH / 2f);
                cx -= _snapshot.BandParallax(sprite.Category);
                _spriteAssemblyCenter[i] = new Vector2(cx, cy);
                _spriteAssemblyHalf[i] = big
                    ? new Vector2(OtyrNative.SheetCellW, OtyrNative.SheetCellH)
                    : new Vector2(OtyrNative.SheetCellW / 2f, OtyrNative.SheetCellH / 2f);
            }
            int FindSpriteRoot(int item)
            {
                int root = item;
                while (_spriteAssemblyParent[root] != root)
                    root = _spriteAssemblyParent[root];
                while (_spriteAssemblyParent[item] != item)
                {
                    int next = _spriteAssemblyParent[item];
                    _spriteAssemblyParent[item] = root;
                    item = next;
                }
                return root;
            }
            const float linkedGap = 32f;
            for (uint i = 0; i < snapshot->SpriteCount; i++)
            {
                if (!_spriteAssemblyValid[i])
                    continue;
                for (uint j = i + 1; j < snapshot->SpriteCount; j++)
                {
                    if (!_spriteAssemblyValid[j] || sprites[i].AssemblyId != sprites[j].AssemblyId)
                        continue;
                    Vector2 delta = (_spriteAssemblyCenter[i] - _spriteAssemblyCenter[j]).Abs();
                    Vector2 reach = _spriteAssemblyHalf[i] + _spriteAssemblyHalf[j];
                    bool joined = (delta.X < reach.X && delta.Y <= reach.Y + linkedGap) ||
                                  (delta.Y < reach.Y && delta.X <= reach.X + linkedGap);
                    if (joined)
                        _spriteAssemblyParent[FindSpriteRoot((int)j)] = FindSpriteRoot((int)i);
                }
            }
            Array.Clear(_spriteAssemblyGround);
            Array.Clear(_spriteAssemblySurface);
            for (uint i = 0; i < snapshot->SpriteCount; i++)
            {
                if (!_spriteAssemblyValid[i])
                    continue;
                int root = FindSpriteRoot((int)i);
                _spriteAssemblyGround[root] |= sprites[i].Aux == 3 ||
                    SemanticFor(_snapshot.Episode, sprites[i].EntityType) == HeightSemantic.Surface;
                float surface = _background?.SurfaceZAt(
                    new Vector2(_spriteAssemblyCenter[i].X - 24f, _spriteAssemblyCenter[i].Y)) ?? 0f;
                _spriteAssemblySurface[root] = Math.Max(_spriteAssemblySurface[root], surface);
            }
            for (uint i = 0; i < snapshot->SpriteCount; i++)
            {
                if (!_spriteAssemblyValid[i])
                    continue;
                int root = FindSpriteRoot((int)i);
                if (!_spriteAssemblyGround[root])
                    continue;
                _groundAssemblySources.Add(sprites[i].SourceId);
                _surfaceBySource[sprites[i].SourceId] = _spriteAssemblySurface[root];
            }

            for (uint i = 0; i < snapshot->SpriteCount; i++)
            {
                var sprite = sprites[i];

                if (sprite.Kind == 3)  // PIXEL_GLOW: palette-colored debris quad
                {
                    AddGlow(sprite, i);
                    continue;
                }

                if (sprite.Kind == 2)  // SPRITE_BLEND on the old table
                {
                    AddOldCell(sprite, i);
                    continue;
                }

                if (sprite.Kind == 4)  // SPRITE_HV: text glyph (v13)
                {
                    AddTextCell(sprite, i);
                    continue;
                }

                if (sprite.SheetId >= OtyrNative.SheetCount || sprite.Index == 0)
                    continue;
                // Fixed-offset legacy player/projectile shadow records are
                // superseded by the height-driven virtual-sun projection.
                if (sprite.Category == (byte)OtyrNative.Category.Shadow)
                    continue;
                // Shadows and baked structures render in 3D too (nothing
                // dynamic stays in the frame): shadows as translucent dark
                // quads, structures as map-locked coplanar cells.

                // SPRITE2X2 renders as a SINGLE 24x28 quad (the shader picks
                // the legacy cell -- +0/+1/+19/+20 -- by UV quadrant): the
                // sprite moves, pairs, and bands as one rigid unit.  Per-cell
                // quads interpolated independently and could shear a sprite
                // across its own cells (the dome-square / wedge artifacts).
                AddCell(sprite, i, sprite.Index, 0, 0);
            }
        }

        StabilizeRigidAssemblies();
        AddVirtualSunShadows();
        UpdateApronGhosts();
    }

    private const float VirtualSunShadowXPerMeter = 100f;  // .04 m -> 4 px right
    private const float VirtualSunShadowYPerMeter = 250f;  // .04 m -> 10 px down
    private const float VirtualShadowLift = 0.00045f;
    public int VirtualShadowCount { get; private set; }
    public int MapCastShadowCount => _background?.CastShadowLayerCount ?? 0;
    public int ElevatedReceiverLayerCount => _background?.ElevatedReceiverLayerCount ?? 0;

    /// <summary>Create silhouette casters once per snapshot. Their exact
    /// projection is resolved in WriteTransforms so interpolation and editor
    /// height changes update the shadow immediately.</summary>
    private void AddVirtualSunShadows()
    {
        VirtualShadowCount = 0;
        int casterCount = _cellCount;
        for (int i = 0; i < casterCount && _cellCount < _cells.Length; i++)
        {
            ref readonly RenderCell caster = ref _cells[i];
            bool casterCategory = caster.Category <= (byte)OtyrNative.Category.EnemyGroundB ||
                                  caster.Category == (byte)OtyrNative.Category.Player ||
                                  caster.Category == (byte)OtyrNative.Category.Sidekick;
            if (!casterCategory || caster.SheetId < 0 || caster.SheetId >= OtyrNative.SheetCount)
                continue;

            ref RenderCell shadow = ref _cells[_cellCount];
            shadow = caster;
            shadow.SheetId = ShadowLayerBase + caster.SheetId;
            shadow.FilterColor = 168;  // multiply destination by about 0.66
            shadow.EntityType = 0;
            shadow.AssemblyId = 0;
            shadow.Category = (byte)OtyrNative.Category.Shadow;
            shadow.CastFrom = i;
            shadow.DecalOrder = -2f;  // generated-shadow sentinel
            _cellSource[_cellCount] = OtyrNative.NoSource;
            ++_cellCount;
            ++VirtualShadowCount;
        }
    }

    private static Vector2 CellSizePx(in RenderCell cell) =>
        (cell.Flags & 8) != 0
            ? new Vector2(OtyrNative.SheetCellW * 2f, OtyrNative.SheetCellH * 2f)
            : new Vector2(OtyrNative.SheetCellW, OtyrNative.SheetCellH);

    /// <summary>
    /// General composite pass: connected same-source art and connected enemy
    /// slots with the same nonzero linknum become a rigid component. A single
    /// median interpolation delta prevents partial pairing from opening a
    /// moving gap, while SeamGuard closes sub-pixel stereo raster cracks.
    /// </summary>
    private void StabilizeRigidAssemblies()
    {
        const float joinTolerancePx = 0.75f;
        // Linked bosses are often authored as aligned enemy slots with a
        // transparent gap between their 24x28 sections. Treat a modest gap
        // as one rigid assembly; requiring literal tile contact split the
        // small level-1 boss into independently interpolated halves.
        const float linkedSectionGapPx = 32f;
        for (int i = 0; i < _cellCount; i++)
        {
            _assemblyComponent[i] = i;
            _cells[i].SeamGuard = false;
        }

        bool Related(int a, int b)
        {
            ushort sa = _cellSource[a], sb = _cellSource[b];
            if (sa == OtyrNative.NoSource || sb == OtyrNative.NoSource)
                return false;
            if (sa == sb)
                return true;
            int assembly = _cells[a].AssemblyId;
            return assembly != 0 && assembly == _cells[b].AssemblyId &&
                   _cells[a].EntityType != 0 && _cells[b].EntityType != 0;
        }

        bool Joined(int a, int b)
        {
            Vector2 half = (CellSizePx(in _cells[a]) + CellSizePx(in _cells[b])) * 0.5f;
            float dx = Mathf.Abs(_cells[a].CurrPx.X - _cells[b].CurrPx.X);
            float dy = Mathf.Abs(_cells[a].CurrPx.Y - _cells[b].CurrPx.Y);
            bool sameSource = _cellSource[a] == _cellSource[b];
            if (!sameSource)
            {
                bool overlapsX = dx < half.X - joinTolerancePx;
                bool overlapsY = dy < half.Y - joinTolerancePx;
                return (overlapsX && dy <= half.Y + linkedSectionGapPx) ||
                       (overlapsY && dx <= half.X + linkedSectionGapPx);
            }
            if (dx > half.X + joinTolerancePx || dy > half.Y + joinTolerancePx)
                return false;
            // Exclude corner-only contact: a real join overlaps along at
            // least one axis and touches/overlaps along the other.
            return (dx < half.X - joinTolerancePx && dy <= half.Y + joinTolerancePx) ||
                   (dy < half.Y - joinTolerancePx && dx <= half.X + joinTolerancePx);
        }

        int Find(int item)
        {
            int root = item;
            while (_assemblyComponent[root] != root)
                root = _assemblyComponent[root];
            while (_assemblyComponent[item] != item)
            {
                int next = _assemblyComponent[item];
                _assemblyComponent[item] = root;
                item = next;
            }
            return root;
        }

        for (int a = 0; a < _cellCount; a++)
            for (int b = a + 1; b < _cellCount; b++)
                if (Related(a, b) && Joined(a, b))
                {
                    int rootA = Find(a), rootB = Find(b);
                    if (rootA != rootB)
                        _assemblyComponent[rootB] = rootA;
                }

        for (int i = 0; i < _cellCount; i++)
            _assemblyComponent[i] = Find(i);

        RigidAssemblyCount = 0;
        SeamGuardCellCount = 0;
        for (int root = 0; root < _cellCount; root++)
        {
            if (_assemblyComponent[root] != root)
                continue;
            int members = 0, motionCount = 0;
            bool dynamic = true;
            for (int i = 0; i < _cellCount; i++)
            {
                if (_assemblyComponent[i] != root)
                    continue;
                members++;
                dynamic &= _cells[i].DecalOrder <= 0f;
                if (_cells[i].HasPrev)
                {
                    Vector2 motion = _cells[i].CurrPx - _cells[i].PrevPx;
                    _assemblyMotionX[motionCount] = motion.X;
                    _assemblyMotionY[motionCount] = motion.Y;
                    motionCount++;
                }
            }
            if (members < 2)
                continue;

            RigidAssemblyCount++;
            SeamGuardCellCount += members;
            for (int i = 0; i < _cellCount; i++)
                if (_assemblyComponent[i] == root)
                    _cells[i].SeamGuard = true;

            if (!dynamic || motionCount == 0)
                continue;
            Array.Sort(_assemblyMotionX, 0, motionCount);
            Array.Sort(_assemblyMotionY, 0, motionCount);
            Vector2 componentMotion = new(
                _assemblyMotionX[motionCount / 2],
                _assemblyMotionY[motionCount / 2]);
            for (int i = 0; i < _cellCount; i++)
            {
                if (_assemblyComponent[i] != root)
                    continue;
                _cells[i].PrevPx = _cells[i].CurrPx - componentMotion;
                _cells[i].HasPrev = true;
            }
        }
    }

    // E1 apron ghosts: the sim frees enemies just past the legacy bottom
    // edge while the apron terrain flows on for another ~56 px, so ships
    // and structures popped out mid-apron.  An enemy cell that existed low
    // on screen last tick with no matching cell this tick continues as a
    // GHOST -- visual only, sliding at its last paired velocity (statics:
    // the ground scroll rate) until it exits the apron.  Ghost cells carry
    // DecalOrder = -1 so they never seed further ghosts and skip decal
    // machinery.
    private struct ApronGhost
    {
        public RenderCell Cell;
        public Vector2 Velocity;
    }
    private readonly System.Collections.Generic.List<ApronGhost> _ghosts = new();
    private uint _ghostEpoch;

    private void UpdateApronGhosts()
    {
        if (_snapshot.SheetEpoch != _ghostEpoch)
        {
            _ghosts.Clear();
            _ghostEpoch = _snapshot.SheetEpoch;
        }
        int realCells = _cellCount;
        float scrollDy = Mathf.Clamp(_background?.GroundScrollDy ?? 1f, 0.25f, 4f);

        // Advance survivors and re-emit them as cells.
        for (int g = _ghosts.Count - 1; g >= 0; g--)
        {
            ApronGhost ghost = _ghosts[g];
            ghost.Cell.PrevPx = ghost.Cell.CurrPx;
            ghost.Cell.CurrPx += ghost.Velocity;
            ghost.Cell.HasPrev = true;
            if (ghost.Cell.CurrPx.Y > 240f + OtyrNative.SheetCellH || _cellCount >= _cells.Length)
            {
                _ghosts.RemoveAt(g);
                continue;
            }
            _ghosts[g] = ghost;
            _cellSource[_cellCount] = OtyrNative.NoSource;
            _cells[_cellCount++] = ghost.Cell;
        }

        // Scan last tick's real enemy cells low on screen for disappearances.
        for (int p = 0; p < _prevCellCount; p++)
        {
            ref readonly RenderCell prev = ref _prevCells[p];
            if (prev.EntityType == 0 || prev.DecalOrder < 0f || prev.CurrPx.Y <= 180f)
                continue;
            Vector2 velocity = prev.HasPrev ? prev.CurrPx - prev.PrevPx : new Vector2(0f, scrollDy);
            if (velocity.Y < 0f)
                continue;  // climbing back up: let the sim's own cull stand
            Vector2 predicted = prev.CurrPx + velocity;
            // Sheet + proximity only: animation advances CellIndex between
            // ticks, and an index test would ghost-duplicate live enemies.
            bool matched = false;
            for (int c = 0; c < realCells && !matched; c++)
                matched = _cells[c].SheetId == prev.SheetId &&
                          _cells[c].CurrPx.DistanceSquaredTo(predicted) < 12f * 12f;
            if (matched || _cellCount >= _cells.Length)
                continue;
            var ghost = new ApronGhost { Cell = prev, Velocity = velocity };
            ghost.Cell.PrevPx = prev.CurrPx;
            ghost.Cell.CurrPx = predicted;
            ghost.Cell.HasPrev = true;
            ghost.Cell.EntityType = 0;    // not pickable/editable
            ghost.Cell.Flags &= 8;        // keep the 2x2 bit only (no halos)
            ghost.Cell.DecalOrder = -1f;  // ghost sentinel
            _ghosts.Add(ghost);
            _cellSource[_cellCount] = OtyrNative.NoSource;
            _cells[_cellCount++] = ghost.Cell;
        }
    }

    private void AddGlow(in OtyrNative.SnapshotSprite sprite, uint recordIndex)
    {
        if (_cellCount >= _cells.Length)
            return;

        // Legacy writes (bg & 0x0f + z) >> 1 + color; approximate the read-
        // modify-write against a mid-brightness background.
        int intensity = Math.Min(15, (7 + sprite.FilterColor) / 2);
        int paletteIndex = Math.Min(255, sprite.Index + intensity);

        ref RenderCell cell = ref _cells[_cellCount];
        cell.SheetId = GlowLayer;
        cell.CellIndex = paletteIndex;
        cell.EntityType = 0;  // reused array slot: clear stale enemy types
        cell.Flags = 0;
        cell.FilterColor = 0;
        cell.Category = sprite.Category;
        cell.CastFrom = -1;
        cell.Z = BandHeight[(byte)OtyrNative.Category.Superpixel] + recordIndex * OrderBias;
        cell.CurrPx = new Vector2(sprite.X, sprite.Y);
        cell.PrevPx = cell.CurrPx;
        cell.HasPrev = false;
        PairWithPrevious(ref cell, sprite.SourceId);
        ++_cellCount;
    }

    // Stage B hover heights: per-enemytype class from the user-editable
    // res://hover_heights.json.  "ground" rides the surface beneath (+offset);
    // the air classes are absolute lane heights; unlisted types keep the
    // legacy category band.  Loaded once; classes resolve to heights here.
    private readonly System.Collections.Generic.Dictionary<ushort, float> _typeHeights = new();
    // Explicit numeric heights are deliberate refinements/exceptions. Keep
    // their provenance so low grounded values can become offsets from an
    // assembly's sampled surface without flattening high flying exceptions.
    private readonly System.Collections.Generic.HashSet<ushort> _explicitHeightTypes = new();
    private readonly System.Collections.Generic.Dictionary<string, float> _classHeights = new();
    private enum HeightSemantic : byte { Unknown, Surface, Air }
    private readonly System.Collections.Generic.Dictionary<int, HeightSemantic> _heightSemantics = new();
    private readonly int[] _semanticCountByEpisode = new int[5];
    private byte _lastLoggedEpisode;
    private float _groundClassOffset = -1f;  // <0: "ground" class absent

    /// <summary>Class name -> height table from hover_heights.json (editor).</summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, float> ClassHeights => _classHeights;

    private void LoadHoverHeights()
    {
        const string path = "res://hover_heights.json";
        if (!FileAccess.FileExists(path))
        {
            GD.Print("OpenTyrianVR: no hover_heights.json, legacy bands only");
            return;
        }
        var parsed = Json.ParseString(FileAccess.GetFileAsString(path));
        if (parsed.VariantType != Variant.Type.Dictionary)
        {
            GD.PushWarning("OpenTyrianVR: hover_heights.json did not parse; ignoring");
            return;
        }
        var root = parsed.AsGodotDictionary();
        if (!root.TryGetValue("classes", out Variant classesValue) ||
            classesValue.VariantType != Variant.Type.Dictionary ||
            !root.TryGetValue("types", out Variant typesValue) ||
            typesValue.VariantType != Variant.Type.Dictionary)
        {
            GD.PushWarning("OpenTyrianVR: hover_heights.json needs dictionary 'classes' and 'types'; ignoring");
            return;
        }
        var classes = classesValue.AsGodotDictionary();
        foreach (var key in classes.Keys)
        {
            Variant value = classes[key];
            if (value.VariantType is Variant.Type.Int or Variant.Type.Float)
                _classHeights[key.AsString()] = (float)value.AsDouble();
            else
                GD.PushWarning($"OpenTyrianVR: hover height class '{key}' is not numeric; skipping");
        }
        _groundClassOffset = _classHeights.TryGetValue("ground", out float ground) ? ground : -1f;
        var types = typesValue.AsGodotDictionary();
        foreach (var key in types.Keys)
        {
            if (!ushort.TryParse(key.AsString(), out ushort type))
                continue;
            Variant entryValue = types[key];
            if (entryValue.VariantType != Variant.Type.Dictionary)
            {
                GD.PushWarning($"OpenTyrianVR: hover height type '{key}' is not a dictionary; skipping");
                continue;
            }
            var entry = entryValue.AsGodotDictionary();
            if (entry.ContainsKey("review"))
                _reviewTypes.Add(type);
            if (entry.ContainsKey("height"))
            {
                Variant height = entry["height"];
                if (height.VariantType is Variant.Type.Int or Variant.Type.Float)
                {
                    _typeHeights[type] = (float)height.AsDouble();
                    _explicitHeightTypes.Add(type);
                }
                else
                    GD.PushWarning($"OpenTyrianVR: hover height type '{key}' has a nonnumeric height; skipping");
            }
            else if (entry.ContainsKey("class"))
            {
                Variant classValue = entry["class"];
                if (classValue.VariantType != Variant.Type.String)
                {
                    GD.PushWarning($"OpenTyrianVR: hover height type '{key}' has a non-string class; skipping");
                    continue;
                }
                string cls = classValue.AsString();
                if (cls == "ground")
                    _typeHeights[type] = float.NegativeInfinity;  // marker: surface + offset
                else if (_classHeights.TryGetValue(cls, out float classHeight))
                    _typeHeights[type] = classHeight;
            }
        }
        GD.Print($"OpenTyrianVR: hover heights loaded ({_typeHeights.Count} types)");
    }

    private static int SemanticKey(byte episode, ushort type) => (episode << 16) | type;

    private HeightSemantic SemanticFor(byte episode, ushort type) =>
        _heightSemantics.TryGetValue(SemanticKey(episode, type), out HeightSemantic semantic)
            ? semantic : HeightSemantic.Unknown;

    private void LoadHeightSemantics()
    {
        const string path = "res://height_semantics.json";
        if (!FileAccess.FileExists(path))
        {
            GD.Print("OpenTyrianVR: no episode height semantics; authored E1 heights only");
            return;
        }
        Variant parsed = Json.ParseString(FileAccess.GetFileAsString(path));
        if (parsed.VariantType != Variant.Type.Dictionary)
        {
            GD.PushWarning("OpenTyrianVR: height_semantics.json did not parse; ignoring");
            return;
        }
        var root = parsed.AsGodotDictionary();
        if (!root.TryGetValue("episodes", out Variant episodesValue) ||
            episodesValue.VariantType != Variant.Type.Dictionary)
        {
            GD.PushWarning("OpenTyrianVR: height_semantics.json needs 'episodes'; ignoring");
            return;
        }
        var episodes = episodesValue.AsGodotDictionary();
        foreach (Variant episodeKey in episodes.Keys)
        {
            if (!byte.TryParse(episodeKey.AsString(), out byte episode) ||
                episodes[episodeKey].VariantType != Variant.Type.Dictionary)
                continue;
            var types = episodes[episodeKey].AsGodotDictionary();
            foreach (Variant typeKey in types.Keys)
            {
                if (!ushort.TryParse(typeKey.AsString(), out ushort type) ||
                    types[typeKey].VariantType != Variant.Type.Dictionary)
                    continue;
                var entry = types[typeKey].AsGodotDictionary();
                if (!entry.TryGetValue("class", out Variant classValue) ||
                    classValue.VariantType != Variant.Type.String)
                    continue;
                HeightSemantic semantic = classValue.AsString() switch
                {
                    "surface" => HeightSemantic.Surface,
                    "air" => HeightSemantic.Air,
                    _ => HeightSemantic.Unknown,
                };
                if (semantic != HeightSemantic.Unknown)
                {
                    _heightSemantics[SemanticKey(episode, type)] = semantic;
                    if (episode < _semanticCountByEpisode.Length)
                        _semanticCountByEpisode[episode]++;
                }
            }
        }
        if (root.TryGetValue("dynamic_graphics", out Variant dynamicValue) &&
            dynamicValue.VariantType == Variant.Type.Dictionary)
        {
            var dynamicEpisodes = dynamicValue.AsGodotDictionary();
            foreach (Variant episodeKey in dynamicEpisodes.Keys)
            {
                if (!byte.TryParse(episodeKey.AsString(), out byte episode) ||
                    dynamicEpisodes[episodeKey].VariantType != Variant.Type.Dictionary)
                    continue;
                var graphics = dynamicEpisodes[episodeKey].AsGodotDictionary();
                foreach (Variant graphicKey in graphics.Keys)
                {
                    if (!ushort.TryParse(graphicKey.AsString(), out ushort graphic) ||
                        graphic >= 0x8000 || graphics[graphicKey].VariantType != Variant.Type.Dictionary)
                        continue;
                    var entry = graphics[graphicKey].AsGodotDictionary();
                    if (!entry.TryGetValue("class", out Variant classValue) ||
                        classValue.VariantType != Variant.Type.String)
                        continue;
                    HeightSemantic semantic = classValue.AsString() switch
                    {
                        "surface" => HeightSemantic.Surface,
                        "air" => HeightSemantic.Air,
                        _ => HeightSemantic.Unknown,
                    };
                    if (semantic != HeightSemantic.Unknown)
                    {
                        _heightSemantics[SemanticKey(episode, (ushort)(0x8000 | graphic))] = semantic;
                        if (episode < _semanticCountByEpisode.Length)
                            _semanticCountByEpisode[episode]++;
                    }
                }
            }
        }
        GD.Print($"OpenTyrianVR: episode height semantics loaded ({_heightSemantics.Count} placements)");
    }

    // --- Height editor support (OTYR_HEIGHT_EDITOR) --------------------

    /// <summary>Nearest enemy cell to a screen point (editor picking).
    /// Each cell's pick radius is its own PROJECTED quad size plus a
    /// margin, so picking works at any zoom (a fixed screen radius made
    /// close-up selection impossible: sprites grew, the radius did not).</summary>
    public bool TryPick(Camera3D camera, Vector2 screenPos,
                        out ushort entityType, out Vector3 worldPos)
    {
        entityType = 0;
        worldPos = Vector3.Zero;
        float best = float.MaxValue;
        for (int i = 0; i < _cellCount; i++)
        {
            ref readonly RenderCell cell = ref _cells[i];
            if (cell.EntityType == 0)
                continue;
            Vector3 lane = CellLanePos(in cell);
            Vector3 world = ToGlobal(lane);
            if (camera.IsPositionBehind(world))
                continue;
            Vector2 screen = camera.UnprojectPosition(world);
            float dist = screen.DistanceTo(screenPos);
            // Projected half-size: art half-width in lane units, projected.
            float halfPx = (cell.Flags & 8) != 0 ? OtyrNative.SheetCellW : OtyrNative.SheetCellW / 2f;
            Vector2 edge = camera.UnprojectPosition(ToGlobal(lane + new Vector3(halfPx * PxToMeters, 0f, 0f)));
            float radius = screen.DistanceTo(edge) + 12f;
            if (dist <= radius && dist < best)
            {
                best = dist;
                entityType = cell.EntityType;
                worldPos = world;
            }
        }
        return entityType != 0;
    }

    /// <summary>World position of the first live cell of a type, for
    /// anchoring the editor's selection label; false if none this tick.</summary>
    public bool TryLocateType(ushort entityType, out Vector3 worldPos)
    {
        for (int i = 0; i < _cellCount; i++)
        {
            if (_cells[i].EntityType == entityType)
            {
                worldPos = ToGlobal(CellLanePos(in _cells[i]));
                return true;
            }
        }
        worldPos = Vector3.Zero;
        return false;
    }

    private Vector3 CellLanePos(in RenderCell cell)
    {
        float h = _editorHeights.TryGetValue(cell.EntityType, out float o) ? o : cell.Z;
        return new Vector3(((cell.CurrPx.X - 24f) / 320f - 0.5f) * LaneWidth,
                           (0.5f - cell.CurrPx.Y / 200f) * LaneHeight, h);
    }

    // Selection highlight: translucent additive quads over every live cell
    // of the selected type, so the pick is unambiguous.
    private readonly System.Collections.Generic.List<MeshInstance3D> _editorMarkers = new();
    private StandardMaterial3D? _editorMarkerMaterial;

    public void EditorHighlight(ushort entityType)
    {
        int used = 0;
        if (entityType != 0)
        {
            _editorMarkerMaterial ??= new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                AlbedoColor = new Color(1f, 0.85f, 0.25f, 0.18f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = 10,
            };
            for (int i = 0; i < _cellCount && used < 24; i++)
            {
                if (_cells[i].EntityType != entityType)
                    continue;
                if (used == _editorMarkers.Count)
                {
                    var marker = new MeshInstance3D
                    {
                        Mesh = new QuadMesh { Size = new Vector2(15f / 320f * LaneWidth, 17f / 200f * LaneHeight) },
                        MaterialOverride = _editorMarkerMaterial,
                    };
                    AddChild(marker);
                    _editorMarkers.Add(marker);
                }
                _editorMarkers[used].Position = CellLanePos(in _cells[i]) + new Vector3(0f, 0f, 0.004f);
                _editorMarkers[used].Visible = true;
                ++used;
            }
        }
        for (int i = used; i < _editorMarkers.Count; i++)
            _editorMarkers[i].Visible = false;
    }

    // Review markers: pulsing green halos behind every live instance of a
    // type whose hover_heights.json entry carries a "review" key -- the
    // ambiguous families the auto-propagation could not settle.  Always on
    // in the editor; delete the JSON key once a height is confirmed.
    private readonly System.Collections.Generic.HashSet<ushort> _reviewTypes = new();
    private readonly System.Collections.Generic.List<MeshInstance3D> _reviewMarkers = new();
    private StandardMaterial3D? _reviewMaterial;

    public bool IsReviewType(ushort entityType) => _reviewTypes.Contains(entityType);

    public void EditorReviewMarkers()
    {
        int used = 0;
        if (_reviewTypes.Count > 0)
        {
            // Mix (not Add): additive green summed with the red hazard halo
            // read as yellow (user, SAVARA over water); mix keeps the hue.
            _reviewMaterial ??= new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = 8,
            };
            float pulse = 0.42f + 0.18f * Mathf.Sin(Time.GetTicksMsec() / 280f);
            _reviewMaterial.AlbedoColor = new Color(0.15f, 0.95f, 0.35f, pulse);
            for (int i = 0; i < _cellCount && used < 32; i++)
            {
                ref readonly RenderCell cell = ref _cells[i];
                if (cell.EntityType == 0 || !_reviewTypes.Contains(cell.EntityType))
                    continue;
                if (used == _reviewMarkers.Count)
                {
                    var marker = new MeshInstance3D
                    {
                        Mesh = new QuadMesh { Size = new Vector2(20f / 320f * LaneWidth, 22f / 200f * LaneHeight) },
                        MaterialOverride = _reviewMaterial,
                    };
                    AddChild(marker);
                    _reviewMarkers.Add(marker);
                }
                var m = _reviewMarkers[used];
                m.Scale = (cell.Flags & 8) != 0 ? new Vector3(2f, 2f, 1f) : Vector3.One;
                m.Position = CellLanePos(in cell) + new Vector3(0f, 0f, -0.002f);
                m.Visible = true;
                ++used;
            }
        }
        for (int i = used; i < _reviewMarkers.Count; i++)
            _reviewMarkers[i].Visible = false;
    }

    // Hazard (collider) markers: red halos under every record whose contact
    // damages the player (flag bit 64, mirroring JE_playerCollide); blue
    // halos for magnet objects (flag bit 128 -- attract/push force fields,
    // the MINES wall bumpers).  Same B toggle.
    private readonly System.Collections.Generic.List<MeshInstance3D> _hazardMarkers = new();
    private StandardMaterial3D? _hazardMaterial;
    private StandardMaterial3D? _magnetMaterial;
    public bool HazardMarkersEnabled = true;

    public void EditorHazardMarkers()
    {
        int used = 0;
        if (HazardMarkersEnabled)
        {
            _hazardMaterial ??= new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                AlbedoColor = new Color(1f, 0.15f, 0.1f, 0.28f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = 9,
            };
            _magnetMaterial ??= new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                AlbedoColor = new Color(0.2f, 0.45f, 1f, 0.30f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = 9,
            };
            for (int i = 0; i < _cellCount && used < 64; i++)
            {
                ref readonly RenderCell cell = ref _cells[i];
                if (cell.EntityType == 0 || (cell.Flags & (64 | 128)) == 0)
                    continue;
                // Review-flagged types show only the green triage glow; the
                // red halo on top summed to a misleading yellow.
                if (_reviewTypes.Contains(cell.EntityType))
                    continue;
                if (used == _hazardMarkers.Count)
                {
                    var marker = new MeshInstance3D
                    {
                        Mesh = new QuadMesh { Size = new Vector2(16f / 320f * LaneWidth, 18f / 200f * LaneHeight) },
                    };
                    AddChild(marker);
                    _hazardMarkers.Add(marker);
                }
                var m = _hazardMarkers[used];
                // Damage wins the color when an object is both.
                m.MaterialOverride = (cell.Flags & 64) != 0 ? _hazardMaterial : _magnetMaterial;
                bool big = (cell.Flags & 8) != 0;
                m.Scale = big ? new Vector3(2f, 2f, 1f) : Vector3.One;
                m.Position = CellLanePos(in cell) + new Vector3(0f, 0f, -0.0015f);
                m.Visible = true;
                ++used;
            }
        }
        for (int i = used; i < _hazardMarkers.Count; i++)
            _hazardMarkers[i].Visible = false;
    }

    /// <summary>Editor: pick the topmost background layer under the cursor
    /// (fallback when no enemy is within pick radius).</summary>
    public bool TryPickLayer(Camera3D camera, Vector2 screenPos, out int layer, out float z, out string name)
    {
        layer = -1;
        z = 0f;
        name = "";
        if (_background == null)
            return false;
        Transform3D inv = _background.GlobalTransform.AffineInverse();
        Vector3 origin = inv * camera.ProjectRayOrigin(screenPos);
        Vector3 dir = (inv.Basis * camera.ProjectRayNormal(screenPos)).Normalized();
        if (!_background.TryPickLayer(origin, dir, out layer, out z))
            return false;
        name = _background.LayerName(layer);
        return true;
    }

    /// <summary>Editor: whole-layer highlight passthrough (-1 hides).</summary>
    public void EditorHighlightLayer(int layer) => _background?.EditorHighlightLayer(layer);

    /// <summary>Frame.StormWater passthrough (host-rendered water smoothie).</summary>
    public void SetStorm(byte code) => _background?.SetStorm(code);

    /// <summary>Editor: human-readable band description for a type --
    /// pending edit, assigned class, explicit height, or the legacy band.</summary>
    public string EditorDescribe(ushort entityType)
    {
        if (_editorPending.TryGetValue(entityType, out string? pending))
            return $"{pending} (UNSAVED)";
        if (_typeHeights.TryGetValue(entityType, out float h))
        {
            if (float.IsNegativeInfinity(h))
                return "ground";
            foreach (var (name, classH) in _classHeights)
                if (name != "ground" && Mathf.Abs(classH - h) < 0.0004f)
                    return name;
            return "explicit height";
        }
        return "legacy band";
    }

    // Editor overrides: resolved heights applied live (also mid-pause, via
    // WriteTransforms) and the class/height strings pending a save.
    private readonly System.Collections.Generic.Dictionary<ushort, float> _editorHeights = new();
    private readonly System.Collections.Generic.Dictionary<ushort, string> _editorPending = new();

    /// <summary>Editor: current effective height of a type (edited, table,
    /// or a representative live cell's band).  NaN = ground class (surface-
    /// following, no fixed height -- the readout must not show a stale
    /// number for it).</summary>
    public float EditorHeightOf(ushort entityType)
    {
        if (_editorHeights.TryGetValue(entityType, out float h))
            return h;
        if (_typeHeights.TryGetValue(entityType, out float t))
            return float.IsNegativeInfinity(t) ? float.NaN : t;
        for (int i = 0; i < _cellCount; i++)
            if (_cells[i].EntityType == entityType)
                return _cells[i].Z;
        return 0.04f;
    }

    /// <summary>Editor: set an explicit height for a type (applies live).</summary>
    public void EditorSetHeight(ushort entityType, float height)
    {
        _editorHeights[entityType] = height;
        _typeHeights[entityType] = height;
        _editorPending[entityType] = height.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Ground-class assignments made while PAUSED get a temporary override
    // sampled from a live instance's surface so they move immediately; the
    // next real snapshot clears them and true per-instance banding takes
    // over.
    private readonly System.Collections.Generic.HashSet<ushort> _editorGroundTemp = new();

    /// <summary>Editor: assign a class by name (resolves through the loaded
    /// classes table; "ground" surface-resolves per instance).</summary>
    public void EditorSetClass(ushort entityType, string cls, float classHeight)
    {
        if (cls == "ground")
        {
            _typeHeights[entityType] = float.NegativeInfinity;
            _editorHeights.Remove(entityType);
            // Paused preview: approximate with the first live instance's
            // surface so the assignment is visible before the next tick.
            for (int i = 0; i < _cellCount; i++)
            {
                if (_cells[i].EntityType != entityType)
                    continue;
                float surface = _background?.SurfaceZAt(_cells[i].CurrPx - new Vector2(24f, 0f)) ?? 0f;
                _editorHeights[entityType] = surface + 0.0012f;
                _editorGroundTemp.Add(entityType);
                break;
            }
        }
        else
        {
            _typeHeights[entityType] = classHeight;
            _editorHeights[entityType] = classHeight;
            _editorGroundTemp.Remove(entityType);
        }
        _editorPending[entityType] = cls;
    }

    /// <summary>Editor: write pending edits back into hover_heights.json,
    /// preserving untouched entries.  Returns the number saved.</summary>
    public int EditorSave()
    {
        const string path = "res://hover_heights.json";
        if (!FileAccess.FileExists(path) || (_editorPending.Count == 0 && !_waterCloudDirty))
            return 0;
        Variant parsed = Json.ParseString(FileAccess.GetFileAsString(path));
        if (parsed.VariantType != Variant.Type.Dictionary)
        {
            GD.PushWarning("OpenTyrianVR: cannot save hover heights: JSON root is not a dictionary");
            return 0;
        }
        var root = parsed.AsGodotDictionary();
        if (!root.TryGetValue("types", out Variant typesValue) ||
            typesValue.VariantType != Variant.Type.Dictionary ||
            !root.TryGetValue("classes", out Variant classesValue) ||
            classesValue.VariantType != Variant.Type.Dictionary)
        {
            GD.PushWarning("OpenTyrianVR: cannot save hover heights: dictionary 'classes' or 'types' is missing");
            return 0;
        }
        var types = typesValue.AsGodotDictionary();
        foreach (var (type, value) in _editorPending)
        {
            var entry = types.ContainsKey(type.ToString())
                ? types[type.ToString()].AsGodotDictionary()
                : new Godot.Collections.Dictionary();
            if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out float h))
            {
                entry["height"] = h;
                entry.Remove("class");
            }
            else
            {
                entry["class"] = value;
                entry.Remove("height");
            }
            // A hand-set value settles the type: drop the propagation
            // provenance and the review glow.
            entry.Remove("auto");
            entry.Remove("review");
            _reviewTypes.Remove(type);
            types[type.ToString()] = entry;
        }
        int saved = _editorPending.Count;
        if (_waterCloudDirty && _background != null)
        {
            var classes = classesValue.AsGodotDictionary();
            classes["water-clouds"] = _background.WaterCloudHeight;
            root["classes"] = classes;
            _waterCloudDirty = false;
            ++saved;
        }
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        f.StoreString(Json.Stringify(root, "  "));
        _editorPending.Clear();
        return saved;
    }

    /// <summary>Editor: the pending (unsaved) edit for a type, if any.</summary>
    public string? EditorPendingOf(ushort entityType) =>
        _editorPending.TryGetValue(entityType, out string? v) ? v : null;

    /// <summary>Editor: distinct live band heights of a type's on-screen
    /// cells -- surface-following diagnosis (which surface did each
    /// instance actually resolve to?).</summary>
    public string EditorLiveZOf(ushort entityType)
    {
        var seen = new System.Collections.Generic.SortedSet<float>();
        for (int i = 0; i < _cellCount; i++)
            if (_cells[i].EntityType == entityType)
                seen.Add(Mathf.Round(_cells[i].Z * 1000f) / 1000f);
        return seen.Count == 0 ? "" :
            string.Join("/", System.Linq.Enumerable.Select(seen, z => z.ToString("0.###")));
    }

    // Water-cloud layer height (editor-adjustable, saved to
    // classes["water-clouds"]).
    private bool _waterCloudDirty;
    public bool WaterCloudsSelectable => _background?.WaterCloudsArmed ?? false;
    public float EditorWaterCloudHeight =>
        _background?.WaterCloudHeight ?? BackgroundLayer.WaterCloudZ;

    public void EditorSetWaterCloudHeight(float h)
    {
        if (_background == null)
            return;
        _background.SetWaterCloudHeight(h);
        _classHeights["water-clouds"] = _background.WaterCloudHeight;
        _waterCloudDirty = true;
    }

    // One surface decision per entity per tick: querying per cell split
    // multi-cell structures across heights when they straddled a platform
    // edge (4 cells floating, 2 on the ground).
    private readonly System.Collections.Generic.Dictionary<ushort, float> _surfaceBySource = new();
    private readonly System.Collections.Generic.HashSet<ushort> _groundAssemblySources = new();
    private readonly int[] _spriteAssemblyParent = new int[OtyrNative.SnapshotSpriteMax];
    private readonly bool[] _spriteAssemblyValid = new bool[OtyrNative.SnapshotSpriteMax];
    private readonly bool[] _spriteAssemblyGround = new bool[OtyrNative.SnapshotSpriteMax];
    private readonly float[] _spriteAssemblySurface = new float[OtyrNative.SnapshotSpriteMax];
    private readonly Vector2[] _spriteAssemblyCenter = new Vector2[OtyrNative.SnapshotSpriteMax];
    private readonly Vector2[] _spriteAssemblyHalf = new Vector2[OtyrNative.SnapshotSpriteMax];

    private float SurfaceForEntity(ushort sourceId, float centerX, float centerY)
    {
        if (sourceId != OtyrNative.NoSource && _surfaceBySource.TryGetValue(sourceId, out float cached))
            return cached;
        float surface = _background?.SurfaceZAt(new Vector2(centerX - 24f, centerY)) ?? 0f;
        if (sourceId != OtyrNative.NoSource)
            _surfaceBySource[sourceId] = surface;
        return surface;
    }

    /// <summary>Pairs a cell with last tick's nearest same-source cell on
    /// the same render layer (within a small radius, so genuinely new cells
    /// appear in place instead of stretching from a sibling).</summary>
    private ushort _pairRunSource = OtyrNative.NoSource;
    private int _pairRunOrdinal;

    private void PairWithPrevious(ref RenderCell cell, ushort sourceId, bool interpolatePosition = true)
    {
        _cellSource[_cellCount] = sourceId;
        _pairRunOrdinal = sourceId == _pairRunSource ? _pairRunOrdinal + 1 : 0;
        _pairRunSource = sourceId;
        if (sourceId == OtyrNative.NoSource || !_prevRuns.TryGetValue(sourceId, out var run))
            return;

        // Single-cell sources keep the conservative legacy radius.  Rigid
        // multi-cell assemblies get enough radius for skipped publications;
        // beyond the bounded gap they all snap together (no partial pairing,
        // hence no seam) rather than guessing across recycled entity slots.
        bool rigidRun = run.Count > 1;
        if (rigidRun && _pairTickGap > MaxInterpolatedTickGap)
            return;
        float pairRadius = rigidRun ? RigidPairRadiusPerTickPx * _pairTickGap : PairRadiusPx;

        // Ordinal-first: same-source cells emit in a stable record order,
        // so cell N pairs with last tick's cell N.  Nearest-match alone let
        // a fast-falling stacked enemy's TOP cell pair with last tick's
        // BOTTOM cell (closer than its own previous position) -- the halves
        // interpolated apart and a seam opened between them (user-caught).
        // Nearest remains the fallback for run-shape changes (edge-of-
        // screen row gating).
        int ordinalIdx = run.Start + _pairRunOrdinal;
        if (_pairRunOrdinal < run.Count && _prevCells[ordinalIdx].SheetId == cell.SheetId &&
            _prevCells[ordinalIdx].EntityType == cell.EntityType &&
            _prevCells[ordinalIdx].CurrPx.DistanceTo(cell.CurrPx) < pairRadius)
        {
            InheritFromPrevious(ref cell, ordinalIdx, interpolatePosition);
            return;
        }

        float bestDist = pairRadius;
        int bestIdx = -1;
        for (int i = run.Start; i < run.Start + run.Count; i++)
        {
            if (_prevCells[i].SheetId != cell.SheetId)
                continue;
            if (_prevCells[i].EntityType != cell.EntityType)
                continue;
            float dist = _prevCells[i].CurrPx.DistanceTo(cell.CurrPx);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }
        if (bestIdx >= 0)
            InheritFromPrevious(ref cell, bestIdx, interpolatePosition);
    }

    private void InheritFromPrevious(ref RenderCell cell, int prevIdx, bool interpolatePosition)
    {
        if (!interpolatePosition)
            return;  // statics step with the tile grid; position interpolation
                     // would make art swim against its baked underlay
        cell.PrevPx = _prevCells[prevIdx].CurrPx;
        cell.HasPrev = true;
    }

    private void AddOldCell(in OtyrNative.SnapshotSprite sprite, uint recordIndex)
    {
        if (_cellCount >= _cells.Length || sprite.Index >= OtyrNative.OldSpriteMax)
            return;
        Vector2I size = _oldSize[sprite.Index];
        if (size == Vector2I.Zero)
            return;

        ref RenderCell cell = ref _cells[_cellCount];
        cell.SheetId = OldLayer;
        cell.CellIndex = sprite.Index;
        cell.EntityType = 0;  // reused array slot: stale enemy types here
                              // made the editor move/highlight these cells
        // Repurposed for the old layer: quad scale in pixels (fits a byte).
        cell.Flags = (byte)size.X;
        cell.FilterColor = (byte)size.Y;
        cell.Category = sprite.Category;
        cell.CastFrom = -1;
        cell.Z = BandHeight[(byte)OtyrNative.Category.PlayerShot] + recordIndex * OrderBias;
        cell.CurrPx = new Vector2(sprite.X + size.X / 2f, sprite.Y + size.Y / 2f);
        cell.PrevPx = cell.CurrPx;
        cell.HasPrev = false;
        PairWithPrevious(ref cell, sprite.SourceId);
        ++_cellCount;
    }

    private void AddTextCell(in OtyrNative.SnapshotSprite sprite, uint recordIndex)
    {
        if (_cellCount >= _cells.Length || sprite.Index >= OtyrNative.OldSpriteMax)
            return;
        int table = sprite.FilterColor >> 4;  // 0 big, 1 small, 2 tiny
        if (table > 2)
            return;
        int slot = (table + 1) * OtyrNative.OldSpriteMax + sprite.Index;
        Vector2I size = _oldSize[slot];
        if (size == Vector2I.Zero)
            return;

        // Flag decode mirrors OTYR_KIND_SPRITE_HV: 4 = halve-dest drop
        // shadow (multiplicative layer), 8 = solid black, 2 = dest blend,
        // 16 = clamped value (plain hv), else unsafe wrap.
        bool darken = (sprite.Flags & 4) != 0;
        int mode = (sprite.Flags & 8) != 0 ? 3
                 : (sprite.Flags & 2) != 0 ? 2
                 : (sprite.Flags & 16) != 0 ? 1 : 0;

        ref RenderCell cell = ref _cells[_cellCount];
        cell.SheetId = darken ? TextShadowLayer : TextLayer;
        cell.CellIndex = slot;
        cell.EntityType = 0;  // reused array slot: stale enemy types here
                              // broke the Player 1 HUD text under the editor
        // Repurposed like the old-table layer: quad scale in pixels.
        cell.Flags = (byte)size.X;
        cell.FilterColor = (byte)size.Y;
        cell.Aux0 = mode + (sprite.FilterColor & 0x0f) * 4;
        cell.Aux1 = sprite.Aux;  // signed value shift, as a byte
        cell.Category = sprite.Category;
        cell.CastFrom = -1;
        // Drop shadows live on their own sub-plane well below the glyphs:
        // a shadow one OrderBias step under its glyph quantized to the SAME
        // depth, and the glyph/shadow nodes have no stable draw order, so
        // letters flickered half-dark as the tie broke differently per
        // frame.  (Legacy draws the next letter's shadow over the previous
        // glyph's right edge; the sub-plane loses that 1px darkening.)
        float band = BandHeight[(byte)OtyrNative.Category.Text] - (darken ? 0.0008f : 0f);
        cell.Z = band + _textOrder++ * OrderBias;
        cell.DecalOrder = 0f;
        cell.CurrPx = new Vector2(sprite.X + size.X / 2f, sprite.Y + size.Y / 2f);
        cell.PrevPx = cell.CurrPx;
        cell.HasPrev = false;
        // Text is stationary; render at the recorded position every tick.
        _cellSource[_cellCount] = OtyrNative.NoSource;
        ++_cellCount;
    }

    private void AddCell(in OtyrNative.SnapshotSprite sprite, uint recordIndex, int cellIndex, int pixelOffsetX, int pixelOffsetY)
    {
        if (_cellCount >= _cells.Length)
            return;

        bool big = sprite.Kind == 1;  // 2x2: one 24x28 quad
        float centerX = sprite.X + pixelOffsetX + (big ? OtyrNative.SheetCellW : OtyrNative.SheetCellW / 2f);
        float centerY = sprite.Y + pixelOffsetY + (big ? OtyrNative.SheetCellH : OtyrNative.SheetCellH / 2f);

        // E2 de-parallax: enemy-band records rebase to their fixed map
        // offsets (v21 deltas) -- the SIM keeps legacy parallax, so hitbox
        // vs visual diverges up to the half-swing (accepted, round 9).
        // Screen-space categories (player, shots, explosions, shadows of
        // the player) stay put.
        if (sprite.Category <= (byte)OtyrNative.Category.EnemyGroundB)
            centerX -= _snapshot.BandParallax(sprite.Category);

        ref RenderCell cell = ref _cells[_cellCount];
        // Darken blits (shadows, iced) go to the multiplicative shadow
        // layer of their sheet instead of the color layer.
        bool darken = (sprite.Flags & 4) != 0;
        cell.SheetId = darken ? ShadowLayerBase + sprite.SheetId : sprite.SheetId;
        cell.CellIndex = cellIndex - 1;
        // Bit 8 tells the shader (and the transform pass) this is a 2x2
        // quad; the record flags only use bits 1/2/4.
        cell.Flags = (byte)(sprite.Flags | (big ? 8 : 0));
        cell.FilterColor = sprite.FilterColor;
        cell.Category = sprite.Category;
        cell.CastFrom = -1;
        bool isEnemy = sprite.Category <= (byte)OtyrNative.Category.EnemyGroundB;
        bool isShadow = sprite.Category == (byte)OtyrNative.Category.Shadow;
        // Dynamic type-zero semantic keys are deliberately not editor keys:
        // the same temporary slot is reused for unrelated graphics.
        bool dynamicSemantic = (sprite.EntityType & 0x8000) != 0;
        cell.EntityType = isEnemy && !dynamicSemantic ? sprite.EntityType : (ushort)0;
        cell.AssemblyId = isEnemy ? sprite.AssemblyId : (byte)0;
        float band;
        float decalOrder = 0f;
        float authored = 0f;
        // hover_heights.json is the hand-tuned Episode 1 fine-height table.
        // Type ids are episode-local, so it must never leak into E2-E4.
        bool episodeOne = _snapshot.Episode == 1;
        bool hasAuthored = isEnemy && episodeOne &&
            _typeHeights.TryGetValue(sprite.EntityType, out authored);
        bool explicitHeight = isEnemy && episodeOne &&
            _explicitHeightTypes.Contains(sprite.EntityType);
        HeightSemantic semantic = isEnemy
            ? SemanticFor(_snapshot.Episode, sprite.EntityType)
            : HeightSemantic.Unknown;
        if (semantic == HeightSemantic.Surface &&
            !(explicitHeight && hasAuthored && !float.IsNegativeInfinity(authored) && authored < 0.015f))
        {
            authored = float.NegativeInfinity;
            hasAuthored = true;
        }
        else if (semantic == HeightSemantic.Air &&
                 (!hasAuthored || float.IsNegativeInfinity(authored)))
        {
            authored = _classHeights.TryGetValue("air-mid", out float airMid) ? airMid : 0.0355f;
            hasAuthored = true;
        }
        bool weakGroundSignal = isEnemy &&
            (sprite.Aux == 3 || _groundAssemblySources.Contains(sprite.SourceId));
        bool authoredSurface = hasAuthored && float.IsNegativeInfinity(authored);
        bool semanticGround = semantic == HeightSemantic.Surface ||
            (semantic == HeightSemantic.Unknown && weakGroundSignal && authoredSurface);
        // Low explicit values are hand-tuned offsets above the original
        // ground plane.  Preserve that offset when the same building/tank
        // is instantiated on an aerial platform.  High explicit values are
        // deliberate flying exceptions (type 15 is the E1 example).
        bool surfaceRelativeExplicit = semanticGround && explicitHeight &&
            hasAuthored && !float.IsNegativeInfinity(authored) && authored < 0.015f;
        // Native metadata corroborates a surface placement; it does not
        // overrule an authored air class.  Several E1 flying component
        // families intentionally use the ground-explosion palette.
        bool automaticSemanticSurface = semanticGround &&
            (!explicitHeight || authoredSurface || surfaceRelativeExplicit);
        bool staticEnemy = isEnemy && (sprite.Flags & 32) != 0;
        if (hasAuthored && !float.IsNegativeInfinity(authored) && !automaticSemanticSurface)
        {
            // Authored hover height (Stage B): an explicit table/class
            // height wins EVEN over decal banding -- the under-platform
            // spikes are aux-1 rider records that must sit BELOW their
            // platform, which surface banding can never produce.  (It also
            // makes editor nudges apply to statics, which are the objects
            // most worth tuning.)
            band = authored;
        }
        else if (automaticSemanticSurface)
        {
            float below = SurfaceForEntity(sprite.SourceId, centerX, centerY);
            float surface = below > 0f ? below : BackgroundLayer.GroundZ;
            if (surfaceRelativeExplicit)
                band = surface + (authored - BackgroundLayer.GroundZ);
            else if (staticEnemy)
                band = surface;
            else
                band = surface + Math.Max(_groundClassOffset, 0.002f);
            if (staticEnemy)
                decalOrder = (recordIndex + 1f) / OtyrNative.SnapshotSpriteMax;
        }
        else if (isEnemy && (sprite.Aux == 1 || sprite.Aux == 2 ||
                             ((sprite.Flags & 32) != 0 && hasAuthored)))
        {
            // Flag 32 = sim-truth static (never latched as a mover): a
            // GROUND-CLASS one surface-glues like any static even when its
            // sparse art failed the native opaque-cell test (aux 0).
            // DELIANI decorations split -0.0008 vs +0.004 on that art
            // coin flip (237/238 etc.).
            // Every static/rider decals the surface actually beneath it:
            // platform art if a platform covers its center, ground
            // otherwise.  Banding non-TOP categories to the ground put
            // first-spawn/static enemies UNDER the elevated platform layer
            // whenever they crossed one -- the platform drew over them
            // ("transparent over platforms, solid over true ground").
            // A "ground"-class entry also lands here: surface-riding IS the
            // ground class for statics -- and it OUTRANKS the top-band
            // platform floor: DELIANI draws plain ground decorations in the
            // enemy-top band, and the unconditional floor hoisted them to
            // platform height while their ground-band twins sat on the
            // ground (types 237/238, snap-back on unpause).  Unauthored
            // top statics keep the floor (legacy paint order).
            float below = SurfaceForEntity(sprite.SourceId, centerX, centerY);
            band = sprite.Category == (byte)OtyrNative.Category.EnemyTop && !hasAuthored
                ? Math.Max(below, BackgroundLayer.PlatformZ)
                : (below > 0f ? below : BackgroundLayer.GroundZ);
            decalOrder = (recordIndex + 1f) / OtyrNative.SnapshotSpriteMax;
        }
        else if (isShadow)
        {
            // Shadows decal the topmost scenery under them (clouds
            // included: legacy draws player/shot shadows after the cloud
            // layer); the depth bias orders them against the statics on
            // the same plane.
            float surface = _background?.SurfaceZAt(new Vector2(centerX - 24f, centerY), includeClouds: true) ?? 0f;
            band = surface > 0f ? surface : BackgroundLayer.GroundZ;
            decalOrder = (recordIndex + 1f) / OtyrNative.SnapshotSpriteMax;
        }
        else if (hasAuthored)
        {
            // "ground" class for MOVERS: the surface actually beneath
            // (terrain or platform) plus a small offset.
            float below = SurfaceForEntity(sprite.SourceId, centerX, centerY);
            band = (below > 0f ? below : 0f) + Math.Max(_groundClassOffset, 0.002f);
        }
        else
        {
            band = BandHeight[Math.Min(sprite.Category, (byte)(BandHeight.Length - 1))];
        }
        cell.Z = band + (decalOrder > 0f ? 0f
            : cell.EntityType != 0 ? EnemyOrderBias(centerY, recordIndex)
            : recordIndex * OrderBias);
        cell.DecalOrder = decalOrder;
        cell.CurrPx = new Vector2(centerX, centerY);
        cell.PrevPx = cell.CurrPx;
        cell.HasPrev = false;

        // Baked structures (and statics stacked on them) are locked to the
        // map tiles beneath; the tile layers step per tick, so interpolating
        // the art over them would swim against its own baked underlay.
        // Explosions don't interpolate either: their slot ids recycle every
        // few ticks, so a recycled slot paired a NEW burst with a dead one
        // within the radius and the quad slid across whatever it followed --
        // for the player-following shield/thruster sparkles that smeared
        // translucent explosion art over the ship every frame (the
        // long-standing "speckle", writ large by single-quad 2x2s).  Bursts
        // live 3-12 ticks and drift ~1px/tick; stepping is imperceptible.
        bool isExplosion = sprite.Category == (byte)OtyrNative.Category.Explosion;
        // An authored FIXED height opts an aux-2 record back into mover
        // interpolation.  Aux 2 is the native's slow-mover-over-a-static
        // guess, and it flips per tick as a crawling enemy (tank turret)
        // crosses baked statics -- alternating stepped/interpolated motion
        // reads as flicker.  A fixed height says "free mover here": nothing
        // to glue to.  Aux-1 art and ground-class (surface-following)
        // authored heights keep stepping with the tile layers.
        bool authoredFloat = hasAuthored && !float.IsNegativeInfinity(authored);
        if (isExplosion || (isEnemy && sprite.Aux == 1) ||
            (isEnemy && sprite.Aux == 2 && !authoredFloat) ||
            (automaticSemanticSurface && staticEnemy) ||
            (isEnemy && (sprite.Flags & 32) != 0 && hasAuthored && !authoredFloat))
        {
            if (isExplosion)
            {
                // Bursts must never pair: recycled slots smeared new bursts
                // across dead ones. The playfield shader crops them cleanly.
                _cellSource[_cellCount] = OtyrNative.NoSource;
            }
            else
            {
                // Keep static source lineage for aux-flip handoffs, but never
                // position-interpolate art away from its baked underlay.
                PairWithPrevious(ref cell, sprite.SourceId, interpolatePosition: false);
            }
            ++_cellCount;
            return;
        }

        PairWithPrevious(ref cell, sprite.SourceId);
        ++_cellCount;
    }

    private void WriteTransforms()
    {
        // Interpolation phase within the snapshot interval.
        float t = 1f;
        if (_snapshotArrivalUsec != 0)
        {
            double elapsed = (Time.GetTicksUsec() - _snapshotArrivalUsec) / 1_000_000.0;
            t = (float)Mathf.Clamp(elapsed / _snapshotPeriod, 0.0, 1.0);
        }

        _background?.OnRender(t);
        UpdateClipTransforms();

        Array.Clear(_instanceCount);

        for (int i = 0; i < _cellCount; i++)
        {
            ref readonly RenderCell cell = ref _cells[i];

            Vector2 px = cell.HasPrev ? cell.PrevPx.Lerp(cell.CurrPx, t) : cell.CurrPx;
            float castShadowZ = 0f;
            int castShadowReceiver = 0;
            if (cell.CastFrom >= 0)
            {
                ref readonly RenderCell caster = ref _cells[cell.CastFrom];
                Vector2 casterPx = caster.HasPrev
                    ? caster.PrevPx.Lerp(caster.CurrPx, t) : caster.CurrPx;
                float casterZ = caster.Z;
                if (caster.EntityType != 0 &&
                    _editorHeights.TryGetValue(caster.EntityType, out float editedCasterZ))
                    casterZ = editedCasterZ;
                if (!ProjectVirtualSunShadow(casterPx, casterZ, out px, out castShadowZ,
                                             out castShadowReceiver))
                    continue;
            }

            // Every terrain decal receives the exact sub-tick motion of its
            // underlying map layer. Ground, structures and platforms now all
            // glide at render rate without the art swimming off its underlay.
            if (cell.DecalOrder > 0f && _background != null)
                px += _background.SubTickOffsetAt(cell.Z);
            // Authored-height STATICS (e.g. platform-under spikes) left the
            // decal path but still step per tick over a smooth-scrolling
            // elevated layer: glue them to the nearest one.
            else if (!cell.HasPrev && cell.EntityType != 0 && _background != null)
                px += _background.SubTickOffsetAt(cell.Z, 0.006f);

            int id = cell.SheetId;

            // Pixel-scaled quads: the old-table and text layers store the
            // sprite size in Flags/FilterColor.
            bool pixelQuad = id == OldLayer || id == TextLayer || id == TextShadowLayer;
            // Sheet-layer 2x2 sprites render as one 24x28 quad (flag bit 8).
            bool big = !pixelQuad && id != GlowLayer && (cell.Flags & 8) != 0;

            // Cull cells fully outside the cropped de-parallax playfield.
            // The shader clips partially crossing quads precisely at the
            // boundary, reproducing the original bezel reveal without alpha.
            float halfW = id == GlowLayer ? 1f : pixelQuad ? cell.Flags / 2f : big ? OtyrNative.SheetCellW : OtyrNative.SheetCellW / 2f;
            float halfH = id == GlowLayer ? 1f : pixelQuad ? cell.FilterColor / 2f : big ? OtyrNative.SheetCellH : OtyrNative.SheetCellH / 2f;
            float frameX = px.X - 24f;
            if (frameX + halfW <= CropX0 || frameX - halfW >= CropX1 ||
                px.Y + halfH <= CropY0 || px.Y - halfH >= CropY1)
                continue;

            // Frame pixels (game_screen, composited -24) -> lane local.
            float laneX = (frameX / 320f - 0.5f) * LaneWidth;
            float laneY = (0.5f - px.Y / 200f) * LaneHeight;

            // ALL decals get a real geometric lift above their layer, with
            // the paint order folded into real height: the in-shader depth
            // bias (1e-5) sits below the VR multiview depth-precision floor,
            // so exactly-coplanar decals z-fight their own layer -- worst at
            // the lane's FAR half where precision is coarsest (the round-7
            // "offset in the top half of the screen" ground statics and the
            // see-through carrier wings).  Ground decals ride 0.0006 above
            // the tiles (~0.2 mm parallax) which also matches legacy paint
            // order over the structure layers; elevated decals ride 0.0015
            // above their platform/cloud.  Real depth wins everywhere; the
            // shader bias stays as flat-mode belt-and-braces.
            // Height-editor live override FIRST (decals included -- statics
            // are the objects most worth tuning); applies to frozen (paused)
            // cells too, so nudges are visible without unpausing.  The
            // override stacks same-type instances by screen Y (any slot-
            // derived order is unstable -- see EnemyOrderBias) and
            // suppresses the decal lift (an overridden platform-under decal
            // otherwise gained the lift back and hid at the platform plane
            // until unpause re-banded it).
            float z = cell.Z;
            if (cell.CastFrom >= 0)
                z = castShadowZ;
            float editH = 0f;
            bool overridden = cell.EntityType != 0 &&
                _editorHeights.TryGetValue(cell.EntityType, out editH);
            if (overridden)
                z = editH + EnemyOrderBias(cell.CurrPx.Y, i);
            // The lift exists for VR multiview (per-eye depth-precision
            // ghosting); viewed obliquely it parallaxes decals up to ~1 px
            // off their baked underlay.  The editor is flat single-view,
            // where the in-shader depth bias alone is reliable -- skip the
            // lift there so alignment reads pixel-true at any orbit angle.
            // EXCEPT shadows: they have no baked underlay to align with,
            // and unlifted they sit exactly on the ground quad's depth-
            // prepass plane -- the depth-test tie flickered the player
            // shadow in and out with camera tilt.
            bool isShadowCell = cell.SheetId >= ShadowLayerBase &&
                                cell.SheetId < ShadowLayerBase + OtyrNative.SheetCount;
            if (cell.DecalOrder > 0f && !overridden && (!FlatEditorMode || isShadowCell))
                z += (z > 0.001f ? 0.0015f : 0.0006f) + cell.DecalOrder * 0.0004f;
            int instance = _instanceCount[id]++;
            if (instance >= _multiMesh[id].InstanceCount)
            {
                _instanceCount[id] = (int)_multiMesh[id].InstanceCount;
                continue;
            }

            // The old-table and text layers use a unit-pixel quad scaled to
            // the sprite's size (stored in Flags/FilterColor); 2x2 sheet
            // sprites scale the 12x14 cell quad to 24x28.
            Basis basis = pixelQuad
                ? Basis.Identity.Scaled(new Vector3(cell.Flags, cell.FilterColor, 1f))
                : big ? Basis.Identity.Scaled(new Vector3(2f, 2f, 1f))
                : Basis.Identity;

            _multiMesh[id].SetInstanceTransform(instance,
                new Transform3D(basis, new Vector3(laneX, laneY, z)));
            _multiMesh[id].SetInstanceCustomData(instance,
                id == TextLayer || id == TextShadowLayer
                    ? new Color(cell.CellIndex, cell.Flags + cell.FilterColor * 65f, cell.Aux0, cell.Aux1)
                    : new Color(cell.CellIndex, cell.Flags + (cell.SeamGuard ? 256f : 0f),
                                cell.FilterColor, cell.CastFrom >= 0
                                    ? -2f - castShadowReceiver : cell.DecalOrder));

        }

        for (int id = 0; id < LayerCount; id++)
            _multiMesh[id].VisibleInstanceCount = _instanceCount[id];
        VisibleInstanceCount = 0;
        for (int id = 0; id < LayerCount; id++)
            VisibleInstanceCount += _instanceCount[id];
    }

    private bool ProjectVirtualSunShadow(Vector2 casterPx, float casterZ,
                                         out Vector2 shadowPx, out float shadowZ,
                                         out int receiverLayer)
    {
        shadowPx = casterPx;
        receiverLayer = 0;
        float surface = _background?.SurfaceZAt(
            casterPx - new Vector2(24f, 0f), includeClouds: true,
            out receiverLayer) ?? BackgroundLayer.GroundZ;
        if (surface <= 0f)
            surface = BackgroundLayer.GroundZ;

        // Re-sample after projection: a flyer can cast from cloud to platform
        // or from a platform edge onto the deep ground.
        for (int pass = 0; pass < 2; pass++)
        {
            float gap = casterZ - surface;
            if (gap <= 0.0015f)
            {
                shadowZ = 0f;
                return false;
            }
            shadowPx = casterPx + new Vector2(
                gap * VirtualSunShadowXPerMeter,
                gap * VirtualSunShadowYPerMeter);
            int sampledLayer = 0;
            float sampled = _background?.SurfaceZAt(
                shadowPx - new Vector2(24f, 0f), includeClouds: true,
                out sampledLayer) ?? BackgroundLayer.GroundZ;
            surface = sampled > 0f ? sampled : BackgroundLayer.GroundZ;
            receiverLayer = sampled > 0f ? sampledLayer : 0;
        }
        shadowZ = surface + VirtualShadowLift;
        return true;
    }
}
