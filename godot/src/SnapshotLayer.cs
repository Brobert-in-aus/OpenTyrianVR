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

    // Lane-local Z (out of the board) per category — the diorama height bands.
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
        0.050f,  // EnemyShot
        0.050f,  // PlayerShot
        0.040f,  // Player
        0.0008f, // Shadow (fallback; normally surface-following, see AddCell)
        0.040f,  // Sidekick
        0.050f,  // Explosion
        0.050f,  // Superpixel
    };

    // Baked structures (aux 1) and shadows share one coplanar band a hair
    // above the terrain, layered by record order (OrderBias) exactly like
    // the legacy paint order: a structure's own shadow is recorded just
    // before it and lands just beneath it (hidden, as in legacy), while
    // the player/shot shadows recorded late in the tick land above
    // structure art and visibly cross it.
    private const float StructureZ = 0.0008f;
    // Platform riders (aux 2): just above the platform map layer.
    private const float RiderZ = BackgroundLayer.PlatformZ + 0.004f;

    // Draw-order bias within a tick: later records sit imperceptibly higher,
    // reproducing legacy layering without z-fighting.
    private const float OrderBias = 0.00001f;

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
    private const int LayerCount = ShadowLayerBase + OtyrNative.SheetCount;
    private const int OldAtlasSlotsPerRow = 16;  // 16x16 grid of 64x64 slots

    private readonly MultiMesh[] _multiMesh = new MultiMesh[LayerCount];
    private readonly ImageTexture[] _atlas = new ImageTexture[OtyrNative.SheetCount];
    private ImageTexture _oldAtlas = null!;
    private OtyrNative.OldSprite _oldSprite;  // fetch scratch
    private readonly Vector2I[] _oldSize = new Vector2I[OtyrNative.OldSpriteMax];
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
                render_mode unshaded, cull_disabled, depth_prepass_alpha;

                uniform sampler2D atlas : filter_nearest;
                uniform sampler2D palette : filter_nearest;

                varying float cell;
                varying float v_flags;
                varying float v_filter;

                void vertex() {
                    cell = INSTANCE_CUSTOM.x;
                    v_flags = INSTANCE_CUSTOM.y;
                    v_filter = INSTANCE_CUSTOM.z;
                }

                void fragment() {
                    // Half-texel inset keeps edge fragments inside this cell
                    // (no atlas bleeding from neighboring cells).
                    vec2 cell_origin_px = vec2(mod(cell, 32.0) * 12.0, floor(cell / 32.0) * 14.0);
                    vec2 cell_px = clamp(UV * vec2(12.0, 14.0), vec2(0.5), vec2(11.5, 13.5));
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

            var material = new ShaderMaterial { Shader = shader };
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

                uniform sampler2D palette : filter_nearest;

                varying float pal_index;

                void vertex() {
                    pal_index = INSTANCE_CUSTOM.x;
                }

                void fragment() {
                    ALBEDO = texture(palette, vec2((pal_index + 0.5) / 256.0, 0.5)).rgb;
                }
                """,
        };
        var glowMaterial = new ShaderMaterial { Shader = glowShader };
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
                uniform sampler2D palette : filter_nearest;

                varying vec3 slot_wh;

                void vertex() {
                    slot_wh = INSTANCE_CUSTOM.xyz;
                }

                void fragment() {
                    vec2 wh = slot_wh.yz;
                    vec2 origin_px = vec2(mod(slot_wh.x, 16.0), floor(slot_wh.x / 16.0)) * 64.0;
                    vec2 px = clamp(UV * wh, vec2(0.5), wh - 0.5);
                    vec2 s = texture(atlas, (origin_px + px) / 1024.0).rg;
                    if (s.g < 0.5)
                        discard;
                    float idx = floor(s.r * 255.0 + 0.5);
                    ALBEDO = texture(palette, vec2((idx + 0.5) / 256.0, 0.5)).rgb;
                    ALPHA = 0.55;
                }
                """,
        };
        var oldMaterial = new ShaderMaterial { Shader = oldShader };
        _oldAtlas = ImageTexture.CreateFromImage(Image.CreateEmpty(
            OldAtlasSlotsPerRow * OtyrNative.OldSpriteWMax,
            OldAtlasSlotsPerRow * OtyrNative.OldSpriteHMax, false, Image.Format.Rg8));
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
                render_mode unshaded, cull_disabled, blend_mul, depth_draw_never;

                uniform sampler2D atlas : filter_nearest;

                varying float cell;

                void vertex() {
                    cell = INSTANCE_CUSTOM.x;
                }

                void fragment() {
                    vec2 cell_origin_px = vec2(mod(cell, 32.0) * 12.0, floor(cell / 32.0) * 14.0);
                    vec2 cell_px = clamp(UV * vec2(12.0, 14.0), vec2(0.5), vec2(11.5, 13.5));
                    if (texture(atlas, (cell_origin_px + cell_px) / vec2(384.0, 448.0)).g < 0.5)
                        discard;
                    ALBEDO = vec3(0.5);  // halve brightness, keep hue
                }
                """,
        };
        for (int id = 0; id < OtyrNative.SheetCount; id++)
        {
            var shadowMaterial = new ShaderMaterial { Shader = shadowShader };
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
        int atlasH = OldAtlasSlotsPerRow * OtyrNative.OldSpriteHMax;
        var pixels = new byte[atlasW * atlasH * 2];  // rg: index, opacity
        _oldSprite.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.OldSprite>();

        for (uint i = 0; i < OtyrNative.OldSpriteMax; i++)
        {
            int rc;
            fixed (OtyrNative.OldSprite* ptr = &_oldSprite)
                rc = OtyrNative.GetOldSprite(session, OtyrNative.OldTableOption, i, ptr, _oldSprite.StructSize);
            _oldSize[i] = rc == OtyrNative.Ok
                ? new Vector2I(_oldSprite.Width, _oldSprite.Height)
                : Vector2I.Zero;
            if (_oldSize[i] == Vector2I.Zero)
                continue;

            int originX = ((int)i % OldAtlasSlotsPerRow) * OtyrNative.OldSpriteWMax;
            int originY = ((int)i / OldAtlasSlotsPerRow) * OtyrNative.OldSpriteHMax;
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

        _oldAtlas.Update(Image.CreateFromData(atlasW, atlasH, false, Image.Format.Rg8, pixels));
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
        public Vector2 CurrPx;     // cell center, frame pixels
        public Vector2 PrevPx;     // previous-tick center (== CurrPx if new)
        public bool HasPrev;
    }

    private RenderCell[] _cells = new RenderCell[OtyrNative.SnapshotSpriteMax * 4];
    private int _cellCount;
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

                if (sprite.SheetId >= OtyrNative.SheetCount || sprite.Index == 0)
                    continue;
                // Shadows and baked structures render in 3D too (nothing
                // dynamic stays in the frame): shadows as translucent dark
                // quads, structures as map-locked coplanar cells.

                if (sprite.Kind == 1)  // SPRITE2X2: four cells (i, i+1, i+19, i+20)
                {
                    AddCell(sprite, i, sprite.Index,      0, 0);
                    AddCell(sprite, i, sprite.Index + 1,  OtyrNative.SheetCellW, 0);
                    AddCell(sprite, i, sprite.Index + 19, 0, OtyrNative.SheetCellH);
                    AddCell(sprite, i, sprite.Index + 20, OtyrNative.SheetCellW, OtyrNative.SheetCellH);
                }
                else
                {
                    AddCell(sprite, i, sprite.Index, 0, 0);
                }
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
        cell.Flags = 0;
        cell.FilterColor = 0;
        cell.Z = BandHeight[(byte)OtyrNative.Category.Superpixel] + recordIndex * OrderBias;
        cell.CurrPx = new Vector2(sprite.X, sprite.Y);
        cell.PrevPx = cell.CurrPx;
        cell.HasPrev = false;

        PairWithPrevious(ref cell, sprite.SourceId);
        ++_cellCount;
    }

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

    private void AddCell(in OtyrNative.SnapshotSprite sprite, uint recordIndex, int cellIndex, int pixelOffsetX, int pixelOffsetY)
    {
        if (_cellCount >= _cells.Length)
            return;

        float centerX = sprite.X + pixelOffsetX + OtyrNative.SheetCellW / 2f;
        float centerY = sprite.Y + pixelOffsetY + OtyrNative.SheetCellH / 2f;

        ref RenderCell cell = ref _cells[_cellCount];
        // Darken blits (shadows, iced) go to the multiplicative shadow
        // layer of their sheet instead of the color layer.
        bool darken = (sprite.Flags & 4) != 0;
        cell.SheetId = darken ? ShadowLayerBase + sprite.SheetId : sprite.SheetId;
        cell.CellIndex = cellIndex - 1;
        cell.Flags = sprite.Flags;
        cell.FilterColor = sprite.FilterColor;
        bool isEnemy = sprite.Category <= (byte)OtyrNative.Category.EnemyGroundB;
        bool isShadow = sprite.Category == (byte)OtyrNative.Category.Shadow;
        float band;
        if (isEnemy && sprite.Aux == 1)
        {
            // Stationary structures sit on whatever surface is beneath
            // them: the platform map layer or the ground itself.  The
            // offset is sub-pixel against head parallax (a 4 mm gap made
            // platform structures visibly wobble).
            float surface = SurfaceForSource(sprite.SourceId, centerX, centerY);
            band = surface + StructureZ;
        }
        else if (isEnemy && sprite.Aux == 2)
        {
            float surface = SurfaceForSource(sprite.SourceId, centerX, centerY);
            band = Math.Max(surface, BackgroundLayer.PlatformZ) + StructureZ;
        }
        else if (isShadow)
        {
            // Shadows fall on the topmost scenery under them -- including
            // the clouds (legacy draws player/shot shadows after the cloud
            // layer, so they land on it) -- in the SAME band as structure
            // art: record order decides who paints over whom.
            float surface = _background?.SurfaceZAt(new Vector2(centerX - 24f, centerY), includeClouds: true) ?? 0f;
            band = surface + StructureZ;
        }
        else
        {
            band = BandHeight[Math.Min(sprite.Category, (byte)(BandHeight.Length - 1))];
        }
        cell.Z = band + recordIndex * OrderBias;
        cell.CurrPx = new Vector2(centerX, centerY);
        cell.PrevPx = cell.CurrPx;
        cell.HasPrev = false;

        // Baked structures are locked to the map tiles beneath them; the
        // tile layers step per tick, so interpolating the art over them
        // would swim against its own baked underlay.
        if (isEnemy && sprite.Aux == 1)
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

            int id = cell.SheetId;

            // Cull cells fully outside the visible play region (frame copies
            // draw columns 24..288, rows 0..184): legacy clips these at the
            // margins, so they must not float past the lane edges.
            float halfW = id == GlowLayer ? 1f : id == OldLayer ? cell.Flags / 2f : OtyrNative.SheetCellW / 2f;
            float halfH = id == GlowLayer ? 1f : id == OldLayer ? cell.FilterColor / 2f : OtyrNative.SheetCellH / 2f;
            float frameX = px.X - 24f;
            if (frameX + halfW <= 0f || frameX - halfW >= 264f ||
                px.Y + halfH <= 0f || px.Y - halfH >= 184f)
                continue;

            // Frame pixels (game_screen, composited -24) -> lane local.
            float laneX = (frameX / 320f - 0.5f) * LaneWidth;
            float laneY = (0.5f - px.Y / 200f) * LaneHeight;
            int instance = _instanceCount[id]++;
            if (instance >= _multiMesh[id].InstanceCount)
            {
                _instanceCount[id] = (int)_multiMesh[id].InstanceCount;
                continue;
            }

            // The old-table layer uses a unit-pixel quad scaled to the
            // sprite's size (stored in Flags/FilterColor).
            Basis basis = id == OldLayer
                ? Basis.Identity.Scaled(new Vector3(cell.Flags, cell.FilterColor, 1f))
                : Basis.Identity;

            _multiMesh[id].SetInstanceTransform(instance,
                new Transform3D(basis, new Vector3(laneX, laneY, cell.Z)));
            _multiMesh[id].SetInstanceCustomData(instance,
                new Color(cell.CellIndex, cell.Flags, cell.FilterColor, 0));
        }

        for (int id = 0; id < LayerCount; id++)
            _multiMesh[id].VisibleInstanceCount = _instanceCount[id];
    }
}
