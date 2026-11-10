using System.Security.Cryptography;
using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>Headless attract title animation compared with work/gen_attract_ref.py output (format documented there).</summary>
public static class AttractVerify
{
    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public static int Run(string bootPath, string originalDir, string refFile)
    {
        var assets = MenuAssets.Load(BootImage.Load(bootPath), originalDir);
        int want = 0;
        var frames = new Dictionary<int, string>();
        string? baseHash = null;
        foreach (string raw in File.ReadLines(refFile))
        {
            string[] p = raw.Trim().Split(':');
            if (p[0] == "FRAMES") want = int.Parse(p[1]);
            else if (p[0] == "F") frames[int.Parse(p[1])] = p[2];
            else if (p[0] == "BASE") baseHash = p[1];
        }

        int pass = 0, fail = 0;
        // The MENU2 base deplanarized from page 0x2000 (the picture while the title fades in) == the stage-2 image.
        var basePlanes = new byte[4][];
        for (int i = 0; i < 4; i++) { basePlanes[i] = new byte[0x10000]; Array.Copy(assets.AttractPlanes[i], 0, basePlanes[i], AttractTitle.Page, assets.AttractPlanes[i].Length); }
        bool baseOk = baseHash == Hex(MenuScenePage.Deplanarize(basePlanes, AttractTitle.Page)) && Hex(assets.AttractImage) == baseHash;
        Console.WriteLine($"{(baseOk ? "PASS" : "FAIL")} base image");
        if (baseOk) pass++; else fail++;

        var title = new AttractTitle(assets.NewTitleState(), assets.AttractPlanes);
        int n = 0;
        foreach (byte[] img in title.Frames())
        {
            bool ok = frames.TryGetValue(n, out var h) && h == Hex(img);
            if (!ok) Console.WriteLine($"FAIL frame {n}");
            if (ok) pass++; else fail++;
            n++;
        }
        bool countOk = n == want;
        Console.WriteLine($"{(countOk ? "PASS" : "FAIL")} frame count {n} want {want}");
        if (countOk) pass++; else fail++;
        Console.WriteLine($"attract: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }

    /// <summary>
    /// Debug probe (<c>attractflow</c>): drives a <see cref="FrontEndRunner"/> from the press-1/2 menu (the intro is skipped through reflection) with no key held, so the menu
    /// idles into the attract, and prints every phase change with its tick count (one runner Update == one tick of the CURRENT scene: 70 Hz for the menu / title / carte / fade,
    /// 70/3 Hz for the demo level). Optionally <paramref name="pressAtDemoTick"/> >= 0 taps Space that many demo-level ticks after the level starts (abort test).
    /// </summary>
    public static int Flow(string bootPath, string originalDir, int maxTicks, int pressAtDemoTick, string? traceFile)
    {
        byte[] boot = BootImage.Load(bootPath);
        var runner = new FrontEndRunner(boot, originalDir);
        var assets = MenuAssets.Load(boot, originalDir);
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(FrontEndRunner).GetField("_menuAssets", bf)!.SetValue(runner, assets);
        typeof(FrontEndRunner).GetField("_menu", bf)!.SetValue(runner, new MenuFlow(assets));
        var input = new Input();
        var fb = new Framebuffer();
        string last = "";
        int phaseStart = 0, demoTicks = 0, retraces = 0;
        double hzPrev = 70;
        var sb = new System.Text.StringBuilder();
        string? pngDir = Environment.GetEnvironmentVariable("PRE2_PNG_DIR");
        var pngTicks = pngDir is null ? new HashSet<int>() : (Environment.GetEnvironmentVariable("PRE2_PNG_TICKS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        Pre2Port.Game.Level.LevelScene? demo = null;
        int menusSeen = 0;
        bool dumped = false;
        for (int t = 0; t < maxTicks; t++)
        {
            bool tap = pressAtDemoTick >= 0 && runner.Phase == "DEMO" && demoTicks >= pressAtDemoTick && demoTicks < pressAtDemoTick + 2;
            input.Set(Input.Key.Space, tap, tap && !input.IsKeyDown(Input.Key.Space));
            double hz = runner.LogicHz;
            runner.Update(input);
            runner.Draw(fb);
            if (pngTicks.Contains(t)) Pre2Port.Game.Level.LevelVerify.SavePng(fb, Path.Combine(pngDir!, $"attract_{t:D5}.png"));   // debug: PRE2_PNG_DIR + PRE2_PNG_TICKS=a,b,c
            if (runner.Phase == "DEMO") { demoTicks++; demo = runner.ActiveLevel ?? demo; }
            retraces += hz > 60 ? 1 : 3;
            string cur = $"{runner.Phase}/{runner.SceneName}";
            if (demo is not null && runner.Phase == "DEMO" && traceFile is not null)
            {
                var st = demo.State;
                sb.AppendLine($"{demoTicks}:{st.Rw(Pre2Port.Recovered.Player.PX)}:{st.Rw(Pre2Port.Recovered.Player.PY)}:{st.Rw(Pre2Port.Recovered.DemoInput.Ptr)}:{st.Rb(Pre2Port.Recovered.DemoInput.EndSignal)}:{st.Rb(0x6BE4)}");
            }
            if (demo is not null && runner.Phase == "DEMO" && traceFile is not null && !dumped && demo.State.Rb(0x6BE4) == 1)
            {
                dumped = true;
                var st = demo.State;
                sb.AppendLine($"# death at demo tick {demoTicks}: player ({st.Rw(Pre2Port.Recovered.Player.PX)},{st.Rw(Pre2Port.Recovered.Player.PY)}) energy={st.Rb(0x27D6)} lives={st.Rb(0x27D8)}");
                for (int off = 0x5720; off >= 0x4F0A; off -= 0x12)
                    if (st.Rw(off + 4) != 0xFFFF) sb.AppendLine($"#   slot {off:X4}: x={st.Rw(off)} y={st.Rw(off + 2)} spr={st.Rw(off + 4):X4} b6={st.Rb(off + 6):X2} b7={st.Rb(off + 7):X2} b8={st.Rb(off + 8):X2} b9={st.Rb(off + 9):X2} bA={st.Rb(off + 10):X2} bB={st.Rb(off + 11):X2} life={st.Rb(off + 0x11):X2}");
            }
            if (cur != last)
            {
                string extra = "";
                if (last.StartsWith("DEMO") && demo is not null)
                {
                    var st = demo.State;
                    extra = $" [demo level ran {demoTicks} ticks, outcome={demo.Outcome}, player=({st.Rw(Pre2Port.Recovered.Player.PX)},{st.Rw(Pre2Port.Recovered.Player.PY)}), cursor={st.Rw(Pre2Port.Recovered.DemoInput.Ptr)}, endSignal={st.Rb(Pre2Port.Recovered.DemoInput.EndSignal)}, respawn={st.Rb(0x6BE4)}]";
                }
                Console.WriteLine($"tick {t,6} (retrace~{retraces,6}) -> {cur,-18} hz={runner.LogicHz:F2}  (previous phase lasted {t - phaseStart} updates){extra}");
                last = cur; phaseStart = t; hzPrev = runner.LogicHz;
                if (cur == "MENU/MENU") menusSeen++;
                if (cur == "MENU/MENU" && runner.Returns >= 1) { Console.WriteLine($"back at the menu after the demo (Returns={runner.Returns})"); break; }
            }
        }
        if (traceFile is not null) File.WriteAllText(traceFile, sb.ToString());
        Console.WriteLine($"final phase {runner.Phase}/{runner.SceneName}, demo-level ticks {demoTicks}");
        return 0;
    }
}
