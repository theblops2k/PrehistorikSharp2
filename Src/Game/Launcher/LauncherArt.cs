namespace Pre2Port.Game.Launcher;

/// <summary>
/// Palette, bitmap font and primitives of the launcher. Everything is drawn into an indexed <see cref="Framebuffer"/> (640x400, same 8:5 proportion
/// as the game) with a stone / cave / fire palette; the game art (MENU.SQZ preview) lives at palette entries <see cref="Menu0"/>..255.
/// Note on fonts: before the copy there is no original data on disk (FRONT/ALLFONTS are not there yet), so the launcher carries its own 5x7 font.
/// </summary>
internal static class LauncherArt
{
    public const int W = 640, H = 400;

    // Palette layout (0 = black)
    public const int Stone0 = 1;    // 1..16   warm grey stone, dark to light
    public const int Earth0 = 17;   // 17..32  cave walls / wood
    public const int Fire0 = 33;    // 33..48  embers -> flame -> white-yellow
    public const int Moss0 = 49;    // 49..56  success green
    public const int Blood0 = 57;   // 57..64  error red
    public const int Menu0 = 96;    // 96..255 game art (MENU.SQZ palette, 160 entries)

    public static byte S(int i) => (byte)(Stone0 + Math.Clamp(i, 0, 15));
    public static byte E(int i) => (byte)(Earth0 + Math.Clamp(i, 0, 15));
    public static byte F(int i) => (byte)(Fire0 + Math.Clamp(i, 0, 15));
    public static byte M(int i) => (byte)(Moss0 + Math.Clamp(i, 0, 7));
    public static byte B(int i) => (byte)(Blood0 + Math.Clamp(i, 0, 7));

    static (byte, byte, byte) Lerp((int r, int g, int b) a, (int r, int g, int b) b, double t) =>
        ((byte)Math.Round(a.r + (b.r - a.r) * t), (byte)Math.Round(a.g + (b.g - a.g) * t), (byte)Math.Round(a.b + (b.b - a.b) * t));

    static void Ramp(Framebuffer fb, int start, int count, params (int at, (int r, int g, int b) c)[] stops)
    {
        for (int i = 0; i < count; i++)
        {
            int k = 0;
            while (k + 2 < stops.Length && i > stops[k + 1].at) k++;
            double t = stops[k + 1].at == stops[k].at ? 0 : Math.Clamp((i - stops[k].at) / (double)(stops[k + 1].at - stops[k].at), 0, 1);
            fb.Palette[start + i] = Lerp(stops[k].c, stops[k + 1].c, t);
        }
    }

    /// <summary>Installs the launcher palette (entries 0..64). The game art range (96..255) is left alone.</summary>
    public static void ApplyPalette(Framebuffer fb)
    {
        fb.Palette[0] = (0, 0, 0);
        Ramp(fb, Stone0, 16, (0, (22, 18, 15)), (8, (120, 108, 92)), (15, (236, 226, 202)));
        Ramp(fb, Earth0, 16, (0, (9, 6, 5)), (7, (52, 36, 26)), (15, (132, 96, 62)));
        Ramp(fb, Fire0, 16, (0, (24, 3, 0)), (5, (150, 26, 0)), (9, (240, 112, 0)), (12, (255, 200, 44)), (15, (255, 248, 196)));
        Ramp(fb, Moss0, 8, (0, (18, 38, 16)), (7, (132, 214, 78)));
        Ramp(fb, Blood0, 8, (0, (46, 8, 8)), (7, (240, 82, 64)));
    }

    // ---------------------------------------------------------------- noise
    static uint Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }

    public static float Rnd(int x, int y, int seed) => (Hash(x, y, seed) & 0xFFFF) / 65535f;

    static float VNoise(float x, float y, int seed)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        float a = Rnd(xi, yi, seed), b = Rnd(xi + 1, yi, seed), c = Rnd(xi, yi + 1, seed), d = Rnd(xi + 1, yi + 1, seed);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    static float Fbm(float x, float y, int seed)
    {
        float v = 0, amp = 0.5f;
        for (int i = 0; i < 4; i++) { v += amp * VNoise(x, y, seed + i * 7); x *= 2; y *= 2; amp *= 0.5f; }
        return v / 0.9375f;   // 0..1
    }

    static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
    static float Dither(int x, int y) => Bayer[(y & 3) * 4 + (x & 3)] / 16f;

    // ---------------------------------------------------------------- geometry of the stone slab
    public const int SlabX = 80, SlabY = 62, SlabW = 480, SlabH = 290, Cut = 10;

    /// <summary>Distance to the slab border (rectangle only) and the side it belongs to; negative = outside.</summary>
    static int SlabEdge(int x, int y, out int side)
    {
        int lx = x - SlabX, ly = y - SlabY, rx = SlabW - 1 - lx, by = SlabH - 1 - ly;
        int e = Math.Min(Math.Min(lx, ly), Math.Min(rx, by));
        side = e == lx || e == ly ? 0 : 1;   // 0 = light side (top/left), 1 = dark side (bottom/right)
        return e;
    }

    static bool InSlab(int x, int y, out int cornerEdge, out int cornerSide)
    {
        int lx = x - SlabX, ly = y - SlabY, rx = SlabW - 1 - lx, by = SlabH - 1 - ly;
        cornerEdge = int.MaxValue; cornerSide = 0;
        if (lx < 0 || ly < 0 || rx < 0 || by < 0) return false;
        int tl = lx + ly - Cut, tr = rx + ly - Cut, bl = lx + by - Cut, br = rx + by - Cut;
        int m = Math.Min(Math.Min(tl, tr), Math.Min(bl, br));
        if (m < 0) return false;
        cornerEdge = m / 2;
        cornerSide = m == tl ? 0 : m == br ? 1 : 2;   // 2 = side corners (neutral)
        return true;
    }

    // ---------------------------------------------------------------- static scene
    public static byte[] BuildStatic()
    {
        var fb = new Framebuffer(W, H);
        // cave wall
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float n = Fbm(x / 46f, y / 28f, 1);
                float strata = 0.5f + 0.5f * MathF.Sin(y * 0.11f + Fbm(x / 90f, y / 40f, 7) * 6f);
                float v = 0.18f + n * 0.55f + strata * 0.2f;
                float dx = (x - W / 2f) / (W / 2f), dy = (y - H / 2f) / (H / 2f);
                v *= 1f - 0.5f * (dx * dx * 0.7f + dy * dy * 0.8f);
                float f = Math.Clamp(v, 0f, 1f) * 12.5f;
                fb.Pixels[y * W + x] = E((int)(f + Dither(x, y)));
            }
        // stalactites (top) and stalagmites (bottom)
        for (int x = 0; x < W; x++)
        {
            float tri = 1f - MathF.Abs(((x * 0.037f + 0.31f) % 1f) * 2f - 1f);
            float h = (14f + 46f * Fbm(x / 64f, 3.3f, 11)) * MathF.Pow(tri, 1.5f);
            for (int y = 0; y < (int)h; y++)
                fb.Pixels[y * W + x] = E(y >= (int)h - 2 ? 6 : 1 + (int)(Rnd(x, y, 3) * 2));
            float tri2 = 1f - MathF.Abs(((x * 0.052f + 0.7f) % 1f) * 2f - 1f);
            float h2 = (8f + 30f * Fbm(x / 50f, 9.1f, 17)) * MathF.Pow(tri2, 1.4f);
            for (int k = 0; k < (int)h2; k++)
                fb.Pixels[(H - 1 - k) * W + x] = E(k >= (int)h2 - 2 ? 5 : 1 + (int)(Rnd(x, k, 4) * 2));
        }
        // torches: wooden poles and stone bowls (flames are animated by the scene)
        foreach (int cx in new[] { 40, W - 40 })
        {
            for (int y = 236; y < H; y++)
                for (int x = cx - 5; x <= cx + 4; x++)
                {
                    int shade = 4 + (x - (cx - 5)) * 6 / 10 - (x > cx + 1 ? 3 : 0) + (int)(Rnd(x, y, 9) * 2);
                    if (y % 23 < 3 && y < 330) shade = 1;
                    fb.Pixels[y * W + x] = E(shade);
                }
            for (int y = 226; y < 238; y++)
                for (int x = cx - 12; x <= cx + 11; x++)
                {
                    int half = 12 - (y - 226);
                    if (Math.Abs(x - cx + 0.5f) > half + 2) continue;
                    fb.Pixels[y * W + x] = S(3 + (int)(Rnd(x, y, 5) * 3) + (x < cx ? 2 : 0));
                }
            for (int x = cx - 14; x <= cx + 13; x++) { fb.SetPixel(x, 225, S(11)); fb.SetPixel(x, 226, S(2)); }
        }
        // slab
        for (int y = SlabY; y < SlabY + SlabH; y++)
            for (int x = SlabX; x < SlabX + SlabW; x++)
            {
                if (!InSlab(x, y, out int ce, out int cs)) continue;
                float n = Fbm(x / 15f, y / 10f, 21);
                float v = 0.2f + n * 0.7f - (Rnd(x, y, 31) < 0.025f ? 0.25f : 0f) + (Rnd(x / 3, y / 3, 41) - 0.5f) * 0.12f;
                int idx = 3 + (int)(Math.Clamp(v, 0f, 1f) * 6f + Dither(x, y));
                int e = SlabEdge(x, y, out int side);
                if (cs != 2 && ce < e) { e = ce; side = cs; }
                if (e < 2) idx = side == 0 ? 14 : 0;
                else if (e < 4) idx = side == 0 ? 11 : 2;
                else if (e == 12) idx = SlabLight(x, y) ? 2 : 11;       // groove: dark on the near side, light lip on the far side
                else if (e == 13) idx = SlabLight(x, y) ? 11 : 2;
                fb.Pixels[y * W + x] = S(idx);
            }
        // cracks
        Crack(fb, SlabX + 6, SlabY + 90, 0.15f, 46, 101);
        Crack(fb, SlabX + SlabW - 6, SlabY + SlabH - 70, 3.2f, 52, 102);
        Crack(fb, SlabX + 70, SlabY + SlabH - 6, -1.6f, 12, 103);
        Crack(fb, SlabX + SlabW - 130, SlabY + 6, 1.7f, 22, 104);
        // rivets
        foreach (var (rx, ry) in new[] { (SlabX + 22, SlabY + 22), (SlabX + SlabW - 23, SlabY + 22), (SlabX + 22, SlabY + SlabH - 23), (SlabX + SlabW - 23, SlabY + SlabH - 23) })
            Disc(fb, rx, ry);
        // title, carved in fire gold
        string title = "PREHISTORIK 2";
        Text(fb, title, (W - TextWidth(title, 4)) / 2, 84, 4, F(11), F(14), F(6), S(0));
        // separator groove
        for (int x = 116; x < 524; x++) { fb.SetPixel(x, 124, S(1)); fb.SetPixel(x, 125, S(12)); }
        return fb.Pixels;
    }

    static bool SlabLight(int x, int y)
    {
        int lx = x - SlabX, ly = y - SlabY, rx = SlabW - 1 - lx, by = SlabH - 1 - ly;
        return Math.Min(lx, ly) <= Math.Min(rx, by);
    }

    static void Crack(Framebuffer fb, int x, int y, float ang, int len, int seed)
    {
        float fx = x, fy = y;
        for (int i = 0; i < len; i++)
        {
            ang += (Rnd(i, seed, 1) - 0.5f) * 0.7f;
            fx += MathF.Cos(ang); fy += MathF.Sin(ang);
            int px = (int)fx, py = (int)fy;
            if (!InSlab(px, py, out _, out _)) break;
            fb.SetPixel(px, py, S(0));
            fb.SetPixel(px + 1, py + 1, S(11));
        }
    }

    static void Disc(Framebuffer fb, int cx, int cy)
    {
        for (int y = -5; y <= 5; y++)
            for (int x = -5; x <= 5; x++)
            {
                int d = x * x + y * y;
                if (d > 25) continue;
                byte c = d > 16 ? (x + y > 0 ? S(0) : S(12)) : (x + y < -2 ? S(13) : x + y > 3 ? S(4) : S(9));
                fb.SetPixel(cx + x, cy + y, c);
            }
    }

    // ---------------------------------------------------------------- font 5x7
    static readonly Dictionary<char, ulong> Glyphs = BuildGlyphs();

    static ulong Pack(string rows)
    {
        // 7 rows of 5 chars separated by spaces; bit (row*5+col)
        ulong v = 0;
        var r = rows.Split(' ');
        for (int y = 0; y < 7; y++)
            for (int x = 0; x < 5; x++)
                if (r[y][x] == '#') v |= 1UL << (y * 5 + x);
        return v;
    }

    static ulong FlipV(ulong g)
    {
        ulong o = 0;
        for (int y = 0; y < 7; y++) o |= ((g >> (y * 5)) & 0x1F) << ((6 - y) * 5);
        return o;
    }

    static Dictionary<char, ulong> BuildGlyphs()
    {
        var d = new Dictionary<char, ulong>
        {
            [' '] = 0,
            ['A'] = Pack(".###. #...# #...# ##### #...# #...# #...#"),
            ['B'] = Pack("####. #...# #...# ####. #...# #...# ####."),
            ['C'] = Pack(".###. #...# #.... #.... #.... #...# .###."),
            ['D'] = Pack("####. #...# #...# #...# #...# #...# ####."),
            ['E'] = Pack("##### #.... #.... ####. #.... #.... #####"),
            ['F'] = Pack("##### #.... #.... ####. #.... #.... #...."),
            ['G'] = Pack(".###. #...# #.... #.### #...# #...# .###."),
            ['H'] = Pack("#...# #...# #...# ##### #...# #...# #...#"),
            ['I'] = Pack(".###. ..#.. ..#.. ..#.. ..#.. ..#.. .###."),
            ['J'] = Pack("..### ...#. ...#. ...#. ...#. #..#. .##.."),
            ['K'] = Pack("#...# #..#. #.#.. ##... #.#.. #..#. #...#"),
            ['L'] = Pack("#.... #.... #.... #.... #.... #.... #####"),
            ['M'] = Pack("#...# ##.## #.#.# #.#.# #...# #...# #...#"),
            ['N'] = Pack("#...# ##..# #.#.# #..## #...# #...# #...#"),
            ['O'] = Pack(".###. #...# #...# #...# #...# #...# .###."),
            ['P'] = Pack("####. #...# #...# ####. #.... #.... #...."),
            ['Q'] = Pack(".###. #...# #...# #...# #.#.# #..#. .##.#"),
            ['R'] = Pack("####. #...# #...# ####. #.#.. #..#. #...#"),
            ['S'] = Pack(".#### #.... #.... .###. ....# ....# ####."),
            ['T'] = Pack("##### ..#.. ..#.. ..#.. ..#.. ..#.. ..#.."),
            ['U'] = Pack("#...# #...# #...# #...# #...# #...# .###."),
            ['V'] = Pack("#...# #...# #...# #...# #...# .#.#. ..#.."),
            ['W'] = Pack("#...# #...# #...# #.#.# #.#.# ##.## #...#"),
            ['X'] = Pack("#...# #...# .#.#. ..#.. .#.#. #...# #...#"),
            ['Y'] = Pack("#...# #...# .#.#. ..#.. ..#.. ..#.. ..#.."),
            ['Z'] = Pack("##### ....# ...#. ..#.. .#... #.... #####"),
            ['0'] = Pack(".###. #...# #..## #.#.# ##..# #...# .###."),
            ['1'] = Pack("..#.. .##.. ..#.. ..#.. ..#.. ..#.. .###."),
            ['2'] = Pack(".###. #...# ....# ...#. ..#.. .#... #####"),
            ['3'] = Pack("####. ....# ....# .###. ....# ....# ####."),
            ['4'] = Pack("...#. ..##. .#.#. #..#. ##### ...#. ...#."),
            ['5'] = Pack("##### #.... ####. ....# ....# #...# .###."),
            ['6'] = Pack(".###. #.... #.... ####. #...# #...# .###."),
            ['7'] = Pack("##### ....# ...#. ..#.. .#... .#... .#..."),
            ['8'] = Pack(".###. #...# #...# .###. #...# #...# .###."),
            ['9'] = Pack(".###. #...# #...# .#### ....# ....# .###."),
            ['.'] = Pack("..... ..... ..... ..... ..... .##.. .##.."),
            [','] = Pack("..... ..... ..... ..... .##.. ..#.. .#..."),
            [':'] = Pack("..... .##.. .##.. ..... .##.. .##.. ....."),
            ['-'] = Pack("..... ..... ..... ##### ..... ..... ....."),
            ['_'] = Pack("..... ..... ..... ..... ..... ..... #####"),
            ['/'] = Pack("....# ....# ...#. ..#.. .#... #.... #...."),
            ['\\'] = Pack("#.... #.... .#... ..#.. ...#. ....# ....#"),
            ['('] = Pack("...#. ..#.. .#... .#... .#... ..#.. ...#."),
            [')'] = Pack(".#... ..#.. ...#. ...#. ...#. ..#.. .#..."),
            ['!'] = Pack("..#.. ..#.. ..#.. ..#.. ..#.. ..... ..#.."),
            ['?'] = Pack(".###. #...# ....# ...#. ..#.. ..... ..#.."),
            ['%'] = Pack("##..# ##..# ...#. ..#.. .#... #..## #..##"),
            ['+'] = Pack("..... ..#.. ..#.. ##### ..#.. ..#.. ....."),
            ['\''] = Pack("..#.. ..#.. .#... ..... ..... ..... ....."),
            ['<'] = Pack("...#. ..#.. .#... #.... .#... ..#.. ...#."),
            ['>'] = Pack(".#... ..#.. ...#. ....# ...#. ..#.. .#..."),
            ['='] = Pack("..... ..... ##### ..... ##### ..... ....."),
            ['#'] = Pack(".#.#. ##### .#.#. .#.#. ##### .#.#. ....."),   // added for the ESC overlay ("Joy Sony #1")
        };
        d['¡'] = FlipV(d['!']);
        d['¿'] = FlipV(d['?']);
        return d;
    }

    public static int TextWidth(string s, int scale) => Math.Max(0, s.Length * 6 * scale - scale);

    static (char Base, int Mark) Decompose(char c) => c switch
    {
        'Á' => ('A', 1), 'É' => ('E', 1), 'Í' => ('I', 1), 'Ó' => ('O', 1), 'Ú' => ('U', 1),
        'Ñ' => ('N', 2), 'Ü' => ('U', 3),
        _ => (c, 0),
    };

    /// <summary>Glyph bitmap (bit y*5+x, 5x7) and accent mark (0 none, 1 acute, 2 tilde, 3 diaeresis) of a character, for the ESC overlay's text (same font as the launcher). Unknown characters give '?'.</summary>
    internal static ulong GlyphOf(char raw, out int mark)
    {
        var (ch, m) = Decompose(char.ToUpperInvariant(raw));
        mark = m;
        return Glyphs.TryGetValue(ch, out ulong g) ? g : Glyphs['?'];
    }

    /// <summary>RGB of palette entry <paramref name="index"/> of the launcher palette (stone / earth / fire / moss / blood ramps).</summary>
    internal static (byte R, byte G, byte B) PaletteRgb(int index)
    {
        if (_rgb is null) { var fb = new Framebuffer(1, 1); ApplyPalette(fb); _rgb = fb.Palette; }
        return _rgb[index];
    }
    static (byte R, byte G, byte B)[]? _rgb;

    internal static bool LitPx(ulong g, int x, int y) => Lit(g, x, y);

    static bool Lit(ulong g, int x, int y) => x >= 0 && x < 5 && y >= 0 && y < 7 && ((g >> (y * 5 + x)) & 1) != 0;

    /// <summary>Draws <paramref name="s"/> (upper-cased) with a chiselled look: drop shadow, light top edge and dark bottom edge on every stroke.</summary>
    public static void Text(Framebuffer fb, string s, int x, int y, int scale, byte main, byte hi, byte lo, byte shadow)
    {
        s = s.ToUpperInvariant();
        int sh = Math.Max(1, scale / 2);
        for (int pass = 0; pass < 2; pass++)
        {
            int cx = x;
            foreach (char raw in s)
            {
                var (ch, mark) = Decompose(raw);
                if (!Glyphs.TryGetValue(ch, out ulong g)) g = Glyphs['?'];
                for (int gy = 0; gy < 7; gy++)
                    for (int gx = 0; gx < 5; gx++)
                    {
                        if (!Lit(g, gx, gy)) continue;
                        int px = cx + gx * scale, py = y + gy * scale;
                        if (pass == 0) { fb.FillRect(px + sh, py + sh, scale, scale, shadow); continue; }
                        fb.FillRect(px, py, scale, scale, main);
                        if (scale >= 2)
                        {
                            if (!Lit(g, gx, gy - 1)) fb.FillRect(px, py, scale, Math.Max(1, scale / 3), hi);
                            if (!Lit(g, gx, gy + 1)) fb.FillRect(px, py + scale - Math.Max(1, scale / 3), scale, Math.Max(1, scale / 3), lo);
                        }
                    }
                if (mark != 0)
                {
                    (int mx, int my)[] pts = mark switch
                    {
                        1 => new[] { (3, -2), (2, -1) },
                        2 => new[] { (2, -2), (3, -2), (1, -1), (4, -1) },
                        _ => new[] { (1, -1), (3, -1) },
                    };
                    foreach (var (mx, my) in pts)
                    {
                        int px = cx + mx * scale, py = y + my * scale;
                        if (pass == 0) fb.FillRect(px + sh, py + sh, scale, scale, shadow);
                        else fb.FillRect(px, py, scale, scale, main);
                    }
                }
                cx += 6 * scale;
            }
        }
    }

    /// <summary>Plain stone-coloured text (light letters, dark shadow).</summary>
    public static void Text(Framebuffer fb, string s, int x, int y, int scale, byte main) =>
        Text(fb, s, x, y, scale, main, S(15), S(9), S(0));

    public static void TextCentered(Framebuffer fb, string s, int cx, int y, int scale, byte main) =>
        Text(fb, s, cx - TextWidth(s, scale) / 2, y, scale, main);

    // ---------------------------------------------------------------- widgets
    public static void Button(Framebuffer fb, int x, int y, int w, int h, string label, bool focused, bool pressed, bool enabled, int tick)
    {
        int o = pressed ? 1 : 0;
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                float n = Fbm((x + xx) / 9f, (y + yy) / 7f, 55);
                int idx = 4 + (int)(n * 5f + Dither(x + xx, y + yy)) + (focused ? 1 : 0) - (enabled ? 0 : 2);
                int e = Math.Min(Math.Min(xx, yy), Math.Min(w - 1 - xx, h - 1 - yy));
                if (e < 2)
                {
                    bool light = Math.Min(xx, yy) <= Math.Min(w - 1 - xx, h - 1 - yy);
                    if (pressed) light = !light;
                    idx = light ? 13 : 1;
                }
                if (xx + yy < 4 || (w - 1 - xx) + (h - 1 - yy) < 4) continue;   // chipped corners
                fb.SetPixel(x + xx, y + yy, S(idx));
            }
        if (focused)
        {
            // living fire outline
            for (int i = 0; i < w; i++)
            {
                int c = 9 + (int)(3.5f * MathF.Sin(i * 0.35f - tick * 0.28f)) + (int)(Rnd(i, tick / 2, 77) * 2);
                for (int t = 0; t < 2; t++)
                {
                    fb.SetPixel(x + i, y + t - 2, F(c - t * 3));
                    fb.SetPixel(x + i, y + h - 1 - t + 2, F(c - t * 3 - 1));
                }
            }
            for (int j = 0; j < h; j++)
            {
                int c = 9 + (int)(3.5f * MathF.Sin(j * 0.4f + tick * 0.25f)) + (int)(Rnd(j, tick / 2, 78) * 2);
                for (int t = 0; t < 2; t++)
                {
                    fb.SetPixel(x + t - 2, y + j, F(c - t * 3));
                    fb.SetPixel(x + w - 1 - t + 2, y + j, F(c - t * 3 - 1));
                }
            }
        }
        int tw = TextWidth(label, 2);
        byte main = enabled ? (focused ? F(14) : S(14)) : S(7);
        Text(fb, label, x + (w - tw) / 2 + o, y + (h - 14) / 2 + o, 2, main, enabled ? S(15) : S(8), enabled ? S(9) : S(5), S(0));
    }

    /// <summary>Stone trough with a lava-fire fill.</summary>
    public static void ProgressBar(Framebuffer fb, int x, int y, int w, int h, double frac, int tick)
    {
        // trough
        fb.FillRect(x - 3, y - 3, w + 6, h + 6, S(1));
        fb.FillRect(x - 2, y - 2, w + 4, h + 4, S(11));
        fb.FillRect(x - 1, y - 1, w + 2, h + 2, S(0));
        for (int yy = 0; yy < h; yy++)
            fb.FillRect(x, y + yy, w, 1, S(1 + (yy < 3 ? 0 : 1)));
        int fill = (int)Math.Round(Math.Clamp(frac, 0, 1) * w);
        for (int xx = 0; xx < fill; xx++)
        {
            float along = xx / (float)Math.Max(1, w - 1);
            float wave = MathF.Sin(xx * 0.21f - tick * 0.25f) * 1.6f + MathF.Sin(xx * 0.07f + tick * 0.11f) * 1.2f;
            for (int yy = 0; yy < h; yy++)
            {
                float v = (yy + 0.5f) / h;                       // 0 top .. 1 bottom
                float tube = 1f - MathF.Abs(v - 0.42f) * 1.9f;     // brightest slightly above the middle
                int c = (int)(4 + along * 4.5f + tube * 5f + wave + Rnd(xx, yy, 5 + tick / 3) * 1.1f);
                if (xx >= fill - 3) c += 2;                      // white-hot head
                fb.SetPixel(x + xx, y + yy, F(c));
            }
        }
        // sheen
        if (fill > 4) fb.FillRect(x + 2, y + 2, fill - 4, 1, F(14));
    }

    /// <summary>Picture frame (carved stone) around a w x h region at (x, y).</summary>
    public static void Frame(Framebuffer fb, int x, int y, int w, int h)
    {
        const int t = 5;
        for (int yy = -t; yy < h + t; yy++)
            for (int xx = -t; xx < w + t; xx++)
            {
                if (xx >= 0 && xx < w && yy >= 0 && yy < h) continue;
                int d = Math.Max(Math.Max(-xx, xx - (w - 1)), Math.Max(-yy, yy - (h - 1)));   // 1..t
                bool light = (xx < 0 && xx <= yy) || (yy < 0 && yy < xx) || (xx - (w - 1) < 0 && yy < 0);
                int idx = d <= 1 ? 1 : d == t ? (light ? 13 : 2) : 5 + (int)(Rnd(x + xx, y + yy, 66) * 3);
                if (d >= 2 && d < t) idx += light ? 2 : 0;
                fb.SetPixel(x + xx, y + yy, S(idx));
            }
    }
}
