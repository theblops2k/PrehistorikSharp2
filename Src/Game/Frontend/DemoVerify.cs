using System.Security.Cryptography;
using Pre2Port.Game.Level;
using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>Headless check of the DC1 demo buffer port (<see cref="DemoInput"/>) against work/gen_demo_ref.py output (format documented there).</summary>
public static class DemoVerify
{
    static readonly int[] Flags = { 0x27ED, 0x27EC, 0x27EA, 0x27EB, 0x27E8, 0x27E9 };

    // keep in sync with gen_demo_ref.py SCRIPTS
    static List<(int Flags, int Count)> Script(string name) => name switch
    {
        "idle" => new() { (0, 700) },
        "mix" => new() { (0, 10), (2, 300), (6, 5), (0, 1), (16, 1), (17, 2), (0, 255), (0, 256), (32, 3), (1, 1), (2, 1), (1, 1), (2, 1), (63, 4), (0, 3) },
        "fill" => Enumerable.Range(0, 1100).Select(i => ((i * 5 + 1) & 0x3F, 1)).ToList(),
        _ => throw new FormatException($"unknown script '{name}'"),
    };

    static int Flags6(LevelState s)
    {
        int v = 0;
        for (int i = 0; i < 6; i++) v |= (s.Rb(Flags[i]) & 1) << i;
        return v;
    }

    static string Region(LevelState s) =>
        Convert.ToHexString(SHA256.HashData(s.Data.AsSpan(BootImage.DsBase + DemoInput.BufStart, DemoInput.RegionLen))).ToLowerInvariant();

    public static int Run(string bootPath, string originalDir, string refFile)
    {
        byte[] boot = BootImage.Load(bootPath);
        var play = new List<string[]>(); var playKey = new List<string[]>(); var rec = new List<string[]>(); var traj = new List<string[]>();
        foreach (string raw in File.ReadLines(refFile))
        {
            string[] p = raw.Trim().Split(':');
            if (p[0] == "PLAY") play.Add(p); else if (p[0] == "PLAYKEY") playKey.Add(p); else if (p[0] == "REC") rec.Add(p); else if (p[0] == "TRAJ") traj.Add(p);
        }

        int pass = 0, fail = 0;
        foreach (var (tag, lines, keyFrom) in new[] { ("PLAY", play, -1), ("PLAYKEY", playKey, 7) })
        {
            var s = new LevelState(boot, originalDir);
            s.Wb(DemoInput.Mode, 1); s.Ww(DemoInput.Ptr, 0); s.Wb(DemoInput.CurByte, 0); s.Wb(DemoInput.CurCnt, 0); s.Wb(DemoInput.PendingKey, 0); s.Wb(DemoInput.EndSignal, 0);
            int bad = 0, firstBad = -1;
            for (int f = 0; f < lines.Count; f++)
            {
                if (keyFrom >= 0 && f >= keyFrom) s.Wb(DemoInput.PendingKey, 0x39);
                DemoInput.Playback(s);
                var want = lines[f];
                bool ok = int.Parse(want[1]) == f && int.Parse(want[2]) == Flags6(s) && int.Parse(want[3]) == s.Rw(DemoInput.Ptr) && int.Parse(want[4]) == s.Rb(DemoInput.EndSignal);
                if (!ok) { bad++; if (firstBad < 0) firstBad = f; }
            }
            Console.WriteLine($"{(bad == 0 ? "PASS" : "FAIL")} {tag}: {lines.Count} frames, {bad} mismatches{(bad == 0 ? "" : $" (first at frame {firstBad})")}");
            if (bad == 0) pass++; else fail++;
        }

        foreach (var r in rec)
        {
            var s = new LevelState(boot, originalDir);
            s.Wb(DemoInput.Mode, 0); s.Ww(DemoInput.Ptr, 0); s.Wb(DemoInput.CurByte, 0); s.Wb(DemoInput.CurCnt, 0); s.Wb(LevelState.Off.Level, 2);
            int frames = 0;
            foreach (var (fl, cnt) in Script(r[1]))
                for (int c = 0; c < cnt; c++)
                {
                    for (int i = 0; i < 6; i++) s.Wb(Flags[i], (fl >> i) & 1);     // what DC1's live read leaves in the six flags
                    DemoInput.Record(s);
                    frames++;
                }
            bool ok = int.Parse(r[2]) == frames && int.Parse(r[3]) == s.Rw(DemoInput.Ptr) && int.Parse(r[4]) == s.Rb(DemoInput.HdrLevel) && r[5] == Region(s);
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} REC {r[1]}: {frames} frames, cursor {s.Rw(DemoInput.Ptr)} (want {r[3]}), level header {s.Rb(DemoInput.HdrLevel)}, buffer {(r[5] == Region(s) ? "identical" : "DIFFERENT")}");
            if (ok) pass++; else fail++;
        }
        if (traj.Count > 0)
        {
            // The attract demo through the real level scene (what FrontEndRunner builds): player X/Y, demo cursor, [0x6BE5], [0x6BE4] after every tick vs the Python frame step.
            var store = new DemoInput.Store(boot);
            var scene = new Pre2Port.Game.Level.LevelScene(boot, originalDir, store.Level, damage: true, bonus: true, attack: true, modeCopy: store.ModeByte)
            { HoldEnd = true, AnimateDeath = true, DemoStore = store, DemoPlayback = true };
            var input = new Input();
            int bad = 0, firstBad = -1;
            string firstDiff = "";
            for (int f = 0; f < traj.Count; f++)
            {
                scene.Update(input);
                var st = scene.State;
                string got = $"{st.Rw(Player.PX)}:{st.Rw(Player.PY)}:{st.Rw(DemoInput.Ptr)}:{st.Rb(DemoInput.EndSignal)}:{st.Rb(0x6BE4)}";
                string want = string.Join(':', traj[f][2..]);
                if (got != want) { bad++; if (firstBad < 0) { firstBad = f + 1; firstDiff = $"got {got} want {want}"; } }
            }
            Console.WriteLine($"{(bad == 0 ? "PASS" : "FAIL")} TRAJ: {traj.Count} ticks of the attract demo (level {store.Level + 1}, mode {store.ModeByte}), {bad} mismatches{(bad == 0 ? "" : $" (first at tick {firstBad}: {firstDiff})")}");
            if (bad == 0) pass++; else fail++;
        }
        // ---- ROUNDTRIP (no Python reference): a scripted live session in the level scene RECORDS into the store (DC1 0F0A), a second scene REPLAYS it (DC1 mode 1):
        //      the player X/Y/sprite of every recorded tick must come back identical, and the replay must hit the 0x55AA sentinel right after the last tick. ----
        {
            var store = new DemoInput.Store(boot);
            store.Wipe(0, 0);                                                        // [asm 8EFD] a level was chosen: wiped buffer
            LevelScene New(bool playback) => new(boot, originalDir, 0, damage: true, bonus: true, attack: true) { HoldEnd = true, AnimateDeath = true, DemoStore = store, DemoPlayback = playback };
            var script = new (Input.Key[] Keys, int Count)[]
            {
                (Array.Empty<Input.Key>(), 20), (new[] { Input.Key.Right }, 60), (new[] { Input.Key.Right, Input.Key.Up }, 14), (new[] { Input.Key.Right }, 40),
                (Array.Empty<Input.Key>(), 10), (new[] { Input.Key.Left }, 50), (new[] { Input.Key.Space }, 1), (new[] { Input.Key.Right, Input.Key.Space }, 30), (Array.Empty<Input.Key>(), 5),
            };
            var live = New(false);
            var input = new Input();
            var seen = new List<string>();
            foreach (var (keys, count) in script)
                for (int c = 0; c < count; c++)
                {
                    foreach (Input.Key k in Enum.GetValues<Input.Key>()) { bool down = keys.Contains(k); input.Set(k, down, down && !input.IsKeyDown(k)); }
                    live.Update(input);
                    var st = live.State;
                    seen.Add($"{st.Rw(Player.PX)}:{st.Rw(Player.PY)}:{st.Rw(Player.PSprite)}");
                }
            int cursor = store.Cursor;
            var replay = New(true);
            var idle = new Input();
            int bad = 0, firstBad = -1;
            for (int f = 0; f < seen.Count; f++)
            {
                replay.Update(idle);
                var st = replay.State;
                if ($"{st.Rw(Player.PX)}:{st.Rw(Player.PY)}:{st.Rw(Player.PSprite)}" != seen[f]) { bad++; if (firstBad < 0) firstBad = f + 1; }
            }
            replay.Update(idle);                                                     // the tick after the last recorded one reads the sentinel
            bool sentinel = replay.State.Rb(DemoInput.EndSignal) == 1;
            bool ok = bad == 0 && sentinel;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} ROUNDTRIP: {seen.Count} live ticks recorded ({cursor / 2} RLE entries, cursor {cursor}), replay mismatches {bad}{(bad == 0 ? "" : $" (first at tick {firstBad})")}, sentinel after the last tick: {sentinel}");
            if (ok) pass++; else fail++;
        }
        Console.WriteLine($"demo: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }
}
