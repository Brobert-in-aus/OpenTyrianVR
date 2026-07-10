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
    private static readonly float[] BandHeight =
    {
        0.055f,  // EnemySky (mid band)
        0.0008f, // EnemyGroundA: pixel-coplanar with the lane -- ground art is
                 // baked over exact tile pixels (incl. covering destroyed-state
                 // art), so any visible parallax breaks the illusion
        0.085f,  // EnemyTop (high band)
        0.0008f, // EnemyGroundB
        0.050f,  // EnemyShot
        0.050f,  // PlayerShot
        0.040f,  // Player
        0.002f,  // Shadow (not rendered in 3D; see AddCell)
        0.040f,  // Sidekick
        0.050f,  // Explosion
        0.050f,  // Superpixel
    };

    // Draw-order bias within a tick: later records sit imperceptibly higher,
    // reproducing legacy layering without z-fighting.
    private const float OrderBias = 0.00001f;

    private OtyrNative.Snapshot _snapshot;
    private OtyrNative.SpriteSheet _sheet;
    private uint _sheetEpoch;
    private uint _lastRenderedTick;
    private ulong _snapshotArrivalUsec;
    private double _snapshotPeriod = 0.02875;  // nominal 35 Hz tick

    // Layers 0..SheetCount-1 are sprite sheets; the last is the glow layer
    // (superpixel debris as small palette-colored quads).
    private const int GlowLayer = OtyrNative.SheetCount;
    private const int LayerCount = OtyrNative.SheetCount + 1;

    private readonly MultiMesh[] _multiMesh = new MultiMesh[LayerCount];
    private readonly ImageTexture[] _atlas = new ImageTexture[OtyrNative.SheetCount];
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
                    float idx = floor(texture(atlas, uv).r * 255.0 + 0.5);
                    if (idx < 0.5)
                        discard;
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
                false, Image.Format.R8);
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
        var pixels = new byte[atlasW * atlasH];

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

                    for (int y = 0; y < OtyrNative.SheetCellH; y++)
                        for (int x = 0; x < OtyrNative.SheetCellW; x++)
                            pixels[(originY + y) * atlasW + originX + x] = src[y * OtyrNative.SheetCellW + x];
                }
            }

            var image = Image.CreateFromData(atlasW, atlasH, false, Image.Format.R8, pixels);
            _atlas[id].Update(image);

            if (DumpAtlases)
                image.SavePng($"user://atlas_{id}_epoch{_sheetEpoch}.png");
        }
        GD.Print($"OpenTyrianVR: sprite atlases refreshed (epoch {_sheetEpoch})");
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
    // Pairing: (source_id, per-source ordinal) of each cell this tick.
    private uint[] _cellKeys = new uint[OtyrNative.SnapshotSpriteMax * 4];
    private uint[] _prevCellKeys = new uint[OtyrNative.SnapshotSpriteMax * 4];
    private readonly System.Collections.Generic.Dictionary<uint, int> _prevByKey = new();
    private readonly System.Collections.Generic.Dictionary<ushort, int> _sourceOrdinal = new();

    private const float TeleportGuardPx = 32f;

    private void BuildSprites()
    {
        // Rotate current -> previous.
        (_prevCells, _cells) = (_cells, _prevCells);
        (_prevCellKeys, _cellKeys) = (_cellKeys, _prevCellKeys);
        _prevCellCount = _cellCount;
        _cellCount = 0;

        _prevByKey.Clear();
        for (int i = 0; i < _prevCellCount; i++)
            _prevByKey.TryAdd(_prevCellKeys[i], i);
        _sourceOrdinal.Clear();

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

                if (sprite.SheetId >= OtyrNative.SheetCount || sprite.Index == 0)
                    continue;  // old-table blend blits: not yet rendered
                if (sprite.Category == (byte)OtyrNative.Category.Shadow)
                    continue;  // stays in the legacy frame (terrain paint)
                if (sprite.Aux != 0 && sprite.Category <= (byte)OtyrNative.Category.EnemyGroundB)
                    continue;  // ground-baked art stays in the legacy frame

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

        if (sprite.SourceId != OtyrNative.NoSource)
        {
            uint key = (uint)sprite.SourceId << 8;
            _cellKeys[_cellCount] = key;
            if (_prevByKey.TryGetValue(key, out int prevIdx) &&
                _prevCells[prevIdx].CurrPx.DistanceTo(cell.CurrPx) <= TeleportGuardPx)
            {
                cell.PrevPx = _prevCells[prevIdx].CurrPx;
                cell.HasPrev = true;
            }
        }
        else
        {
            _cellKeys[_cellCount] = 0xffffffff;
        }

        ++_cellCount;
    }

    private void AddCell(in OtyrNative.SnapshotSprite sprite, uint recordIndex, int cellIndex, int pixelOffsetX, int pixelOffsetY)
    {
        if (_cellCount >= _cells.Length)
            return;

        float centerX = sprite.X + pixelOffsetX + OtyrNative.SheetCellW / 2f;
        float centerY = sprite.Y + pixelOffsetY + OtyrNative.SheetCellH / 2f;

        ref RenderCell cell = ref _cells[_cellCount];
        cell.SheetId = sprite.SheetId;
        cell.CellIndex = cellIndex - 1;
        cell.Flags = sprite.Flags;
        cell.FilterColor = sprite.FilterColor;
        cell.Z = BandHeight[Math.Min(sprite.Category, (byte)(BandHeight.Length - 1))]
               + recordIndex * OrderBias;
        cell.CurrPx = new Vector2(centerX, centerY);
        cell.PrevPx = cell.CurrPx;
        cell.HasPrev = false;

        // Pair with last tick: same source id, k-th occurrence (emit order is
        // deterministic per source).
        if (sprite.SourceId != OtyrNative.NoSource)
        {
            _sourceOrdinal.TryGetValue(sprite.SourceId, out int ordinal);
            _sourceOrdinal[sprite.SourceId] = ordinal + 1;
            uint key = (uint)sprite.SourceId << 8 | (uint)(ordinal & 0xff);
            _cellKeys[_cellCount] = key;

            if (_prevByKey.TryGetValue(key, out int prevIdx))
            {
                Vector2 prev = _prevCells[prevIdx].CurrPx;
                if (prev.DistanceTo(cell.CurrPx) <= TeleportGuardPx)
                {
                    cell.PrevPx = prev;
                    cell.HasPrev = true;
                }
            }
        }
        else
        {
            _cellKeys[_cellCount] = 0xffffffff;
        }

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

            // Frame pixels (game_screen, composited -24) -> lane local.
            float laneX = ((px.X - 24f) / 320f - 0.5f) * LaneWidth;
            float laneY = (0.5f - px.Y / 200f) * LaneHeight;

            int id = cell.SheetId;
            int instance = _instanceCount[id]++;
            if (instance >= OtyrNative.SnapshotSpriteMax)
            {
                _instanceCount[id] = OtyrNative.SnapshotSpriteMax;
                continue;
            }

            _multiMesh[id].SetInstanceTransform(instance,
                new Transform3D(Basis.Identity, new Vector3(laneX, laneY, cell.Z)));
            _multiMesh[id].SetInstanceCustomData(instance,
                new Color(cell.CellIndex, cell.Flags, cell.FilterColor, 0));
        }

        for (int id = 0; id < LayerCount; id++)
            _multiMesh[id].VisibleInstanceCount = _instanceCount[id];
    }
}
