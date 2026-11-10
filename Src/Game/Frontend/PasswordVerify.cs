using System.Security.Cryptography;
using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>
/// Headless probe of the ENTER-CODE screen (`Pre2Port pwprobe &lt;boot&gt; &lt;originalDir&gt;`), expectations taken from the ASM (Ghidra, 1030:9985-9ADF, 932F)
/// and from Sources/pre2_port/pre2/recovered/password.py (the 19 accepted codes), NOT from the C# code under test:
///  A) the 19 codes typed one by one on a fresh screen commit (level, mode) = (index mod 10, index >= 10); index 19 (B005) and garbage are rejected;
///  B) the history [0xB1B3-B7] persists between visits (cheat DEAD C0DE F00D typed on one visit, the level on the next), a fresh memory does not;
///  C) a valid code leaves ONE poll later than fire does (status 2, asm 9AAD -> 9AC4);
///  D) the Q key (scancode 0x10) types 'A';
///  E) the whole flow through FrontEndRunner (menu '2' -> code -> carte -> level) lands in the right level / mode with a sane level state,
///     and the level reached by password A305 is byte-identical (LevelState.Data hash) to the one reached by mode-select BEGINNER after the same ticks.
/// </summary>
public static class PasswordVerify
{
    // password.py level_code(index) for index 0..19: index 0..9 beginner levels 1..10, 10..19 expert levels 1..10 (the validator takes 0..0x12 only).
    static readonly string[] Codes =
    {
        "A305", "A205", "A105", "A005", "A705", "A605", "A505", "A405", "AB05", "AA05",
        "A905", "A805", "AF05", "AE05", "AD05", "AC05", "B305", "B205", "B105", "B005",
    };

    static int _pass, _fail;
    static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
        if (ok) _pass++; else _fail++;
    }

    static readonly Input.Key[] AllKeys = Enum.GetValues<Input.Key>();

    static Input.Key KeyOf(char c) => c switch
    {
        '0' => Input.Key.D0, '1' => Input.Key.D1, '2' => Input.Key.D2, '3' => Input.Key.D3, '4' => Input.Key.D4, '5' => Input.Key.D5,
        '6' => Input.Key.D6, '7' => Input.Key.D7, '8' => Input.Key.D8, '9' => Input.Key.D9,
        'A' => Input.Key.A, 'B' => Input.Key.B, 'C' => Input.Key.C, 'D' => Input.Key.D, 'E' => Input.Key.E, 'F' => Input.Key.F,
        _ => throw new ArgumentException(c.ToString()),
    };

    /// <summary>One scripted input source: keys down this tick, pressed = rising edge.</summary>
    sealed class Pad
    {
        public readonly Input In = new();
        public void Set(params Input.Key[] down)
        {
            foreach (var k in AllKeys)
            {
                bool d = down.Contains(k);
                In.Set(k, d, d && !In.IsKeyDown(k));
            }
        }
    }

    sealed class Screen
    {
        public readonly MapScene S;
        readonly Pad _p = new();
        public int T;
        public Screen(MenuAssets a, PasswordMemory mem, int mode = 0) => S = new MapScene(a, MapKind.Password, mode, mem);
        public bool Done => S.Result != MapResult.Running;
        public void Tick(params Input.Key[] down)
        {
            if (Done) return;
            _p.Set(down);
            S.Update(_p.In);
            T++;
        }
        public void Idle(int n) { for (int i = 0; i < n; i++) Tick(); }
        public void Tap(Input.Key k) { Tick(k); Tick(); Tick(); }          // make, break, one idle poll
        public void Type(string code) { foreach (char c in code) Tap(KeyOf(c)); }
        public void RunOut(int cap = 400) { for (int i = 0; i < cap && !Done; i++) Tick(); }
    }

    public static int Run(string bootPath, string originalDir)
    {
        byte[] boot = BootImage.Load(bootPath);
        var assets = MenuAssets.Load(boot, originalDir);

        // A) every accepted code, fresh screen each time
        for (int idx = 0; idx < Codes.Length; idx++)
        {
            var s = new Screen(assets, new PasswordMemory());
            s.Idle(3);
            s.Type(Codes[idx]);
            s.RunOut();
            if (idx <= 0x12)
            {
                int wantLevel = idx % 10, wantMode = idx >= 10 ? 1 : 0;
                Check(s.S.Result == MapResult.LevelChosen && s.S.Level == wantLevel && s.S.Mode == wantMode,
                      $"A code {Codes[idx]} (index {idx:X2}) -> level {wantLevel} mode {wantMode}; got {s.S.Result} level={s.S.Level} mode={s.S.Mode}");
            }
            else
                Check(s.S.Result == MapResult.Running && s.S.Level == 0xFF, $"A code {Codes[idx]} (index {idx:X2}, past the 0x12 bound of asm 9A7C) is rejected; got {s.S.Result} level={s.S.Level}");
        }
        {
            var s = new Screen(assets, new PasswordMemory());
            s.Idle(3); s.Type("1234"); s.RunOut(200);
            Check(s.S.Result == MapResult.Running && s.S.Level == 0xFF, "A garbage code 1234 is rejected (screen stays)");
        }

        // B) the history persists between visits
        {
            var mem = new PasswordMemory();
            var v1 = new Screen(assets, mem);
            v1.Idle(3);
            foreach (string g in new[] { "DEAD", "C0DE", "F00D" }) { v1.Type(g); v1.Idle(0x90); }
            v1.Tick(Input.Key.Space);
            v1.RunOut();
            Check(v1.S.Result == MapResult.BackToMenu && v1.S.Level == 0xFF, $"B visit 1 (DEAD C0DE F00D, then Space) leaves with Level 0xFF; got {v1.S.Result} level={v1.S.Level}");
            var v2 = new Screen(assets, mem);
            v2.Idle(3); v2.Type("0002"); v2.RunOut();
            Check(v2.S.Result == MapResult.LevelChosen && v2.S.Level == 1 && v2.S.Mode == 0, $"B visit 2 types 0002 -> warp to level index 1 beginner via the kept history; got {v2.S.Result} level={v2.S.Level} mode={v2.S.Mode}");
            var v3 = new Screen(assets, new PasswordMemory());
            v3.Idle(3); v3.Type("0002"); v3.RunOut(200);
            Check(v3.S.Result == MapResult.Running, "B the same 0002 on a fresh memory is just a wrong code");
        }

        // C) timing: fire leaves in the same poll, a matched code one poll later
        {
            var f = new Screen(assets, new PasswordMemory());
            f.Idle(10);
            int tf = f.T;                       // the next Tick is the one that polls fire
            f.Tick(Input.Key.Space); f.RunOut();
            int fireToResult = f.T - tf;
            var m = new Screen(assets, new PasswordMemory());
            m.Idle(3); m.Type("A30"); m.Tick(Input.Key.D5);
            int tm = m.T;                       // the Tick that matched was the one just run (T-1)
            m.RunOut();
            int matchToResult = m.T - (tm - 1);
            Check(matchToResult == fireToResult + 1, $"C a matched code leaves one poll after a fire exit (fire {fireToResult} ticks to Result, match {matchToResult})");
        }

        // D) Q = scancode 0x10 = 'A'
        {
            var s = new Screen(assets, new PasswordMemory());
            s.Idle(3); s.Tap(Input.Key.Q); s.Tap(Input.Key.D3); s.Tap(Input.Key.D0); s.Tap(Input.Key.D5); s.RunOut();
            Check(s.S.Result == MapResult.LevelChosen && s.S.Level == 0 && s.S.Mode == 0, $"D Q3 05 == A305 (level 1 beginner); got {s.S.Result} level={s.S.Level} mode={s.S.Mode}");
        }

        // E) full flow through FrontEndRunner
        string aHash = "", bHash = "";
        foreach (var (name, route, code, wantLevel, wantMode) in new[]
        {
            ("password A305", 2, "A305", 0, 0), ("password A905", 2, "A905", 0, 1), ("password A205", 2, "A205", 1, 0),
            ("password A105", 2, "A105", 2, 0), ("mode-select", 1, "", 0, 0),
        })
        {
            var (ok, hash, info) = Flow(boot, originalDir, assets, route, code, wantLevel, wantMode);
            Check(ok, $"E {name}: {info}");
            if (name == "password A305") aHash = hash;
            if (name == "mode-select") bHash = hash;
        }
        Check(aHash.Length > 0 && aHash == bHash, $"E level 1 reached by password A305 == by mode-select BEGINNER after the same ticks (LevelState hash {aHash[..Math.Min(12, aHash.Length)]} / {bHash[..Math.Min(12, bHash.Length)]})");

        Console.WriteLine($"pwprobe: {_pass} PASS, {_fail} FAIL");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>menu -> ('1' + Space | '2' + code) -> carte -> level; returns whether the level reached has the expected index / lives / energy and runs 300 ticks.</summary>
    static (bool Ok, string Hash, string Info) Flow(byte[] boot, string originalDir, MenuAssets assets, int route, string code, int wantLevel, int wantMode)
    {
        var runner = new FrontEndRunner(boot, originalDir);
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(FrontEndRunner).GetField("_menuAssets", bf)!.SetValue(runner, assets);
        typeof(FrontEndRunner).GetField("_menu", bf)!.SetValue(runner, new MenuFlow(assets));
        var pad = new Pad();
        var fb = new Framebuffer();
        int mapStart = -1;
        var phases = new List<string>();
        string last = "";
        for (int t = 0; t < 6000; t++)
        {
            var keys = new List<Input.Key>();
            string scene = runner.SceneName;
            if (mapStart < 0 && (scene == "MAP_PASSWORD" || scene == "MAP_MODE_SELECT")) mapStart = t;
            if (mapStart < 0 && t >= 200 && t <= 202) keys.Add(route == 1 ? Input.Key.One : Input.Key.Two);   // the menu idles out at ~430
            if (mapStart >= 0 && route == 2 && scene == "MAP_PASSWORD")
                for (int i = 0; i < code.Length; i++)
                    if (t == mapStart + 10 + 4 * i) keys.Add(KeyOf(code[i]));
            if (mapStart >= 0 && route == 1 && scene == "MAP_MODE_SELECT" && t == mapStart + 10) keys.Add(Input.Key.Space);
            pad.Set(keys.ToArray());
            runner.Update(pad.In);
            runner.Draw(fb);
            string cur = $"{runner.Phase}/{runner.SceneName}";
            if (cur != last) { phases.Add($"{cur}@{t}"); last = cur; }
            if (runner.Phase == "LEVEL" && runner.ActiveLevel is { } lv && lv.Ticks >= 300)
            {
                var st = lv.State;
                string hash = Convert.ToHexString(SHA256.HashData(st.Data)).ToLowerInvariant();
                bool ok = lv.Level == wantLevel && runner.LastChosenLevel == wantLevel && runner.LastChosenMode == wantMode
                          && st.Rb(0x27D8) == 2 && st.Rb(0x27D6) == 3 && EndScreens.Score(st) == 0;
                return (ok, hash, $"level index {lv.Level} (want {wantLevel}) mode {runner.LastChosenMode} (want {wantMode}) lives={st.Rb(0x27D8)} energy={st.Rb(0x27D6)} score={EndScreens.Score(st)} after {lv.Ticks} level ticks; phases {string.Join(" > ", phases)}");
            }
        }
        return (false, "", $"level not reached in 6000 ticks; phases {string.Join(" > ", phases)}");
    }
}
