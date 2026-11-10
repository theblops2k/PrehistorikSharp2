using Pre2Port.Native;

namespace Pre2Port.Game.Level;

/// <summary>
/// Headless glider check (planeador: ASM 596A flying FSM, cs:[0x7D6F] dispatch, 484E wing) against work/gen_glider_ref.py (Python player oracle).
/// Same scenes and key scripts as PlayerVerify (a real <see cref="LevelScene"/>, player on, objects off); the glider is armed before tick 0 like the pickup 850A
/// does ([0x6BC5]=1, [0x4F27]=0xFF). Ref line: <c>G&lt;level&gt;:&lt;scenario&gt;:&lt;tick&gt;:x:y:xvel:yvel:sprite:6BC5:6BC6:6BC7:6BC8:7B1A:4F27:4F0A:4F0C:4F0E:4F28:6BD1:6BD3:6BCE</c>
/// (generator header). Keep <see cref="Scenarios"/> in sync with SCENARIOS there. Exit code 0 when every compared tick matches and at least one glide start was seen.
/// </summary>
public static class GliderVerify
{
    static readonly Dictionary<string, (string Keys, int Ticks)[]> Scenarios = new()
    {
        ["ground"] = new[] { ("-", 6), ("U", 12), ("-", 30), ("R", 25), ("RU", 14), ("R", 20), ("L", 25), ("LU", 14), ("-", 40) },
        ["launch"] = new[] { ("-", 5), ("R", 80), ("RU", 3), ("R", 60), ("-", 60) },
        ["launchL"] = new[] { ("-", 5), ("L", 80), ("LU", 3), ("L", 60), ("-", 60) },
        ["steer"] = new[] { ("-", 5), ("R", 80), ("RU", 3), ("R", 6), ("U", 20), ("-", 8), ("D", 20), ("RD", 10), ("U", 25), ("-", 10), ("L", 30), ("LU", 12), ("D", 12), ("-", 50) },
        ["flap"] = new[] { ("-", 5), ("R", 80), ("RU", 3), ("RU", 40), ("R", 10), ("RU", 40), ("RD", 10), ("U", 60), ("R", 60) },
        ["relaunch"] = new[] { ("-", 5), ("R", 80), ("RU", 3), ("R", 120), ("-", 10), ("R", 80), ("RU", 3), ("R", 80), ("D", 30), ("-", 40) },
    };

    static string KeysAt((string Keys, int Ticks)[] script, int t)
    {
        foreach (var (keys, n) in script)
        {
            if (t < n) return keys;
            t -= n;
        }
        return "-";
    }

    /// <summary>The compared state after a tick, in the generator's field order (values after the player step; a negative velocity prints signed).</summary>
    static string Snapshot(LevelState s) =>
        $"{s.Rw(0x4F1C)}:{s.Rw(0x4F1E)}:{(short)s.Rw(0x4F22)}:{(short)s.Rw(0x4F2A)}:{s.Rw(0x4F20):X4}:{s.Rb(0x6BC5)}:{s.Rb(0x6BC6)}:{s.Rb(0x6BC7)}:{s.Rb(0x6BC8)}:{s.Rb(0x7B1A)}:{s.Rb(0x4F27)}:" +
        $"{s.Rw(0x4F0A)}:{s.Rw(0x4F0C)}:{s.Rw(0x4F0E):X4}:{s.Rw(0x4F28):X4}:{s.Rb(0x6BD1)}:{s.Rb(0x6BD3)}:{s.Rb(0x6BCE)}";

    public static int Run(string bootPath, string originalDir, int level, string[] refLines)
    {
        var byScenario = new Dictionary<string, List<string[]>>();
        foreach (string raw in refLines)
        {
            string[] p = raw.Trim().Split(':');
            if (p.Length < 21 || p[0] != $"G{level}") continue;
            if (!byScenario.TryGetValue(p[1], out var l)) byScenario[p[1]] = l = new List<string[]>();
            l.Add(p);
        }
        if (byScenario.Count == 0) { Console.WriteLine($"glider: no reference lines for G{level}"); return 1; }

        byte[] boot = BootImage.Load(bootPath);
        int pass = 0, fail = 0, glides = 0, tiltMax = 0, wings = 0, wingVisible = 0, wingChecked = 0;
        foreach (var (name, lines) in byScenario)
        {
            if (!Scenarios.TryGetValue(name, out var script)) { Console.WriteLine($"FAIL G{level} {name}: unknown scenario"); fail++; continue; }
            var scene = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: false);
            var fb = new Framebuffer();
            var input = new Input();
            var want = lines.ToDictionary(p => int.Parse(p[2]));
            LevelState s = scene.State;
            s.Wb(0x6BC5, 1); s.Wb(0x4F27, 0xFF);                                  // [8517] [851F] the pickup
            int total = script.Sum(x => x.Ticks), scPass = 0, scFail = 0;
            for (int t = 0; t < total; t++)
            {
                string keys = KeysAt(script, t);
                input.Set(Input.Key.Left, keys.Contains('L'), false);
                input.Set(Input.Key.Right, keys.Contains('R'), false);
                input.Set(Input.Key.Up, keys.Contains('U'), false);
                input.Set(Input.Key.Down, keys.Contains('D'), false);
                scene.Update(input);
                scene.Draw(fb);                                                   // the wing / glide frames go through the real renderer (must not throw)
                if ((s.Rb(0x6BC7) & 1) != 0) glides++;
                tiltMax = Math.Max(tiltMax, s.Rb(0x7B1A));
                if (s.Rw(0x4F0E) != 0xFFFF) wings++;
                if (!want.TryGetValue(t, out var w)) continue;
                string got = Snapshot(s), exp = string.Join(":", w[3..21]);
                if (got == exp) { scPass++; continue; }
                scFail++;
                Console.WriteLine($"FAIL G{level} {name} tick {t} keys={keys}:\n  got ={got}\n  want={exp}");
                if (scFail >= 3) { Console.WriteLine($"  ... {name}: stopping after 3 failures"); break; }
            }
            Console.WriteLine($"{(scFail == 0 && scPass > 0 ? "PASS" : "FAIL")} G{level} {name}: {scPass} ticks ok, {scFail} failed (of {total}; ref has {lines.Count})");
            pass += scPass; fail += scFail;
        }
        // the wing through the real renderer: a scene WITH the object/slot pass (objects:false draws only the player record), same keys; hide the wing slot and redraw
        {
            var scene = new LevelScene(boot, originalDir, level, hud: false, player: true, objects: true);
            var fbA = new Framebuffer(); var fbB = new Framebuffer(); var input = new Input();
            LevelState s = scene.State;
            s.Wb(0x6BC5, 1); s.Wb(0x4F27, 0xFF);
            var script = Scenarios["steer"];
            int total = script.Sum(x => x.Ticks);
            for (int t = 0; t < total; t++)
            {
                string keys = KeysAt(script, t);
                input.Set(Input.Key.Left, keys.Contains('L'), false); input.Set(Input.Key.Right, keys.Contains('R'), false);
                input.Set(Input.Key.Up, keys.Contains('U'), false); input.Set(Input.Key.Down, keys.Contains('D'), false);
                scene.Update(input); scene.Draw(fbA);
                if (s.Rw(0x4F0E) == 0xFFFF || t % 3 != 0) continue;
                int keep = s.Rw(0x4F0E);
                s.Ww(0x4F0E, 0xFFFF); scene.Draw(fbB); s.Ww(0x4F0E, keep);
                wingChecked++;
                if (Environment.GetEnvironmentVariable("PRE2_GLIDER_DUMP") is { } dump && (t == 120 || t == 150 || t == 190 || t == 240))   // PPM of the frame (debug only)
                {
                    using var fs = File.Create(Path.Combine(dump, $"glider_L{level}_t{t}.ppm"));
                    fs.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{fbA.Width} {fbA.Height}\n255\n"));
                    foreach (byte px in fbA.Pixels) { var c = fbA.Palette[px]; fs.WriteByte(c.R); fs.WriteByte(c.G); fs.WriteByte(c.B); }
                }
                if (!fbA.Pixels.AsSpan().SequenceEqual(fbB.Pixels)) wingVisible++;
            }
        }
        Console.WriteLine($"glider: ticks in glide (bit0 of [6BC7]) = {glides}, wing slot drawn ticks = {wings}, wing changes the picture on {wingVisible}/{wingChecked} sampled ticks, max tilt = {tiltMax}");
        Console.WriteLine($"level: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 && glides > 0 ? 0 : 1;
    }
}
