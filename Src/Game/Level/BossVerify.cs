using System.Security.Cryptography;
using Pre2Port.Native;
using Pre2Port.Recovered;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Game.Level;

/// <summary>
/// Headless run of the mode-9 last-boss scenarios (LEVELA.SQZ, level index 9) compared with work/gen_boss_ref.py output. Ref lines (':' separated, format documented there):
/// <c>BOSS:&lt;scenario&gt;:level=&lt;n&gt;:ticks=&lt;n&gt;</c> and
/// <c>BS&lt;level&gt;:&lt;scenario&gt;:&lt;tick&gt;:A517:A4F7:dwell:cadence:health:energy:lives:px:py:end:pool:poolsha:proj:rng:&lt;sha fb&gt;:&lt;sha pal&gt;</c>.
/// The scene is a real <see cref="LevelScene"/> (player + objects + pickups + damage + attack, no HUD, no platforms) fed with the same scripted keys / actions as the generator:
/// keys over R/L/U/D/F or an action with ticks 0 applied before the next tick: "@x,y" warp, "=rng:HEX", "=proj:x,y" (thrown-weapon record 0x4F2E[0]), "=clr", "=hp:N", "=energy:N".
/// Keep <see cref="Scenarios"/> in sync with SCENARIOS there. A scenario whose last ref tick has the level-end mode set ([0x6BE6] != 0) is checked up to that tick; on that tick
/// only the boss/player fields up to <c>end</c> are compared (the C# 4C69 handler already ran the end-of-frame half, the generator reads the state before it).
/// </summary>
public static class BossVerify
{
    const string Rng0 = "=rng:12345678ab";

    static readonly Dictionary<string, (string Keys, int Ticks)[]> Scenarios = new()
    {
        ["idle"] = new[] { (Rng0, 0), ("-", 260) },
        ["walk"] = new[] { ("=rng:0102030405", 0), ("-", 20), ("R", 40), ("-", 60), ("L", 40), ("-", 100) },
        ["hit1"] = new[] { (Rng0, 0), ("-", 30), ("=proj:200,50", 0), ("-", 1), ("=clr", 0), ("-", 60) },
        ["hit4"] = new[] { (Rng0, 0), ("-", 20), ("=proj:200,50", 0), ("-", 1), ("=clr", 0), ("-", 6), ("=proj:190,70", 0), ("-", 1), ("=clr", 0), ("-", 6),
                           ("=proj:230,35", 0), ("-", 1), ("=clr", 0), ("-", 6), ("=proj:210,60", 0), ("-", 1), ("=clr", 0), ("-", 6),
                           ("=proj:100,50", 0), ("-", 1), ("=clr", 0), ("-", 30) },
        ["kill"] = new[] { (Rng0, 0), ("-", 12), ("=hp:2", 0), ("=proj:200,50", 0), ("-", 1), ("=clr", 0), ("-", 6), ("=proj:200,50", 0), ("-", 1), ("=clr", 0), ("-", 40) },
        ["victory"] = new[] { (Rng0, 0), ("-", 12), ("=hp:1", 0), ("=proj:200,50", 0), ("-", 1), ("=clr", 0), ("-", 14), ("@150,160", 0), ("-", 60), ("@190,150", 0), ("-", 60),
                              ("@230,150", 0), ("-", 80) },
    };

    const int RngBase = 0x2CEC, ProjBase = 0x4F2E, Pool = 0x50A8, PoolCount = 0x20, Stride = 0x12;

    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static List<(string Keys, List<string> Actions)> Expand((string Keys, int Ticks)[] script)
    {
        var list = new List<(string, List<string>)>();
        var pending = new List<string>();
        foreach (var (keys, n) in script)
        {
            if (keys.StartsWith('@') || keys.StartsWith('=')) { pending.Add(keys); continue; }
            for (int i = 0; i < n; i++) { list.Add((keys, pending)); pending = new List<string>(); }
        }
        return list;
    }

    static void Apply(LevelScene scene, string action)
    {
        var s = scene.State;
        if (action[0] == '@')
        {
            string[] xy = action[1..].Split(',');
            scene.Warp(int.Parse(xy[0]), int.Parse(xy[1]));
            return;
        }
        string[] kv = action[1..].Split(':');
        switch (kv[0])
        {
            case "rng":
            {
                byte[] b = Convert.FromHexString(kv[1]);
                for (int i = 0; i < b.Length; i++) s.Wb(RngBase + i, b[i]);
                break;
            }
            case "proj":
            {
                string[] xy = kv[1].Split(',');
                s.Ww(ProjBase, int.Parse(xy[0])); s.Ww(ProjBase + 2, int.Parse(xy[1])); s.Ww(ProjBase + 4, 0x2043);
                s.Ww(ProjBase + 6, 0); s.Ww(ProjBase + 0xE, 0); s.Wb(ProjBase + 8, 0);
                break;
            }
            case "clr": for (int i = 0; i < 4; i++) s.Ww(ProjBase + i * Stride + 4, 0xFFFF); break;
            case "hp": s.Ww(Boss.Health, int.Parse(kv[1])); break;
            case "energy": s.Wb(0x27D6, int.Parse(kv[1])); break;
            default: throw new InvalidOperationException($"unknown poke '{action}'");
        }
    }

    static string PoolText(LevelState s)
    {
        var l = new List<string>();
        for (int i = 0; i < PoolCount && l.Count < 24; i++)
        {
            int b = Pool + i * Stride, sp = s.Rw(b + 4);
            if (sp != 0xFFFF) l.Add($"{i}/{s.Rw(b)}/{s.Rw(b + 2)}/{sp:X4}/{s.Rw(b + 0xC)}");
        }
        return l.Count == 0 ? "-" : string.Join(",", l);
    }

    static string ProjText(LevelState s)
    {
        var l = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            int b = ProjBase + i * Stride, sp = s.Rw(b + 4);
            if (sp != 0xFFFF) l.Add($"{i}/{s.Rw(b)}/{s.Rw(b + 2)}/{sp:X4}");
        }
        return l.Count == 0 ? "-" : string.Join(",", l);
    }

    static readonly string[] Names = { "A517", "A4F7", "dwell", "cadence", "health", "energy", "lives", "px", "py", "end", "pool", "poolsha", "proj", "rng" };

    static string[] StateFields(LevelState s)
    {
        var raw = new byte[PoolCount * Stride];
        Array.Copy(s.Data, BootImage.DsBase + Pool, raw, 0, raw.Length);
        string rng = string.Join(",", Enumerable.Range(0, 5).Select(i => s.Rb(RngBase + i).ToString("x2")));
        return new[]
        {
            $"{s.Rw(Boss.Script):X4}", $"{s.Rw(Boss.Table):X4}", $"{s.Rb(Boss.Dwell)}", $"{s.Rb(Boss.Cadence)}", $"{s.Rw(Boss.Health)}",
            $"{s.Rb(0x27D6)}", $"{s.Rb(0x27D8)}", $"{s.Rw(Off.PlayerX)}", $"{s.Rw(Off.PlayerY)}", $"{s.Rb(0x6BE6)}", PoolText(s), Hex(raw), ProjText(s), rng,
        };
    }

    public static int Run(string bootPath, string originalDir, int level, int ticks, string[] refLines)
    {
        var byScenario = new Dictionary<string, List<string[]>>();
        foreach (string raw in refLines)
        {
            string[] p = raw.Trim().Split(':');
            if (p.Length < 19 || p[0] != $"BS{level}") continue;
            if (!byScenario.TryGetValue(p[1], out var l)) byScenario[p[1]] = l = new List<string[]>();
            l.Add(p);
        }
        if (byScenario.Count == 0)
        {
            Console.WriteLine($"level: no boss reference lines for BS{level}");
            return 1;
        }

        byte[] boot = BootImage.Load(bootPath);
        int pass = 0, fail = 0;
        foreach (var (name, lines) in byScenario)
        {
            if (!Scenarios.TryGetValue(name, out var script))
            {
                Console.WriteLine($"FAIL BS{level} {name}: unknown scenario (no input script)");
                fail++;
                continue;
            }
            var scene = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: true, damage: true, bonus: true, platforms: false, attack: true) { HoldEnd = true };
            var fb = new Framebuffer();
            var input = new Input();
            var want = lines.ToDictionary(p => int.Parse(p[2]));
            int last = want.Keys.Max();
            bool endsWithWarp = lines[^1][12] != "0";
            var plan = Expand(script);
            int scPass = 0, scFail = 0, bolts = 0, hitsOnPlayer = 0, minHealth = 99, energy0 = 3;
            for (int t = 0; t < Math.Min(Math.Min(plan.Count, ticks), last + 1); t++)
            {
                var (keys, actions) = plan[t];
                string err = "";
                try
                {
                    foreach (string a in actions) Apply(scene, a);
                    input.Set(Input.Key.Left, keys.Contains('L'), false);
                    input.Set(Input.Key.Right, keys.Contains('R'), false);
                    input.Set(Input.Key.Up, keys.Contains('U'), false);
                    input.Set(Input.Key.Down, keys.Contains('D'), false);
                    input.Set(Input.Key.Space, keys.Contains('F'), false);
                    scene.Update(input);
                    scene.Draw(fb);
                }
                catch (ObjectGapException e) { err = e.Message; }
                catch (NotSupportedException e) { err = e.Message; }
                if (err != "")
                {
                    Console.WriteLine($"FAIL BS{level} {name} tick {t}: gap: {err}");
                    scFail++;
                    break;
                }
                LevelState s = scene.State;
                minHealth = Math.Min(minHealth, s.Rw(Boss.Health));
                if (s.Rb(0x27D6) < energy0) { hitsOnPlayer++; energy0 = s.Rb(0x27D6); }
                for (int i = 0; i < PoolCount; i++) if ((s.Rw(Pool + i * Stride + 4) & 0x1FFF) is 0x1CA or 0x1CB) bolts++;
                if (!want.TryGetValue(t, out var w)) continue;
                string[] got = StateFields(s), exp = w[3..17];
                bool finalWarpTick = endsWithWarp && t == last;
                int cmpLen = finalWarpTick ? 10 : got.Length;               // up to and including "end"
                int di = -1;
                for (int i = 0; i < cmpLen; i++) if (got[i] != exp[i]) { di = i; break; }
                bool stateOk = di < 0, fbOk = true;
                string gotFb = "", gotPal = "";
                if (!finalWarpTick)
                {
                    var pal = new byte[16 * 3];
                    for (int i = 0; i < 16; i++) { pal[i * 3] = fb.Palette[i].R; pal[i * 3 + 1] = fb.Palette[i].G; pal[i * 3 + 2] = fb.Palette[i].B; }
                    gotFb = Hex(fb.Pixels); gotPal = Hex(pal);
                    fbOk = gotFb == w[17] && gotPal == w[18];
                }
                if (stateOk && fbOk) { scPass++; continue; }
                scFail++;
                string what = stateOk ? "ok" : $"{Names[di]} got={got[di]} want={exp[di]}";
                Console.WriteLine($"FAIL BS{level} {name} tick {t} keys={keys}: state {what} fb {(fbOk ? "ok" : $"got={gotFb[..8]}/{gotPal[..8]} want={w[17][..8]}/{w[18][..8]}")}");
                if (scFail >= 5) { Console.WriteLine($"  ... {name}: stopping after 5 failures"); break; }
            }
            Console.WriteLine($"{(scFail == 0 && scPass > 0 ? "PASS" : "FAIL")} BS{level} {name}: {scPass} ticks ok, {scFail} failed (ref has {lines.Count}); min boss health {minHealth}, bolt-ticks {bolts}, energy drops {hitsOnPlayer}");
            pass += scPass; fail += scFail;
            if (scPass == 0 && scFail == 0) fail++;
        }
        Console.WriteLine($"level: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }
}
