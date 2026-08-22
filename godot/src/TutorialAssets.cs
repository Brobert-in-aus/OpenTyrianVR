using Godot;
using System;

namespace OpenTyrianVR;

/// <summary>Small runtime decoder for the original Tyrian artwork used by the
/// pre-game lesson. Keeping this data-driven means the tutorial uses the same
/// GPL-distributed game assets as the real renderer, without copied stand-ins.</summary>
internal static class TutorialAssets
{
    private const string DataRoot = "res://tyrian21/";

    public static Texture2D PlayerShip() => DecodeArcadeShip();
    public static Texture2D Powerup() => DecodeTyrianComposite(arrayNumber: 10, baseIndex: 7);
    // First-level blue flyer: a valid 2x2 base at 85. Index 76 is terrain
    // artwork in the same mixed bank and was the stray ground fragment seen
    // in the tutorial.
    public static Texture2D EnemyShip() => DecodeSpriteComposite("newsh2.shp", baseIndex: 85);
    public static Texture2D Terrain() => DecodeFirstLevelTerrain();

    private static Texture2D DecodeArcadeShip()
    {
        byte[] file = Read("tyrian.shp");
        int count = U16(file, 0);
        const int arrayNumber = 9;
        int start = I32(file, 2 + (arrayNumber - 1) * 4);
        int end = arrayNumber < count ? I32(file, 2 + arrayNumber * 4) : file.Length;
        ReadOnlySpan<byte> data = file.AsSpan(start, end - start);

        // Arcade shipGr == 1 is the game's canonical two-part craft. The
        // simulation draws its left and right 24x28 blocks at x-17 and x+7.
        byte[] rgba = new byte[48 * 28 * 4];
        CopyComposite(data, 220, rgba, 48, 0);
        CopyComposite(data, 222, rgba, 48, 24);
        return Texture(rgba, 48, 28);
    }

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
        CopyComposite(data, baseIndex, rgba, 24, 0);
        return Texture(rgba, 24, 28);
    }

    private static void CopyComposite(ReadOnlySpan<byte> data, int baseIndex,
                                      byte[] rgba, int outputWidth, int outX)
    {
        CopyCell(data, baseIndex, rgba, outputWidth, outX, 0);
        CopyCell(data, baseIndex + 1, rgba, outputWidth, outX + 12, 0);
        CopyCell(data, baseIndex + 19, rgba, outputWidth, outX, 14);
        CopyCell(data, baseIndex + 20, rgba, outputWidth, outX + 12, 14);
    }

    private static void CopyCell(ReadOnlySpan<byte> data, int oneBasedIndex,
                                 byte[] rgba, int outputWidth, int outX, int outY)
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
                    PutPalettePixel(rgba, outputWidth, outX + x, outY + y, index, 255);
            }
        }
    }

    private static Texture2D DecodeFirstLevelTerrain()
    {
        byte[] file = Read("shapesz.dat");
        byte[] terrain = new byte[24 * 28];
        int offset = 0;
        // TYRIAN's first level uses the Z shapeset. Shape 100 (zero-based)
        // is its seamless dark-cobble ground fill, visible beneath the paths,
        // ice, cliffs, and structures in the real level.
        for (int i = 0; i <= 100 && offset < file.Length; ++i)
        {
            bool blank = file[offset++] != 0;
            if (!blank)
            {
                if (i == 100)
                    file.AsSpan(offset, 24 * 28).CopyTo(terrain);
                offset += 24 * 28;
            }
        }

        const int width = 264, height = 168;
        byte[] rgba = new byte[width * height * 4];
        for (int tileY = 0; tileY < 6; ++tileY)
        for (int tileX = 0; tileX < 11; ++tileX)
        {
            for (int y = 0; y < 28; ++y)
            for (int x = 0; x < 24; ++x)
                PutPalettePixel(rgba, width, tileX * 24 + x, tileY * 28 + y,
                                terrain[y * 24 + x], 255);
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
