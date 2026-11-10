using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>
/// Port-only (FULLSCREEN=WIDESCREEN): front-end screens drawn wider than 320 without stretching. Same width criterion as the level
/// (<see cref="LevelRender.WideWidth"/> = 320 + 16 * ExtraCols from the screen proportion). The original 320x200 frame is drawn as is and
/// centred; only the side margins (WideWidth - 320) / 2 are new. Presentation only: no scene logic, timing or palette changes.
/// </summary>
public static class WideFrontEnd
{
    public enum Kind { None, EdgeExtend, Carte, Present, Menu, Flat, MapPattern, End, TheEnd, Attract }

    /// <summary>
    /// Copies the 320-wide frame <paramref name="src"/> centred into <paramref name="dst"/> (palette included) and fills each side margin
    /// with that row's edge pixel (TITUS: the backdrop around the card is one flat colour, so this is the same flat colour extended).
    /// </summary>
    public static void EdgeExtend(Framebuffer src, Framebuffer dst)
    {
        int w = dst.Width, m = (w - src.Width) / 2, sw = src.Width;
        Array.Copy(src.Palette, dst.Palette, dst.Palette.Length);
        for (int y = 0; y < dst.Height; y++)
        {
            int so = y * sw, d = y * w;
            Array.Fill(dst.Pixels, src.Pixels[so], d, m);
            Array.Copy(src.Pixels, so, dst.Pixels, d + m, sw);
            Array.Fill(dst.Pixels, src.Pixels[so + sw - 1], d + m + sw, w - m - sw);
        }
    }

    /// <summary>
    /// CARTE: the 320 frame is a window on the 640x200 master (4 planes x 0x3E80, 80 bytes per row). On screen, pixel (x, y) of the 312-px
    /// active area shows master pixel (8 * (ds - 40) + pel + x, y + 1) (page stride 40 vs master stride 80: <see cref="Carte.ScrollBlitColumn"/>
    /// writes master row r to page row r - 1 relative to the display start), black outside the master. The wide frame keeps the original
    /// 312 px exact at the centre and continues the same window into the side margins (and the 312..319 border column); master pixels
    /// beyond 0..639 stay black (the map ends there).
    /// </summary>
    public static void CarteWindow(Framebuffer src, byte[] master, int ds, int pel, Framebuffer dst)
    {
        const int ActiveWidth = 312;
        int w = dst.Width, m = (w - src.Width) / 2;
        Array.Copy(src.Palette, dst.Palette, dst.Palette.Length);
        int baseX = 8 * (ds - 40) + (pel & 7);
        for (int y = 0; y < dst.Height; y++)
        {
            int my = y + 1;
            for (int x = 0; x < w; x++)
            {
                int ox = x - m;
                byte v;
                if (ox >= 0 && ox < ActiveWidth) v = src.Pixels[y * src.Width + ox];
                else v = MasterPixel(master, baseX + ox, my);
                dst.Pixels[y * w + x] = v;
            }
        }
    }

    /// <summary>Index 0..15 of master pixel (mx, my); 0 outside 0..639 x 0..199.</summary>
    public static byte MasterPixel(byte[] master, int mx, int my)
    {
        if ((uint)mx >= 640 || (uint)my >= 200) return 0;
        int o = my * 80 + (mx >> 3), s = 7 - (mx & 7), v = 0;
        for (int p = 0; p < 4; p++)
        {
            int i = p * 0x3E80 + o;
            if (i < master.Length) v |= ((master[i] >> s) & 1) << p;
        }
        return (byte)v;
    }

    /// <summary>Usage: Pre2Port wideshot &lt;bootImage.bin&gt; &lt;originalDir&gt; &lt;outDir&gt;. PNGs of each screen at 320 and at the widescreen
    /// width for PRE2_ASPECT (default 16:9), plus a check that the CARTE window mapping reproduces the original 312 px.</summary>
    public static int Shot(string bootPath, string originalDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        if (double.TryParse(Environment.GetEnvironmentVariable("PRE2_ASPECT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double asp) && asp > 0)
            LevelRender.TargetAspect = asp;   // test tool: force the proportion (e.g. 2.4)
        byte[] boot = BootImage.Load(bootPath);
        var fa = FrontEndAssets.Load(boot, originalDir);
        var ma = MenuAssets.Load(boot, originalDir);
        var none = new Input();
        int wide = Screen.Width + 16 * LevelRender.ExtraColsFor(LevelRender.TargetAspect);
        int rc = 0;

        void Edge(string name, Framebuffer fb)
        {
            for (int side = 0; side < 2; side++)
            {
                int x = side == 0 ? 0 : fb.Width - 1;
                var set = new HashSet<byte>();
                for (int y = 0; y < fb.Height; y++) set.Add(fb.Pixels[y * fb.Width + x]);
                bool rowsFlat = true;   // every row: the 8 edge pixels equal
                for (int y = 0; y < fb.Height && rowsFlat; y++)
                    for (int k = 1; k < 8; k++)
                        if (fb.Pixels[y * fb.Width + (side == 0 ? k : fb.Width - 1 - k)] != fb.Pixels[y * fb.Width + x]) { rowsFlat = false; break; }
                Console.WriteLine($"  {name} {(side == 0 ? "left" : "right")} edge: {set.Count} distinct indices, 8-px rows flat={rowsFlat}");
            }
        }

        void Save(IScene sc, int ticks, string name)
        {
            for (int t = 0; t < ticks; t++) sc.Update(none);
            var fb = new Framebuffer();
            sc.Draw(fb);
            Launcher.LauncherVerify.WritePng(fb, Path.Combine(outDir, name + ".png"));
            Edge(name, fb);
            var wfb = new Framebuffer(wide);
            if (sc is CarteScene cs)
            {
                cs.DrawWide(fb, wfb);
                int bad = 0, pel = cs.Pel, baseX = 8 * (cs.Ds - 40) + (pel & 7);
                for (int y = 0; y < 200; y++)
                    for (int x = 0; x < 312; x++)
                        if (fb.Pixels[y * 320 + x] != MasterPixel(cs.Master, baseX + x, y + 1)) bad++;
                Console.WriteLine($"  {name} window mapping vs original 312x200: {bad} mismatches (ds={cs.Ds} pel={pel})");
                if (bad != 0) rc = 1;
            }
            else if (sc is PresentScene) WideArt.Present(fb, wfb);
            else if (sc is MenuScene) WideMenuArt.Menu(fb, wfb);
            else if (sc is MapScene ms)
            {
                ms.DrawWide(fb, wfb);
                // seam check: rows where adjacent columns differ, at the two frame seams vs the mean over every adjacent pair of the wide frame
                int m = (wfb.Width - 320) / 2, centreBad = 0;
                for (int y = 0; y < 200; y++) for (int x = 0; x < 320; x++) if (wfb.Pixels[y * wfb.Width + m + x] != fb.Pixels[y * 320 + x]) centreBad++;
                int Diff(int x) { int n = 0; for (int y = 0; y < 200; y++) if (wfb.Pixels[y * wfb.Width + x] != wfb.Pixels[y * wfb.Width + x + 1]) n++; return n; }
                double mean = Enumerable.Range(0, wfb.Width - 1).Average(Diff);
                Console.WriteLine($"  {name} wide seams: left {Diff(m - 1)}, right {Diff(m + 319)} differing rows (mean adjacent pair {mean:F1}); centre differs from the 320 frame in {centreBad} px");
                if (centreBad != 0) rc = 1;
            }
            else if (sc is OldiesScene or TheEndScene) WideMapArt.Flat(fb, wfb);
            else if (sc is AttractScene at) { if (at.ShowsMenu) WideMenuArt.Menu(fb, wfb); else WideAttractArt.Attract(fb, at.Base, wfb, at.SpriteLayer); }
            else EdgeExtend(fb, wfb);
            if (sc is TheEndScene or AttractScene)
            {
                int m = (wfb.Width - 320) / 2, centreBad = 0, ground = 0;
                for (int y = 0; y < 200; y++) for (int x = 0; x < 320; x++) if (wfb.Pixels[y * wfb.Width + m + x] != fb.Pixels[y * 320 + x]) centreBad++;
                Console.WriteLine($"  {name} wide centre differs from the 320 frame in {centreBad} px");
                if (centreBad != 0) rc = 1;
                if (sc is AttractScene at2 && !at2.ShowsMenu)
                {
                    // rows 168..199 are the period-64 strip: the margins must continue the base's own tile (same pixel 64 px apart over the whole canvas row, base only)
                    var plain = new Framebuffer(wide);   // margins without the sprites: pure rule
                    WideAttractArt.Attract(fb, at2.Base, plain);
                    for (int y = 168; y < 200; y++)
                        for (int x = 0; x < m; x++)
                            if (plain.Pixels[y * wide + x] != at2.Base[y * 320 + (((x - m) % 64) + 64) % 64]
                                || plain.Pixels[y * wide + m + 320 + x] != at2.Base[y * 320 + (320 + x) % 64]) ground++;
                    int spr = 0;
                    for (int y = 0; y < 200; y++) for (int x = 0; x < wide; x++) if ((x < m || x >= m + 320) && plain.Pixels[y * wide + x] != wfb.Pixels[y * wide + x]) spr++;
                    Console.WriteLine($"  {name} period-64 strip margins vs the base tile: {ground} px differ; sprite pixels continued through the margins: {spr}");
                    if (ground != 0) rc = 1;
                }
            }
            Launcher.LauncherVerify.WritePng(wfb, Path.Combine(outDir, name + "_wide.png"));
            Console.WriteLine($"{name}: 320x200 and {wfb.Width}x{wfb.Height}");
        }

        Save(new OldiesScene(fa), 3, "oldies");
        Save(new TitusScene(fa, false), TitusScene.Entries + 30, "titus");
        Save(new PresentScene(fa, false, true), 3000, "present");
        Save(new MenuScene(ma), 400, "menu");
        foreach (int t in new[] { 1, 60, 200, 400 }) Save(new MapScene(ma, MapKind.ModeSelect), t, $"mode_{t}");
        foreach (int t in new[] { 1, 120, 330, 700 }) Save(new CarteScene(ma, 0), t, $"carte_{t}");
        Save(new CarteScene(ma, 5), 700, "carte_l6_700");
        Save(new TheEndScene(fa), TheEndScene.Entries + 20, "theend");
        foreach (int t in new[] { 250, 330, 400, 425, 450, 480, 520, 596 }) Save(new AttractScene(ma), t, $"attract_{t}");
        {
            var bfb = new Framebuffer();
            bfb.SetPalette6(ma.AttractFadeIn[^1], 0, 0, 16);
            Array.Copy(ma.AttractImage, bfb.Pixels, ma.AttractImage.Length);
            Launcher.LauncherVerify.WritePng(bfb, Path.Combine(outDir, "attract_base.png"));
        }

        rc |= WideEnd.Shots(boot, originalDir, outDir, wide);

        // Through FrontEndRunner as the Presenter drives it (fb rebuilt when FbWidth changes); follows Settings.Current (Prehistorik2.cfg).
        var run = new FrontEndRunner(boot, originalDir);
        var rfb = new Framebuffer();
        var inp = new Input();
        string last = "";
        int titusAt = -1, carteAt = -1;
        for (int t = 0; t < 4000 && (titusAt < 0 || carteAt < 0 || t <= carteAt + 400); t++)
        {
            bool sp = (t >= 5 && t < 10) || (t >= 400 && t < 405);   // leave OLDIES; skip PRESENT (then the menu idles into the attract CARTE)
            inp.Set(Input.Key.Space, sp, sp && (t == 5 || t == 400));
            run.Update(inp);
            if (run.FbWidth != rfb.Width) rfb = new Framebuffer(run.FbWidth, run.FbHeight);
            run.Draw(rfb);
            string ph = run.Phase + "/" + run.SceneName;
            if (ph != last) { Console.WriteLine($"  runner tick {t}: {ph} fb {rfb.Width}x{rfb.Height}"); last = ph; }
            if (run.SceneName == "TITUS" && titusAt < 0) titusAt = t;
            if (run.SceneName == "CARTE" && carteAt < 0) carteAt = t;
            if (titusAt >= 0 && t == titusAt + 40) Launcher.LauncherVerify.WritePng(rfb, Path.Combine(outDir, "runner_titus.png"));
            if (carteAt >= 0 && t == carteAt + 400) Launcher.LauncherVerify.WritePng(rfb, Path.Combine(outDir, "runner_carte.png"));
        }
        Console.WriteLine($"  runner: Settings WIDESCREEN={Settings.Current.WidescreenActive}, TITUS at {titusAt}, CARTE at {carteAt}");
        rc |= FadeShots(boot, originalDir, outDir);
        return rc;
    }

    /// <summary>
    /// The demo-end fade (FadeHoldScene) through the real FrontEndRunner: (a) menu idle -> ATTRACT -> CARTE -> DEMO -> FADE -> MENU, printing the framebuffer width of every phase
    /// (the fade must keep the width of the frame it freezes and the menu that follows must be as wide); (b) a demo that reaches the tally (StartDemoFade(true), reflection).
    /// Writes demofade_* PNGs (start / middle / end of the fade) for each origin.
    /// </summary>
    static int FadeShots(byte[] boot, string originalDir, string outDir)
    {
        int rc = 0;
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var t = typeof(FrontEndRunner);
        FrontEndRunner Fresh()
        {
            var r = new FrontEndRunner(boot, originalDir);
            var a = MenuAssets.Load(boot, originalDir);
            t.GetField("_menuAssets", bf)!.SetValue(r, a);
            t.GetField("_menu", bf)!.SetValue(r, new MenuFlow(a));
            return r;
        }
        // (a) natural flow
        {
            var run = Fresh();
            var inp = new Input();
            var fb = new Framebuffer();
            string last = "";
            int fadeAt = -1, fadeLen = 0;
            for (int k = 0; k < 6000; k++)
            {
                run.Update(inp);
                if (run.FbWidth != fb.Width || run.FbHeight != fb.Height) fb = new Framebuffer(run.FbWidth, run.FbHeight);
                run.Draw(fb);
                string ph = run.Phase + "/" + run.SceneName;
                if (ph != last) { Console.WriteLine($"  demo flow tick {k}: {ph} fb {fb.Width}x{fb.Height}"); last = ph; }
                if (run.Phase == "FADE")
                {
                    if (fadeAt < 0) { fadeAt = k; Launcher.LauncherVerify.WritePng(fb, Path.Combine(outDir, "demofade_level_0.png")); }
                    fadeLen = k - fadeAt;
                    if (fadeLen == 25) Launcher.LauncherVerify.WritePng(fb, Path.Combine(outDir, "demofade_level_25.png"));
                    if (fadeLen == 50) Launcher.LauncherVerify.WritePng(fb, Path.Combine(outDir, "demofade_level_50.png"));
                }
                if (fadeAt >= 0 && run.Phase == "MENU") { Launcher.LauncherVerify.WritePng(fb, Path.Combine(outDir, "demofade_menu_after.png")); break; }
            }
            Console.WriteLine($"  demo flow: FADE at tick {fadeAt}, {fadeLen + 1} ticks; fb width at the end {fb.Width}");
            if (fadeAt < 0) rc = 1;
        }
        // (b) the demo reached the level end: tally -> fade
        {
            var run = Fresh();
            t.GetField("_demoRun", bf)!.SetValue(run, true);
            var st = WideEnd.Finished(boot, originalDir);
            var end = new Pre2Port.Game.Level.EndScene(st, originalDir, false, animated: true);
            var none = new Input();
            for (int k = 0; k < 120; k++) end.Update(none);
            t.GetField("_end", bf)!.SetValue(run, end);
            t.GetMethod("StartDemoFade", bf)!.Invoke(run, new object[] { true });
            var fb = new Framebuffer(run.FbWidth, run.FbHeight);
            for (int k = 0; k < 60; k++)
            {
                run.Update(none);
                if (run.FbWidth != fb.Width) fb = new Framebuffer(run.FbWidth, run.FbHeight);
                run.Draw(fb);
                if (k == 0 || k == 25) Launcher.LauncherVerify.WritePng(fb, Path.Combine(outDir, $"demofade_tally_{k}.png"));
            }
            Console.WriteLine($"  demo fade from the tally: phase {run.Phase}, fb {fb.Width}x{fb.Height}");
        }
        // (c) THE END through the runner: wide canvas, 320 frame identical at the centre
        {
            var run = Fresh();
            var fa = FrontEndAssets.Load(boot, originalDir);
            t.GetField("_theEnd", bf)!.SetValue(run, new TheEndScene(fa));
            var none = new Input();
            var fb = new Framebuffer(run.FbWidth, run.FbHeight);
            var narrow = new Framebuffer();
            var plain = new TheEndScene(fa);
            for (int k = 0; k < 40; k++)
            {
                run.Update(none); plain.Update(none);
                if (run.FbWidth != fb.Width) fb = new Framebuffer(run.FbWidth, run.FbHeight);
                run.Draw(fb); plain.Draw(narrow);
            }
            int m = (fb.Width - 320) / 2, bad = 0;
            for (int y = 0; y < 200; y++) for (int x = 0; x < 320; x++) if (fb.Pixels[y * fb.Width + m + x] != narrow.Pixels[y * 320 + x]) bad++;
            Console.WriteLine($"  THE END through the runner: phase {run.Phase}, fb {fb.Width}x{fb.Height}, centre differs from the 320 frame in {bad} px");
            Launcher.LauncherVerify.WritePng(fb, Path.Combine(outDir, "runner_theend.png"));
            if (bad != 0) rc = 1;
        }
        return rc;
    }
}
