using Godot;
using System;

namespace OpenTyrianVR;

/// <summary>
/// Renders the game's three scrolling background map layers from the exported
/// tile data (ABI v8) in place of the legacy framebuffer background, which is
/// suppressed natively via ConfigFlags.SuppressBackground.
///
/// Layers 0 (ground) and 1 (structures) stay pixel-locked to the sim tick and
/// sit a fraction of a millimeter behind the lane overlay: terrain-paint art
/// in the legacy frame (ground enemies covering their baked destroyed state)
/// must stay pixel-coplanar with the tiles beneath it, so those layers get no
/// scroll interpolation.  Layer 2 (clouds/top) carries no terrain paint and
/// floats at real diorama height with per-render-frame scroll interpolation.
///
/// Each layer is a single quad over the 264x184 play region; the fragment
/// shader resolves frame pixel -> map tile -> shape pixel -> palette, with a
/// seam-aware bilinear blend in post-palette RGB for anti-aliasing.
/// </summary>
public unsafe partial class BackgroundLayer : Node3D
{
    private const float LaneWidth = 1.0f, LaneHeight = 0.625f;
    private const int PlayW = 264, PlayH = 184;
    private const int AtlasCols = 8;  // 8x9 grid of 24x28 shapes

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
    private static float LayerHeight(int layer, byte overMode) => layer switch
    {
        0 => GroundZ,
        1 => overMode == 1 ? 0.020f : -0.0004f,
        _ => overMode == 2 ? 0.025f : PlatformZ,
    };

    private OtyrNative.BackgroundMap _map;  // fetch scratch (57 KB)
    private uint _mapEpoch;

    private readonly MeshInstance3D[] _quads = new MeshInstance3D[OtyrNative.BgLayerCount];
    private readonly ShaderMaterial[] _materials = new ShaderMaterial[OtyrNative.BgLayerCount];
    private readonly ImageTexture[] _tilemapTex = new ImageTexture[OtyrNative.BgLayerCount];
    private readonly ImageTexture[] _atlasTex = new ImageTexture[OtyrNative.BgLayerCount];
    private readonly Vector2I[] _mapSize = new Vector2I[OtyrNative.BgLayerCount];
    private readonly byte[][] _tilesCpu = new byte[OtyrNative.BgLayerCount][];
    private readonly byte[][] _atlasCpu = new byte[OtyrNative.BgLayerCount][];

    private readonly OtyrNative.BackgroundDraw[] _currDraw = new OtyrNative.BackgroundDraw[OtyrNative.BgLayerCount];
    private readonly OtyrNative.BackgroundDraw[] _prevDraw = new OtyrNative.BackgroundDraw[OtyrNative.BgLayerCount];

    // A scroll step larger than this between ticks is a map jump (wrap
    // event); snap instead of interpolating across it.
    private const float TeleportGuardPx = 56f;

    private readonly ImageTexture _palette;

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
                render_mode unshaded, cull_disabled, depth_prepass_alpha;

                uniform sampler2D tilemap : filter_nearest;
                uniform sampler2D atlas : filter_nearest;
                uniform sampler2D palette : source_color, filter_nearest;
                uniform ivec2 map_size;
                uniform vec2 origin_px;   // map tile (0,0) position, play-region px
                uniform float alpha_mul;  // 1.0, or 0.55 for the legacy blend variant

                // Palette RGB at one integer map pixel; a = coverage.
                vec4 sample_map(ivec2 mp) {
                    if (mp.x < 0 || mp.y < 0)
                        return vec4(0.0);
                    ivec2 tile = mp / ivec2(24, 28);
                    if (tile.x >= map_size.x || tile.y >= map_size.y)
                        return vec4(0.0);
                    int idx = int(texelFetch(tilemap, tile, 0).r * 255.0 + 0.5);
                    if (idx > 200)  // 0xff = empty cell
                        return vec4(0.0);
                    ivec2 ap = ivec2((idx % 8) * 24, (idx / 8) * 28) + (mp - tile * ivec2(24, 28));
                    float pi = floor(texelFetch(atlas, ap, 0).r * 255.0 + 0.5);
                    if (pi < 0.5)  // palette 0 = transparent
                        return vec4(0.0);
                    vec3 rgb = texture(palette, vec2((pi + 0.5) / 256.0, 0.5)).rgb;
                    return vec4(rgb, 1.0);
                }

                void fragment() {
                    vec2 frame_px = UV * vec2(264.0, 184.0);
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

        // The play region (frame px 0..264 x 0..184) in lane-local coordinates
        // (the lane maps the full 320x200 frame; play area publishes at -24).
        var quadMesh = new QuadMesh
        {
            Size = new Vector2(PlayW / 320f * LaneWidth, PlayH / 200f * LaneHeight),
        };
        float centerX = (PlayW / 2f / 320f - 0.5f) * LaneWidth;
        float centerY = (0.5f - PlayH / 2f / 200f) * LaneHeight;

        for (int l = 0; l < OtyrNative.BgLayerCount; l++)
        {
            // Default transparent sorting (node-origin distance): the
            // elevated layers draw LAST among transparents, which is what
            // gives the clouds their kept-on-purpose translucent look over
            // the scene.  Entities above them survive via their real depth;
            // decals riding a layer sit at a real geometric lift above it
            // (see SnapshotLayer) rather than fighting it in depth-bias
            // space -- a RenderPriority experiment here broke the cloud
            // look without fixing the riders (headset round 6).
            _materials[l] = new ShaderMaterial { Shader = shader };
            _materials[l].SetShaderParameter("palette", _palette);
            _materials[l].SetShaderParameter("alpha_mul", 1.0f);

            _quads[l] = new MeshInstance3D
            {
                Name = $"BgLayer{l}",
                Mesh = quadMesh,
                MaterialOverride = _materials[l],
                Position = new Vector3(centerX, centerY, LayerHeight(l, 0)),
                Visible = false,
            };
            AddChild(_quads[l]);
        }

        // Opaque backing behind everything: where the suppressed legacy frame
        // and the tile layers are all transparent, the board reads as black
        // (menus, erased HUD rects, astral events) instead of see-through.
        AddChild(new MeshInstance3D
        {
            Name = "Backing",
            Mesh = new QuadMesh { Size = new Vector2(LaneWidth, LaneHeight) },
            Position = new Vector3(0f, 0f, -0.0012f),
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
            FetchMaps(session);
        }

        for (int l = 0; l < OtyrNative.BgLayerCount; l++)
        {
            _prevDraw[l] = _currDraw[l];
            _currDraw[l] = snapshot.Background(l);
            _quads[l].Visible = _currDraw[l].Drawn != 0;
            _materials[l].SetShaderParameter("alpha_mul", _currDraw[l].Blend != 0 ? 0.55f : 1.0f);

            float z = LayerHeight(l, _currDraw[l].OverMode);
            Vector3 position = _quads[l].Position;
            if (position.Z != z)
                _quads[l].Position = new Vector3(position.X, position.Y, z);

            // Cloud-height layers (elevated but not the ridable platform
            // plane) draw LAST among transparents -- the kept translucent-
            // cloud look blends them over the scene, and entities above
            // them survive by real depth.  Explicit: the look used to ride
            // on unspecified distance-sort tie-breaking and flipped between
            // level runs.  Platform-height layers keep default order; their
            // riders sit at a real lift and win by depth either way.
            _materials[l].RenderPriority =
                z > 0.001f && Mathf.Abs(z - PlatformZ) > 0.0005f ? 5 : 0;

            // Coplanar layers are pixel-locked to the tick (terrain-paint
            // coplanarity); their origin updates here and only here.
            // Elevated layers carry no terrain paint and scroll-interpolate
            // in OnRender instead.
            if (z <= 0.001f && _currDraw[l].Drawn != 0)
                _materials[l].SetShaderParameter("origin_px", Origin(l, _currDraw[l]));
        }
    }

    // Sub-tick scroll offset per layer (interpolated origin minus this
    // tick's origin): quads riding an elevated layer add this so they move
    // with the smooth-scrolled tiles instead of stepping against them.
    private readonly Vector2[] _subTickPx = new Vector2[OtyrNative.BgLayerCount];

    /// <summary>Called every render frame with the tick interpolation phase;
    /// smooth-scrolls the elevated layers.</summary>
    public void OnRender(float t)
    {
        for (int l = 1; l < OtyrNative.BgLayerCount; l++)
        {
            _subTickPx[l] = Vector2.Zero;
            if (_currDraw[l].Drawn == 0 || _quads[l].Position.Z <= 0.001f)
                continue;

            Vector2 curr = Origin(l, _currDraw[l]);
            Vector2 origin = curr;
            if (_prevDraw[l].Drawn != 0)
            {
                Vector2 prev = Origin(l, _prevDraw[l]);
                if (prev.DistanceTo(curr) <= TeleportGuardPx)
                    origin = prev.Lerp(curr, t);
            }
            _materials[l].SetShaderParameter("origin_px", origin);
            _subTickPx[l] = origin - curr;
        }
    }

    /// <summary>Sub-tick scroll offset of the elevated layer sitting at the
    /// given lane height, or zero when no layer matches (the ground layer
    /// steps per tick and contributes none).</summary>
    public Vector2 SubTickOffsetAt(float z)
    {
        for (int l = 1; l < OtyrNative.BgLayerCount; l++)
        {
            if (_currDraw[l].Drawn == 0)
                continue;
            if (Mathf.Abs(LayerHeight(l, _currDraw[l].OverMode) - z) < 0.0005f)
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
    public float SurfaceZAt(Vector2 framePx, bool includeClouds = false)
    {
        int lowestLayer = includeClouds ? 1 : 2;
        for (int l = OtyrNative.BgLayerCount - 1; l >= lowestLayer; l--)
        {
            if (_currDraw[l].Drawn == 0 || _tilesCpu[l] == null)
                continue;
            float z = LayerHeight(l, _currDraw[l].OverMode);
            if (z <= 0.001f)
                continue;  // coplanar layers are not ridable surfaces

            Vector2 mp = framePx - Origin(l, _currDraw[l]);
            int tx = (int)Mathf.Floor(mp.X / OtyrNative.BgTileW);
            int ty = (int)Mathf.Floor(mp.Y / OtyrNative.BgTileH);
            if (tx < 0 || ty < 0 || tx >= _mapSize[l].X || ty >= _mapSize[l].Y)
                continue;
            byte shape = _tilesCpu[l][ty * _mapSize[l].X + tx];
            if (shape == OtyrNative.BgTileEmpty)
                continue;
            // Pixel-granular: a PLACED tile can still be transparent at this
            // pixel (sparse decoration art).  Tile-granular banding hoisted
            // ground statics under such tiles to platform height -- they
            // floated misaligned above their own baked terrain.
            if (_atlasCpu[l] == null)
                return z;
            int px = (int)mp.X - tx * OtyrNative.BgTileW;
            int py = (int)mp.Y - ty * OtyrNative.BgTileH;
            int ax = (shape % AtlasCols) * OtyrNative.BgTileW + px;
            int ay = (shape / AtlasCols) * OtyrNative.BgTileH + py;
            if (_atlasCpu[l][ay * AtlasCols * OtyrNative.BgTileW + ax] != 0)
                return z;
        }
        return 0f;
    }

    /// <summary>Play-region position of map tile (0,0) for a draw record: the
    /// record pins map cell (row0, col0) at frame (x, y); draw x is in
    /// pre-composite coordinates (play area publishes shifted -24).</summary>
    private Vector2 Origin(int layer, in OtyrNative.BackgroundDraw draw)
    {
        int width = Math.Max((int)_mapSize[layer].X, 1);
        int row0 = (int)Math.Floor(draw.TileOffset / (double)width);
        int col0 = draw.TileOffset - row0 * width;
        return new Vector2(draw.X - 24 - col0 * 24, draw.Y - row0 * 28);
    }

    private void FetchMaps(ulong session)
    {
        for (uint l = 0; l < OtyrNative.BgLayerCount; l++)
        {
            int rc;
            fixed (OtyrNative.BackgroundMap* mapPtr = &_map)
                rc = OtyrNative.GetBackgroundMap(session, l, mapPtr, _map.StructSize);
            if (rc != OtyrNative.Ok || _map.Width == 0)
                continue;

            _mapSize[l] = new Vector2I(_map.Width, _map.Height);
            _materials[l].SetShaderParameter("map_size", _mapSize[l]);

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
            var atlasImage = Image.CreateFromData(atlasW, atlasH, false, Image.Format.R8, atlas);
            _atlasTex[l] = ImageTexture.CreateFromImage(atlasImage);
            _materials[l].SetShaderParameter("atlas", _atlasTex[l]);
        }
        GD.Print($"OpenTyrianVR: background maps refreshed (epoch {_mapEpoch})");
    }
}
