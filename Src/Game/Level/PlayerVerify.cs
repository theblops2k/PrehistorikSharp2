using System.Security.Cryptography;
using Pre2Port.Native;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Game.Level;

/// <summary>
/// Headless run of the player scenarios compared with work/gen_player_ref.py output. Ref lines (':' separated, format documented there):
/// <c>PLAYER:&lt;scenario&gt;:level=&lt;n&gt;:ticks=&lt;n&gt;</c> and <c>P&lt;level&gt;:&lt;scenario&gt;:&lt;tick&gt;:x:y:xvel:yvel:sprite:camcol:camrow:fine:&lt;sha fb&gt;:&lt;sha pal&gt;</c>.
/// The scene is a real <see cref="LevelScene"/> (player on, no HUD) fed with the same scripted keys as the generator:
/// scripts are (keys, ticks) with keys over R/L/U/D ('-' none); keep <see cref="Scenarios"/> in sync with SCENARIOS in the generator.
/// </summary>
public static class PlayerVerify
{
    static readonly Dictionary<string, (string Keys, int Ticks)[]> Scenarios = new()
    {
        ["idle"] = new[] { ("-", 40) },
        ["walk"] = new[] { ("-", 10), ("R", 30), ("-", 20), ("L", 30), ("-", 20) },
        ["jump"] = new[] { ("-", 15), ("U", 12), ("-", 50) },
        ["jumpright"] = new[] { ("-", 10), ("RU", 14), ("R", 16), ("-", 40) },
        ["scroll"] = new[] { ("-", 8), ("R", 150), ("RU", 12), ("R", 40), ("L", 60), ("-", 30) },
        // level 7 (index 6): ice slopes (floor index 3 / 4); same scripts as gen_player_ref.py
        ["l7_slope4a"] = new[] { ("@3096,632", 0), ("-", 20), ("R", 60), ("-", 20), ("L", 60), ("RU", 14), ("R", 30), ("-", 30) },
        ["l7_slope3a"] = new[] { ("@3688,552", 0), ("-", 20), ("R", 60), ("-", 20), ("L", 60), ("RU", 14), ("L", 30), ("-", 30) },
        ["l7_slope4b"] = new[] { ("@584,1144", 0), ("-", 20), ("R", 50), ("-", 20), ("L", 70), ("LU", 14), ("L", 30), ("-", 30) },
        ["l7_slope4c"] = new[] { ("@296,1208", 0), ("-", 20), ("R", 50), ("-", 20), ("L", 70), ("RU", 14), ("R", 30), ("-", 30) },
        ["l7_slope4d"] = new[] { ("@2408,1400", 0), ("-", 20), ("L", 60), ("-", 20), ("R", 70), ("LU", 14), ("R", 30), ("-", 30) },
        // level 8 (index 7): same scripts as gen_player_ref.py
        ["l8_slope3a"] = new[] { ("@2432,172", 0), ("-", 20), ("R", 60), ("-", 20), ("L", 60), ("RU", 14), ("R", 30), ("-", 30) },
        ["l8_slope3b"] = new[] { ("@3104,92", 0), ("-", 20), ("R", 60), ("-", 20), ("L", 60), ("RU", 14), ("R", 30), ("-", 30) },
        ["l8_idx2"] = new[] { ("@2032,524", 0), ("-", 20), ("R", 60), ("-", 20), ("L", 60), ("RU", 14), ("R", 30), ("-", 30) },
        // level 8 (index 7): floor indices 3 and 2, same scripts as gen_player_ref.py
        ["l8_f3a"] = new[] { ("@3112,80", 0), ("-", 20), ("R", 50), ("-", 20), ("L", 70), ("RU", 14), ("R", 30), ("-", 30) },
        ["l8_f3b"] = new[] { ("@2456,160", 0), ("-", 20), ("R", 50), ("-", 20), ("L", 70), ("RU", 14), ("R", 30), ("-", 30) },
        ["l8_f3c"] = new[] { ("@1448,352", 0), ("-", 20), ("R", 50), ("-", 20), ("L", 70), ("RU", 14), ("R", 30), ("-", 30) },
        ["l8_f2a"] = new[] { ("@2040,512", 0), ("-", 20), ("R", 50), ("-", 20), ("L", 70), ("RU", 14), ("R", 30), ("-", 30) },
        ["l8_f2b"] = new[] { ("@3192,992", 0), ("-", 20), ("R", 50), ("-", 20), ("L", 70), ("RU", 14), ("R", 30), ("-", 30) },
        ["l8_f2c"] = new[] { ("@1816,1072", 0), ("-", 20), ("R", 50), ("-", 20), ("L", 70), ("RU", 14), ("R", 30), ("-", 30) },

    };

    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Keys held at tick <paramref name="t"/> of a script ("-" when past the end).</summary>
    static string KeysAt((string Keys, int Ticks)[] script, int t)
    {
        foreach (var (keys, n) in script)
        {
            if (t < n) return keys;
            t -= n;
        }
        return "-";
    }

    public static int Run(string bootPath, string originalDir, int level, int ticks, string[] refLines)
    {
        var byScenario = new Dictionary<string, List<string[]>>();
        foreach (string raw in refLines)
        {
            string[] p = raw.Trim().Split(':');
            if (p.Length < 13 || p[0] != $"P{level}") continue;
            if (!byScenario.TryGetValue(p[1], out var l)) byScenario[p[1]] = l = new List<string[]>();
            l.Add(p);
        }
        if (byScenario.Count == 0)
        {
            Console.WriteLine($"level: no player reference lines for P{level}");
            return 1;
        }

        byte[] boot = BootImage.Load(bootPath);
        int pass = 0, fail = 0, ipPass = 0, ipFail = 0;
        foreach (var (name, lines) in byScenario)
        {
            if (!Scenarios.TryGetValue(name, out var script))
            {
                Console.WriteLine($"FAIL P{level} {name}: unknown scenario (no input script)");
                fail++;
                continue;
            }
            var scene = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: false);
            var fb = new Framebuffer();
            var input = new Input();
            var want = lines.ToDictionary(p => int.Parse(p[2]));
            int total = script.Sum(x => x.Ticks), scPass = 0, scFail = 0;
            for (int t = 0; t < Math.Min(total, ticks); t++)
            {
                { int acc = 0; foreach (var (wk, wn) in script) { if (wk.StartsWith('@') && acc == t) { var wp = wk[1..].Split(','); scene.Warp(int.Parse(wp[0]), int.Parse(wp[1])); } acc += wn; } }   // "@x,y": warp + camera snap before this tick
                string keys = KeysAt(script, t);
                input.Set(Input.Key.Left, keys.Contains('L'), false);
                input.Set(Input.Key.Right, keys.Contains('R'), false);
                input.Set(Input.Key.Up, keys.Contains('U'), false);
                input.Set(Input.Key.Down, keys.Contains('D'), false);
                scene.Update(input);
                scene.Draw(fb);
                if (t >= 2) (ipPass, ipFail) = Add((ipPass, ipFail), CheckHalfway(scene, fb, $"P{level} {name} tick {t}", t % 9 == 0));
                if (!want.TryGetValue(t, out var w)) continue;
                LevelState s = scene.State;
                var pal = new byte[16 * 3];
                for (int i = 0; i < 16; i++) { pal[i * 3] = fb.Palette[i].R; pal[i * 3 + 1] = fb.Palette[i].G; pal[i * 3 + 2] = fb.Palette[i].B; }
                string got = $"{s.Rw(Off.PlayerX)}:{s.Rw(Off.PlayerY)}:{(short)s.Rw(Off.PlayerXvel)}:{(short)s.Rw(Off.PlayerYvel)}:{s.Rw(0x4F20):X4}:{s.Rw(Off.CamCol)}:{s.Rw(Off.CamRow)}:{s.Rb(Off.FineScroll)}";
                string exp = string.Join(":", w[3..11]);
                string gotFb = Hex(fb.Pixels), gotPal = Hex(pal);
                bool stateOk = got == exp, fbOk = gotFb == w[11] && gotPal == w[12];
                if (stateOk && fbOk) { scPass++; continue; }
                scFail++;
                Console.WriteLine($"FAIL P{level} {name} tick {t} keys={keys}: state {(stateOk ? "ok" : $"got={got} want={exp}")} fb {(fbOk ? "ok" : $"got={gotFb[..8]}/{gotPal[..8]} want={w[11][..8]}/{w[12][..8]}")}");
                if (scFail >= 5) { Console.WriteLine($"  ... {name}: stopping after 5 failures"); break; }
            }
            Console.WriteLine($"{(scFail == 0 && scPass > 0 ? "PASS" : "FAIL")} P{level} {name}: {scPass} ticks ok, {scFail} failed (of {Math.Min(total, ticks)}; ref has {lines.Count})");
            pass += scPass; fail += scFail;
            if (scPass == 0 && scFail == 0) fail++;
        }
        Console.WriteLine($"{(ipFail == 0 && ipPass > 0 ? "PASS" : "FAIL")} INTERP alpha=0.5 halfway (player+camera, logic state untouched): {ipPass} ok, {ipFail} failed; {ObjChecked} visible object slots checked");
        var (cPass, cFail) = ClockChecks(boot, originalDir, level);
        Console.WriteLine($"level: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 && ipFail == 0 && ipPass > 0 && cFail == 0 ? 0 : 1;
    }

    static int ObjChecked;

    static (int, int) Add((int P, int F) a, (int P, int F) b) => (a.P + b.P, a.F + b.F);

    static bool Halfway(int prev, int cur, int got) => Math.Abs(2 * (got - prev) - (cur - prev)) <= 1 && got >= Math.Min(prev, cur) && got <= Math.Max(prev, cur);

    /// <summary>Pose at alpha 0.5 must sit halfway (rounded px) between the poses of ticks t-1 and t; a Draw at 0.5 must not change the logic state; alpha 1 == plain draw.</summary>
    static (int, int) CheckHalfway(LevelScene scene, Framebuffer fb, string what, bool deep)
    {
        var a = scene.PrevPose; var b = scene.State is { } s ? Recovered.LevelRender.Pose.Of(s) : a; var m = scene.PoseAt(0.5);
        bool snap = Math.Abs(b.CamX - a.CamX) >= 320 || Math.Abs(b.CamY - a.CamY) >= 176 || Math.Abs(b.PX - a.PX) >= 128 || Math.Abs(b.PY - a.PY) >= 128;
        bool ok = snap || (Halfway(a.PX, b.PX, m.PX) && Halfway(a.PY, b.PY, m.PY) && Halfway(a.CamX, b.CamX, m.CamX) && Halfway(a.CamY, b.CamY, m.CamY));
        if (deep)
        {
            string before = Hex(scene.State.Data);
            if (!Recovered.LevelRender.LayersReproduceFrame(scene.State)) { Console.WriteLine($"FAIL INTERP {what}: T|(B&V) != frame"); ok = false; }
            var half = new Framebuffer();
            scene.Draw(half, 0.5);
            ok &= Hex(scene.State.Data) == before;
            var one = new Framebuffer();
            scene.Draw(one, 1.0);
            ok &= Hex(one.Pixels) == Hex(fb.Pixels);
        }
        if (!snap && scene.PrevState is { } pr)
            for (int off = Recovered.SpriteRender.ListTop; off > Recovered.SpriteRender.ListBase + Recovered.SpriteRender.RecordBytes; off -= Recovered.SpriteRender.RecordBytes)
            {
                if (scene.State.Rw(off + 4) == 0xFFFF || !Recovered.SpriteRender.InterpSlot(scene.State, pr, off, 0.5, out int ox, out int oy)) continue;
                bool hx = Halfway((short)pr.Rw(off), (short)scene.State.Rw(off), ox), hy = Halfway((short)pr.Rw(off + 2), (short)scene.State.Rw(off + 2), oy);
                ObjChecked++;
                if (!(hx && hy)) { ok = false; Console.WriteLine($"FAIL INTERP {what}: object slot {off:X4} not halfway"); }
            }
        if (!ok) Console.WriteLine($"FAIL INTERP {what}: prev={a} cur={b} half={m}");
        return ok ? (1, 0) : (0, 1);
    }

    /// <summary>Simulated render clocks (no real time): logic ticks in 1 s must be 70/3 (+-1) at every render rate, and the pose at the mid instant between two ticks must be halfway.</summary>
    static (int, int) ClockChecks(byte[] boot, string originalDir, int level)
    {
        int pass = 0, fail = 0;
        const double hz = 70.0 / 3.0;
        foreach (double fps in new[] { 60.0, 120.0, 144.0, 200.0 })
        {
            var scene = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: false);
            var input = new Input();
            input.Set(Input.Key.Right, true, false);
            int ticks = 0, midOk = 0, midBad = 0, alphaBad = 0;
            double prevAlpha = -1;
            int frames = (int)fps;   // frame f at t = f/fps, t in [0, 1 s)
            for (int f = 0; f < frames; f++)
            {
                double now = f / fps;
                long due = FrameClock.Due(now, 0, hz, ticks);
                while (due-- > 0) { scene.Update(input); ticks++; }
                double al = FrameClock.Alpha(now, 0, hz, ticks);
                if (al < 0 || al > 1) alphaBad++;
                prevAlpha = al;
                // Mid instant between tick (ticks-1) and ticks: alpha is time-derived, so it is 0.5 at (ticks-1+0.5)/hz at every render rate.
                if (ticks >= 3)
                {
                    double am = FrameClock.Alpha((ticks - 1 + 0.5) / hz, 0, hz, ticks);
                    var a = scene.PrevPose; var b = Recovered.LevelRender.Pose.Of(scene.State); var m = scene.PoseAt(am);
                    bool ok = Math.Abs(am - 0.5) < 1e-9 && Halfway(a.PX, b.PX, m.PX) && Halfway(a.PY, b.PY, m.PY) && Halfway(a.CamX, b.CamX, m.CamX) && Halfway(a.CamY, b.CamY, m.CamY);
                    if (ok) midOk++; else midBad++;
                }
            }
            bool tickOk = Math.Abs(ticks - hz) <= 1.0 + 1e-9 || Math.Abs(ticks - Math.Ceiling(hz)) <= 1;
            bool good = tickOk && midBad == 0 && midOk > 0 && alphaBad == 0;
            Console.WriteLine($"{(good ? "PASS" : "FAIL")} CLOCK {fps:0}fps: {ticks} logic ticks in 1 s (want {hz:0.00} +-1), mid-instant halfway {midOk} ok / {midBad} bad, alpha range bad {alphaBad}");
            if (good) pass++; else fail++;
        }
        return (pass, fail);
    }
}
