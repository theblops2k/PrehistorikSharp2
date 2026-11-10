using System.Security.Cryptography;
using Pre2Port.Native;

namespace Pre2Port.Game.Frontend;

/// <summary>Headless run of the map scenes (mode-select, password, carte) compared with work/gen_map_ref.py output (format documented there).</summary>
public static class MapVerify
{
    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static Input.Key KeyOf(string n) => n switch
    {
        "left" => Input.Key.Left, "right" => Input.Key.Right, "up" => Input.Key.Up, "down" => Input.Key.Down,
        "space" => Input.Key.Space, "enter" => Input.Key.Enter, "one" => Input.Key.One, "two" => Input.Key.Two,
        "a" => Input.Key.A, "b" => Input.Key.B, "c" => Input.Key.C, "d" => Input.Key.D, "e" => Input.Key.E, "f" => Input.Key.F,
        "d0" => Input.Key.D0, "d1" => Input.Key.D1, "d2" => Input.Key.D2, "d3" => Input.Key.D3, "d4" => Input.Key.D4,
        "d5" => Input.Key.D5, "d6" => Input.Key.D6, "d7" => Input.Key.D7, "d8" => Input.Key.D8, "d9" => Input.Key.D9,
        "q" => Input.Key.Q,
        _ => throw new FormatException($"unknown key '{n}' in reference"),
    };

    sealed record Scenario(string Scene, int Mode, int Level, List<(Input.Key K, int A, int B)> Keys);

    public static int Run(string bootPath, string originalDir, int ticks, string refFile)
    {
        var assets = MenuAssets.Load(BootImage.Load(bootPath), originalDir);
        var scenarios = new Dictionary<string, Scenario>();
        var samples = new List<(string Id, int Tick, string[] Remaining)>();
        var results = new Dictionary<string, string[]>();
        foreach (string raw in File.ReadLines(refFile))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string[] p = line.Split(':');
            if (p[0] == "SCENARIO")
            {
                string scene = p[2]["scene=".Length..];
                int mode = int.Parse(p[3]["mode=".Length..]), level = int.Parse(p[4]["level=".Length..]);
                var keys = new List<(Input.Key, int, int)>();
                foreach (string r in p[5]["keys=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] kr = r.Split('@');                       // key@a-b
                    string[] ab = kr[1].Split('-');
                    keys.Add((KeyOf(kr[0]), int.Parse(ab[0]), int.Parse(ab[1])));
                }
                scenarios[p[1]] = new Scenario(scene, mode, level, keys);
            }
            else if (p[0] == "RESULT") results[p[1]] = p[2..];       // tick:kind:level:mode
            else samples.Add((p[0], int.Parse(p[1]), p[2..]));
        }

        int pass = 0, fail = 0;
        foreach (var (id, sc) in scenarios)
        {
            if (id.StartsWith('_')) continue;
            var mine = samples.Where(s => s.Id == id).ToList();
            MenuStage stage = sc.Scene switch
            {
                "carte" => new CarteScene(assets, sc.Level),
                "password" => new MapScene(assets, MapKind.Password, sc.Mode),
                _ => new MapScene(assets, MapKind.ModeSelect, sc.Mode),
            };
            var fb = new Framebuffer();
            var input = new Input();
            var got = new Dictionary<int, string[]>();
            var want = mine.Select(s => s.Tick).ToHashSet();
            int resultTick = -1;
            for (int t = 0; t < ticks && resultTick < 0; t++)
            {
                foreach (Input.Key k in Enum.GetValues<Input.Key>())
                {
                    bool down = sc.Keys.Any(r => r.K == k && t >= r.A && t <= r.B);
                    bool was = input.IsKeyDown(k);
                    input.Set(k, down, down && !was);
                }
                stage.Update(input);
                MapResult res = stage is MapScene ms ? ms.Result : ((CarteScene)stage).Result;
                if (res != MapResult.Running) { resultTick = t; break; }
                if (!want.Contains(t) || !stage.HasFrame) continue;
                stage.Draw(fb);
                int n = stage.PaletteEntries;
                var pal = new byte[n * 3];
                for (int i = 0; i < n; i++)
                {
                    pal[i * 3] = fb.Palette[i].R; pal[i * 3 + 1] = fb.Palette[i].G; pal[i * 3 + 2] = fb.Palette[i].B;
                }
                got[t] = new[] { n.ToString(), Hex(fb.Pixels), Hex(pal) };
            }

            foreach (var s in mine)
            {
                string label = $"{id} tick {s.Tick}";
                if (s.Tick >= ticks) { Console.WriteLine($"SKIP {label} (>= {ticks})"); continue; }
                bool ok = got.TryGetValue(s.Tick, out var g) && g[0] == s.Remaining[0] && g[1] == s.Remaining[1] && g[2] == s.Remaining[2];
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label} {sc.Scene}{(ok || g is null ? "" : $" got={g[1][..8]}/{g[2][..8]} want={s.Remaining[1][..8]}/{s.Remaining[2][..8]}")}");
                if (ok) pass++; else fail++;
            }

            if (results.TryGetValue(id, out var want2))
            {
                int wantTick = int.Parse(want2[0]);
                if (wantTick >= ticks) Console.WriteLine($"SKIP {id} result (tick {wantTick} >= {ticks})");
                else
                {
                    string kind = "-"; int lvl = -1, mode = -1;
                    if (stage is MapScene m2) { kind = m2.Result.ToString(); lvl = m2.Level; mode = m2.Mode; }
                    else if (stage is CarteScene c2) { kind = c2.Result.ToString(); lvl = c2.Level; mode = sc.Mode; }
                    bool ok = resultTick == wantTick && kind == want2[1] && lvl == int.Parse(want2[2]) && mode == int.Parse(want2[3]);
                    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id} result{(ok ? "" : $" got tick={resultTick} {kind} level={lvl} mode={mode}; want tick={want2[0]} {want2[1]} level={want2[2]} mode={want2[3]}")}");
                    if (ok) pass++; else fail++;
                }
            }
        }
        Console.WriteLine($"map: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }
}
