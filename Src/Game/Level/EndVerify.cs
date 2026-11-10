using System.Security.Cryptography;
using Pre2Port.Native;
using Pre2Port.Recovered;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Game.Level;

/// <summary>
/// Headless run of the end scenarios compared with work/gen_end_ref.py. Ref lines (':' separated, format documented there):
/// <c>E&lt;level&gt;:&lt;scenario&gt;:&lt;tick&gt;:&lt;G|T|O&gt;:level:mode:signal:lives:score:px:py:&lt;sha fb|-&gt;:&lt;sha pal|-&gt;</c>.
/// The scene is the one the runner builds (player + objects + damage + bonus + attack, HoldEnd on); when <see cref="LevelScene.Outcome"/> is set an
/// <see cref="EndScene"/> is drawn for the remaining ref ticks. Keep <see cref="Scenarios"/> in sync with SCENARIOS there.
/// </summary>
public static class EndVerify
{
    static readonly Dictionary<string, (string Keys, int Ticks)[]> Scenarios = new()
    {
        ["levelend"] = new[] { ("-", 10), ("=reveal", 0), ("@4068,574", 0), ("-", 80) },
        ["gameover"] = new[] { ("=lives:0", 0), ("-", 10), ("=energy:0", 0), ("R", 200), ("-", 60) },
    };

    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static string Fields(LevelState s)
        => $"{s.Rb(Off.Level)}:{s.Rb(0x6BE6)}:{s.Rb(0x6BE5)}:{s.Rb(0x27D8)}:{(uint)s.Rw(0x6C0E) | ((uint)s.Rw(0x6C10) << 16)}:{s.Rw(Off.PlayerX)}:{s.Rw(Off.PlayerY)}";

    static void Apply(LevelScene scene, string a)
    {
        if (a[0] == '@') { string[] xy = a[1..].Split(','); scene.Warp(int.Parse(xy[0]), int.Parse(xy[1])); return; }
        if (a == "=reveal")                                           // effect source 0x116 -> 0x117 (what the lighter pickup does)
        {
            for (int k = 0; k < 0x46; k++)
                if (scene.State.Rw(0x8F1D + k * 7 + 4) == 0x116) scene.State.Ww(0x8F1D + k * 7 + 4, 0x117);
            return;
        }
        string[] kv = a[1..].Split(':');
        switch (kv[0])
        {
            case "lives": scene.State.Wb(0x27D8, int.Parse(kv[1])); break;
            case "energy": scene.State.Wb(0x27D6, int.Parse(kv[1])); break;
            default: throw new InvalidOperationException($"unknown poke '{a}'");
        }
    }

    sealed class SpyAudio : Pre2Port.Game.Frontend.IGameAudio
    {
        public readonly List<(int Frame, int Id)> Sfx = new();
        public int Frame;
        public void LoadSfxBank(string file) { }
        public void LoadSong(string file) { }
        public void PlaySfx(int id) => Sfx.Add((Frame, id));
    }

    /// <summary>
    /// Headless probe of the animated tally (not a reference comparison): ends level <paramref name="level"/> like the "levelend" scenario, queues
    /// <paramref name="items"/> collected food items ([0x6C12] counts + [0x6C9E]), runs the <see cref="EndScene"/> cutscene to its end and prints the phase lengths,
    /// the score before/after against an independent sum of the ASM tables (0xA375 index lut, 0xA353 score words), the sfx and frame hashes.
    /// </summary>
    public static int TallyProbe(string bootPath, string originalDir, int level, string items)
    {
        byte[] boot = BootImage.Load(bootPath);
        var scene = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: true, damage: true, bonus: true, attack: true) { HoldEnd = true };
        var input = new Input();
        scene.Update(input);
        scene.Warp(4068, 574);
        for (int k = 0; k < 0x46; k++)
            if (scene.State.Rw(0x8F1D + k * 7 + 4) == 0x116) scene.State.Ww(0x8F1D + k * 7 + 4, 0x117);
        for (int i = 0; i < 200 && scene.Outcome == LevelScene.EndKind.None; i++) scene.Update(input);
        if (scene.Outcome != LevelScene.EndKind.LevelEnd) { Console.WriteLine($"FAIL: the level did not end (outcome {scene.Outcome})"); return 1; }
        var st = scene.State;
        long expect = 0; int total = 0;
        if (items != "-")
            foreach (string it in items.Split(','))                     // idx:count
            {
                string[] ic = it.Split(':');
                int idx = int.Parse(ic[0], System.Globalization.NumberStyles.HexNumber), n = int.Parse(ic[1]);
                st.Wb(0x6C12 + idx, n); total += n;
                expect += (long)n * st.Rw(0xA353 + st.Rb(0xA375 + idx) * 2);
            }
        st.Ww(0x6C9E, total);
        if (Environment.GetEnvironmentVariable("PRE2_LIGHTSOFF") == "1") { st.Wb(0x6C04, 1); st.Wb(0x6C01, 0); st.Wb(0x6C02, 0); st.Wb(0x6C03, 0); }   // diagnostic: end with the lights off
        uint score0 = EndScreens.Score(st);
        Console.WriteLine($"start: score={score0} items={total} expected +{expect} px={st.Rw(0x4F1C)} py={st.Rw(0x4F1E)} percent={EndScreens.Percent(st)}");
        var spy = new SpyAudio();
        var end = new EndScene(st, originalDir, false, animated: true) { Audio = spy };
        var fb = new Framebuffer();
        int frames = 0, walk = 0, count = 0, changes = 0, lastPeriod = -1, vsyncs = 0;
        var hashes = new List<string>();
        var phaseStart = new List<string>();
        while (!end.Done && frames < 20000)
        {
            spy.Frame = frames;
            end.Update(input);
            if (end.Done) break;
            double hz = end.LogicHz;
            int period = (int)Math.Round(70.0 / hz);
            vsyncs += period;
            if (period == 1) count++; else walk++;
            if (period != lastPeriod) { changes++; phaseStart.Add($"{(period == 1 ? "countup" : "walk")}@{frames}"); lastPeriod = period; }
            end.Draw(fb);
            if (frames % 40 == 0) hashes.Add($"{frames}:{Hex(fb.Pixels)[..8]}");
            if (Environment.GetEnvironmentVariable("PRE2_TALLYPNG") is string pngDir && (frames == 10 || frames == 40 || frames == 130 || frames == 190 || frames == 215 || frames == 240 || frames == 330)) LevelVerify.SavePng(fb, Path.Combine(pngDir, $"tally_{frames}.png"));
            frames++;
        }
        uint score1 = EndScreens.Score(st);
        Console.WriteLine($"frames={frames} (70/3 Hz frames={walk}, 70 Hz count-up frames={count}) vsyncs={vsyncs} rate changes={changes} [{string.Join(' ', phaseStart)}]");
        Console.WriteLine($"end: done={end.Done} score={score1} added={score1 - score0} {(score1 - score0 == expect ? "== expected" : "!= EXPECTED")} sfx={spy.Sfx.Count} (ids {string.Join(',', spy.Sfx.Select(x => x.Id).Distinct())}) at frames [{string.Join(',', spy.Sfx.Select(x => x.Frame))}]");
        Console.WriteLine($"final: px={st.Rw(0x4F1C)} py={st.Rw(0x4F1E)} food={(short)st.Rw(0x4F2E)} queue-left={Enumerable.Range(0, 0x71).Sum(i => st.Rb(0x6C12 + i))} falling-left={Enumerable.Range(0, 0x14).Count(k => st.Rw(0x52E8 + k * 0x12 + 4) != 0xFFFF)}");
        Console.WriteLine("hashes " + string.Join(' ', hashes));
        bool ok = end.Done && score1 - score0 == expect && spy.Sfx.Count == total;
        Console.WriteLine(ok ? "PASS tally probe" : "FAIL tally probe");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Headless probe of the whole between-levels flow of <see cref="Pre2Port.Game.Frontend.FrontEndRunner"/> (level end -> animated tally -> CARTE -> next level),
    /// injecting a finished level through reflection (the runner normally gets there by playing). Checks that lives / score / BONUS mask carry into the next level.
    /// </summary>
    public static int FlowProbe(string bootPath, string originalDir)
    {
        byte[] boot = BootImage.Load(bootPath);
        var runner = new Pre2Port.Game.Frontend.FrontEndRunner(boot, originalDir);
        var scene = new LevelScene(boot, originalDir, 0, hud: false, player: true, objects: true, damage: true, bonus: true, attack: true) { HoldEnd = true };
        var input = new Input();
        scene.Update(input);
        scene.Warp(4068, 574);
        for (int k = 0; k < 0x46; k++)
            if (scene.State.Rw(0x8F1D + k * 7 + 4) == 0x116) scene.State.Ww(0x8F1D + k * 7 + 4, 0x117);
        scene.State.Wb(0x27D8, 3); scene.State.Wb(0x6CA7, 0x05); scene.State.Ww(0x6C0E, 500);
        scene.State.Wb(0x6C12 + 3, 2); scene.State.Ww(0x6C9E, 2);
        var f = typeof(Pre2Port.Game.Frontend.FrontEndRunner).GetField("_level", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        f.SetValue(runner, scene);
        var fb = new Framebuffer();
        string last = "";
        var seq = new List<string>();
        int ticks = 0;
        for (; ticks < 40000; ticks++)
        {
            runner.Update(input);
            runner.Draw(fb);
            string ph = runner.Phase;
            if (ph != last) { seq.Add($"{ph}@{ticks}(hz={runner.LogicHz:F1})"); last = ph; }
            if (ph == "LEVEL" && runner.ActiveLevel!.Level == 1 && runner.ActiveLevel.Ticks >= 1) break;
        }
        var nl = runner.ActiveLevel;
        Console.WriteLine("phases: " + string.Join(" -> ", seq));
        if (nl == null || nl.Level != 1) { Console.WriteLine($"FAIL: next level not reached (phase {runner.Phase}, ticks {ticks})"); return 1; }
        var ns = nl.State;
        uint score = EndScreens.Score(ns);
        Console.WriteLine($"next level {nl.Level + 1}: lives={ns.Rb(0x27D8)} energy={ns.Rb(0x27D6)} bonusMask={ns.Rb(0x6CA7):X2} score={score} queue/total cleared={ns.Rw(0x6C9E) == 0}");
        bool ok = ns.Rb(0x27D8) == 3 && ns.Rb(0x6CA7) == 5 && score > 500 && ns.Rb(0x27D6) == 3;
        Console.WriteLine(ok ? "PASS flow probe" : "FAIL flow probe");
        return ok ? 0 : 1;
    }

    public static int Run(string bootPath, string originalDir, int level, int ticks, string[] refLines)
    {
        var byScenario = new Dictionary<string, List<string[]>>();
        foreach (string raw in refLines)
        {
            string[] p = raw.Trim().Split(':');
            if (p.Length < 13 || p[0] != $"E{level}") continue;
            if (!byScenario.TryGetValue(p[1], out var l)) byScenario[p[1]] = l = new List<string[]>();
            l.Add(p);
        }
        if (byScenario.Count == 0) { Console.WriteLine($"level: no end reference lines for E{level}"); return 1; }

        byte[] boot = BootImage.Load(bootPath);
        int pass = 0, fail = 0;
        foreach (var (name, lines) in byScenario)
        {
            if (!Scenarios.TryGetValue(name, out var script)) { Console.WriteLine($"FAIL E{level} {name}: unknown scenario"); fail++; continue; }
            var scene = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: true, damage: true, bonus: true, attack: true) { HoldEnd = true };
            var input = new Input();
            var fb = new Framebuffer();
            int sp = 0, sf = 0, t = 0;
            var pending = new List<string>();
            bool ended = false;
            foreach (var (keys, n) in script)
            {
                if (keys[0] == '@' || keys[0] == '=') { pending.Add(keys); continue; }
                for (int i = 0; i < n && !ended; i++, t++)
                {
                    try
                    {
                        foreach (string a in pending) Apply(scene, a);
                        pending.Clear();
                        input.Set(Input.Key.Left, keys.Contains('L'), false);
                        input.Set(Input.Key.Right, keys.Contains('R'), false);
                        input.Set(Input.Key.Up, keys.Contains('U'), false);
                        input.Set(Input.Key.Down, keys.Contains('D'), false);
                        scene.Update(input);
                    }
                    catch (ObjectGapException e) { Console.WriteLine($"FAIL E{level} {name} tick {t}: gap: {e.Message}"); sf++; ended = true; break; }
                    var w = lines.FirstOrDefault(x => int.Parse(x[2]) == t);
                    if (w == null) { Console.WriteLine($"FAIL E{level} {name} tick {t}: no ref line (scene ran longer than the ref)"); sf++; ended = true; break; }
                    string got = $"G:{Fields(scene.State)}", exp = string.Join(":", w[3..11]);
                    if (got == exp) sp++; else { sf++; Console.WriteLine($"FAIL E{level} {name} tick {t}: state got={got} want={exp}"); }
                    if (scene.Outcome != LevelScene.EndKind.None) ended = true;
                }
                if (ended) break;
            }
            if (scene.Outcome != LevelScene.EndKind.None)
            {
                bool go = scene.Outcome == LevelScene.EndKind.GameOver;
                var end = new EndScene(scene.State, originalDir, go);
                foreach (var w in lines.Where(x => int.Parse(x[2]) >= t))
                {
                    int tk = int.Parse(w[2]);
                    end.Update(new Input());
                    end.Draw(fb);
                    var pal = new byte[16 * 3];
                    for (int i = 0; i < 16; i++) { pal[i * 3] = fb.Palette[i].R; pal[i * 3 + 1] = fb.Palette[i].G; pal[i * 3 + 2] = fb.Palette[i].B; }
                    string got = $"{(go ? "O" : "T")}:{Fields(scene.State)}:{Hex(fb.Pixels)}:{Hex(pal)}", exp = string.Join(":", w[3..13]);
                    if (got == exp) sp++; else { sf++; Console.WriteLine($"FAIL E{level} {name} tick {tk} ({(go ? "gameover" : "tally")}): got={got[..Math.Min(60, got.Length)]} want={exp[..Math.Min(60, exp.Length)]}"); }
                }
            }
            else { sf++; Console.WriteLine($"FAIL E{level} {name}: the C# scene never ended"); }
            Console.WriteLine($"{(sf == 0 && sp > 0 ? "PASS" : "FAIL")} E{level} {name}: {sp} ticks ok, {sf} failed (ref has {lines.Count})");
            pass += sp; fail += sf;
        }
        Console.WriteLine($"level: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }
}
