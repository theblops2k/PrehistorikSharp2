using Pre2Port.Codecs;
using Pre2Port.Game;
using Pre2Port.Game.Level;
using Pre2Port.Native;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Recovered;

/// <summary>
/// The two STATIC end screens. Ports of <c>pre2/recovered/tally_panel.py</c> (render_tally_panel 51A3, number_to_digits 4803, compute_percent 5139,
/// glyph blit 4780/47C0) and <c>pre2/recovered/gameover_background.py</c> (render_gameover_background 9B66 + 9C87) with the bridge inputs of
/// <c>pre2/views/tally_panel.py</c> (digit font = ALLFONTS.SQZ blob at +0x1C70, verified identical to [[0x3D]]:0x1C70; letters/'%' = sprite directory 0x5F48/0x62E8
/// ids (c-'A')+0xF1 / 0x1A+0xF1, data at seg:off+0x16).
/// SIMPLIFICATIONS: DrawTally = black + panel only (the static frame the Python reference verifies; the walk-in / food throw / score count-up / object pass
/// cutscene is <see cref="TallyScene"/>, used by the animated EndScene); game over = the diorama at its final scroll
/// [0x6BC4]=0xB9 (no falling letters, birds, crying caveman, music, fade). Nothing moves, so there is nothing to interpolate at presentation.
/// </summary>
public static class EndScreens
{
    const int Rows = 11, Row = 0x28, GlyphLen = 0x58, DigitFont = 0x1C70, Hdr = 0x16;
    const int ScoreLabelDi = 0x230, ScoreDigitsDi = 0x23C, PctLabelDi = 0x410, PctDigitsDi = 0x430;
    const int GameOverPalette = 0xAFE8, GameOverScroll = 0xB9, Plane = 0x1F40;

    static void BlitGlyph(byte[][] pl, ReadOnlySpan<byte> g, int di)
    {
        int src = 0;
        for (int p = 0; p < 4; p++)
        {
            int d = di & 0xFFFF;
            for (int r = 0; r < Rows; r++)
            {
                pl[p][d] = g[src];
                pl[p][(d + 1) & 0xFFFF] = g[src + 1];
                src += 2;
                d = (d + Row) & 0xFFFF;
            }
        }
    }

    /// <summary>[asm 4803] 10 ASCII digits of a 32-bit value (leading zeros).</summary>
    public static string NumberToDigits(uint value) => value.ToString("D10");

    /// <summary>
    /// [asm 5139] (collected &amp; 0xFF) * 100 / total, collected = [0x2A76]+[0x2A7A], total = [0x2A74]+[0x2A78]. With total == 0 the ASM
    /// (5149 MOV AX,0x64; 5154 JCXZ 5165; 5165 MOV DX,AX) prints 100, not 0 (the Python reference returns 0 there).
    /// </summary>
    public static int Percent(LevelState s)
    {
        int total = (s.Rw(Off.CollectMain) + s.Rw(Off.CollectDecor)) & 0xFFFF;
        if (total == 0) return 100;
        int collected = (s.Rw(Off.CollectCounter) + s.Rw(Off.CollectLinked)) & 0xFFFF;
        return (collected & 0xFF) * 100 / total;
    }

    public static uint Score(LevelState s) => (uint)s.Rw(0x6C0E) | ((uint)s.Rw(0x6C10) << 16);

    static byte[] Letter(LevelState s, int idx)
    {
        idx &= 0xFFFF;
        int off = s.Rw(0x5F48 + idx * 2), seg = s.Rw(0x62E8 + idx * 2);
        int b = ((seg << 4) + ((off + Hdr) & 0xFFFF)) & AddrMask;
        var g = new byte[GlyphLen];
        for (int i = 0; i < GlyphLen; i++) g[i] = (byte)s.Fb(b + i);
        return g;
    }

    /// <summary>[asm 51A3] "SCORE" + 6 score digits + '0', then "LEVEL COMPLETED" + percent + '%' onto <paramref name="pl"/> at <paramref name="page"/>.</summary>
    public static void RenderTallyPanel(byte[][] pl, LevelState s, byte[] fontBlob, int page)
    {
        void Digit(int d, int di) => BlitGlyph(pl, fontBlob.AsSpan(DigitFont + d * GlyphLen, GlyphLen), di);
        void Label(string text, int di)
        {
            foreach (char ch in text)
            {
                if (ch != ' ') BlitGlyph(pl, Letter(s, ch - 'A' + 0xF1), di);
                di = (di + 2) & 0xFFFF;
            }
        }
        Label("SCORE", (ScoreLabelDi + page) & 0xFFFF);
        int dd = (ScoreDigitsDi + page) & 0xFFFF;
        foreach (char c in NumberToDigits(Score(s))[4..]) { Digit(c - '0', dd); dd = (dd + 2) & 0xFFFF; }
        Digit(0, dd);                                                 // [51D6] trailing '0'
        Label("LEVEL COMPLETED", (PctLabelDi + page) & 0xFFFF);
        dd = (PctDigitsDi + page) & 0xFFFF;
        string pbuf = NumberToDigits((uint)Percent(s))[7..];
        if (pbuf[0] == '0') pbuf = pbuf[1..];                         // [5176] skip a single leading zero
        foreach (char c in pbuf) { Digit(c - '0', dd); dd = (dd + 2) & 0xFFFF; }
        BlitGlyph(pl, Letter(s, 0x1A + 0xF1), dd);                    // [518A] '%'
    }

    internal static byte[][] NewPlanes() => new[] { new byte[0x10000], new byte[0x10000], new byte[0x10000], new byte[0x10000] };

    internal static void ToFramebuffer(byte[][] pl, int page, Framebuffer fb)
    {
        for (int y = 0; y < Screen.Height; y++)
            for (int c = 0; c < Screen.RowBytes; c++)
            {
                int o = (page + y * Screen.RowBytes + c) & 0xFFFF;
                int b0 = pl[0][o], b1 = pl[1][o], b2 = pl[2][o], b3 = pl[3][o];
                for (int bit = 0; bit < 8; bit++)
                {
                    int sh = 7 - bit;
                    fb.Pixels[y * Screen.Width + c * 8 + bit] = (byte)(((b0 >> sh) & 1) | (((b1 >> sh) & 1) << 1) | (((b2 >> sh) & 1) << 2) | (((b3 >> sh) & 1) << 3));
                }
            }
    }

    /// <summary>Level-end tally screen: black + panel, level palette, page 0.</summary>
    public static void DrawTally(LevelState s, HudAssets hud, Framebuffer fb)
    {
        var pl = NewPlanes();
        RenderTallyPanel(pl, s, hud.Font, 0);
        ToFramebuffer(pl, 0, fb);
        LevelRender.SetLevelPalette(s, fb);
    }

    /// <summary>Decoded GAMEOVER.SQZ (4 plane-major planes of 0x1F40).</summary>
    public static byte[] LoadGameOverAsset(LevelState s) => Sqz.Unpack(File.ReadAllBytes(s.ResolvePath("GAMEOVER.SQZ")));

    /// <summary>Game-over screen: staging (top 200 rows black, bottom 200 = asset), 176-row window at scroll 0xB9 to page 0, palette DGROUP 0xAFE8.</summary>
    public static void DrawGameOver(LevelState s, byte[] asset, Framebuffer fb)
    {
        var stg = new byte[4][];
        var pl = NewPlanes();
        int srcOff = (Row * GameOverScroll) & 0xFFFF;
        for (int p = 0; p < 4; p++)
        {
            stg[p] = new byte[0x3E80 + 0x180];
            Array.Copy(asset, p * Plane, stg[p], Plane, Plane);
            for (int i = 0; i < 0x1B80; i++) pl[p][i] = stg[p][(srcOff + i) & 0xFFFF];
        }
        ToFramebuffer(pl, 0, fb);
        fb.SetPalette6(s.Data, BootImage.DsBase + GameOverPalette, 0, 16);
    }
}
