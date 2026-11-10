using Pre2Port.Native;

namespace Pre2Port.Game;

/// <summary>
/// Planar EGA screen (320x200, 4 planes) to colour indices and RGBA.
/// Layout follows the Python reference: <c>native_level_load_background</c> (level_load.py:322) and
/// <c>_planar_to_rgb</c> (scripts/sdl_view.py:496): planes are consecutive (4 x 0x1F40 bytes, 200 rows x 40
/// bytes), plane p contributes bit p of the colour index, bits are MSB-first (bit 7 = leftmost pixel).
/// </summary>
public static class Screen
{
    public const int Width = 320;
    public const int Height = 200;
    public const int RowBytes = 40;
    public const int PlaneBytes = RowBytes * Height;   // 0x1F40
    public const int PlanarLength = PlaneBytes * 4;    // 32000

    /// <summary>Boot-DGROUP table of per-level palette pointers: ptr = word[0x2D00 + level*2] (dgroup_offsets.py).</summary>
    public const int PalettePtrTable = 0x2D00;

    /// <summary>Planar 4-plane buffer (BACK*.SQZ decoded) to 320x200 colour indices 0..15.</summary>
    public static byte[] PlanarToIndices(byte[] planar)
    {
        if (planar.Length != PlanarLength)
            throw new InvalidDataException($"planar buffer is {planar.Length} bytes, expected {PlanarLength}");

        var idx = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int xb = 0; xb < RowBytes; xb++)
            {
                int off = y * RowBytes + xb;
                int b0 = planar[off];
                int b1 = planar[PlaneBytes + off];
                int b2 = planar[2 * PlaneBytes + off];
                int b3 = planar[3 * PlaneBytes + off];
                int o = y * Width + xb * 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    int sh = 7 - bit;
                    idx[o + bit] = (byte)(((b0 >> sh) & 1) | (((b1 >> sh) & 1) << 1) | (((b2 >> sh) & 1) << 2) | (((b3 >> sh) & 1) << 3));
                }
            }
        }
        return idx;
    }

    /// <summary>
    /// The 16-colour DAC palette of <paramref name="level"/> from the boot image: the word at DGROUP
    /// [0x2D00 + level*2] points (DGROUP offset) at 16 x 3 six-bit R,G,B bytes ([asm 0ba0] / render.py
    /// native_load_dac_palette). Expanded to 8 bits with <see cref="Vga.Dac8"/>.
    /// </summary>
    public static (byte R, byte G, byte B)[] LevelPalette(byte[] bootImage, int level)
    {
        int ds = BootImage.DsBase;
        int ptr = bootImage[ds + PalettePtrTable + level * 2] | (bootImage[ds + PalettePtrTable + level * 2 + 1] << 8);
        var pal = new (byte R, byte G, byte B)[16];
        for (int i = 0; i < 16; i++)
        {
            int p = ds + ptr + i * 3;
            pal[i] = ((byte)Vga.Dac8(bootImage[p]), (byte)Vga.Dac8(bootImage[p + 1]), (byte)Vga.Dac8(bootImage[p + 2]));
        }
        return pal;
    }

    /// <summary>Colour indices to RGBA8888 (alpha 255) through <paramref name="palette"/>.</summary>
    public static byte[] IndicesToRgba(byte[] indices, (byte R, byte G, byte B)[] palette)
    {
        var rgba = new byte[indices.Length * 4];
        for (int i = 0; i < indices.Length; i++)
        {
            var c = palette[indices[i] & 0x0F];
            rgba[i * 4] = c.R;
            rgba[i * 4 + 1] = c.G;
            rgba[i * 4 + 2] = c.B;
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }
}
