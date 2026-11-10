using Pre2Port.Codecs;
using Pre2Port.Native;

namespace Pre2Port.Recovered;

/// <summary>
/// The ProTracker song loader 1030:02F4 (GOG build) as a VM-free transform .TRK -> <see cref="SongState"/>.
/// Port of the loader transcription in work/gen_audio_ref.py (disassembled from the boot image code; checked byte-for-byte against a VM
/// snapshot of PRESENTA: order table, remapped patterns, scaled sample bytes, PTR_OFF, loop fields; the lengths of instruments 5 and 9 differ
/// in that snapshot, see the audio notes). Volume table: (volume * sample) >> 6 (65 rows x 64 columns, checked against the same snapshot).
/// </summary>
public static class SongLoader
{
    const int SearchOff = 0xE75, StepOff = 0xEBD;      // DS:0E75 36-word MOD period table, DS:0EBB+2*idx the idx -> resample-step words

    /// <summary>The 36-entry 12-bit period table the pattern remap searches (repne scasw).</summary>
    public static int[] SearchTable(byte[] boot) => Words(boot, SearchOff, 36);

    /// <summary>Period index (1-based after the remap, 0x24 = not found) -> resample step; indexable by any 15-bit period (0 outside 1..36).</summary>
    public static int[] StepTable(byte[] boot)
    {
        var t = new int[0x8000];
        var w = Words(boot, StepOff, 36);
        for (int i = 0; i < 36; i++) t[i + 1] = w[i];
        return t;
    }

    public static byte[] VolumeTable()
    {
        var t = new byte[65 * 64 + 256];
        for (int v = 0; v < 65; v++)
            for (int s = 0; s < 64; s++)
                t[v * 64 + s] = (byte)((v * s) >> 6);
        return t;
    }

    static int[] Words(byte[] boot, int off, int n)
    {
        var r = new int[n];
        for (int i = 0; i < n; i++)
            r[i] = boot[BootImage.DsBase + off + 2 * i] | (boot[BootImage.DsBase + off + 2 * i + 1] << 8);
        return r;
    }

    static int Get(byte[] a, int i) => (uint)i < (uint)a.Length ? a[i] : 0;
    static int Be16(byte[] a, int o) => (Get(a, o) << 8) | Get(a, o + 1);

    /// <summary><paramref name="raw"/> = the decompressed .TRK (standard "M.K." module, or a 15-sample one).</summary>
    public static SongState Load(byte[] raw, int[] search, int[] step, byte[] vol)
    {
        int count = (Get(raw, 0x438) == 'M' && Get(raw, 0x439) == '.' && Get(raw, 0x43A) == 'K' && Get(raw, 0x43B) == '.') ? 31 : 15;   // [+4A..5A]
        int si = 20;
        var len = new int[count]; var volw = new int[count]; var ls = new int[count]; var ll = new int[count];
        for (int i = 0; i < count; i++)                                   // [+61..+8E]
        {
            si += 22;
            len[i] = (Be16(raw, si) * 2) & 0xFFFF;
            volw[i] = Be16(raw, si + 2);
            ls[i] = (Be16(raw, si + 4) * 2) & 0xFFFF;
            ll[i] = (Be16(raw, si + 6) * 2) & 0xFFFF;
            si += 8;
        }
        int songLen = Get(raw, si);                                       // [+90] -> [0xDC2]
        si += 2;
        var order = new byte[128];                                        // [+96..+9C] -> [0xDC7]
        for (int i = 0; i < 128; i++) order[i] = (byte)Get(raw, si + i);
        si += 128;
        if (count != 15) si += 4;                                         // [+AE] signature
        int maxPat = 0;
        for (int i = 0; i < songLen && i < 128; i++) maxPat = Math.Max(maxPat, order[i]);
        int npat = maxPat + 1;                                            // [+C7] -> [0xDC3]
        int patOff = si;                                                  // [0xDC5]

        var patterns = new byte[npat][];
        for (int p = 0; p < npat; p++)                                    // remap [+135..+152]
        {
            var cells = new byte[1024];
            for (int i = 0; i < 1024; i++) cells[i] = (byte)Get(raw, patOff + p * 1024 + i);
            for (int c = 0; c < 1024; c += 4)
            {
                if ((cells[c] | (cells[c + 1] << 8)) == 0) continue;
                int period = ((cells[c] & 0x0F) << 8) | cells[c + 1];
                int idx = Array.IndexOf(search, period);
                idx = idx >= 0 ? idx + 1 : 0x24;                          // 1-based match position, 0x24 = not found
                cells[c] = (byte)(idx & 0xFF);
                cells[c + 1] = (byte)((idx >> 8) & 0xFF);
            }
            patterns[p] = cells;
        }

        int pos = patOff + npat * 1024, lin = pos;                        // samples follow the patterns, contiguous
        var arena = new List<byte>();
        var bases = new int[count]; var po = new int[count];
        for (int i = 0; i < count; i++)
        {
            bases[i] = arena.Count;
            po[i] = lin & 0xF;                                            // [+E5..+E8]
            for (int k = 0; k < len[i] && pos + k < raw.Length; k++)
                arena.Add((byte)(((raw[pos + k] + 0x80) & 0xFF) >> 2));   // [+108..+111] scale in place
            pos += len[i];
            lin += len[i];
        }
        byte[] ar = arena.ToArray();

        var s = new SongState
        {
            Tick = 1, Speed = 6, OrderPos = 0, Row = 0,                   // [+158..+169]
            Order = order, Patterns = patterns, SongLength = songLen, Step = step, VolTable = vol,
            InstrLength = len, InstrVolume = volw,
            Instr = new MixInstrument[count],
        };
        for (int i = 0; i < count; i++)
        {
            int want = Math.Max(len[i], ls[i] + ll[i]) + AudioMix.BlockLen + 8;
            s.Instr[i] = new MixInstrument
            {
                LoopStart = ls[i], LoopLen = ll[i], PtrOff = po[i], Arena = ar, Base = bases[i],
                WinLen = Math.Max(0, Math.Min(want, ar.Length - bases[i])),
            };
        }
        return s;
    }
}

/// <summary>Song file for a level ([asm 01AB]: [0x2D20 + level] song index -> .TRK) and the index -> file map of pre2/native/audio.py.</summary>
public static class AudioFiles
{
    // 02CC song index -> file, CONFIRMED by the ASM: 0B81 (called by the boot preload 0A2C and by 02CC's 0492 loader with CX = the index) walks the NUL-separated names that start at DS:09E0 and returns the CX-th one, so the
    // index is the position in the name list: 0 PRES, 1 CODE, 2 CARTE, 3 PRESENTA, 4 GLACE, 5 MAP.SQZ, 6 MOTIF.SQZ, 7 UNION.SQZ, 8 MENU.SQZ, 9 MINES, 10 MYSTERY, 11 GAMEOVER.SQZ, 12 MENU2.SQZ, 13 MONSTER, 14 FINAL, 15 BRAVO,
    // 16 KOOL, 17 BOULA. Call sites: 96E3 02CC(1) = the map/code screen (CODE.TRK), 952D 02CC(2) = the carte shown between levels (CARTE.TRK). (Until 2026-10-29 the table had 1 and 2 swapped, and 16 was only inferred.)
    static readonly Dictionary<int, string> IndexToFile = new()
    {
        [0] = "PRES.TRK", [1] = "CODE.TRK", [2] = "CARTE.TRK", [3] = "PRESENTA.TRK", [4] = "GLACE.TRK",
        [9] = "MINES.TRK", [10] = "MYSTERY.TRK", [13] = "MONSTER.TRK", [14] = "FINAL.TRK", [15] = "BRAVO.TRK", [16] = "KOOL.TRK", [17] = "BOULA.TRK",   // 0x11: game-over song (5063: 02CC ax=0x11)
    };

    /// <summary>Song file for a 02CC song index (null when unmapped).</summary>
    public static string? SongFile(int idx) => IndexToFile.TryGetValue(idx, out var f) ? f : null;

    public static string LevelSong(byte[] boot, int level)
    {
        int idx = boot[BootImage.DsBase + 0x2D20 + level];
        return IndexToFile.TryGetValue(idx, out var f) ? f : "MINES.TRK";
    }
}

/// <summary>
/// Thread-safe audio engine: owns the current song and the SFX overlay; the output side (Raylib thread, or the verifier) pulls
/// <see cref="NextBlock"/> at the audio rate, the game side calls <see cref="LoadSong"/> / <see cref="PlaySfx"/> from its own ticks.
/// </summary>
public sealed class AudioEngine
{
    readonly object _lock = new();
    readonly byte[] _boot;
    readonly string _dir;
    readonly int[] _search, _step;
    readonly byte[] _vol;
    readonly int[] _sfxSrc = new int[11], _sfxLen = new int[11];
    byte[]? _bank;
    SongState? _song;
    readonly SfxPlay _sfx = new();

    public string? CurrentSong { get; private set; }

    /// <summary>True (game default) = the deliberate signed-&gt;unsigned XOR 0x80 of the SFX bank; false = bytes raw as the EXE copies them (AudioVerify sets false, set before <see cref="LoadSfxBank"/>).</summary>
    public bool SignedSfxFix { get; set; } = true;

    public AudioEngine(byte[] boot, string originalDir)
    {
        _boot = boot;
        _dir = originalDir;
        _search = SongLoader.SearchTable(boot);
        _step = SongLoader.StepTable(boot);
        _vol = SongLoader.VolumeTable();
        int src = 0;
        for (int i = 0; i < 11; i++)                                      // DS:1009 + dl*4 = {src, len}; len is static, src = running sum [07C9]
        {
            int o = BootImage.DsBase + 0x100B + 4 * i;
            _sfxLen[i] = boot[o] | (boot[o + 1] << 8);
            _sfxSrc[i] = src;
            src = (src + _sfxLen[i]) & 0xFFFF;
        }
    }

    /// <summary>
    /// [asm 07C9] decode SAMPLE.SQZ (11 8-bit effects, 60768 bytes). Idempotent.
    /// The effects are signed PCM (centre 0x00); the EXE copies them raw into the unsigned
    /// DMA buffer ([asm 20AB]), which shifts the wave half a scale and clicks at zero
    /// crossings. Deliberate deviation: XOR 0x80 converts them to unsigned (see docs/hallazgos-port.md).
    /// </summary>
    public void LoadSfxBank(string file)
    {
        lock (_lock)
        {
            if (_bank != null) return;
            _bank = Sqz.Unpack(File.ReadAllBytes(Path.Combine(_dir, file)));
            if (SignedSfxFix) for (int i = 0; i < _bank.Length; i++) _bank[i] ^= 0x80;
        }
    }

    /// <summary>[asm 02CC] load a .TRK and start it from the top. A song that cannot be loaded leaves the music silent (reported on stderr).</summary>
    public void LoadSong(string file)
    {
        try
        {
            byte[] raw = Sqz.Unpack(File.ReadAllBytes(Path.Combine(_dir, file)));
            var s = SongLoader.Load(raw, _search, _step, _vol);
            lock (_lock) { _song = s; CurrentSong = file; }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"audio: song {file} not loaded: {e.Message}");
            lock (_lock) { _song = null; CurrentSong = null; }
        }
    }

    /// <summary>[asm 0282] play_sfx(id): replaces any effect still playing.</summary>
    public void PlaySfx(int id)
    {
        lock (_lock)
        {
            if (_bank == null || id < 0 || id >= 11) return;
            _sfx.Data = _bank;
            _sfx.Off = _sfxSrc[id];
            _sfx.Remaining = Math.Max(0, Math.Min(_sfxLen[id], _bank.Length - _sfxSrc[id]));
        }
    }

    /// <summary>The next 168-byte unsigned 8-bit block (one audio interrupt).</summary>
    public byte[] NextBlock()
    {
        lock (_lock) return AudioMix.NextBlock(_song, _sfx);
    }
}
