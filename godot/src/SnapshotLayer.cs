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
    private const bool DumpAtlases = false;  // debug: writes user://atlas_N.png on fetch

    private const float LaneWidth = 1.0f, LaneHeight = 0.625f;
    private const float PxToMeters = LaneWidth / 320f;
    private const int AtlasCellsPerRow = 32;  // 32x32 grid of 12x14 cells

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

    // Height-editor sessions render flat/single-view, where the in-shader
    // decal depth bias is reliable and the VR geometric lift only adds
    // oblique-view parallax against the baked art.
    private static readonly bool FlatEditorMode =
        System.Environment.GetEnvironmentVariable("OTYR_HEIGHT_EDITOR") == "1";

    private OtyrNative.Snapshot _snapshot;
    private OtyrNative.SpriteSheet _sheet;
    private uint _sheetEpoch;
    private uint _lastRenderedTick;
    private ulong _snapshotArrivalUsec;
    private double _snapshotPeriod = 0.02875;  // nominal 35 Hz tick

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

    /// <summary>Set before adding to the tree: render the background map
    /// layers in 3D (pair with ConfigFlags.SuppressBackground).</summary>
    public bool EnableBackground;
    private BackgroundLayer? _background;

    public override void _Ready()
    {
        _snapshot.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.Snapshot>();
        _sheet.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.SpriteSheet>();
        LoadHoverHeights();

        _paletteImage = Image.CreateEmpty(256, 1, false, Image.Format.Rgba8);
        _paletteTexture = ImageTexture.CreateFromImage(_paletteImage);

        if (EnableBackground)
        {
            _background = new BackgroundLayer(_paletteTexture) { Name = "BackgroundLayer" };
            AddChild(_background);
        }

        var shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, depth_draw_always;

                uniform sampler2D atlas : filter_nearest;
                uniform sampler2D palette : source_color, filter_nearest;

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

                void vertex() {
                    cell = INSTANCE_CUSTOM.x;
                    v_flags = INSTANCE_CUSTOM.y;
                    v_filter = INSTANCE_CUSTOM.z;
                    v_decal = INSTANCE_CUSTOM.w;
                }

                void fragment() {
                    // Terrain decals sit at EXACTLY the tile plane (zero
                    // head parallax, so transparent art pixels composite
                    // the baked tiles beneath); a depth-only bias encodes
                    // the paint order against the tiles and each other.
                    DEPTH = FRAGCOORD.z + (v_decal > 0.0 ? 0.00001 + v_decal * 0.00002 : 0.0);
                    // Rounded decode (see the text layer): custom data can
                    // arrive a hair under the integer and wrap the atlas
                    // origin at column boundaries.
                    float cid = floor(cell + 0.5);
                    vec2 uv0 = UV;
                    // 2x2 sprites (flag bit 8): one quad; pick the legacy
                    // cell (+0/+1/+19/+20) by UV quadrant.
                    bool dbg_big = mod(floor(v_flags / 8.0), 2.0) >= 1.0;
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

                varying flat float pal_index;  // flat: see the sprite shader

                void vertex() {
                    pal_index = INSTANCE_CUSTOM.x;
                }

                void fragment() {
                    ALBEDO = texture(palette, vec2((pal_index + 0.5) / 256.0, 0.5)).rgb;
                }
                """,
        };
        var glowMaterial = new ShaderMaterial { Shader = glowShader, RenderPriority = 2 };
        glowMaterial.SetShaderParameter("palette", _paletteTexture);

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

                varying flat vec3 slot_wh;  // flat: see the sprite shader

                void vertex() {
                    slot_wh = INSTANCE_CUSTOM.xyz;
                }

                void fragment() {
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

                // FLAT: see the sprite shader.
                varying flat float cell;
                varying flat float v_flags;
                varying flat float v_decal;

                void vertex() {
                    cell = INSTANCE_CUSTOM.x;
                    v_flags = INSTANCE_CUSTOM.y;
                    v_decal = INSTANCE_CUSTOM.w;
                }

                void fragment() {
                    // No DEPTH write (see render_mode note); paint order vs
                    // the statics on the same plane is real geometry now
                    // (the decal lift folds decalOrder into z).
                    float cid = floor(cell + 0.5);
                    vec2 uv0 = UV;
                    if (mod(floor(v_flags / 8.0), 2.0) >= 1.0) {  // 2x2 quad
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
                    ALBEDO = vec3(0.5);  // halve brightness, keep hue
                }
                """,
        };
        for (int id = 0; id < OtyrNative.SheetCount; id++)
        {
            var shadowMaterial = new ShaderMaterial { Shader = shadowShader, RenderPriority = 1 };
            shadowMaterial.SetShaderParameter("atlas", _atlas[id]);

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

                varying flat vec4 v_data;  // slot, w + h*65, mode + hue*4, value byte

                void vertex() {
                    v_data = INSTANCE_CUSTOM;
                }

                void fragment() {
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

                varying flat vec4 v_data;  // flat: see the sprite shader

                void vertex() {
                    v_data = INSTANCE_CUSTOM;
                }

                void fragment() {
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
        fixed (OtyrNative.Snapshot* snapshotPtr = &_snapshot)
            rc = OtyrNative.GetSnapshot(session, snapshotPtr, _snapshot.StructSize, 0);
        if (rc == OtyrNative.Ok && _snapshot.LevelTick != _lastRenderedTick)
        {
            _lastRenderedTick = _snapshot.LevelTick;

            if (_snapshot.SheetEpoch != _sheetEpoch)
            {
                _sheetEpoch = _snapshot.SheetEpoch;
                FetchAtlases(session);
            }

            UpdatePalette(paletteArgb);
            // A real tick re-bands ground-class types per instance; drop
            // their paused-preview overrides so banding wins.
            foreach (ushort t in _editorGroundTemp)
                _editorHeights.Remove(t);
            _editorGroundTemp.Clear();
            BuildSprites();
            _background?.OnSnapshot(session, in _snapshot);

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
    private readonly System.Collections.Generic.Dictionary<ushort, (int Start, int Count)> _prevRuns = new();

    private const float PairRadiusPx = 16f;

    private void BuildSprites()
    {
        // Rotate current -> previous.
        (_prevCells, _cells) = (_cells, _prevCells);
        (_prevCellSource, _cellSource) = (_cellSource, _prevCellSource);
        _prevCellCount = _cellCount;
        _cellCount = 0;
        _textOrder = 0;

        // Same-source cells are emitted contiguously; index the runs.
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

        fixed (OtyrNative.Snapshot* snapshot = &_snapshot)
        {
            var sprites = (OtyrNative.SnapshotSprite*)snapshot->SpritesRaw;

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
    private readonly System.Collections.Generic.Dictionary<string, float> _classHeights = new();
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
        var classes = root["classes"].AsGodotDictionary();
        foreach (var key in classes.Keys)
            _classHeights[key.AsString()] = (float)classes[key].AsDouble();
        _groundClassOffset = classes.ContainsKey("ground") ? (float)classes["ground"].AsDouble() : -1f;
        var types = root["types"].AsGodotDictionary();
        foreach (var key in types.Keys)
        {
            if (!ushort.TryParse(key.AsString(), out ushort type))
                continue;
            var entry = types[key].AsGodotDictionary();
            if (entry.ContainsKey("height"))
                _typeHeights[type] = (float)entry["height"].AsDouble();
            else if (entry.ContainsKey("class"))
            {
                string cls = entry["class"].AsString();
                if (cls == "ground")
                    _typeHeights[type] = float.NegativeInfinity;  // marker: surface + offset
                else if (classes.ContainsKey(cls))
                    _typeHeights[type] = (float)classes[cls].AsDouble();
            }
        }
        GD.Print($"OpenTyrianVR: hover heights loaded ({_typeHeights.Count} types)");
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

    // Hazard (collider) markers: red halos under every record whose contact
    // damages the player (flag bit 64, mirroring JE_playerCollide).
    private readonly System.Collections.Generic.List<MeshInstance3D> _hazardMarkers = new();
    private StandardMaterial3D? _hazardMaterial;
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
            for (int i = 0; i < _cellCount && used < 64; i++)
            {
                ref readonly RenderCell cell = ref _cells[i];
                if (cell.EntityType == 0 || (cell.Flags & 64) == 0)
                    continue;
                if (used == _hazardMarkers.Count)
                {
                    var marker = new MeshInstance3D
                    {
                        Mesh = new QuadMesh { Size = new Vector2(16f / 320f * LaneWidth, 18f / 200f * LaneHeight) },
                        MaterialOverride = _hazardMaterial,
                    };
                    AddChild(marker);
                    _hazardMarkers.Add(marker);
                }
                var m = _hazardMarkers[used];
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
        if (!FileAccess.FileExists(path) || _editorPending.Count == 0)
            return 0;
        var root = Json.ParseString(FileAccess.GetFileAsString(path)).AsGodotDictionary();
        var types = root["types"].AsGodotDictionary();
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
            types[type.ToString()] = entry;
        }
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        f.StoreString(Json.Stringify(root, "  "));
        int saved = _editorPending.Count;
        _editorPending.Clear();
        return saved;
    }

    /// <summary>Editor: the pending (unsaved) edit for a type, if any.</summary>
    public string? EditorPendingOf(ushort entityType) =>
        _editorPending.TryGetValue(entityType, out string? v) ? v : null;

    // One surface decision per entity per tick: querying per cell split
    // multi-cell structures across heights when they straddled a platform
    // edge (4 cells floating, 2 on the ground).
    private readonly System.Collections.Generic.Dictionary<ushort, float> _surfaceBySource = new();

    private float SurfaceForSource(ushort sourceId, float centerX, float centerY)
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
    private void PairWithPrevious(ref RenderCell cell, ushort sourceId)
    {
        _cellSource[_cellCount] = sourceId;
        if (sourceId == OtyrNative.NoSource || !_prevRuns.TryGetValue(sourceId, out var run))
            return;

        float bestDist = PairRadiusPx;
        int bestIdx = -1;
        for (int i = run.Start; i < run.Start + run.Count; i++)
        {
            if (_prevCells[i].SheetId != cell.SheetId)
                continue;
            float dist = _prevCells[i].CurrPx.DistanceTo(cell.CurrPx);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }
        if (bestIdx >= 0)
        {
            cell.PrevPx = _prevCells[bestIdx].CurrPx;
            cell.HasPrev = true;
        }
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
        bool isEnemy = sprite.Category <= (byte)OtyrNative.Category.EnemyGroundB;
        bool isShadow = sprite.Category == (byte)OtyrNative.Category.Shadow;
        cell.EntityType = isEnemy ? sprite.EntityType : (ushort)0;
        float band;
        float decalOrder = 0f;
        float authored = 0f;
        bool hasAuthored = isEnemy && sprite.EntityType != 0 &&
            _typeHeights.TryGetValue(sprite.EntityType, out authored);
        if (hasAuthored && !float.IsNegativeInfinity(authored))
        {
            // Authored hover height (Stage B): an explicit table/class
            // height wins EVEN over decal banding -- the under-platform
            // spikes are aux-1 rider records that must sit BELOW their
            // platform, which surface banding can never produce.  (It also
            // makes editor nudges apply to statics, which are the objects
            // most worth tuning.)
            band = authored;
        }
        else if (isEnemy && (sprite.Aux == 1 || sprite.Aux == 2))
        {
            // Every static/rider decals the surface actually beneath it:
            // platform art if a platform covers its center, ground
            // otherwise.  Banding non-TOP categories to the ground put
            // first-spawn/static enemies UNDER the elevated platform layer
            // whenever they crossed one -- the platform drew over them
            // ("transparent over platforms, solid over true ground").
            // A "ground"-class entry also lands here: surface-riding IS the
            // ground class for statics.
            float below = SurfaceForSource(sprite.SourceId, centerX, centerY);
            band = sprite.Category == (byte)OtyrNative.Category.EnemyTop
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
            float below = SurfaceForSource(sprite.SourceId, centerX, centerY);
            band = (below > 0f ? below : 0f) + Math.Max(_groundClassOffset, 0.002f);
        }
        else
        {
            band = BandHeight[Math.Min(sprite.Category, (byte)(BandHeight.Length - 1))];
        }
        cell.Z = band + (decalOrder > 0f ? 0f : recordIndex * OrderBias);
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
        if (isExplosion || (isEnemy && (sprite.Aux == 1 || sprite.Aux == 2)))
        {
            _cellSource[_cellCount] = OtyrNative.NoSource;
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

        Array.Clear(_instanceCount);

        for (int i = 0; i < _cellCount; i++)
        {
            ref readonly RenderCell cell = ref _cells[i];

            Vector2 px = cell.HasPrev ? cell.PrevPx.Lerp(cell.CurrPx, t) : cell.CurrPx;

            // Decals riding an ELEVATED layer (platform statics, shadows on
            // clouds) follow that layer's sub-tick smooth scroll: they step
            // per tick like the ground decals, but their underlay glides
            // between ticks, so without this they swim/blur against their
            // own platform under head motion.  Ground decals contribute
            // zero (the ground layer steps too).
            if (cell.DecalOrder > 0f && cell.Z > 0.001f && _background != null)
                px += _background.SubTickOffsetAt(cell.Z);
            // Authored-height STATICS (e.g. platform-under spikes) left the
            // decal path but still step per tick over a smooth-scrolling
            // elevated layer: glue them to the nearest one.
            else if (!cell.HasPrev && cell.EntityType != 0 && cell.Z > 0.01f && _background != null)
                px += _background.SubTickOffsetAt(cell.Z, 0.006f);

            int id = cell.SheetId;

            // Pixel-scaled quads: the old-table and text layers store the
            // sprite size in Flags/FilterColor.
            bool pixelQuad = id == OldLayer || id == TextLayer || id == TextShadowLayer;
            // Sheet-layer 2x2 sprites render as one 24x28 quad (flag bit 8).
            bool big = !pixelQuad && id != GlowLayer && (cell.Flags & 8) != 0;

            // Cull cells fully outside the visible play region (frame copies
            // draw columns 24..288, rows 0..184): legacy clips these at the
            // margins, so they must not float past the lane edges.
            float halfW = id == GlowLayer ? 1f : pixelQuad ? cell.Flags / 2f : big ? OtyrNative.SheetCellW : OtyrNative.SheetCellW / 2f;
            float halfH = id == GlowLayer ? 1f : pixelQuad ? cell.FilterColor / 2f : big ? OtyrNative.SheetCellH : OtyrNative.SheetCellH / 2f;
            float frameX = px.X - 24f;
            if (frameX + halfW <= 0f || frameX - halfW >= 264f ||
                px.Y + halfH <= 0f || px.Y - halfH >= 184f)
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
            // override keeps per-record draw order (i * OrderBias -- a flat
            // override z-fought overlapping instances of the SAME type) and
            // suppresses the decal lift (an overridden platform-under decal
            // otherwise gained the lift back and hid at the platform plane
            // until unpause re-banded it).
            float z = cell.Z;
            float editH = 0f;
            bool overridden = cell.EntityType != 0 &&
                _editorHeights.TryGetValue(cell.EntityType, out editH);
            if (overridden)
                // Inverted bias (user-verified on the segmented object 30):
                // its later-entered sections reuse EARLIER slots, so
                // ascending record order put first-entered on top; descending
                // makes the last section to enter draw over the rest.
                z = editH + (_cellCount - i) * OrderBias;
            // The lift exists for VR multiview (per-eye depth-precision
            // ghosting); viewed obliquely it parallaxes decals up to ~1 px
            // off their baked underlay.  The editor is flat single-view,
            // where the in-shader depth bias alone is reliable -- skip the
            // lift there so alignment reads pixel-true at any orbit angle.
            if (cell.DecalOrder > 0f && !overridden && !FlatEditorMode)
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
                    : new Color(cell.CellIndex, cell.Flags, cell.FilterColor, cell.DecalOrder));
        }

        for (int id = 0; id < LayerCount; id++)
            _multiMesh[id].VisibleInstanceCount = _instanceCount[id];
    }
}
