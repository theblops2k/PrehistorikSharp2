using System.Security.Cryptography;
using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Level;

/// <summary>
/// Scenario "S&lt;level&gt;" = the audio chain (work/gen_audio_ref.py): the recovered ISR (song loader + tracker + mixer + SFX overlay) fed block by block;
/// per scenario the running SHA-256 of all output bytes at every 4th block (<c>S0:&lt;scenario&gt;:&lt;block&gt;:&lt;sha&gt;</c>). <c>ticks</c> = blocks to compare.
/// Scenarios (identical to the generator): music (level-1 song, no sfx), sfx (level-1 song; sfx 3/8/2/9 at blocks 100/250/300/400),
/// title (PRESENTA; sfx 7 at block 50 replaced by sfx 8 at block 51). Level song = AudioFiles.LevelSong (index 9 = MINES.TRK for level 0).
/// </summary>
public static class AudioVerify
{
    public static int Run(string bootPath, string originalDir, int level, int ticks, string[] lines)
    {
        byte[] boot = BootImage.Load(bootPath);
        string levelSong = AudioFiles.LevelSong(boot, level);
        AudioMix.WrapLikeExe = true;   // reference = EXE 8-bit wrap (the port's saturating sum is a deliberate deviation)
        var scenarios = new (string Name, string Song, Dictionary<int, int> Sfx)[]
        {
            ("music", levelSong, new()),
            ("sfx", levelSong, new() { [100] = 3, [250] = 8, [300] = 2, [400] = 9 }),
            ("title", "PRESENTA.TRK", new() { [50] = 7, [51] = 8 }),
        };
        var want = new Dictionary<(string, int), string>();
        foreach (string raw in lines)
        {
            string[] p = raw.Trim().Split(':');
            if (p.Length == 4 && p[0] == $"S{level}") want[(p[1], int.Parse(p[2]))] = p[3].ToLowerInvariant();
        }
        int pass = 0, fail = 0;
        foreach (var (name, song, sfx) in scenarios)
        {
            var eng = new AudioEngine(boot, originalDir) { SignedSfxFix = false };   // the reference mixes the raw SAMPLE.SQZ bytes (EXE-faithful)
            eng.LoadSfxBank("SAMPLE.SQZ");
            eng.LoadSong(song);
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int firstBad = -1, checkedN = 0;
            for (int b = 0; b < ticks; b++)
            {
                if (sfx.TryGetValue(b, out int id)) eng.PlaySfx(id);
                h.AppendData(eng.NextBlock());
                if (!want.TryGetValue((name, b), out string? w)) continue;
                checkedN++;
                string got = Convert.ToHexString(h.GetCurrentHash()).ToLowerInvariant();
                if (got != w && firstBad < 0) firstBad = b;
            }
            bool ok = firstBad < 0 && checkedN > 0;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} S{level} {name} song={song} blocks={ticks} checkpoints={checkedN}{(firstBad >= 0 ? $" first divergence at block {firstBad}" : "")}");
            if (ok) pass++; else fail++;
        }
        Console.WriteLine($"audio: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
