using Godot;
using System;

namespace OpenTyrianVR;

/// <summary>Small runtime decoder for the original Tyrian artwork used by the
/// pre-game lesson. Keeping this data-driven means the tutorial uses the same
/// GPL-distributed game assets as the real renderer, without copied stand-ins.</summary>
internal static class TutorialAssets
{
    private const string DataRoot = "res://tyrian21/";

    public static Texture2D PlayerShip() => DecodeTyrianComposite(arrayNumber: 9, baseIndex: 191);
    public static Texture2D Powerup() => DecodeTyrianComposite(arrayNumber: 10, baseIndex: 7);
    public static Texture2D EnemyShip() => DecodeSpriteComposite("newsh2.shp", baseIndex: 76);
    public static Texture2D Terrain() => DecodeTerrainPatch("shapesx.dat");

    private static Texture2D DecodeTyrianComposite(int arrayNumber, int baseIndex)
    {
        byte[] file = Read("tyrian.shp");
        int count = U16(file, 0);
        int start = I32(file, 2 + (arrayNumber - 1) * 4);
        int end = arrayNumber < count ? I32(file, 2 + arrayNumber * 4) : file.Length;
        return DecodeComposite(file.AsSpan(start, end - start), baseIndex);
    }

    private static Texture2D DecodeSpriteComposite(string fileName, int baseIndex) =>
        DecodeComposite(Read(fileName), baseIndex);

    private static Texture2D DecodeComposite(ReadOnlySpan<byte> data, int baseIndex)
    {
        byte[] rgba = new byte[24 * 28 * 4];
        CopyCell(data, baseIndex, rgba, 0, 0);
        CopyCell(data, baseIndex + 1, rgba, 12, 0);
        CopyCell(data, baseIndex + 19, rgba, 0, 14);
        CopyCell(data, baseIndex + 20, rgba, 12, 14);
        return Texture(rgba, 24, 28);
    }

    private static void CopyCell(ReadOnlySpan<byte> data, int oneBasedIndex,
                                 byte[] rgba, int outX, int outY)
    {
        int offset = U16(data, (oneBasedIndex - 1) * 2);
        int x = 0, y = 0;
        while (offset < data.Length)
        {
            byte control = data[offset++];
            if (control == 0x0f) break;
            x += control & 0x0f;
            int opaque = control >> 4;
            if (opaque == 0) { ++y; x = 0; continue; }
            for (int i = 0; i < opaque && offset < data.Length; ++i, ++x)
            {
                byte index = data[offset++];
                if (x < 12 && y < 14)
                    PutPalettePixel(rgba, 24, outX + x, outY + y, index, 255);
            }
        }
    }

    private static Texture2D DecodeTerrainPatch(string fileName)
    {
        byte[] file = Read(fileName);
        byte[][] shapes = new byte[60][];
        int offset = 0;
        for (int i = 0; i < shapes.Length && offset < file.Length; ++i)
        {
            bool blank = file[offset++] != 0;
            shapes[i] = blank ? new byte[24 * 28] : file.AsSpan(offset, 24 * 28).ToArray();
            if (!blank) offset += 24 * 28;
        }

        const int width = 264, height = 168;
        byte[] rgba = new byte[width * height * 4];
        // A contiguous, authored metal/rock strip from the TYRIAN shapeset.
        // Offset each row so the patch reads like scrolling terrain instead
        // of a repeated checkerboard.
        for (int tileY = 0; tileY < 6; ++tileY)
        for (int tileX = 0; tileX < 11; ++tileX)
        {
            byte[] shape = shapes[45 + (tileX + tileY * 3) % 15];
            for (int y = 0; y < 28; ++y)
            for (int x = 0; x < 24; ++x)
                PutPalettePixel(rgba, width, tileX * 24 + x, tileY * 28 + y,
                                shape[y * 24 + x], 255);
        }
        return Texture(rgba, width, height);
    }

    private static readonly byte[] Palette = LoadPalette();

    private static byte[] LoadPalette()
    {
        byte[] source = Read("palette.dat");
        byte[] result = new byte[256 * 3];
        Array.Copy(source, result, result.Length);
        return result;
    }

    private static void PutPalettePixel(byte[] rgba, int width, int x, int y,
                                        byte index, byte alpha)
    {
        int destination = (y * width + x) * 4;
        int source = index * 3;
        rgba[destination] = Vga(Palette[source]);
        rgba[destination + 1] = Vga(Palette[source + 1]);
        rgba[destination + 2] = Vga(Palette[source + 2]);
        rgba[destination + 3] = alpha;
    }

    private static byte Vga(byte value) => (byte)((value << 2) | (value >> 4));
    private static Texture2D Texture(byte[] rgba, int width, int height) =>
        ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba));
    private static byte[] Read(string name) => Godot.FileAccess.GetFileAsBytes(DataRoot + name);
    private static int U16(ReadOnlySpan<byte> data, int offset) => data[offset] | data[offset + 1] << 8;
    private static int I32(ReadOnlySpan<byte> data, int offset) =>
        data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24;
}
