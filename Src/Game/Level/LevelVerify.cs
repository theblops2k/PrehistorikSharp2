using System.Security.Cryptography;
using Pre2Port.Native;
using Pre2Port.Recovered;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Game.Level;

/// <summary>
/// Headless run of the level first frame compared with work/gen_level_ref.py output (format documented there):
/// STATE lines (intermediate memory hashes, checked right after the load, before tick 0) and per-tick lines
/// <c>L&lt;level&gt;:&lt;tick&gt;:16:&lt;sha256 indexed 64000 px&gt;:&lt;sha256 palette 16 entries x rgb&gt;</c>.
/// </summary>
public static class LevelVerify
{
    const int Ds = BootImage.DsBase;

    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    static string Hex(byte[] b, int start, int len) => Convert.ToHexString(SHA256.HashData(b.AsSpan(start, len))).ToLowerInvariant();

    /// <summary>The value of one STATE entry computed from the memory image (same names/formats as gen_level_ref.py).</summary>
    public static string StateValue(string name, LevelState s)
    {
        byte[] d = s.Data;
        switch (name)
        {
            case "map_seg": return s.Rw(Off.MapSeg).ToString();
            case "map_rows": return s.Rb(Off.MapRows).ToString();
            case "load_top": return s.Rw(Off.LoadTop).ToString();
            case "grid": return Hex(d, (s.Rw(Off.MapSeg) << 4) & AddrMask, s.Rb(Off.MapRows) * 0x100);
            case "cache": return PlaneHash(d, 0x5E80, 0x7E80);
            case "back": return PlaneHash(d, 0x7E80, 0x9DC0);
            case "types": return Hex(d, Ds + Off.MaskRegion, 0x4EF8 - 0x2DF8);
            case "anim": return Hex(d, Ds + Off.AnimTable0, 0x400);
            case "tileprop": return Hex(d, Ds + Off.PropDst, 0x815E - 0x7E5E);
            case "header": return Hex(d, Ds + Off.PropHeader, 0x12);
            case "bonus": return Hex(d, Ds + Off.BonusCells, 0x50 * 5);
            case "tileidx": return Hex(d, Ds + Off.TileIndex, 0x200);
            case "player": return $"{s.Rw(Off.PlayerX)},{s.Rw(Off.PlayerY)}";
            case "camera": return $"{s.Rw(Off.CamCol)},{s.Rw(Off.CamRow)},{s.Rw(Off.ColRing)},{s.Rw(Off.RowRing)},{s.Rw(Off.ScrollSrc)},{s.Rb(Off.FineScroll)}";
            case "flags": return $"{s.Rb(Off.AnyAnimated)},{s.Rb(Off.GridDirty)},{s.Rb(Off.LevelFlags)},{s.Rw(Off.AnimRemapPtr)},{s.Rb(Off.AnimThrottle)}";
            case "limits": return $"{s.Rw(Off.StartX)},{s.Rw(Off.StartY)},{s.Rw(Off.PropHeader)},{s.Rw(Off.ScrollAccum)}";
            case "hud": { var h = Hud.Read(s); return $"{h.Lives},{h.Energy},{h.Score},{h.BonusMask}"; }   // lives,energy,displayed score,effective bonus mask
            default: return "?";
        }
    }

    static string PlaneHash(byte[] d, int lo, int hi)
    {
        var all = new byte[4 * (hi - lo)];
        for (int p = 0; p < 4; p++) Array.Copy(d, Plane(p, lo), all, p * (hi - lo), hi - lo);
        return Hex(all);
    }

    /// <summary>Writes the 320x200 indexed framebuffer as a 640x400 (x2 nearest) RGB PNG (no Raylib needed).</summary>
    internal static void SavePng(Framebuffer fb, string path)
    {
        var raw = new byte[400 * (1 + 640 * 3)];
        int o = 0;
        for (int y = 0; y < 400; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < 640; x++)
            {
                var c = fb.Palette[fb.Pixels[(y / 2) * 320 + x / 2] & 15];
                raw[o++] = c.R; raw[o++] = c.G; raw[o++] = c.B;
            }
        }
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(raw);
        using var f = File.Create(path);
        f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string t, byte[] d)
        {
            var tb = System.Text.Encoding.ASCII.GetBytes(t);
            var len = new byte[] { (byte)(d.Length >> 24), (byte)(d.Length >> 16), (byte)(d.Length >> 8), (byte)d.Length };
            f.Write(len); f.Write(tb); f.Write(d);
            uint c = 0xFFFFFFFF;
            foreach (var b in tb.Concat(d)) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1; }
            c ^= 0xFFFFFFFF;
            f.Write(new byte[] { (byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c });
        }
        Chunk("IHDR", new byte[] { 0, 0, 2, 0x80, 0, 0, 1, 0x90, 8, 2, 0, 0, 0 });
        Chunk("IDAT", ms.ToArray());
        Chunk("IEND", Array.Empty<byte>());
    }

    public static int Run(string bootPath, string originalDir, int level, int ticks, string refFile, string? capturePng = null)
    {
        // Scenarios: "L<level>" = terrain only (work/gen_level_ref.py); "H<level>" = terrain + HUD rows 176-199 (work/gen_hud_ref.py,
        // extra field 5 = sha256 of the 24 HUD rows). The scenario is chosen by the ids present in the ref file (L first).
        string[] allLines = File.ReadAllLines(refFile);
        // Scenario "P<level>" = the playable caveman (work/gen_player_ref.py): scripted keys, per-tick X/Y + camera + framebuffer hash.
        // Scenario "O<level>" = player + level objects (work/gen_objects_ref.py): slot lists, raw slot/spawner hashes and the framebuffer per tick.
        // Scenario "D<level>" = player + objects + damage/death/respawn (work/gen_damage_ref.py): energy, lives, respawn state, player X/Y and the framebuffer hash per tick.
        // Scenario "B<level>" / "PL<level>" = player + objects + pickups (bonus cells, float items, food bursts) / moving platforms (work/gen_bonus_ref.py):
        // score, energy, lives, hurt counter, player X/Y, cells collected, platform positions, live bursts, rng state and the framebuffer hash per tick.
        // Scenario "A<level>" = fire / club attack (work/gen_attack_ref.py): attack phase + anim, score, enemy list (x/hp/state/sprite), cells collected, energy, player X/Y,
        // thrown weapons, rng and the framebuffer hash per tick.
        // Scenario "S<level>" = audio (work/gen_audio_ref.py): song loader + tracker + mixer + SFX; `ticks` = audio blocks (168 bytes each) to compare.
        if (allLines.Any(l => l.StartsWith($"S{level}:")))
            return AudioVerify.Run(bootPath, originalDir, level, ticks, allLines);
        if (allLines.Any(l => l.StartsWith($"E{level}:")))
            return EndVerify.Run(bootPath, originalDir, level, ticks, allLines);
        if (allLines.Any(l => l.StartsWith($"BS{level}:")))
            return BossVerify.Run(bootPath, originalDir, level, ticks, allLines);
        if (allLines.Any(l => l.StartsWith($"TB{level}:")))
            return TreeVerify.Run(bootPath, originalDir, level, ticks, allLines);
        if (allLines.Any(l => l.StartsWith($"A{level}:")))
            return AttackVerify.Run(bootPath, originalDir, level, ticks, allLines);
        if (allLines.Any(l => l.StartsWith($"B{level}:") || l.StartsWith($"PL{level}:")))
            return BonusVerify.Run(bootPath, originalDir, level, ticks, allLines);
        if (allLines.Any(l => l.StartsWith($"D{level}:")))
            return DamageVerify.Run(bootPath, originalDir, level, ticks, allLines);
        if (allLines.Any(l => l.StartsWith($"O{level}:")))
            return ObjectVerify.Run(bootPath, originalDir, level, ticks, allLines);
        if (allLines.Any(l => l.StartsWith($"P{level}:")))
            return PlayerVerify.Run(bootPath, originalDir, level, ticks, allLines);
        string id = $"L{level}";
        if (!allLines.Any(l => l.StartsWith(id + ":"))) id = $"H{level}";
        bool hud = id[0] == 'H';
        var states = new List<(string Name, string Want)>();
        var samples = new List<(int Tick, string Fb, string Pal, string? Rows)>();
        foreach (string raw in allLines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string[] p = line.Split(':');
            if (p[0] == "LEVEL") continue;
            if (p[0] == "STATE") { if (p[1] == id) states.Add((p[2], string.Join(":", p[3..]))); }
            else if (p[0] == id) samples.Add((int.Parse(p[1]), p[3], p[4], p.Length > 5 ? p[5] : null));
        }
        if (samples.Count == 0)
        {
            Console.WriteLine($"level: no reference lines for L{level}/H{level} in {refFile}");
            return 1;
        }

        var scene = new LevelScene(BootImage.Load(bootPath), originalDir, level, hud, player: false);
        int pass = 0, fail = 0;
        foreach (var (name, want) in states)
        {
            string got = StateValue(name, scene.State);
            bool ok = got == want;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id} state {name}{(ok ? "" : $" got={got} want={want}")}");
            if (ok) pass++; else fail++;
        }

        var fb = new Framebuffer();
        var input = new Input();
        var want2 = samples.ToDictionary(x => x.Tick);
        for (int t = 0; t < ticks; t++)
        {
            scene.Update(input);
            if (!want2.TryGetValue(t, out var w)) continue;
            scene.Draw(fb);
            var pal = new byte[16 * 3];
            for (int i = 0; i < 16; i++)
            {
                pal[i * 3] = fb.Palette[i].R; pal[i * 3 + 1] = fb.Palette[i].G; pal[i * 3 + 2] = fb.Palette[i].B;
            }
            if (capturePng != null) SavePng(fb, capturePng);
            string gotFb = Hex(fb.Pixels), gotPal = Hex(pal);
            bool ok = gotFb == w.Fb && gotPal == w.Pal;
            if (w.Rows != null)                                    // HUD band: rows 176-199 only
            {
                string gotRows = Hex(fb.Pixels, 176 * Screen.Width, 24 * Screen.Width);
                if (gotRows != w.Rows) { ok = false; Console.WriteLine($"  hud rows got={gotRows[..8]} want={w.Rows[..8]}"); }
            }
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id} tick {t}{(ok ? "" : $" got={gotFb[..8]}/{gotPal[..8]} want={w.Fb[..8]}/{w.Pal[..8]}")}");
            if (ok) pass++; else fail++;
        }
        foreach (var smp in samples)
            if (smp.Tick >= ticks) Console.WriteLine($"SKIP {id} tick {smp.Tick} (>= {ticks})");

        Console.WriteLine($"level: {pass} PASS, {fail} FAIL");
        return fail == 0 && pass > 0 ? 0 : 1;
    }
}
