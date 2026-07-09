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

    private readonly MultiMesh[] _multiMesh = new MultiMesh[OtyrNative.SheetCount];
    private readonly ImageTexture[] _atlas = new ImageTexture[OtyrNative.SheetCount];
    private ImageTexture _paletteTexture = null!;
    private Image _paletteImage = null!;
    private readonly byte[] _paletteRgba = new byte[256 * 4];
    private readonly int[] _instanceCount = new int[OtyrNative.SheetCount];

    public override void _Ready()
    {
        _snapshot.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.Snapshot>();
        _sheet.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OtyrNative.SpriteSheet>();

        _paletteImage = Image.CreateEmpty(256, 1, false, Image.Format.Rgba8);
        _paletteTexture = ImageTexture.CreateFromImage(_paletteImage);

        var shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, depth_prepass_alpha;

                uniform sampler2D atlas : filter_nearest;
                uniform sampler2D palette : filter_nearest;

                varying float cell;

                void vertex() {
                    cell = INSTANCE_CUSTOM.x;
                }

                void fragment() {
                    // Half-texel inset keeps edge fragments inside this cell
                    // (no atlas bleeding from neighboring cells).
                    vec2 cell_origin_px = vec2(mod(cell, 32.0) * 12.0, floor(cell / 32.0) * 14.0);
                    vec2 cell_px = clamp(UV * vec2(12.0, 14.0), vec2(0.5), vec2(11.5, 13.5));
                    vec2 uv = (cell_origin_px + cell_px) / vec2(384.0, 448.0);
                    float idx = texture(atlas, uv).r * 255.0;
                    if (idx < 0.5)
                        discard;
                    ALBEDO = texture(palette, vec2((idx + 0.5) / 256.0, 0.5)).rgb;
                    ALPHA = 1.0;
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
    }

    /// <summary>Polls for a new snapshot and updates the sprite quads.
    /// paletteArgb is the live 0xAARRGGBB palette from the legacy frame.</summary>
    public void Poll(ulong session, uint[] paletteArgb)
    {
        int rc;
        fixed (OtyrNative.Snapshot* snapshotPtr = &_snapshot)
            rc = OtyrNative.GetSnapshot(session, snapshotPtr, _snapshot.StructSize, 0);
        if (rc != OtyrNative.Ok)
            return;
        if (_snapshot.LevelTick == _lastRenderedTick)
            return;
        _lastRenderedTick = _snapshot.LevelTick;

        if (_snapshot.SheetEpoch != _sheetEpoch)
        {
            _sheetEpoch = _snapshot.SheetEpoch;
            FetchAtlases(session);
        }

        UpdatePalette(paletteArgb);
        UpdateInstances();
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

    private void UpdateInstances()
    {
        Array.Clear(_instanceCount);

        fixed (OtyrNative.Snapshot* snapshot = &_snapshot)
        {
            var sprites = (OtyrNative.SnapshotSprite*)snapshot->SpritesRaw;

            for (uint i = 0; i < snapshot->SpriteCount; i++)
            {
                var sprite = sprites[i];
                if (sprite.SheetId >= OtyrNative.SheetCount || sprite.Index == 0)
                    continue;  // glow pixels / old-table blits: not yet rendered
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

        for (int id = 0; id < OtyrNative.SheetCount; id++)
            _multiMesh[id].VisibleInstanceCount = _instanceCount[id];
    }

    private void AddCell(in OtyrNative.SnapshotSprite sprite, uint recordIndex, int cellIndex, int pixelOffsetX, int pixelOffsetY)
    {
        // Record coordinates are game_screen pixels (top-left of the cell);
        // the composited lane frame is shifted left by 24.
        float centerX = sprite.X + pixelOffsetX + OtyrNative.SheetCellW / 2f - 24f;
        float centerY = sprite.Y + pixelOffsetY + OtyrNative.SheetCellH / 2f;

        float laneX = (centerX / 320f - 0.5f) * LaneWidth;
        float laneY = (0.5f - centerY / 200f) * LaneHeight;

        // Terrain-baked art (enemyground structures) must stay pixel-coplanar
        // with the lane regardless of which slot band the level spawned it in.
        bool groundArt = sprite.Aux != 0 &&
            (sprite.Category <= (byte)OtyrNative.Category.EnemyGroundB);
        float laneZ = (groundArt ? 0.0008f
                                 : BandHeight[Math.Min(sprite.Category, (byte)(BandHeight.Length - 1))])
                    + recordIndex * OrderBias;

        int id = sprite.SheetId;
        int instance = _instanceCount[id]++;
        if (instance >= OtyrNative.SnapshotSpriteMax)
        {
            _instanceCount[id] = OtyrNative.SnapshotSpriteMax;
            return;
        }

        _multiMesh[id].SetInstanceTransform(instance,
            new Transform3D(Basis.Identity, new Vector3(laneX, laneY, laneZ)));
        _multiMesh[id].SetInstanceCustomData(instance,
            new Color(cellIndex - 1, sprite.Flags, sprite.FilterColor, 0));
    }
}
