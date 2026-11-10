using Pre2Port.Game.Level;
using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>
/// Port-only (FULLSCREEN=WIDESCREEN, presentation only): the two end-of-level screens drawn wider than 320 without stretching.
/// The 320x200 frame the original draws is copied as is, centred; only the side margins (m left, W - 320 - m right) are new.
/// Neither screen has a wider source and neither needs a reference image: the margins come from RULES.
/// <list type="bullet">
/// <item><b>TALLY</b> (TallyScene.Draw): black page (every plane byte 0 = palette index 0) + the SCORE / LEVEL COMPLETED text + sprite records.
/// Margin = index 0, plus the sprite records seen through the margins: the cutscene walks the caveman off to the right and slides the food in
/// from x = 0x168 / out to x = -0x34, so a sprite that crosses the 320 frame edge keeps moving into the margin instead of being cut at an
/// invisible line. Done by re-running the original sprite pass (SpriteRender.DrawAll) with every record shifted by a constant dx (records
/// restored afterwards) and copying only the margin columns. The text stays in the centred 320 frame.</item>
/// <item><b>GAME OVER</b> (GameOverScene.Draw / EndScreens.DrawGameOver): the diorama (GAMEOVER.SQZ, 4 planes of 320x200, window of 176 rows
/// scrolled up from black) is a picture whose edge columns are not flat. Rule, same as the TITUS/PRESENT/MENU margins: the diorama is split in
/// bands of rows, each band mirrors a strip of its own edge (WideArt style, no image data). The letters / caveman / birds stay centred over
/// it (their X is inside 16..300, never near the edges); the status bar rows (176..199) repeat the bar's edge column like the level HUD.</item>
/// </list>
/// The palette of the 320 frame (level palette / 0xAFE8 + fades) is copied unchanged: every margin pixel is an index of the 320 frame.
/// </summary>
public static class WideEnd
{
    static byte[][]? _pl;
    static Framebuffer? _scratch;

    /// <summary>Copies the 320 frame centred into <paramref name="dst"/> (palette included); returns the left margin m.</summary>
    static int Centre(Framebuffer src, Framebuffer dst)
    {
        int w = dst.Width, sw = src.Width, m = (w - sw) / 2;
        Array.Copy(src.Palette, dst.Palette, dst.Palette.Length);
        for (int y = 0; y < dst.Height; y++) Array.Copy(src.Pixels, y * sw, dst.Pixels, y * w + m, sw);
        return m;
    }

    /// <summary>
    /// GAME OVER, widescreen: rows 0..175 (diorama, letters, birds) = <see cref="WideFrontEnd.EdgeExtend"/> (centred); the status-bar rows (176..199) get the
    /// level's own widescreen treatment (LevelRender.DrawWide): the 320-px bar + HUD at x = 0 and the extra columns on the right repeat the bar's edge column.
    /// </summary>
    public static void GameOver(Framebuffer narrow, Framebuffer wide)
    {
        WideFrontEnd.EdgeExtend(narrow, wide);
        int W = wide.Width, B = narrow.Width;
        for (int y = LevelRender.ViewportRows; y < Screen.Height; y++)
        {
            Buffer.BlockCopy(narrow.Pixels, y * B, wide.Pixels, y * W, B);
            Array.Fill(wide.Pixels, narrow.Pixels[y * B + B - 1], y * W + B, W - B);
        }
    }

    /// <summary>Level tally: 320 frame centred, black margins, sprite records continued through the margins (see the class summary).</summary>
    public static void Tally(LevelState s, Framebuffer narrow, Framebuffer dst, bool sprites = true, LevelState? prev = null, double alpha = 1.0)
    {
        int w = dst.Width, sw = narrow.Width, m = Centre(narrow, dst);
        for (int y = 0; y < dst.Height; y++)
        {
            Array.Fill(dst.Pixels, (byte)0, y * w, m);
            Array.Fill(dst.Pixels, (byte)0, y * w + m + sw, w - m - sw);
        }
        if (sprites) SpriteMargins(s, dst, m, prev, alpha);
    }

    /// <summary>Sprite records through the margin columns [0, m) and [m + 320, W): one DrawAll per chunk of at most 320 columns, records shifted by dx (positions interpolated like the centred frame).</summary>
    static void SpriteMargins(LevelState s, Framebuffer dst, int m, LevelState? prev, double alpha)
    {
        int w = dst.Width, sw = Screen.Width;
        _pl ??= EndScreens.NewPlanes();
        _scratch ??= new Framebuffer();
        for (int side = 0; side < 2; side++)
        {
            int from = side == 0 ? 0 : m + sw, to = side == 0 ? m : w;
            for (int c0 = from; c0 < to; c0 += sw)
            {
                int n = Math.Min(sw, to - c0), dx = m - c0;     // pass column j shows world x = j - dx = (c0 + j) - m
                foreach (var p in _pl) Array.Clear(p);
                SpriteRender.DrawAll(s, prev, alpha, _pl, 0, dx);
                EndScreens.ToFramebuffer(_pl, 0, _scratch);
                for (int y = 0; y < Screen.Height; y++)
                    for (int j = 0; j < n; j++)
                    {
                        byte v = _scratch.Pixels[y * sw + j];
                        if (v != 0) dst.Pixels[y * w + c0 + j] = v;
                    }
            }
        }
    }

    /// <summary>Test tool (wideshot): a level-1 state at its end, to feed the end scenes without a window.</summary>
    internal static LevelState Finished(byte[] boot, string originalDir)
    {
        var scene = new LevelScene(boot, originalDir, 0, hud: false, player: true, objects: true, damage: true, bonus: true, attack: true) { HoldEnd = true };
        var input = new Input();
        scene.Update(input);
        scene.Warp(4068, 574);
        for (int k = 0; k < 0x46; k++)
            if (scene.State.Rw(0x8F1D + k * 7 + 4) == 0x116) scene.State.Ww(0x8F1D + k * 7 + 4, 0x117);
        for (int i = 0; i < 200 && scene.Outcome == LevelScene.EndKind.None; i++) scene.Update(input);
        var st = scene.State;
        st.Wb(0x6C12 + 0, 3); st.Wb(0x6C12 + 2, 2); st.Ww(0x6C9E, 5);
        return st;
    }

    /// <summary>Usage from WideFrontEnd.Shot: PNGs of GAME OVER and TALLY at 320 and at the wide width; returns non-zero if a centre differs from the 320 frame.</summary>
    internal static int Shots(byte[] boot, string originalDir, string outDir, int wide)
    {
        int rc = 0;
        var none = new Input();
        void Save(string name, Framebuffer fb, Framebuffer wfb, Action<Framebuffer, Framebuffer> draw)
        {
            Launcher.LauncherVerify.WritePng(fb, Path.Combine(outDir, name + ".png"));
            draw(fb, wfb);
            int m = (wfb.Width - 320) / 2, bad = 0, wb = wfb.Width;
            bool gover = name.StartsWith("gameover");
            for (int y = 0; y < 200; y++)
            {
                // GAME OVER: rows 176+ (status bar) sit at x = 0 and repeat the edge column on the right, like LevelRender.DrawWide; the rest is centred
                bool hudRow = gover && y >= LevelRender.ViewportRows;
                for (int x = 0; x < 320; x++) if (wfb.Pixels[y * wb + (hudRow ? 0 : m) + x] != fb.Pixels[y * 320 + x]) bad++;
                if (hudRow) for (int x = 320; x < wb; x++) if (wfb.Pixels[y * wb + x] != fb.Pixels[y * 320 + 319]) bad++;
            }
            Launcher.LauncherVerify.WritePng(wfb, Path.Combine(outDir, name + "_wide.png"));
            Console.WriteLine($"{name}: 320x200 and {wfb.Width}x{wfb.Height}; centre differs from the 320 frame in {bad} px" + (bad != 0 && name.StartsWith("tally") ? " (sprites over the centre)" : ""));
            if (bad != 0 && name.StartsWith("gameover")) rc = 1;
        }
        // GAME OVER: the rule (per-row edge pixel) is only valid if the diorama edge columns are flat; measure that on every tick of the scene.
        {
            var st0 = Finished(boot, originalDir);
            var end0 = new EndScene(st0, originalDir, true, animated: true);
            var f0 = new Framebuffer();
            int nonFlat = 0, frames = 0;
            for (int i = 0; i < EndScene.HoldTicks; i++)
            {
                end0.Update(none); end0.Draw(f0); frames++;
                for (int y = 0; y < 176; y++)
                    for (int k = 0; k < 8; k++)
                        if (f0.Pixels[y * 320 + k] != f0.Pixels[y * 320] || f0.Pixels[y * 320 + 319 - k] != f0.Pixels[y * 320 + 319]) nonFlat++;
            }
            Console.WriteLine($"  gameover edge check over {frames} ticks: {nonFlat} px in the 8-px edge columns of rows 0..175 differ from the row's edge pixel");
            if (nonFlat != 0) rc = 1;
        }
        foreach (int t in new[] { 20, 100, 200 })
        {
            var st = Finished(boot, originalDir);
            var end = new EndScene(st, originalDir, true, animated: true);
            for (int i = 0; i < t; i++) end.Update(none);
            var fb = new Framebuffer(); end.Draw(fb);
            Save($"gameover_{t}", fb, new Framebuffer(wide), (n, d) => end.DrawWide(new Framebuffer(), d));
        }
        // Level in WIDESCREEN (same HUD path as GAME OVER's bar): PNG for a visual comparison of the HUD band.
        {
            var lvl = new LevelScene(boot, originalDir, 0, damage: true, bonus: true, platforms: true, attack: true);
            for (int i = 0; i < 30; i++) lvl.Update(none);
            var lfb = new Framebuffer(wide);
            lvl.Draw(lfb);
            Launcher.LauncherVerify.WritePng(lfb, Path.Combine(outDir, "level_wide.png"));
            Console.WriteLine($"level_wide: {lfb.Width}x{lfb.Height}");
        }
        // TALLY: walk-in, food slide-in, count-up, walk-off (the cutscene is long; one PNG per phase of interest).
        {
            var st = Finished(boot, originalDir);
            var end = new EndScene(st, originalDir, false, animated: true);
            var fb = new Framebuffer();
            var marks = new HashSet<int> { 5, 40, 90, 140, 200, 245, 260, 268, 272 };
            int t = 0, saved = 0, hashBad = 0;
            while (!end.Done && t < 3000)
            {
                end.Update(none); t++;
                if (end.Done) break;
                bool mark = marks.Contains(t) || t % 25 == 0;
                if (!mark) continue;
                end.Draw(fb);
                var wfb = new Framebuffer(wide);
                int before = Hash(st);
                end.DrawWide(new Framebuffer(), wfb);
                if (Hash(st) != before) hashBad++;
                int m = (wide - 320) / 2, spriteBad = 0;
                for (int y = 0; y < 200; y++) for (int x = 0; x < 320; x++) if (wfb.Pixels[y * wide + m + x] != fb.Pixels[y * 320 + x]) spriteBad++;
                if (spriteBad != 0) rc = 1;
                if (marks.Contains(t) && saved < 12)
                {
                    Launcher.LauncherVerify.WritePng(fb, Path.Combine(outDir, $"tally_{t}.png"));
                    Launcher.LauncherVerify.WritePng(wfb, Path.Combine(outDir, $"tally_{t}_wide.png"));
                    saved++;
                    Console.WriteLine($"tally_{t}: 320x200 and {wfb.Width}x{wfb.Height}; centre differs in {spriteBad} px");
                }
            }
            Console.WriteLine($"  tally: {t} ticks, state changed by DrawWide in {hashBad} frames");
            if (hashBad != 0) rc = 1;
        }
        return rc;
    }

    static int Hash(LevelState s)
    {
        int h = 17;
        for (int off = SpriteRender.ListBase; off <= SpriteRender.ListTop; off += SpriteRender.RecordBytes)
            for (int i = 0; i < SpriteRender.RecordBytes; i++) h = h * 31 + s.Rb(off + i);
        return h;
    }
}
