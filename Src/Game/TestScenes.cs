using Pre2Port.Codecs;
using Pre2Port.Native;

namespace Pre2Port.Game;

/// <summary>
/// Layer smoke test: BACK0 with the level-0 palette, then the same picture with the level-1 palette, switching every
/// 2 seconds (140 ticks at 70 Hz). The tick counter is drawn with a built-in 3x5 pixel font.
/// </summary>
public sealed class PaletteCycleScene : IScene
{
    public const int TicksPerScreen = 140;

    readonly byte[] _indices;
    readonly (byte R, byte G, byte B)[][] _palettes;
    int _frame;

    public PaletteCycleScene(byte[] bootImage, string originalDir)
    {
        byte[] planar = Sqz.Unpack(File.ReadAllBytes(Path.Combine(originalDir, "BACK0.SQZ")));
        _indices = Screen.PlanarToIndices(planar);

        var pals = new List<(byte R, byte G, byte B)[]> { Screen.LevelPalette(bootImage, 0) };
        try
        {
            int ds = BootImage.DsBase;
            int ptr1 = bootImage[ds + Screen.PalettePtrTable + 2] | (bootImage[ds + Screen.PalettePtrTable + 3] << 8);
            if (ptr1 != 0 && ds + ptr1 + 48 <= bootImage.Length)
                pals.Add(Screen.LevelPalette(bootImage, 1));
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            // second level palette unavailable: stay with one screen
        }
        _palettes = pals.ToArray();
    }

    public int PaletteCount => _palettes.Length;

    public void Update(Input input) => _frame++;

    public void Draw(Framebuffer fb)
    {
        int screen = (_frame / TicksPerScreen) % _palettes.Length;
        fb.SetPalette(_palettes[screen], 0);
        fb.Blit(_indices, 0, 0);
        PixelFont.DrawNumber(fb, 4, 4, _frame, 15, 0, 2);
    }
}

/// <summary>Fixed 3x5 digit bitmaps (no external fonts).</summary>
public static class PixelFont
{
    // Each digit: 5 rows, 3 bits per row (bit 2 = left).
    static readonly byte[][] Digits =
    {
        new byte[] { 7, 5, 5, 5, 7 }, // 0
        new byte[] { 2, 6, 2, 2, 7 }, // 1
        new byte[] { 7, 1, 7, 4, 7 }, // 2
        new byte[] { 7, 1, 7, 1, 7 }, // 3
        new byte[] { 5, 5, 7, 1, 1 }, // 4
        new byte[] { 7, 4, 7, 1, 7 }, // 5
        new byte[] { 7, 4, 7, 5, 7 }, // 6
        new byte[] { 7, 1, 1, 1, 1 }, // 7
        new byte[] { 7, 5, 7, 5, 7 }, // 8
        new byte[] { 7, 5, 7, 1, 7 }, // 9
    };

    /// <summary>Draw a non-negative number on a solid <paramref name="bg"/> box; each font pixel is scale x scale.</summary>
    public static void DrawNumber(Framebuffer fb, int x, int y, int value, byte fg, byte bg, int scale)
    {
        string s = Math.Max(value, 0).ToString();
        int cell = 4 * scale;
        fb.FillRect(x - scale, y - scale, s.Length * cell + scale, 5 * scale + 2 * scale, bg);
        for (int i = 0; i < s.Length; i++)
        {
            byte[] glyph = Digits[s[i] - '0'];
            for (int row = 0; row < 5; row++)
                for (int col = 0; col < 3; col++)
                    if ((glyph[row] & (4 >> col)) != 0)
                        fb.FillRect(x + i * cell + col * scale, y + row * scale, scale, scale, fg);
        }
    }
}
