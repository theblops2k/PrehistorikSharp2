using System.Security.Cryptography;
using Pre2Port.Native;

namespace Pre2Port.Game.Frontend;

/// <summary>Headless menu run compared with work/gen_menu_ref.py output (format documented there).</summary>
public static class MenuVerify
{
    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static Input.Key KeyOf(string n) => n switch
    {
        "one" => Input.Key.One, "two" => Input.Key.Two, "space" => Input.Key.Space, "enter" => Input.Key.Enter,
        _ => throw new FormatException($"unknown key '{n}' in reference"),
    };

    public static int Run(string bootPath, string originalDir, int ticks, string refFile)
    {
        var assets = MenuAssets.Load(BootImage.Load(bootPath), originalDir);
        var scenarios = new Dictionary<string, List<(Input.Key K, int A, int B)>>();
        var samples = new List<(string Id, int Tick, string[] Remainder)>();
        var results = new Dictionary<string, int>();                  // RESULT:<id>:<tick>:DEMO -> the tick the flow hands over to the attract demo
        foreach (string raw in File.ReadLines(refFile))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string[] p = line.Split(':');
            if (p[0] == "SCENARIO")
            {
                var keys = new List<(Input.Key, int, int)>();
                foreach (string r in p[2]["keys=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] kr = r.Split('@');                       // key@a-b
                    string[] ab = kr[1].Split('-');
                    keys.Add((KeyOf(kr[0]), int.Parse(ab[0]), int.Parse(ab[1])));
                }
                scenarios[p[1]] = keys;
            }
            else if (p[0] == "RESULT") results[p[1]] = int.Parse(p[2]);
            else samples.Add((p[0], int.Parse(p[1]), p[2..]));
        }

        int pass = 0, fail = 0;
        foreach (var (id, keys) in scenarios)
        {
            var mine = samples.Where(s => s.Id == id).ToList();
            var flow = new MenuFlow(assets);
            var fb = new Framebuffer();
            var input = new Input();
            var got = new Dictionary<int, string[]>();
            var want = mine.Select(s => s.Tick).ToHashSet();
            int demoTick = -1;
            for (int t = 0; t < ticks; t++)
            {
                foreach (Input.Key k in Enum.GetValues<Input.Key>())
                {
                    bool down = keys.Any(r => r.K == k && t >= r.A && t <= r.B);
                    bool was = input.IsKeyDown(k);
                    input.Set(k, down, down && !was);
                }
                flow.Update(input);
                if (flow.Done && flow.DemoRequested && demoTick < 0) demoTick = t;
                if (!want.Contains(t)) continue;
                flow.Draw(fb);
                int n = flow.PaletteEntries;
                var pal = new byte[n * 3];
                for (int i = 0; i < n; i++)
                {
                    pal[i * 3] = fb.Palette[i].R; pal[i * 3 + 1] = fb.Palette[i].G; pal[i * 3 + 2] = fb.Palette[i].B;
                }
                got[t] = new[] { flow.SceneName, n.ToString(), Hex(fb.Pixels), Hex(pal) };
            }

            if (results.TryGetValue(id, out int rt))
            {
                bool rok = rt >= ticks ? true : demoTick == rt;
                Console.WriteLine($"{(rt >= ticks ? "SKIP" : rok ? "PASS" : "FAIL")} {id} RESULT demo at tick {rt}{(rok || rt >= ticks ? "" : $" got={demoTick}")}");
                if (rt < ticks) { if (rok) pass++; else fail++; }
            }
            foreach (var s in mine)
            {
                string label = $"{id} tick {s.Tick}";
                if (s.Tick >= ticks) { Console.WriteLine($"SKIP {label} (>= {ticks})"); continue; }
                bool ok = got.TryGetValue(s.Tick, out var g) && g[0] == s.Remainder[0] && g[1] == s.Remainder[1]
                          && (s.Remainder[2] == "-" || g[2] == s.Remainder[2]) && g[3] == s.Remainder[3];
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label} {s.Remainder[0]}{(ok || g is null ? "" : $" got={g[0]}")}{(s.Remainder[2] == "-" ? " (pixels not compared)" : "")}");
                if (ok) pass++; else fail++;
            }
        }
        Console.WriteLine($"menu: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }
}
