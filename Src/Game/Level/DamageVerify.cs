using System.Security.Cryptography;
using Pre2Port.Native;
using Pre2Port.Recovered;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Game.Level;

/// <summary>
/// Headless run of the damage / death / respawn scenarios compared with work/gen_damage_ref.py output. Ref lines (':' separated, format documented there):
/// <c>DAMAGE:&lt;scenario&gt;:level=&lt;n&gt;:ticks=&lt;n&gt;</c> and
/// <c>D&lt;level&gt;:&lt;scenario&gt;:&lt;tick&gt;:energy:lives:respawn:px:py:death_state:end_signal:&lt;sha fb&gt;:&lt;sha pal&gt;</c>.
/// The scene is a real <see cref="LevelScene"/> (player + objects + damage on, no HUD) fed with the same scripted keys / actions as the generator:
/// scripts are (keys, ticks) with keys over R/L/U/D ('-' none) or an action with ticks 0, applied before the next tick: "@x,y" = player warp + camera
/// snap, "=lives:N" / "=energy:N" / "=lowgravity:N" = poke [0x27D8] / [0x27D6] / [0x6BC7]. Keep <see cref="Scenarios"/> in sync with SCENARIOS there.
/// </summary>
public static class DamageVerify
{
    static readonly Dictionary<string, (string Keys, int Ticks)[]> Scenarios = new()
    {
        ["contact"] = new[] { ("-", 10), ("R", 330), ("-", 60) },
        ["hurt"] = new[] { ("-", 10), ("R", 64), ("U", 12), ("R", 80), ("-", 30) },
        ["bump"] = new[] { ("-", 44), ("@402,500", 0), ("-", 56), ("@316,580", 0), ("-", 10), ("=lowgravity:1", 0), ("-", 15) },
        ["respawn"] = new[] { ("-", 10), ("=energy:0", 0), ("R", 200), ("-", 100) },
        ["l2_contact"] = new[] { ("-", 10), ("@40,432", 0), ("-", 60), ("R", 40), ("-", 40) },
        ["l2_hurt"] = new[] { ("-", 10), ("@296,1624", 0), ("-", 6), ("R", 60), ("U", 12), ("R", 60), ("-", 30) },
        ["l2_respawn"] = new[] { ("-", 10), ("=energy:0", 0), ("@40,432", 0), ("R", 80), ("-", 100) },
        ["l3_contact"] = new[] { ("-", 10), ("@2836,598", 0), ("-", 60), ("R", 40), ("-", 40) },
        ["l3_hurt"] = new[] { ("-", 10), ("@1580,662", 0), ("-", 6), ("R", 60), ("U", 12), ("R", 60), ("-", 30) },
        ["l3_respawn"] = new[] { ("-", 10), ("=energy:0", 0), ("@1580,662", 0), ("R", 80), ("-", 100) },
        ["l4_hurt"] = new[] { ("-", 10), ("@1184,380", 0), ("-", 6), ("R", 60), ("U", 12), ("R", 60), ("-", 30) },
        ["l4_charger"] = new[] { ("-", 10), ("@2600,400", 0), ("-", 80), ("R", 40), ("-", 60) },
        ["l4_aura"] = new[] { ("-", 10), ("@850,200", 0), ("-", 120), ("R", 40), ("-", 80) },
        ["l4_respawn"] = new[] { ("-", 10), ("=energy:0", 0), ("@2600,400", 0), ("R", 80), ("-", 100) },
        // level 5 (index 4): same scripts as gen_damage_ref.py
        ["l5_hurt"] = new[] { ("-", 10), ("@287,1700", 0), ("-", 6), ("R", 60), ("U", 12), ("R", 60), ("-", 30) },
        ["l5_contact"] = new[] { ("-", 10), ("@1317,1540", 0), ("-", 80), ("R", 40), ("-", 60) },
        ["l5_orbit"] = new[] { ("-", 10), ("@1624,1570", 0), ("-", 120), ("R", 40), ("-", 80) },
        ["l5_aura"] = new[] { ("-", 10), ("@1534,1930", 0), ("-", 120), ("R", 40), ("-", 80) },
        ["l5_east"] = new[] { ("-", 10), ("@3360,1584", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l5_respawn"] = new[] { ("-", 10), ("=energy:0", 0), ("@1317,1540", 0), ("R", 80), ("-", 100) },
        ["l5_xhurt"] = new[] { ("=mode:1", 0), ("-", 10), ("@2352,1880", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l5_xorbit"] = new[] { ("=mode:1", 0), ("-", 10), ("@3024,1576", 0), ("-", 100), ("R", 40), ("-", 60) },
        // level 7 (index 6): same scripts as gen_damage_ref.py
        ["l7_pounce"] = new[] { ("-", 10), ("@392,1360", 0), ("-", 100), ("R", 40), ("-", 60) },

        ["l7_pounce2"] = new[] { ("-", 10), ("@648,1448", 0), ("-", 100), ("L", 40), ("-", 60) },

        ["l7_hurt"] = new[] { ("-", 10), ("@1024,896", 0), ("-", 6), ("R", 60), ("U", 12), ("R", 60), ("-", 30) },

        ["l7_orbit"] = new[] { ("-", 10), ("@1906,1056", 0), ("-", 120), ("R", 40), ("-", 80) },

        ["l7_ring"] = new[] { ("-", 10), ("@2616,1112", 0), ("-", 120), ("R", 40), ("-", 80) },

        ["l7_gate"] = new[] { ("-", 10), ("@1176,912", 0), ("-", 120), ("R", 40), ("-", 80) },

        ["l7_east"] = new[] { ("-", 10), ("@3808,1256", 0), ("-", 100), ("R", 40), ("-", 60) },

        ["l7_respawn"] = new[] { ("-", 10), ("=energy:0", 0), ("@392,1360", 0), ("R", 80), ("-", 100) },

        ["l7_xhurt"] = new[] { ("=mode:1", 0), ("-", 10), ("@2281,1046", 0), ("-", 100), ("R", 40), ("-", 60) },

        ["l7_xtall"] = new[] { ("=mode:1", 0), ("-", 10), ("@1456,1280", 0), ("-", 100), ("R", 40), ("-", 60) },

        // level 8 (index 7): same scripts as gen_damage_ref.py
        ["l8_a"] = new[] { ("-", 10), ("@3201,600", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l8_a2"] = new[] { ("-", 10), ("@3056,704", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l8_b"] = new[] { ("-", 10), ("@3496,623", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l8_b2"] = new[] { ("-", 10), ("@3888,720", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l8_c"] = new[] { ("-", 10), ("@3232,416", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l8_d"] = new[] { ("-", 10), ("@3680,1280", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l8_e"] = new[] { ("-", 10), ("@2840,1216", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l8_e2"] = new[] { ("-", 10), ("@2256,1080", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l8_f"] = new[] { ("-", 10), ("@1792,1176", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l8_f2"] = new[] { ("-", 10), ("@1016,1232", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l8_respawn"] = new[] { ("-", 10), ("=energy:0", 0), ("@3201,600", 0), ("R", 80), ("-", 100) },
        ["l8_x8a"] = new[] { ("=mode:1", 0), ("-", 10), ("@3096,720", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l8_x83"] = new[] { ("=mode:1", 0), ("-", 10), ("@2192,1256", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t1"] = new[] { ("-", 10), ("@241,756", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t1b"] = new[] { ("-", 10), ("@2504,1071", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t2"] = new[] { ("-", 10), ("@244,768", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t3"] = new[] { ("-", 10), ("@936,1336", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t4"] = new[] { ("-", 10), ("@834,781", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t4b"] = new[] { ("-", 10), ("@2369,1080", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t5"] = new[] { ("-", 10), ("@3384,1552", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t6"] = new[] { ("-", 10), ("@3188,1560", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t6b"] = new[] { ("-", 10), ("@1080,96", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t8"] = new[] { ("-", 10), ("@552,832", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t8b"] = new[] { ("-", 10), ("@2560,784", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t9"] = new[] { ("-", 10), ("@1184,832", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t9b"] = new[] { ("-", 10), ("@1640,752", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t10"] = new[] { ("-", 10), ("@376,800", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t10b"] = new[] { ("-", 10), ("@3144,1624", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t11"] = new[] { ("-", 10), ("@1357,1342", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t12"] = new[] { ("-", 10), ("@3496,1592", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_t12b"] = new[] { ("-", 10), ("@1704,1072", 0), ("-", 100), ("R", 40), ("-", 60) },
        ["l9_respawn"] = new[] { ("-", 10), ("=energy:0", 0), ("@241,756", 0), ("R", 80), ("-", 100) },
        ["gameover"] = new[] { ("=lives:0", 0), ("-", 10), ("=energy:0", 0), ("R", 200), ("-", 60) },
    };

    const int Lives = 0x27D8, EnergyOff = 0x27D6, LowGravity = 0x6BC7, RespawnState = 0x6BE4, EndSignal = 0x6BE5;

    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Expand a script into per-tick (keys, actions before the tick).</summary>
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
        int v = int.Parse(kv[1]);
        switch (kv[0])
        {
            case "lives": scene.State.Wb(Lives, v); break;
            case "energy": scene.State.Wb(EnergyOff, v); break;
            case "lowgravity": scene.State.Wb(LowGravity, v); break;
            case "mode": scene.State.Wb(0xB198, v); break;
            default: throw new InvalidOperationException($"unknown poke '{action}'");
        }
    }

    public static int Run(string bootPath, string originalDir, int level, int ticks, string[] refLines)
    {
        Pre2Port.Recovered.ForegroundTiles.Disabled = true;   // the Python reference renders no foreground layer (asm 3721, verified separately by fgdump / work/fg_cross.py): compare the frames without it
        var byScenario = new Dictionary<string, List<string[]>>();
        foreach (string raw in refLines)
        {
            string[] p = raw.Trim().Split(':');
            if (p.Length < 12 || p[0] != $"D{level}") continue;
            if (!byScenario.TryGetValue(p[1], out var l)) byScenario[p[1]] = l = new List<string[]>();
            l.Add(p);
        }
        if (byScenario.Count == 0)
        {
            Console.WriteLine($"level: no damage reference lines for D{level}");
            return 1;
        }

        byte[] boot = BootImage.Load(bootPath);
        int pass = 0, fail = 0;
        foreach (var (name, lines) in byScenario)
        {
            if (!Scenarios.TryGetValue(name, out var script))
            {
                Console.WriteLine($"FAIL D{level} {name}: unknown scenario (no input script)");
                fail++;
                continue;
            }
            var scene = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: true, damage: true);
            var fb = new Framebuffer();
            var input = new Input();
            var want = lines.ToDictionary(p => int.Parse(p[2]));
            var plan = Expand(script);
            int scPass = 0, scFail = 0, minEnergy = 9, minLives = 9, lifeLosses = 0, prevLives = 9;
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
                    scene.Update(input);
                    scene.Draw(fb);
                }
                catch (ObjectGapException e) { err = e.Message; }
                catch (NotSupportedException e) { err = e.Message; }
                if (err != "")
                {
                    Console.WriteLine($"FAIL D{level} {name} tick {t}: gap: {err}");
                    scFail++;
                    break;
                }
                LevelState s = scene.State;
                if (s.Rb(Lives) < prevLives && prevLives != 9) lifeLosses++;
                prevLives = s.Rb(Lives);
                minEnergy = Math.Min(minEnergy, s.Rb(EnergyOff));
                minLives = Math.Min(minLives, s.Rb(Lives));
                if (!want.TryGetValue(t, out var w)) continue;
                var pal = new byte[16 * 3];
                for (int i = 0; i < 16; i++) { pal[i * 3] = fb.Palette[i].R; pal[i * 3 + 1] = fb.Palette[i].G; pal[i * 3 + 2] = fb.Palette[i].B; }
                string got = $"{s.Rb(EnergyOff)}:{s.Rb(Lives)}:{s.Rb(RespawnState)}:{s.Rw(Off.PlayerX)}:{s.Rw(Off.PlayerY)}:{s.Rb(Player.PDeath)}:{s.Rb(EndSignal)}";
                string exp = string.Join(":", w[3..10]);
                string gotFb = Hex(fb.Pixels), gotPal = Hex(pal);
                bool stateOk = got == exp, fbOk = gotFb == w[10] && gotPal == w[11];
                if (stateOk && fbOk) { scPass++; continue; }
                scFail++;
                string[] g = got.Split(':'), e2 = exp.Split(':');
                string[] names = { "energy", "lives", "respawn", "px", "py", "death", "end" };
                int di = Enumerable.Range(0, Math.Min(g.Length, e2.Length)).FirstOrDefault(i => g[i] != e2[i]);
                string what = stateOk ? "ok" : $"{names[Math.Min(di, names.Length - 1)]} got={g[di]} want={e2[di]}";
                Console.WriteLine($"FAIL D{level} {name} tick {t} keys={keys}: state {what} fb {(fbOk ? "ok" : $"got={gotFb[..8]}/{gotPal[..8]} want={w[10][..8]}/{w[11][..8]}")}");
                if (scFail >= 5) { Console.WriteLine($"  ... {name}: stopping after 5 failures"); break; }
            }
            Console.WriteLine($"{(scFail == 0 && scPass > 0 ? "PASS" : "FAIL")} D{level} {name}: {scPass} ticks ok, {scFail} failed (of {Math.Min(plan.Count, ticks)}; ref has {lines.Count}); min energy {minEnergy}, min lives {minLives}, life losses {lifeLosses}");
            pass += scPass; fail += scFail;
            if (scPass == 0 && scFail == 0) fail++;
        }
        Console.WriteLine($"level: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }
}
