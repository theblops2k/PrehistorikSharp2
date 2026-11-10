namespace Pre2Port.Native;

/// <summary>
/// VGA DAC palette constants. Port of the palette part of <c>pre2/native/vga.py</c> (<c>_EGA16</c>, <c>_dac8</c>).
/// </summary>
public static class Vga
{
    /// <summary>The power-on DAC: first 16 entries = the EGA palette (R, G, B, 8-bit).</summary>
    public static readonly (byte R, byte G, byte B)[] Ega16 =
    {
        (0x00, 0x00, 0x00), (0x00, 0x00, 0xAA), (0x00, 0xAA, 0x00), (0x00, 0xAA, 0xAA),
        (0xAA, 0x00, 0x00), (0xAA, 0x00, 0xAA), (0xAA, 0x55, 0x00), (0xAA, 0xAA, 0xAA),
        (0x55, 0x55, 0x55), (0x55, 0x55, 0xFF), (0x55, 0xFF, 0x55), (0x55, 0xFF, 0xFF),
        (0xFF, 0x55, 0x55), (0xFF, 0x55, 0xFF), (0xFF, 0xFF, 0x55), (0xFF, 0xFF, 0xFF),
    };

    /// <summary>Expand a 6-bit DAC component to 8 bits: <c>(v&lt;&lt;2) | (v&gt;&gt;4)</c> (63 maps to 255).</summary>
    public static int Dac8(int v6)
    {
        v6 &= 0x3F;
        return (v6 << 2) | (v6 >> 4);
    }

    /// <summary>The 256-entry power-on palette: <see cref="Ega16"/> then a grayscale ramp (i, i, i) for 16..255.</summary>
    public static (byte R, byte G, byte B)[] DefaultPalette()
    {
        var pal = new (byte R, byte G, byte B)[256];
        for (int i = 0; i < 16; i++)
            pal[i] = Ega16[i];
        for (int i = 16; i < 256; i++)
            pal[i] = ((byte)i, (byte)i, (byte)i);
        return pal;
    }
}
