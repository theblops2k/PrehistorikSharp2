using Pre2Port.Game.Launcher;
using Raylib_cs;

namespace Pre2Port.Game.Options;

/// <summary>
/// Drawing of the ESC overlay and of the FPS counter, in WINDOW pixels, in the launcher's look: the same 5x7 chiselled bitmap font (<see cref="LauncherArt"/>), the same stone / earth /
/// fire / moss / blood palette ramps, carved-stone panel with a lava-fire focus outline. Everything scales with the unit <c>g</c> (one font pixel = g window pixels), so the menu has the
/// same proportions in the 960x600 window and in fullscreen. The stone texture is built once per size.
/// </summary>
public sealed class OptionsMenuView : IDisposable
{
    // ---- palette (launcher ramps) ----
    static Color C(int idx, int a = 255) { var (r, g, b) = LauncherArt.PaletteRgb(idx); return new Color(r, g, b, a); }
    static Color S(int i) => C(LauncherArt.S(i));
    static Color E(int i) => C(LauncherArt.E(i));
    static Color F(int i) => C(LauncherArt.F(i));
    static Color M(int i) => C(LauncherArt.M(i));
    static Color B(int i) => C(LauncherArt.B(i));

    // ---- layout, in font pixels (multiplied by g) ----
    internal const int PanelW = 168, PanelH = 172, TitleH = 24, RowH = 14, RowGap = 1, Pad = 8, FooterH = 20;

    Texture2D _panelTex;
    int _panelG = -1;

    /// <summary>Font pixel size for a window height: ~1 font pixel per 200 window pixels, but the panel must fit.</summary>
    public static int UnitFor(int sw, int sh)
    {
        int g = Math.Max(1, (int)Math.Round(sh / 200.0));
        int fit = (int)Math.Floor(Math.Min(sh * 0.94 / PanelH, sw * 0.96 / PanelW));
        return Math.Max(1, Math.Min(g, fit));
    }

    // ---------------------------------------------------------------- text
    public static int TextWidth(string s, int g) => Math.Max(0, s.Length * 6 * g - g);

    /// <summary>Chiselled text (drop shadow, light top edge, dark bottom edge), as <see cref="LauncherArt.Text"/> but straight to the window.</summary>
    public static void DrawText(string s, int x, int y, int g, Color main, Color hi, Color lo, Color shadow, int alpha = 255)
    {
        int sh = Math.Max(1, g / 2);
        for (int pass = 0; pass < 2; pass++)
        {
            int cx = x;
            foreach (char raw in s)
            {
                ulong gl = LauncherArt.GlyphOf(raw, out int mark);
                for (int gy = 0; gy < 7; gy++)
                    for (int gx = 0; gx < 5; gx++)
                    {
                        if (!LauncherArt.LitPx(gl, gx, gy)) continue;
                        int px = cx + gx * g, py = y + gy * g;
                        if (pass == 0) { Raylib.DrawRectangle(px + sh, py + sh, g, g, shadow); continue; }
                        Raylib.DrawRectangle(px, py, g, g, main);
                        if (g >= 2)
                        {
                            int t = Math.Max(1, g / 3);
                            if (!LauncherArt.LitPx(gl, gx, gy - 1)) Raylib.DrawRectangle(px, py, g, t, hi);
                            if (!LauncherArt.LitPx(gl, gx, gy + 1)) Raylib.DrawRectangle(px, py + g - t, g, t, lo);
                        }
                    }
                if (mark != 0)
                {
                    (int mx, int my)[] pts = mark switch { 1 => new[] { (3, -2), (2, -1) }, 2 => new[] { (2, -2), (3, -2), (1, -1), (4, -1) }, _ => new[] { (1, -1), (3, -1) } };
                    foreach (var (mx, my) in pts)
                        Raylib.DrawRectangle(cx + mx * g + (pass == 0 ? sh : 0), y + my * g + (pass == 0 ? sh : 0), g, g, pass == 0 ? shadow : main);
                }
                cx += 6 * g;
            }
        }
    }

    static void Text(string s, int x, int y, int g, int tone, bool fire = false)
    {
        // tone: 14 bright / 7 dim stone, or fire ramp when focused
        if (fire) DrawText(s, x, y, g, F(14), F(15), F(10), S(0));
        else DrawText(s, x, y, g, S(tone), S(15), S(Math.Max(2, tone - 5)), S(0));
    }

    // ---------------------------------------------------------------- FPS counter
    /// <summary>"60" at the top-left corner of <paramref name="at"/> (the presented image area).</summary>
    public static void DrawFps(double fps, Rectangle at, int sh)
    {
        int g = Math.Max(1, (int)Math.Round(sh / 300.0));
        string t = ((int)Math.Round(fps)).ToString();
        int x = (int)at.X + 2 * g, y = (int)at.Y + 2 * g;
        Raylib.DrawRectangle(x - g, y - g, TextWidth(t, g) + 2 * g, 9 * g, new Color(0, 0, 0, 120));
        DrawText(t, x, y, g, F(14), F(15), F(10), S(0));
    }

    // ---------------------------------------------------------------- panel texture
    void EnsurePanel(int g)
    {
        if (_panelG == g) return;
        if (_panelG >= 0) Raylib.UnloadTexture(_panelTex);
        int w = PanelW * g, h = PanelH * g;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int cx = x / g, cy = y / g;   // font-pixel cell
                float n = LauncherArt.Rnd(cx, cy, 31) * 0.55f + LauncherArt.Rnd(cx / 3, cy / 2, 47) * 0.45f;
                int idx = 4 + (int)(n * 4f) + (cy < TitleH ? 1 : 0);
                int e = Math.Min(Math.Min(cx, cy), Math.Min(PanelW - 1 - cx, PanelH - 1 - cy));   // distance to the edge in cells
                if (e < 3)
                {
                    bool light = Math.Min(cx, cy) <= Math.Min(PanelW - 1 - cx, PanelH - 1 - cy);
                    idx = e == 0 ? 1 : light ? 13 - e : 2 + e;
                }
                if (cx + cy < 3 || (PanelW - 1 - cx) + (PanelH - 1 - cy) < 3) { Put(px, w, x, y, 0, 0, 0, 0); continue; }   // chipped corners
                var (r, gg, b) = LauncherArt.PaletteRgb(LauncherArt.S(idx));
                Put(px, w, x, y, r, gg, b, 255);
            }
        Image img = Raylib.GenImageColor(w, h, Color.Blank);
        _panelTex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        Raylib.UpdateTexture(_panelTex, px);
        Raylib.SetTextureFilter(_panelTex, TextureFilter.Point);
        _panelG = g;
    }

    static void Put(byte[] px, int w, int x, int y, byte r, byte g, byte b, byte a)
    {
        int o = (y * w + x) * 4;
        px[o] = r; px[o + 1] = g; px[o + 2] = b; px[o + 3] = a;
    }

    // ---------------------------------------------------------------- menu
    /// <param name="padHints">true when a joystick drives the menu (footer shows Fire / Jump instead of Enter / Esc).</param>
    public void Draw(OptionsMenu m, int sw, int sh, double now, bool padHints)
    {
        int g = UnitFor(sw, sh);
        EnsurePanel(g);
        Raylib.DrawRectangle(0, 0, sw, sh, new Color(0, 0, 0, 150));   // dim the paused scene

        int pw = PanelW * g, ph = PanelH * g;
        int px = (sw - pw) / 2, py = (sh - ph) / 2;
        Raylib.DrawRectangle(px + 3 * g, py + 3 * g, pw, ph, new Color(0, 0, 0, 120));
        Raylib.DrawTexture(_panelTex, px, py, Color.White);

        // title bar: earth-toned plate with the fire-lit title
        int tx = px + Pad * g, ty = py + 5 * g, tw = pw - 2 * Pad * g;
        Raylib.DrawRectangle(tx, ty, tw, 14 * g, E(2));
        Raylib.DrawRectangle(tx, ty, tw, g, E(8));
        Raylib.DrawRectangle(tx, ty + 14 * g - g, tw, g, E(0));
        int tg = Math.Max(g, (g * 3 + 1) / 2);
        string title = m.Title.ToUpperInvariant();
        DrawText(title, px + (pw - TextWidth(title, tg)) / 2, ty + (14 * g - 7 * tg) / 2, tg, F(13), F(15), F(9), S(0));

        // rows
        var rows = m.Rows();
        int y0 = py + (TitleH + 2) * g;
        int rowX = px + Pad * g, rowW = pw - 2 * Pad * g;
        int tick = (int)(now * 28);
        bool confirm = m.Current == OptionsMenu.Page.Confirm;
        if (confirm)
        {
            string q = m.Confirming == OptionsMenu.Request.ExitToWindows ? "Close the game?" : "Back to the menu?";
            Text(q.ToUpperInvariant(), px + (pw - TextWidth(q, g)) / 2, y0 + 8 * g, g, 14);
            string[] yn = { "No", "Yes" };
            for (int i = 0; i < 2; i++)
            {
                int bw = 40 * g, bx = px + pw / 2 + (i == 0 ? -bw - 6 * g : 6 * g), by = y0 + 28 * g;
                Row(bx, by, bw, RowH * g, g, yn[i], "", i == m.ConfirmSel, true, false, tick, centered: true);
            }
        }
        else
        {
            for (int i = 0; i < rows.Count; i++)
            {
                int ry = y0 + i * (RowH + RowGap) * g;
                bool back = m.Current != OptionsMenu.Page.Root && i == rows.Count - 1;
                if (back) ry += 3 * g;   // separate the Back row
                string val = m.RowValue(i);
                bool bar = m.Current == OptionsMenu.Page.Sound && i == 0;
                Row(rowX, ry, rowW, RowH * g, g, rows[i], val, i == m.Sel, m.RowEnabled(i), m.RowAdjustable(i) && i == m.Sel, tick, volume: bar ? m.Options.Volume : -1,
                    capturing: m.Capturing && i == m.Sel, danger: m.Current == OptionsMenu.Page.Root && i == rows.Count - 1);
            }
        }

        // footer
        int hg = Math.Max(1, g * 2 / 3);
        int fy = py + ph - FooterH * g + g;
        Raylib.DrawRectangle(px + Pad * g, fy - 2 * g, pw - 2 * Pad * g, g, S(2));
        string l1, l2;
        if (m.Message.Length > 0) { l1 = m.Message; l2 = ""; }
        else if (m.Capturing) { l1 = m.Controller.UseJoystick ? "Press the new button" : "Press the new key"; l2 = "Esc cancels"; }
        else if (confirm) { l1 = padHints ? "Left/Right: choose   Fire: accept" : "Left/Right: choose   Enter: accept"; l2 = padHints ? "Jump: back" : "Esc: back"; }
        else
        {
            l1 = "Up/Down: move   Left/Right: change";
            l2 = padHints ? "Fire: select   Jump: back" : "Enter: select   Esc: back";
        }
        l1 = l1.ToUpperInvariant(); l2 = l2.ToUpperInvariant();
        Color mc = m.Message.Length > 0 ? F(13) : S(10);
        DrawText(l1, px + (pw - TextWidth(l1, hg)) / 2, fy + g, hg, mc, S(13), S(6), S(0));
        if (l2.Length > 0) DrawText(l2, px + (pw - TextWidth(l2, hg)) / 2, fy + g + 9 * hg, hg, S(10), S(13), S(6), S(0));
    }

    void Row(int x, int y, int w, int h, int g, string label, string value, bool focused, bool enabled, bool adjustable, int tick, int volume = -1, bool capturing = false, bool danger = false, bool centered = false)
    {
        // slot: dark carved recess; focused: lit with a living fire outline
        Raylib.DrawRectangle(x, y, w, h, focused ? (capturing ? B(2) : E(3)) : E(1));
        Raylib.DrawRectangle(x, y, w, g, E(0));                 // top shadow
        Raylib.DrawRectangle(x, y + h - g, w, g, E(focused ? 7 : 4));   // bottom light edge
        if (danger && !focused) Raylib.DrawRectangle(x, y, g, h, B(3));
        if (focused)
        {
            int o = Math.Max(1, g / 2);
            for (int i = 0; i < w; i += g)
            {
                int c = 9 + (int)(3.5f * MathF.Sin(i / (float)g * 0.35f - tick * 0.28f)) + (int)(LauncherArt.Rnd(i / g, tick / 2, 77) * 2);
                Raylib.DrawRectangle(x + i, y - o, g, o, F(c));
                Raylib.DrawRectangle(x + i, y + h, g, o, F(c - 1));
            }
            for (int j = 0; j < h; j += g)
            {
                int c = 9 + (int)(3.5f * MathF.Sin(j / (float)g * 0.4f + tick * 0.25f)) + (int)(LauncherArt.Rnd(j / g, tick / 2, 78) * 2);
                Raylib.DrawRectangle(x - o, y + j, o, g, F(c));
                Raylib.DrawRectangle(x + w, y + j, o, g, F(c - 1));
            }
        }
        int ty = y + (h - 7 * g) / 2;
        string lab = label.ToUpperInvariant();
        bool fire = focused && enabled;
        int tone = enabled ? 14 : 6;
        if (centered) { Text(lab, x + (w - TextWidth(lab, g)) / 2, ty, g, tone, fire); return; }
        Text(lab, x + 4 * g, ty, g, tone, fire);
        if (volume >= 0)
        {
            DrawVolume(x, y, w, h, g, volume, tick);
            return;
        }
        if (value.Length == 0) return;
        string val = value.ToUpperInvariant();
        int avail = w - 4 * g - 4 * g - TextWidth(lab, g) - 4 * g - (adjustable ? 8 * g : 0);   // left margin, right margin, label, gap, arrows
        while (val.Length > 3 && TextWidth(val, g) > avail) val = val.Substring(0, val.Length - 2) + ".";   // very long names (a pad's label, a rare key) are shortened
        int vw = TextWidth(val, g);
        int vx = x + w - 4 * g - vw - (adjustable ? 8 * g : 0);
        if (capturing) { Text(val, vx, ty, g, 14, true); return; }
        Text(val, vx, ty, g, enabled ? 12 : 6, fire);
        if (adjustable)
        {
            Arrow(vx - 5 * g, y + h / 2, g, false, F(14));
            Arrow(vx + vw + 3 * g, y + h / 2, g, true, F(14));
        }
    }

    static void Arrow(int x, int cy, int g, bool right, Color c)
    {
        for (int i = 0; i < 3; i++)   // columns of 5, 3, 1 font pixels, centred on cy
        {
            int cx = right ? x + i * g : x + (2 - i) * g, n = 5 - 2 * i;
            Raylib.DrawRectangle(cx, cy - n * g / 2, g, n * g, c);
        }
    }

    /// <summary>Stone trough with a lava-fire fill (the launcher's progress bar) and the percentage on its right.</summary>
    static void DrawVolume(int x, int y, int w, int h, int g, int volume, int tick)
    {
        string pct = volume + "%";
        int pw = TextWidth(pct, g);
        int bw = 52 * g, bh = 5 * g, bx = x + w - 4 * g - pw - 4 * g - bw, by = y + (h - bh) / 2;
        Raylib.DrawRectangle(bx - g, by - g, bw + 2 * g, bh + 2 * g, S(11));
        Raylib.DrawRectangle(bx, by, bw, bh, S(1));
        int fill = (int)Math.Round(volume / 100.0 * bw / g) * g;
        for (int xx = 0; xx < fill; xx += g)
        {
            float along = xx / (float)Math.Max(1, bw - g);
            for (int yy = 0; yy < bh; yy += g)
            {
                float v = (yy + g * 0.5f) / bh, tube = 1f - MathF.Abs(v - 0.42f) * 1.9f;
                int c = (int)(4 + along * 4.5f + tube * 5f + MathF.Sin(xx / (float)g * 0.21f - tick * 0.25f) * 1.6f);
                if (xx >= fill - 3 * g) c += 2;
                Raylib.DrawRectangle(bx + xx, by + yy, g, g, F(c));
            }
        }
        Text(pct, x + w - 4 * g - pw, y + (h - 7 * g) / 2, g, 14);
    }

    public void Dispose()
    {
        if (_panelG >= 0) { Raylib.UnloadTexture(_panelTex); _panelG = -1; }
    }
}
