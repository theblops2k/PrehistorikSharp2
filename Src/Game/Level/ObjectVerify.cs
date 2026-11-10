using System.Security.Cryptography;
using Pre2Port.Native;
using Pre2Port.Recovered;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Game.Level;

/// <summary>
/// Headless run of the object scenarios compared with work/gen_objects_ref.py output. Ref lines (':' separated, format documented there):
/// <c>OBJECTS:&lt;scenario&gt;:level=&lt;n&gt;:ticks=&lt;n&gt;</c> and
/// <c>O&lt;level&gt;:&lt;scenario&gt;:&lt;tick&gt;:px:py:camcol:camrow:&lt;slots&gt;:&lt;sha raw slots&gt;:&lt;sha spawners&gt;:&lt;sha fb&gt;:&lt;sha pal&gt;</c>.
/// The scene is a real <see cref="LevelScene"/> (player + objects on, no HUD) fed with the same scripted keys / warps as the generator: scripts are
/// (keys, ticks) with keys over R/L/U/D ('-' none) or "@x,y" (ticks 0) = player warp + camera snap; keep <see cref="Scenarios"/> in sync with SCENARIOS there.
/// </summary>
public static class ObjectVerify
{
    static readonly Dictionary<string, (string Keys, int Ticks)[]> Scenarios = new()
    {
        ["walk"] = new[] { ("-", 10), ("R", 330), ("-", 30) },
        ["back"] = new[] { ("-", 5), ("R", 200), ("L", 120), ("-", 30) },
        ["warp"] = new[] { ("@640,96", 0), ("-", 60), ("R", 30), ("-", 30), ("@1560,520", 0), ("-", 50), ("L", 20), ("-", 30),
                           ("@2780,200", 0), ("-", 60), ("@3340,500", 0), ("-", 60), ("@2990,120", 0), ("-", 40) },
        // level 4 (index 3): window of the two type-0 spawners (7F6C aura / AI 7C90); same script as gen_objects_ref.py
        ["l4_aura"] = new[] { ("@850,200", 0), ("-", 120), ("R", 40), ("-", 150), ("@900,230", 0), ("-", 150), ("L", 60), ("-", 100), ("@1000,260", 0), ("-", 200) },
        ["l4_ground"] = new[] { ("@2600,400", 0), ("-", 150), ("@2700,400", 0), ("-", 150), ("R", 60), ("-", 40), ("@1184,380", 0), ("-", 150), ("L", 50), ("-", 50), ("@900,600", 0), ("-", 120) },
        // level 5 (index 4): same scripts as gen_objects_ref.py (spawner clusters of LEVEL5.SQZ)
        ["l5_start"] = new[] { ("@300,1700", 0), ("-", 100), ("R", 60), ("-", 100), ("@600,1720", 0), ("-", 100), ("@940,1704", 0), ("-", 120) },
        ["l5_mid"] = new[] { ("@1317,1540", 0), ("-", 120), ("R", 40), ("-", 100), ("@1624,1570", 0), ("-", 150), ("@1928,1790", 0), ("-", 150), ("L", 40), ("-", 100) },
        ["l5_aura"] = new[] { ("@1534,1930", 0), ("-", 150), ("R", 60), ("-", 150), ("@2352,1880", 0), ("-", 150), ("@2639,1850", 0), ("-", 150), ("@2496,1930", 0), ("-", 100) },
        ["l5_east"] = new[] { ("@3144,1544", 0), ("-", 120), ("@3360,1584", 0), ("-", 150), ("R", 40), ("-", 100), ("@3512,1432", 0), ("-", 150), ("@3647,1430", 0), ("-", 150), ("@3768,1850", 0), ("-", 120) },
        ["l5_top"] = new[] { ("@2163,1450", 0), ("-", 150), ("@2424,1370", 0), ("-", 150), ("@2584,1146", 0), ("-", 150), ("L", 30), ("-", 60) },
        ["l5_caves"] = new[] { ("-", 5), ("=tp:17", 0), ("-", 60), ("R", 20), ("-", 30), ("=tp:18", 0), ("-", 60), ("L", 20), ("-", 30), ("=tp:19", 0), ("-", 60), ("R", 20), ("-", 30) },
        ["l5_hill"] = new[] { ("@352,330", 0), ("-", 100), ("@640,330", 0), ("-", 100), ("@304,186", 0), ("-", 100), ("@456,186", 0), ("-", 100), ("@152,122", 0), ("-", 100) },
        // level 5 in EXPERT mode ([0xB198] = 1), same script as gen_objects_ref.py
        ["l5_expert"] = new[] { ("=mode:1", 0), ("@2352,1880", 0), ("-", 150), ("R", 40), ("-", 100), ("@2496,1930", 0), ("-", 150), ("@3360,1584", 0), ("-", 150), ("@3024,1576", 0), ("-", 150),
                  ("@1215,1875", 0), ("-", 120), ("@991,1858", 0), ("-", 120) },
        // level 7 (index 6): same scripts as gen_objects_ref.py (pouncers 7A60, patrols with the 7EA7 flag, ring/gate/jitter spawners, east group, expert)
        ["l7_pounce"] = new[] { ("@392,1360", 0), ("-", 100), ("R", 40), ("-", 60), ("@648,1448", 0), ("-", 120), ("L", 40), ("-", 60), ("@340,1400", 0), ("-", 100) },

        ["l7_patrol"] = new[] { ("@544,776", 0), ("-", 100), ("@1024,896", 0), ("-", 100), ("R", 40), ("-", 60), ("@1312,944", 0), ("-", 100), ("@1728,1072", 0), ("-", 100), ("@2132,1118", 0), ("-", 100), ("@1288,1320", 0), ("-", 100), ("@120,1216", 0), ("-", 100) },

        ["l7_mid"] = new[] { ("@1904,1048", 0), ("-", 120), ("R", 40), ("-", 80), ("@2086,1091", 0), ("-", 120), ("@2280,1040", 0), ("-", 100) },

        ["l7_ring"] = new[] { ("@2616,1112", 0), ("-", 150), ("R", 50), ("-", 100), ("@2824,1168", 0), ("-", 150), ("@1104,1600", 0), ("-", 150), ("@1808,1672", 0), ("-", 150) },

        ["l7_gate"] = new[] { ("@272,688", 0), ("-", 100), ("@1176,912", 0), ("-", 100), ("@2640,1360", 0), ("-", 100), ("@840,1520", 0), ("-", 100), ("@1472,1640", 0), ("-", 100), ("@1752,1736", 0), ("-", 100), ("@3144,2000", 0), ("-", 100), ("@3464,576", 0), ("-", 100), ("@3160,1880", 0), ("-", 100) },

        ["l7_jitter"] = new[] { ("@989,1236", 0), ("-", 150), ("R", 40), ("-", 100), ("@797,1217", 0), ("-", 150) },

        ["l7_east"] = new[] { ("@3832,800", 0), ("-", 120), ("@3808,1256", 0), ("-", 150), ("@3832,1760", 0), ("-", 120), ("@3944,1608", 0), ("-", 150), ("@3928,1232", 0), ("-", 150), ("@3848,1040", 0), ("-", 100), ("@3824,1504", 0), ("-", 100) },

        ["l7_caves"] = new[] { ("-", 5), ("=tp:12", 0), ("-", 60), ("R", 20), ("-", 30), ("=tp:13", 0), ("-", 60), ("L", 20), ("-", 30), ("=tp:14", 0), ("-", 60), ("R", 20), ("-", 30), ("=tp:15", 0), ("-", 60), ("L", 20), ("-", 30), ("=tp:16", 0), ("-", 60), ("R", 20), ("-", 30), ("=tp:17", 0), ("-", 60), ("L", 20), ("-", 30), ("=tp:18", 0), ("-", 60), ("R", 20), ("-", 30), ("=tp:19", 0), ("-", 60), ("L", 20), ("-", 30) },
        ["l7_expert"] = new[] { ("=mode:1", 0), ("@2281,1046", 0), ("-", 150), ("R", 40), ("-", 100), ("@2072,1099", 0), ("-", 150), ("@1456,1280", 0), ("-", 150), ("@1182,1280", 0), ("-", 150), ("@392,1360", 0), ("-", 100) },

        // level 8 (index 7): same scripts as gen_objects_ref.py (spawner groups of LEVEL8.SQZ, expert 0x8A/0x83)
        ["l8_a"] = new[] { ("@3201,600", 0), ("-", 100), ("R", 40), ("-", 60), ("@2883,604", 0), ("-", 100), ("@3045,597", 0), ("-", 100), ("@3056,704", 0), ("-", 100) },
        ["l8_b"] = new[] { ("@3496,623", 0), ("-", 100), ("R", 40), ("-", 60), ("@3576,623", 0), ("-", 100), ("@3683,622", 0), ("-", 100), ("@3888,720", 0), ("-", 100), ("@3808,680", 0), ("-", 100) },
        ["l8_c"] = new[] { ("@3232,416", 0), ("-", 100), ("R", 40), ("-", 60), ("@3912,400", 0), ("-", 100), ("@512,240", 0), ("-", 100), ("@432,640", 0), ("-", 100) },
        ["l8_d"] = new[] { ("@3680,1280", 0), ("-", 100), ("R", 40), ("-", 60), ("@3552,1312", 0), ("-", 100), ("@3360,1280", 0), ("-", 100), ("@2328,1280", 0), ("-", 100) },
        ["l8_e"] = new[] { ("@2840,1216", 0), ("-", 100), ("R", 40), ("-", 60), ("@2776,1232", 0), ("-", 100), ("@2824,1264", 0), ("-", 100), ("@2256,1080", 0), ("-", 100), ("@2190,1266", 0), ("-", 100) },
        ["l8_f"] = new[] { ("@1792,1176", 0), ("-", 100), ("R", 40), ("-", 60), ("@2052,1174", 0), ("-", 100), ("@1088,1264", 0), ("-", 100), ("@1016,1232", 0), ("-", 100), ("@1752,1256", 0), ("-", 100), ("@1400,1288", 0), ("-", 100) },
        ["l8_expert"] = new[] { ("=mode:1", 0), ("@3096,720", 0), ("-", 150), ("R", 40), ("-", 60), ("@2192,1256", 0), ("-", 150), ("@3201,600", 0), ("-", 150), ("@2256,1080", 0), ("-", 150) },
        ["l8_caves"] = new[] { ("-", 5), ("=tp:16", 0), ("-", 60), ("R", 20), ("-", 30), ("=tp:17", 0), ("-", 60), ("L", 20), ("-", 30), ("=tp:18", 0), ("-", 60), ("R", 20), ("-", 30), ("=tp:19", 0), ("-", 60), ("L", 20), ("-", 30) },
        ["l9_a"] = new[] { ("@1080,96", 0), ("-", 100), ("R", 40), ("-", 60), ("@241,756", 0), ("-", 100), ("@244,768", 0), ("-", 100), ("@400,744", 0), ("-", 100), ("@402,752", 0), ("-", 100), ("@402,760", 0), ("-", 100) },
        ["l9_b"] = new[] { ("@834,781", 0), ("-", 100), ("R", 40), ("-", 60), ("@1384,624", 0), ("-", 100), ("@1640,752", 0), ("-", 100), ("@2560,784", 0), ("-", 100), ("@376,800", 0), ("-", 100), ("@552,832", 0), ("-", 100) },
        ["l9_c"] = new[] { ("@858,879", 0), ("-", 100), ("R", 40), ("-", 60), ("@952,1153", 0), ("-", 100), ("@1000,1152", 0), ("-", 100), ("@1055,832", 0), ("-", 100), ("@1056,1104", 0), ("-", 100), ("@1148,832", 0), ("-", 100) },
        ["l9_d"] = new[] { ("@1160,1152", 0), ("-", 100), ("R", 40), ("-", 60), ("@1176,928", 0), ("-", 100), ("@1184,832", 0), ("-", 100), ("@1360,1080", 0), ("-", 100), ("@1384,1064", 0), ("-", 100), ("@1440,1168", 0), ("-", 100) },
        ["l9_e"] = new[] { ("@1704,1072", 0), ("-", 100), ("R", 40), ("-", 60), ("@1800,1072", 0), ("-", 100), ("@2144,1120", 0), ("-", 100), ("@2366,1071", 0), ("-", 100), ("@2369,1080", 0), ("-", 100), ("@2440,1120", 0), ("-", 100) },
        ["l9_f"] = new[] { ("@2504,1071", 0), ("-", 100), ("R", 40), ("-", 60), ("@2506,1085", 0), ("-", 100), ("@880,1464", 0), ("-", 100), ("@882,1457", 0), ("-", 100), ("@936,1336", 0), ("-", 100), ("@1040,1424", 0), ("-", 100) },
        ["l9_g"] = new[] { ("@1042,1470", 0), ("-", 100), ("R", 40), ("-", 60), ("@1297,1593", 0), ("-", 100), ("@1357,1342", 0), ("-", 100), ("@1425,1209", 0), ("-", 100), ("@2104,1568", 0), ("-", 100), ("@2248,1544", 0), ("-", 100) },
        ["l9_h"] = new[] { ("@2328,1536", 0), ("-", 100), ("R", 40), ("-", 60), ("@2616,1560", 0), ("-", 100), ("@2648,1584", 0), ("-", 100), ("@2737,1566", 0), ("-", 100), ("@2752,1520", 0), ("-", 100), ("@2872,1568", 0), ("-", 100) },
        ["l9_i"] = new[] { ("@3144,1440", 0), ("-", 100), ("R", 40), ("-", 60), ("@3188,1560", 0), ("-", 100), ("@3384,1552", 0), ("-", 100), ("@3496,1592", 0), ("-", 100), ("@1336,1624", 0), ("-", 100), ("@1360,1632", 0), ("-", 100) },
        ["l9_j"] = new[] { ("@1992,1600", 0), ("-", 100), ("R", 40), ("-", 60), ("@3144,1624", 0), ("-", 100) },
    };

    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Expand a script into per-tick (keys, warp before the tick).</summary>
    static List<(string Keys, (int X, int Y)? Warp, int Mode, int Tp)> Expand((string Keys, int Ticks)[] script)
    {
        var list = new List<(string, (int, int)?, int, int)>();
        (int, int)? pending = null;
        int mode = -1, tp = -1;                                     // "=mode:N" pokes [0xB198] before the next tick (-1 = none); "=tp:N" = the state half of the cave teleport for table entry N
        foreach (var (keys, n) in script)
        {
            if (keys.StartsWith('@'))
            {
                string[] xy = keys[1..].Split(',');
                pending = (int.Parse(xy[0]), int.Parse(xy[1]));
                continue;
            }
            if (keys.StartsWith("=mode:")) { mode = int.Parse(keys[6..]); continue; }
            if (keys.StartsWith("=tp:")) { tp = int.Parse(keys[4..]); continue; }
            for (int i = 0; i < n; i++) { list.Add((keys, pending, mode, tp)); pending = null; mode = -1; tp = -1; }
        }
        return list;
    }

    /// <summary>"-" or ','-joined "slot/type/x/y/sprite hex4/state" of the live slots (same text as the generator).</summary>
    static string SlotsText(LevelState s)
    {
        var parts = new List<string>();
        for (int i = 0; i < LevelObjects.ObjCount; i++)
        {
            int b = LevelObjects.ObjBase + i * LevelObjects.ObjStride;
            int sp = s.Rw(b + 4);
            if (sp == 0xFFFF) continue;
            int typ = s.Rb(s.Rw(b + 6) + 1) & 0x7F;
            parts.Add($"{i}/{typ}/{s.Rw(b)}/{s.Rw(b + 2)}/{sp:X4}/{s.Rb(b + 0xE)}");
        }
        return parts.Count == 0 ? "-" : string.Join(",", parts);
    }

    static byte[] Slice(LevelState s, int off, int len) => s.Data.AsSpan(BootImage.DsBase + off, len).ToArray();

    public static int Run(string bootPath, string originalDir, int level, int ticks, string[] refLines)
    {
        Pre2Port.Recovered.ForegroundTiles.Disabled = true;   // the Python reference renders no foreground layer (asm 3721, verified separately by fgdump / work/fg_cross.py): compare the frames without it
        var byScenario = new Dictionary<string, List<string[]>>();
        foreach (string raw in refLines)
        {
            string[] p = raw.Trim().Split(':');
            if (p.Length < 12 || p[0] != $"O{level}") continue;
            if (!byScenario.TryGetValue(p[1], out var l)) byScenario[p[1]] = l = new List<string[]>();
            l.Add(p);
        }
        if (byScenario.Count == 0)
        {
            Console.WriteLine($"level: no object reference lines for O{level}");
            return 1;
        }

        byte[] boot = BootImage.Load(bootPath);
        int pass = 0, fail = 0;

        // Static check: the spawner list parsed from the loaded property block (level 1: 16 records, terminator 0xFF).
        var first = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: true);
        var spawners = LevelObjects.Spawners(first.State);
        Console.WriteLine($"spawners: {spawners.Count} records, types " + string.Join(",", spawners.Select(x => x.Type + (x.BonusOnly ? "*" : "")).OrderBy(x => x).Distinct()));
        if (level == 0)
        {
            bool okList = spawners.Count == 16 && spawners[0].Addr == 0x8489 && spawners[0].Type == 1 && spawners[0].X == 0x0BB6 && spawners[0].Y == 0x73
                && spawners[^1].Addr == 0x856B && spawners[^1].Type == 8 && spawners[^1].BonusOnly;
            Console.WriteLine($"{(okList ? "PASS" : "FAIL")} spawner list of level 1 (16 records 8489..856B)");
            if (okList) pass++; else fail++;
        }

        foreach (var (name, lines) in byScenario)
        {
            if (!Scenarios.TryGetValue(name, out var script))
            {
                Console.WriteLine($"FAIL O{level} {name}: unknown scenario (no input script)");
                fail++;
                continue;
            }
            var scene = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: true);
            var fb = new Framebuffer();
            var input = new Input();
            var want = lines.ToDictionary(p => int.Parse(p[2]));
            var plan = Expand(script);
            int scPass = 0, scFail = 0, maxLive = 0;
            var typesSeen = new SortedSet<int>();
            for (int t = 0; t < Math.Min(plan.Count, ticks); t++)
            {
                var (keys, warp, pokeMode, tp) = plan[t];
                if (pokeMode >= 0) scene.State.Wb(0xB198, pokeMode);
                if (tp >= 0) scene.VerifyTeleport(tp);
                if (warp is { } w0) scene.Warp(w0.X, w0.Y);
                input.Set(Input.Key.Left, keys.Contains('L'), false);
                input.Set(Input.Key.Right, keys.Contains('R'), false);
                input.Set(Input.Key.Up, keys.Contains('U'), false);
                input.Set(Input.Key.Down, keys.Contains('D'), false);
                string err = "";
                try { scene.Update(input); scene.Draw(fb); }
                catch (ObjectGapException e) { err = e.Message; }
                if (err != "")
                {
                    Console.WriteLine($"FAIL O{level} {name} tick {t}: object gap: {err}");
                    scFail++;
                    break;
                }
                if (!want.TryGetValue(t, out var w)) continue;
                LevelState s = scene.State;
                string slots = SlotsText(s);
                maxLive = Math.Max(maxLive, slots == "-" ? 0 : slots.Split(',').Length);
                if (slots != "-") foreach (string part in slots.Split(',')) typesSeen.Add(int.Parse(part.Split('/')[1]));
                var pal = new byte[16 * 3];
                for (int i = 0; i < 16; i++) { pal[i * 3] = fb.Palette[i].R; pal[i * 3 + 1] = fb.Palette[i].G; pal[i * 3 + 2] = fb.Palette[i].B; }
                if (Environment.GetEnvironmentVariable("PRE2_CAMDUMP") is string cd && name == cd.Split(',')[0] && t >= int.Parse(cd.Split(',')[1]) && t <= int.Parse(cd.Split(',')[2]))   // debug: camera-engine state of the tick (same fields as the python probe)
                    Console.WriteLine($"CAM t{t} st={s.Rb(0x91FE):X2} cur=({s.Rw(0x91FF)},{s.Rw(0x9201)}) v=({s.Rw(0x6C06):X},{s.Rw(0x6C08):X}) yv={s.Rw(0x4F2A):X} hf={s.Rb(0xA330)} tm={s.Rw(0xA3F7)} cmd={s.Rb(0xA3F9):X} sp={s.Rw(0xA401):X} trg=" + string.Join(" ", Enumerable.Range(0, 15).Select(k => $"{s.Rw(0x83F3 + k * 10):X}/{s.Rb(0x83F3 + k * 10 + 2)}x{s.Rb(0x83F3 + k * 10 + 3)}/{s.Rw(0x83F3 + k * 10 + 4):X}/{s.Rb(0x83F3 + k * 10 + 8)}")) + $" tick6BD5={s.Rb(0x6BD5)}");
                if (Environment.GetEnvironmentVariable("PRE2_MAPDUMP") is string md && md.Split(',')[0] == name && int.Parse(md.Split(',')[1]) == t)   // debug: level map segment of the tick (python probe dumps the same 0x10000 bytes)
                    File.WriteAllBytes(md.Split(',')[2], s.Data.Skip((s.Rw(Off.MapSeg) << 4) & 0xFFFFF).Take(0x10000).ToArray());
                string got = $"{s.Rw(Off.PlayerX)}:{s.Rw(Off.PlayerY)}:{s.Rw(Off.CamCol)}:{s.Rw(Off.CamRow)}:{slots}:{Hex(Slice(s, LevelObjects.ObjBase, LevelObjects.ObjCount * LevelObjects.ObjStride))}:{Hex(Slice(s, LevelObjects.ListBase, 0x800))}";
                string exp = string.Join(":", w[3..10]);
                string gotFb = Hex(fb.Pixels), gotPal = Hex(pal);
                bool stateOk = got == exp, fbOk = gotFb == w[10] && gotPal == w[11];
                if (stateOk && fbOk) { scPass++; continue; }
                scFail++;
                string[] g = got.Split(':'), e2 = exp.Split(':');
                string[] names = { "px", "py", "camcol", "camrow", "slots", "slotsSha", "spawnersSha" };
                if (!fbOk && Environment.GetEnvironmentVariable("PRE2_FBDUMP") is string dumpDir) File.WriteAllBytes(Path.Combine(dumpDir, $"cs_{name}_{t}.bin"), fb.Pixels);   // debug: failing frame (320x200 indices)
                int di = Enumerable.Range(0, Math.Min(g.Length, e2.Length)).FirstOrDefault(i => g[i] != e2[i]);
                string what = stateOk ? "ok" : $"{names[Math.Min(di, names.Length - 1)]} got={g[di]} want={e2[di]}";
                Console.WriteLine($"FAIL O{level} {name} tick {t} keys={keys}: state {what} fb {(fbOk ? "ok" : $"got={gotFb[..8]}/{gotPal[..8]} want={w[10][..8]}/{w[11][..8]}")}");
                if (scFail >= (int.TryParse(Environment.GetEnvironmentVariable("PRE2_MAXFAIL"), out var mf) ? mf : 5)) { Console.WriteLine($"  ... {name}: stopping after {scFail} failures"); break; }   // PRE2_MAXFAIL=n: debug, keep comparing past the 5th failure
            }
            Console.WriteLine($"{(scFail == 0 && scPass > 0 ? "PASS" : "FAIL")} O{level} {name}: {scPass} ticks ok, {scFail} failed (of {Math.Min(plan.Count, ticks)}; ref has {lines.Count}); max live slots {maxLive}, types seen [{string.Join(",", typesSeen)}]");
            pass += scPass; fail += scFail;
            if (scPass == 0 && scFail == 0) fail++;
        }
        Console.WriteLine($"level: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }
}
