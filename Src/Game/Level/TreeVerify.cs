using System.Security.Cryptography;
using Pre2Port.Native;
using Pre2Port.Recovered;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Game.Level;

/// <summary>
/// Headless run of the level-6 tree boss scenarios (LEVEL6.SQZ, level index 5, 1030:6D34..70D6) compared with work/gen_tree_ref.py output (Python native stack, tick_level6_boss).
/// Ref lines (':' separated): <c>TREE:&lt;scenario&gt;:level=&lt;n&gt;:ticks=&lt;n&gt;</c> and
/// <c>TB&lt;level&gt;:&lt;scenario&gt;:&lt;tick&gt;:boss bytes:rocks 7DAF:render records 55EE:targets 5648:health row:player:end:pool:poolsha:weapons:rng:&lt;sha fb&gt;:&lt;sha pal&gt;</c>.
/// The scene is a real <see cref="LevelScene"/> (player + objects + pickups + damage + attack, no HUD, no platforms) fed with the same scripted keys / actions as the generator
/// ("=tp" = <see cref="LevelScene.VerifyTeleport"/> of the cave table entry 19 = the arena entry). Keep <see cref="Scenarios"/> in sync with SCENARIOS there.
/// The state fields decide PASS/FAIL; the framebuffer / palette hashes are counted separately (a mismatch there can come from anything drawn, not from the boss).
/// </summary>
public static class TreeVerify
{
    const string Rng0 = "=rng:12345678ab";
    static readonly (string, int) Tp = ("=tp", 0);

    static readonly Dictionary<string, (string Keys, int Ticks)[]> Scenarios = new()
    {
        ["idle"] = new[] { ("=energy:30", 0), Tp, (Rng0, 0), ("-", 400) },
        ["walk"] = new[] { ("=energy:30", 0), Tp, ("=rng:0102030405", 0), ("-", 10), ("R", 60), ("-", 40), ("L", 60), ("-", 60), ("R", 40), ("-", 40) },
        ["jump"] = new[] { ("=energy:30", 0), Tp, ("=rng:0a0b0c0d0e", 0), ("-", 6), ("R", 6), ("U", 4), ("-", 20), ("R", 30), ("U", 4), ("-", 40), ("L", 20), ("U", 4), ("-", 60) },
        ["proj1"] = new[] { ("=energy:30", 0), Tp, (Rng0, 0), ("-", 30), ("=projT", 0), ("-", 1), ("=clr", 0), ("-", 40) },
        ["club1"] = new[] { ("=energy:30", 0), Tp, (Rng0, 0), ("-", 30), ("=clubT", 0), ("-", 1), ("=clr", 0), ("-", 10), ("=projT", 0), ("-", 1), ("=clr", 0), ("-", 40) },
        ["miss"] = new[] { ("=energy:30", 0), Tp, (Rng0, 0), ("-", 20), ("=proj:300,1900", 0), ("-", 1), ("=clr", 0), ("-", 30) },
        ["phase"] = new[] { ("=energy:30", 0), Tp, (Rng0, 0), ("-", 20), ("=hits:1", 0), ("=projT", 0), ("-", 1), ("=clr", 0), ("-", 30),
                            ("=hits:1", 0), ("=projT", 0), ("-", 1), ("=clr", 0), ("-", 30), ("=projT", 0), ("-", 1), ("=clr", 0), ("-", 30) },
        ["kill"] = new[] { ("=energy:30", 0), Tp, (Rng0, 0), ("-", 20), ("=hits:1", 0), ("=projT", 0), ("-", 1), ("=clr", 0), ("-", 12),
                           ("=hits:1", 0), ("=projT", 0), ("-", 1), ("=clr", 0), ("-", 12), ("=hits:1", 0), ("=projT", 0), ("-", 1), ("=clr", 0), ("-", 80) },
        ["late"] = new[] { ("=energy:30", 0), Tp, ("=rng:aabbccddee", 0), ("=phase:2", 0), ("=hits:7", 0), ("-", 30), ("=projT", 0), ("-", 1), ("=clr", 0), ("-", 20),
                           ("R", 30), ("-", 40), ("L", 30), ("-", 80) },
        ["run"] = new[] { ("=energy:60", 0), Tp, ("=rng:0102030405", 0), ("-", 4), ("R", 130), ("-", 30), ("L", 80), ("-", 40) },
        ["run2"] = new[] { ("=energy:60", 0), Tp, ("=rng:0f0e0d0c0b", 0), ("R", 40), ("U", 4), ("R", 14), ("U", 4), ("R", 14), ("U", 4), ("R", 14), ("U", 4), ("R", 20), ("-", 40), ("U", 4), ("-", 40) },
        ["run3"] = new[] { ("=energy:60", 0), Tp, ("=rng:aa55aa55aa", 0), ("-", 150), ("R", 70), ("-", 10), ("R", 50), ("U", 4), ("R", 25), ("-", 40) },
        ["crush"] = new[] { ("=energy:60", 0), Tp, ("=rng:0102030405", 0), ("-", 3), ("@925,1984", 0), ("=timer:0", 0), ("-", 6), ("@940,1950", 0), ("=timer:0", 0), ("-", 6),
                            ("@925,1984", 0), ("=timer:3", 0), ("-", 8), ("@925,1984", 0), ("=timer:0", 0), ("=stun:3", 0), ("-", 6) },
        ["crush2"] = new[] { ("=energy:2", 0), Tp, ("=rng:0102030405", 0), ("-", 3), ("@925,1984", 0), ("=timer:0", 0), ("-", 6) },
        ["die"] = new[] { ("=energy:0", 0), Tp, ("=rng:1122334455", 0), ("-", 300) },
    };

    const int RngBase = 0x2CEC, ProjBase = 0x4F2E, Pool = 0x50A8, PoolCount = 0x20, Stride = 0x12, Targets = 0x5648, Phase = 0xA326, EffectRow = 0x56A2;
    const int TeleportEntry = 19;

    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static List<(string Keys, List<string> Actions)> Expand((string Keys, int Ticks)[] script)
    {
        var list = new List<(string, List<string>)>();
        var pending = new List<string>();
        foreach (var (keys, n) in script)
        {
            if (keys.StartsWith('=') || keys.StartsWith('@')) { pending.Add(keys); continue; }
            for (int i = 0; i < n; i++) { list.Add((keys, pending)); pending = new List<string>(); }
        }
        return list;
    }

    static (int X, int Y) TargetPos(LevelState s)
    {
        int si = Targets + 0x24 * (s.Rw(Phase) & 0xFF);
        return (s.Rw(si), s.Rw(si + 2));
    }

    static void Apply(LevelScene scene, string action)
    {
        var s = scene.State;
        scene.VerifyFlush();                                           // the generator pokes AFTER the previous tick's end-of-frame half
        if (action[0] == '@')
        {
            string[] wxy = action[1..].Split(',');
            scene.Warp(int.Parse(wxy[0]), int.Parse(wxy[1]));
            return;
        }
        string[] kv = action[1..].Split(':');
        switch (kv[0])
        {
            case "tp": scene.VerifyTeleport(TeleportEntry); break;
            case "rng":
            {
                byte[] b = Convert.FromHexString(kv[1]);
                for (int i = 0; i < b.Length; i++) s.Wb(RngBase + i, b[i]);
                break;
            }
            case "energy": s.Wb(0x27D6, int.Parse(kv[1])); break;
            case "hits": s.Wb(TreeBoss.Hits, int.Parse(kv[1])); break;
            case "timer": s.Wb(TreeBoss.Timer, int.Parse(kv[1])); break;
            case "stun": s.Wb(TreeBoss.Stun, int.Parse(kv[1])); break;
            case "phase": s.Ww(Phase, int.Parse(kv[1])); break;
            case "proj":
            case "projT":
            {
                int x, y;
                if (kv[0] == "proj") { string[] xy = kv[1].Split(','); x = int.Parse(xy[0]); y = int.Parse(xy[1]); }
                else (x, y) = TargetPos(s);
                s.Ww(ProjBase, x); s.Ww(ProjBase + 2, y); s.Ww(ProjBase + 4, 0x2043);
                s.Ww(ProjBase + 6, 0); s.Ww(ProjBase + 0xE, 0); s.Wb(ProjBase + 8, 0);
                break;
            }
            case "clubT": { var (x, y) = TargetPos(s); scene.PokeClub(x, y); break; }
            case "clr":
                for (int i = 0; i < 4; i++) s.Ww(ProjBase + i * Stride + 4, 0xFFFF);
                s.Ww(0x4F0E, 0xFFFF);
                break;
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

    static string Recs(LevelState s, int baseAddr)
    {
        var l = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            int b = baseAddr + i * Stride, sp = s.Rw(b + 4);
            if (sp != 0xFFFF) l.Add($"{i}/{s.Rw(b)}/{s.Rw(b + 2)}/{sp:X4}");
        }
        return l.Count == 0 ? "-" : string.Join(",", l);
    }

    static string Rocks(LevelState s)
    {
        var l = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            int b = 0x7DAF + i * 0xB, sp = s.Rw(b + 4);
            if (sp != 0xFFFF) l.Add($"{i}/{s.Rw(b)}/{s.Rw(b + 2)}/{sp:X4}/{s.Rw(b + 6)}/{s.Rw(b + 8)}/{s.Rb(b + 0xA)}");
        }
        return l.Count == 0 ? "-" : string.Join(",", l);
    }

    static string Weapons(LevelState s)
    {
        var l = new List<string>();
        if (s.Rw(0x4F0A + 4) != 0xFFFF) l.Add($"C/{s.Rw(0x4F0A)}/{s.Rw(0x4F0C)}/{s.Rw(0x4F0E):X4}");
        for (int i = 0; i < 4; i++)
        {
            int b = ProjBase + i * Stride, sp = s.Rw(b + 4);
            if (sp != 0xFFFF) l.Add($"{i}/{s.Rw(b)}/{s.Rw(b + 2)}/{sp:X4}");
        }
        return l.Count == 0 ? "-" : string.Join(",", l);
    }

    static readonly string[] Names = { "boss", "rocks", "renderRecs", "targets", "row", "player", "end", "pool", "poolsha", "weapons", "rng" };

    static string[] StateFields(LevelState s)
    {
        var raw = new byte[PoolCount * Stride];
        Array.Copy(s.Data, BootImage.DsBase + Pool, raw, 0, raw.Length);
        string rng = string.Join(",", Enumerable.Range(0, 5).Select(i => s.Rb(RngBase + i).ToString("x2")));
        int row = Enumerable.Range(0, 8).Count(i => s.Rw(EffectRow + i * Stride + 4) != 0xFFFF);
        string boss = $"{s.Rb(TreeBoss.Stun):X2}/{s.Rb(TreeBoss.Hits):X2}/{s.Rw(Phase):X4}/{s.Rb(TreeBoss.Anim):X2}/{s.Rb(TreeBoss.SubB):X2}/{s.Rb(TreeBoss.SubA):X2}/{s.Rb(TreeBoss.Timer):X2}/{s.Rb(TreeBoss.Reseed):X2}";
        string player = $"{s.Rb(0x27D6)}/{s.Rb(0x27D8)}/{s.Rw(Off.PlayerX)}/{s.Rw(Off.PlayerY)}/{s.Rw(0x4F22)}/{s.Rw(0x4F2A)}/{s.Rb(0x4F2D):X2}/{s.Rb(0x6BE4)}/{s.Rb(0x4F24)}/{s.Rb(0x6BEA)}";
        return new[] { boss, Rocks(s), Recs(s, 0x55EE), Recs(s, Targets), row.ToString(), player, $"{s.Rb(0x6BE6)}", PoolText(s), Hex(raw), Weapons(s), rng };
    }

    public static int Run(string bootPath, string originalDir, int level, int ticks, string[] refLines)
    {
        var byScenario = new Dictionary<string, List<string[]>>();
        foreach (string raw in refLines)
        {
            string[] p = raw.Trim().Split(':');
            if (p.Length < 16 || p[0] != $"TB{level}") continue;
            if (!byScenario.TryGetValue(p[1], out var l)) byScenario[p[1]] = l = new List<string[]>();
            l.Add(p);
        }
        if (byScenario.Count == 0)
        {
            Console.WriteLine($"level: no tree-boss reference lines for TB{level}");
            return 1;
        }

        byte[] boot = BootImage.Load(bootPath);
        int pass = 0, fail = 0, fbBad = 0;
        foreach (var (name, lines) in byScenario)
        {
            if (!Scenarios.TryGetValue(name, out var script))
            {
                Console.WriteLine($"FAIL TB{level} {name}: unknown scenario (no input script)");
                fail++;
                continue;
            }
            var scene = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: true, damage: true, bonus: true, platforms: false, attack: true) { HoldEnd = true };
            var fb = new Framebuffer();
            var input = new Input();
            var want = lines.ToDictionary(p => int.Parse(p[2]));
            int last = want.Keys.Max();
            var plan = Expand(script);
            int scPass = 0, scFail = 0, scFb = 0, maxPhase = 0, minHits = 99, rockTicks = 0, hurt = 0, energy0 = int.MaxValue;
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
                    Console.WriteLine($"FAIL TB{level} {name} tick {t}: gap: {err}");
                    scFail++;
                    break;
                }
                LevelState s = scene.State;
                maxPhase = Math.Max(maxPhase, s.Rw(Phase));
                minHits = Math.Min(minHits, s.Rb(TreeBoss.Hits));
                if (s.Rb(0x27D6) < energy0) { if (energy0 != int.MaxValue) hurt++; energy0 = s.Rb(0x27D6); }
                for (int i = 0; i < 5; i++) if (s.Rw(0x7DAF + i * 0xB + 4) != 0xFFFF) rockTicks++;
                if (Environment.GetEnvironmentVariable("TREE_DUMP") is string dump && (dump == $"{name}:{t}" || dump == $"{name}:*"))   // debug: the 12 object slots of this tick (same dump as gen_tree_ref.py)
                {
                    Console.WriteLine($"tick {t}");
                    for (int k = 0; k < 12; k++)
                    {
                        int b = 0x4FD0 + k * Stride;
                        if (s.Rw(b + 4) != 0xFFFF) Console.WriteLine($"   slot {k} x {s.Rw(b)} y {s.Rw(b + 2)} spr 0x{s.Rw(b + 4):x} def 0x{s.Rw(b + 6):x} xv {s.Rw(b + 8)} yv {s.Rw(b + 0xA)} st {s.Rb(b + 0xE)}");
                    }
                    Console.WriteLine("   rec8691 " + string.Join(" ", Enumerable.Range(0, 14).Select(i => s.Rb(0x8691 + i).ToString("X2"))) + " rec8683 " + string.Join(" ", Enumerable.Range(0, 14).Select(i => s.Rb(0x8683 + i).ToString("X2"))));
                    Console.WriteLine($"   player {s.Rw(Off.PlayerX)} {s.Rw(Off.PlayerY)}");
                }
                if (!want.TryGetValue(t, out var w)) continue;
                string[] got = StateFields(s), exp = w[3..14];
                int di = -1;
                for (int i = 0; i < got.Length; i++) if (got[i] != exp[i]) { di = i; break; }
                var pal = new byte[16 * 3];
                for (int i = 0; i < 16; i++) { pal[i * 3] = fb.Palette[i].R; pal[i * 3 + 1] = fb.Palette[i].G; pal[i * 3 + 2] = fb.Palette[i].B; }
                string gotFb = Hex(fb.Pixels), gotPal = Hex(pal);
                if (Environment.GetEnvironmentVariable("TREE_FBAT") == $"{name}:{t}") File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "cs_fb.bin"), fb.Pixels);
                bool fbOk = gotFb == w[14] && gotPal == w[15];
                if (!fbOk) { scFb++; if (scFb <= 3) Console.WriteLine($"   fb/pal differ {name} tick {t} keys={keys} (state equal): fb {(gotFb == w[14] ? "same" : "differs")} pal {(gotPal == w[15] ? "same" : "differs")}"); }
                if (di < 0) { scPass++; continue; }
                scFail++;
                Console.WriteLine($"FAIL TB{level} {name} tick {t} keys={keys}: state {Names[di]} got={got[di]} want={exp[di]}");
                if (scFail >= 5) { Console.WriteLine($"  ... {name}: stopping after 5 failures"); break; }
            }
            Console.WriteLine($"{(scFail == 0 && scPass > 0 ? "PASS" : "FAIL")} TB{level} {name}: {scPass} ticks ok, {scFail} failed (ref has {lines.Count}); fb/pal hash mismatches {scFb}; max phase {maxPhase}, min hits {minHits}, rock-slot-ticks {rockTicks}, energy drops {hurt}");
            pass += scPass; fail += scFail; fbBad += scFb;
            if (scPass == 0 && scFail == 0) fail++;
        }
        Console.WriteLine($"fb/pal hash mismatches (informational): {fbBad}");
        Console.WriteLine($"level: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }
}
