using System.Security.Cryptography;
using Pre2Port.Native;

namespace Pre2Port.Game.Frontend;

/// <summary>Headless intro run compared with work/gen_intro_ref.py output (format documented there).</summary>
public static class IntroVerify
{
    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public static int Run(string bootPath, string originalDir, int ticks, string refFile)
    {
        var assets = FrontEndAssets.Load(BootImage.Load(bootPath), originalDir, 2026);
        Console.WriteLine($"INFO font bytes patched vs ALLFONTS.SQZ: {assets.FontPatchDiffs()}");

        var scenarios = new Dictionary<string, (bool Skip, List<(int A, int B)> Fire, bool Wait)>();
        var samples = new List<(string Id, string Tick, string[] Remainder)>();
        foreach (string raw in File.ReadLines(refFile))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string[] p = line.Split(':');
            if (p[0] == "SCENARIO")
            {
                var fire = new List<(int, int)>();
                foreach (string r in p[3]["fire=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] ab = r.Split('-');
                    fire.Add((int.Parse(ab[0]), int.Parse(ab[1])));
                }
                scenarios[p[1]] = (p[2] == "skippable=1", fire, p.Length > 4 && p[4] == "presentwait=1");
            }
            else samples.Add((p[0], p[1], p[2..]));
        }

        int pass = 0, fail = 0;
        foreach (var (id, sc) in scenarios)
        {
            var intro = new FrontEndIntro(assets, sc.Skip, null, sc.Wait);
            var fb = new Framebuffer();
            var input = new Input();
            var got = new Dictionary<int, string>();
            var want = new HashSet<int>();
            foreach (var s in samples)
                if (s.Id == id && int.TryParse(s.Tick, out int t)) want.Add(t);

            int frames = 0, t0 = 0;
            for (; t0 < ticks; t0++)
            {
                bool down = sc.Fire.Any(r => t0 >= r.A && t0 <= r.B);
                input.Set(Input.Key.Space, down, false);
                intro.Update(input);
                if (!intro.HasFrame) break;
                frames++;
                if (want.Contains(t0))
                {
                    intro.Draw(fb);
                    int n = intro.PaletteEntries;
                    var pal = new byte[n * 3];
                    for (int i = 0; i < n; i++)
                    {
                        pal[i * 3] = fb.Palette[i].R; pal[i * 3 + 1] = fb.Palette[i].G; pal[i * 3 + 2] = fb.Palette[i].B;
                    }
                    got[t0] = $"{intro.SceneName}:{n}:{Hex(fb.Pixels)}:{Hex(pal)}";
                }
            }

            foreach (var s in samples.Where(s => s.Id == id))
            {
                string label = $"{id} tick {s.Tick}";
                if (s.Tick == "LEN")
                {
                    if (!intro.Done) { Console.WriteLine($"SKIP {label} (intro not finished in {ticks} ticks)"); continue; }
                    bool ok = frames.ToString() == s.Remainder[0];
                    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label} frames={frames} want={s.Remainder[0]}");
                    if (ok) pass++; else fail++;
                    continue;
                }
                int tick = int.Parse(s.Tick);
                if (tick >= ticks) { Console.WriteLine($"SKIP {label} (>= {ticks})"); continue; }
                bool match = got.TryGetValue(tick, out string? g) && g == string.Join(':', s.Remainder);
                Console.WriteLine($"{(match ? "PASS" : "FAIL")} {label} {s.Remainder[0]}");
                if (match) pass++; else fail++;
            }
        }
        Console.WriteLine($"intro: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }
}
