using System.Security.Cryptography;
using Pre2Port.Native;
using Pre2Port.Recovered;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Game.Level;

/// <summary>
/// Headless run of the fire / club attack scenarios compared with work/gen_attack_ref.py output. Ref lines (':' separated, format documented there):
/// <c>ATTACK:&lt;scenario&gt;:level=&lt;n&gt;:ticks=&lt;n&gt;</c> and
/// <c>A&lt;level&gt;:&lt;scenario&gt;:&lt;tick&gt;:phase:anim_b:frame:score:enemies:collected:energy:px:py:projectiles:rng:&lt;sha fb&gt;:&lt;sha pal&gt;</c>.
/// The scene is a real <see cref="LevelScene"/> (player + objects + pickups + damage + attack, no HUD, no platforms) fed with the same scripted keys / actions as the
/// generator: scripts are (keys, ticks) with keys over R/L/U/D/F ('F' = fire = Space, '-' none) or an action with ticks 0 applied before the next tick:
/// "@x,y" = player warp + camera snap, "=rng:HEX" = LCG state [0x2CEC..0x2CF0], "=phase:N" = poke [0x7B18]. Keep <see cref="Scenarios"/> in sync with SCENARIOS there.
/// </summary>
public static class AttackVerify
{
    const string Rng0 = "=rng:12345678ab";

    static readonly Dictionary<string, (string Keys, int Ticks)[]> Scenarios = new()
    {
        ["block"] = new[] { ("-", 10), (Rng0, 0), ("@136,672", 0), ("-", 6), ("F", 3), ("-", 20), ("F", 3), ("-", 20), ("F", 3), ("-", 30) },
        ["walker"] = new[] { ("-", 10), ("R", 45), ("-", 6), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["walker2"] = new[] { ("=phase:2", 0), ("-", 10), ("R", 45), ("-", 6), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l2_enemy"] = new[] { ("-", 10), ("@40,432", 0), ("-", 8), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l2_enemy2"] = new[] { ("=phase:2", 0), ("-", 10), ("@40,432", 0), ("-", 8), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l2_walkers"] = new[] { ("-", 10), ("@296,1624", 0), ("-", 6), ("R", 45), ("-", 6), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l3_enemy"] = new[] { ("-", 10), ("@2836,598", 0), ("-", 8), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l3_enemy2"] = new[] { ("=phase:2", 0), ("-", 10), ("@2836,598", 0), ("-", 8), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l3_walkers"] = new[] { ("-", 10), ("@1580,662", 0), ("-", 6), ("R", 45), ("-", 6), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l4_patrol"] = new[] { ("-", 10), ("@1184,380", 0), ("-", 6), ("R", 45), ("-", 6), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l4_charger"] = new[] { ("-", 10), ("@2600,400", 0), ("-", 8), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l4_charger2"] = new[] { ("=phase:2", 0), ("-", 10), ("@2600,400", 0), ("-", 8), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l4_aura"] = new[] { ("-", 10), ("@850,200", 0), ("-", 100), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        // level 5 (index 4): same scripts as gen_attack_ref.py
        ["l5_patrol"] = new[] { ("-", 10), ("@287,1700", 0), ("-", 6), ("R", 45), ("-", 6), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l5_mid"] = new[] { ("-", 10), ("@1317,1540", 0), ("-", 8), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l5_mid2"] = new[] { ("=phase:2", 0), ("-", 10), ("@1317,1540", 0), ("-", 8), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l5_orbit"] = new[] { ("-", 10), ("@1624,1570", 0), ("-", 60), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l5_aura"] = new[] { ("-", 10), ("@1534,1930", 0), ("-", 100), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l5_east"] = new[] { ("-", 10), ("@3360,1584", 0), ("-", 60), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l5_x87"] = new[] { ("=mode:1", 0), ("-", 10), ("@2352,1880", 0), ("-", 60), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l5_x84"] = new[] { ("=mode:1", 0), ("-", 10), ("@3024,1576", 0), ("-", 60), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        // level 7 (index 6): same scripts as gen_attack_ref.py
        ["l7_pounce"] = new[] { ("-", 10), ("@392,1360", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },

        ["l7_pounce2"] = new[] { ("-", 10), ("@648,1448", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },

        ["l7_patrol"] = new[] { ("-", 10), ("@1024,896", 0), ("-", 6), ("R", 45), ("-", 6), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },

        ["l7_orbit"] = new[] { ("-", 10), ("@1906,1056", 0), ("-", 60), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },

        ["l7_ring"] = new[] { ("-", 10), ("@2616,1112", 0), ("-", 60), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },

        ["l7_gate"] = new[] { ("-", 10), ("@1176,912", 0), ("-", 80), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },

        ["l7_east"] = new[] { ("-", 10), ("@3808,1256", 0), ("-", 60), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },

        ["l7_x83"] = new[] { ("=mode:1", 0), ("-", 10), ("@2281,1046", 0), ("-", 60), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },

        ["l7_x88"] = new[] { ("=mode:1", 0), ("-", 10), ("@1456,1280", 0), ("-", 60), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },

        ["l7_pkill_a"] = new[] { ("-", 10), ("@392,1360", 0), ("-", 30), ("R", 2), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l7_pkill_b"] = new[] { ("-", 10), ("@424,1360", 0), ("-", 30), ("L", 2), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l7_pkill_c"] = new[] { ("-", 10), ("@488,1360", 0), ("-", 30), ("R", 2), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        // level 8 (index 7): same scripts as gen_attack_ref.py
        ["l8_a"] = new[] { ("-", 10), ("@3201,600", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_a2"] = new[] { ("-", 10), ("@3056,704", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_b"] = new[] { ("-", 10), ("@3496,623", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_b2"] = new[] { ("-", 10), ("@3888,720", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_c"] = new[] { ("-", 10), ("@3232,416", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_d"] = new[] { ("-", 10), ("@3680,1280", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_e"] = new[] { ("-", 10), ("@2840,1216", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_e2"] = new[] { ("-", 10), ("@2256,1080", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_f"] = new[] { ("-", 10), ("@1792,1176", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_f2"] = new[] { ("-", 10), ("@1016,1232", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_f3"] = new[] { ("-", 10), ("@1400,1288", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_x8a"] = new[] { ("=mode:1", 0), ("-", 10), ("@3096,720", 0), ("-", 60), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l8_x83"] = new[] { ("=mode:1", 0), ("-", 10), ("@2192,1256", 0), ("-", 60), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t1"] = new[] { ("-", 10), ("@241,756", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t1b"] = new[] { ("-", 10), ("@2504,1071", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t2"] = new[] { ("-", 10), ("@244,768", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t3"] = new[] { ("-", 10), ("@936,1336", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t4"] = new[] { ("-", 10), ("@834,781", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t4b"] = new[] { ("-", 10), ("@2369,1080", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t5"] = new[] { ("-", 10), ("@3384,1552", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t6"] = new[] { ("-", 10), ("@3188,1560", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t6b"] = new[] { ("-", 10), ("@1080,96", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t8"] = new[] { ("-", 10), ("@552,832", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t8b"] = new[] { ("-", 10), ("@2560,784", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t9"] = new[] { ("-", 10), ("@1184,832", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t9b"] = new[] { ("-", 10), ("@1640,752", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t10"] = new[] { ("-", 10), ("@376,800", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t10b"] = new[] { ("-", 10), ("@3144,1624", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t11"] = new[] { ("-", 10), ("@1357,1342", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t12"] = new[] { ("-", 10), ("@3496,1592", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["l9_t12b"] = new[] { ("-", 10), ("@1704,1072", 0), ("-", 40), ("F", 3), ("-", 25), ("F", 3), ("-", 25), ("F", 3), ("-", 40) },
        ["swing"] = new[] { ("-", 10), ("F", 2), ("-", 20), ("L", 10), ("F", 12), ("-", 20) },
    };

    const int EnergyOff = 0x27D6, ScoreLo = 0x6C0E, ScoreHi = 0x6C10, Collected = 0x2A76, RngBase = 0x2CEC, Phase = 0x7B18;
    const int ObjBase = 0x4FD0, ProjBase = 0x4F2E, Stride = 0x12;

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
                for (int i = 0; i < b.Length; i++) scene.State.Wb(RngBase + i, b[i]);
                break;
            }
            case "phase": scene.State.Wb(Phase, int.Parse(kv[1])); break;
            case "mode": scene.State.Wb(0xB198, int.Parse(kv[1])); break;
            default: throw new InvalidOperationException($"unknown poke '{action}'");
        }
    }

    static string Enemies(LevelState s)
    {
        var l = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            int b = ObjBase + i * Stride, sp = s.Rw(b + 4);
            if (sp != 0xFFFF) l.Add($"{i}/{s.Rw(b)}/{s.Rb(b + 0xF)}/{s.Rb(b + 0xE)}/{sp:X4}");
        }
        return l.Count == 0 ? "-" : string.Join(",", l);
    }

    static string Projectiles(LevelState s)
    {
        var l = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            int b = ProjBase + i * Stride, sp = s.Rw(b + 4);
            if (sp != 0xFFFF) l.Add($"{i}/{s.Rw(b)}/{s.Rw(b + 2)}/{sp:X4}");
        }
        return l.Count == 0 ? "-" : string.Join(",", l);
    }

    static string StateText(LevelState s)
    {
        string rng = string.Join(",", Enumerable.Range(0, 5).Select(i => s.Rb(RngBase + i).ToString("x2")));
        long score = (long)(s.Rw(ScoreLo) & 0xFFFF) | ((long)(s.Rw(ScoreHi) & 0xFFFF) << 16);
        return $"{s.Rb(Phase)}:{s.Rb(Player.PAnimB)}:{s.Rw(Player.PSprite):X4}:{score}:{Enemies(s)}:{s.Rw(Collected)}:{s.Rb(EnergyOff)}:{s.Rw(Off.PlayerX)}:{s.Rw(Off.PlayerY)}:{Projectiles(s)}:{rng}";
    }

    public static int Run(string bootPath, string originalDir, int level, int ticks, string[] refLines)
    {
        Pre2Port.Recovered.ForegroundTiles.Disabled = true;   // the Python reference renders no foreground layer (asm 3721, verified separately by fgdump / work/fg_cross.py): compare the frames without it
        var byScenario = new Dictionary<string, List<string[]>>();
        foreach (string raw in refLines)
        {
            string[] p = raw.Trim().Split(':');
            if (p.Length < 16 || p[0] != $"A{level}") continue;
            if (!byScenario.TryGetValue(p[1], out var l)) byScenario[p[1]] = l = new List<string[]>();
            l.Add(p);
        }
        if (byScenario.Count == 0)
        {
            Console.WriteLine($"level: no attack reference lines for A{level}");
            return 1;
        }

        byte[] boot = BootImage.Load(bootPath);
        int pass = 0, fail = 0;
        foreach (var (name, lines) in byScenario)
        {
            if (!Scenarios.TryGetValue(name, out var script))
            {
                Console.WriteLine($"FAIL A{level} {name}: unknown scenario (no input script)");
                fail++;
                continue;
            }
            var scene = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: true, damage: true, bonus: true, platforms: false, attack: true);
            var fb = new Framebuffer();
            var input = new Input();
            var want = lines.ToDictionary(p => int.Parse(p[2]));
            var plan = Expand(script);
            int scPass = 0, scFail = 0, maxScore = 0, maxCollected = 0, maxPhaseAnim = 0;
            for (int t = 0; t < Math.Min(plan.Count, ticks); t++)
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
                    Console.WriteLine($"FAIL A{level} {name} tick {t}: gap: {err}");
                    scFail++;
                    break;
                }
                LevelState s = scene.State;
                maxScore = Math.Max(maxScore, (int)((long)(s.Rw(ScoreLo) & 0xFFFF) | ((long)(s.Rw(ScoreHi) & 0xFFFF) << 16)));
                maxCollected = Math.Max(maxCollected, s.Rw(Collected));
                if (s.Rb(Player.PAnimB) is 3 or 6 or 7) maxPhaseAnim++;
                if (!want.TryGetValue(t, out var w)) continue;
                var pal = new byte[16 * 3];
                for (int i = 0; i < 16; i++) { pal[i * 3] = fb.Palette[i].R; pal[i * 3 + 1] = fb.Palette[i].G; pal[i * 3 + 2] = fb.Palette[i].B; }
                string got = StateText(s);
                string exp = string.Join(":", w[3..14]);
                string gotFb = Hex(fb.Pixels), gotPal = Hex(pal);
                bool stateOk = got == exp, fbOk = gotFb == w[14] && gotPal == w[15];
                if (stateOk && fbOk) { scPass++; continue; }
                scFail++;
                string[] g = got.Split(':'), e2 = exp.Split(':');
                string[] names = { "phase", "anim_b", "frame", "score", "enemies", "collected", "energy", "px", "py", "projectiles", "rng" };
                int di = Enumerable.Range(0, Math.Min(g.Length, e2.Length)).FirstOrDefault(i => g[i] != e2[i]);
                string what = stateOk ? "ok" : $"{names[Math.Min(di, names.Length - 1)]} got={g[di]} want={e2[di]}";
                Console.WriteLine($"FAIL A{level} {name} tick {t} keys={keys}: state {what} fb {(fbOk ? "ok" : $"got={gotFb[..8]}/{gotPal[..8]} want={w[14][..8]}/{w[15][..8]}")}");
                if (scFail >= 5) { Console.WriteLine($"  ... {name}: stopping after 5 failures"); break; }
            }
            Console.WriteLine($"{(scFail == 0 && scPass > 0 ? "PASS" : "FAIL")} A{level} {name}: {scPass} ticks ok, {scFail} failed (of {Math.Min(plan.Count, ticks)}; ref has {lines.Count}); max score {maxScore}, cells collected {maxCollected}, ticks in attack anim {maxPhaseAnim}");
            pass += scPass; fail += scFail;
            if (scPass == 0 && scFail == 0) fail++;
        }
        Console.WriteLine($"level: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }
}
