namespace Pre2Port.Recovered;

/// <summary>One of the 4 MOD voices: the per-channel playback state of the tracker (DS:0B8A.. arrays) shared with the mixer.</summary>
public sealed class Voice
{
    public int Pos = 0xFFFF;      // [0xB8A] sample position (0xFFFF = silent)
    public int End;               // [0xB92] sample end
    public int Instrument;        // [0xB9A]
    public int Period;            // [0xBAA] resample step
    public int Volume;            // [0xBBA] row of the volume table (<< 6)
    public int Frac;              // [0xBCA] fractional position (low byte)
    public int VolSlide;          // [0xBC2] per-tick volume delta (16-bit)
    public int NotePeriod;        // [0xBB2]
    public int Effect;            // [0xBA2] (cmd << 8) | param
}

/// <summary>An instrument as the mixer sees it: window into the shared scaled-sample arena, loop and PTR_OFF.</summary>
public sealed class MixInstrument
{
    public int LoopStart, LoopLen, PtrOff;
    public byte[] Arena = Array.Empty<byte>();
    public int Base;       // arena offset of sample byte 0 (the ASM PTR_OFF base)
    public int WinLen;     // bytes readable from Base (beyond it the mixer reads 0, like the Python window)
}

/// <summary>The active SFX overlay (mix_sfx): bytes still to copy into the block base. Remaining == 0 = none.</summary>
public sealed class SfxPlay
{
    public byte[] Data = Array.Empty<byte>();
    public int Off;
    public int Remaining;
}

/// <summary>Everything the sequencer + mixer need for one song (pre2.recovered.audio_system.AudioState).</summary>
public sealed class SongState
{
    public int Tick, Speed, OrderPos, Row;                 // [0xB85] [0xB84] [0xB86] [0xB88]
    public readonly Voice[] Voices = { new(), new(), new(), new() };
    public byte[] Order = Array.Empty<byte>();             // [0xDC7] (128 bytes)
    public byte[][] Patterns = Array.Empty<byte[]>();      // remapped 1024-byte patterns
    public int SongLength;                                 // [0xDC2]
    public int[] Step = Array.Empty<int>();                // period index -> resample step (0x8000 entries)
    public int[] InstrLength = Array.Empty<int>();         // [instr*16 + 0xBD2]
    public int[] InstrVolume = Array.Empty<int>();         // [instr*16 + 0xBD4]
    public MixInstrument[] Instr = Array.Empty<MixInstrument>();
    public byte[] VolTable = Array.Empty<byte>();
    public bool MusicOn = true;
}

/// <summary>
/// The audio ISR as a pure engine: tracker 1030:227C (+ note processor 22AF, effects A/B/C/D/F) and mixer 1030:218F / 20AB.
/// Port of pre2/recovered/tracker.py, mixer.py and audio_system.py (AudioSystem.next_block): one call == one 168-byte unsigned 8-bit block.
/// </summary>
public static class AudioMix
{
    public const int BlockLen = 0xA8;
    public const int ChannelOff = 0xFFFF;
    const int LoopMin = 3;
    const int VolMax = 0x40;

    /// <summary>false (game default) = the deliberate saturating sum; true = the EXE's 8-bit wrapping <c>add [di],al</c> [21E9]/[222A] (AudioVerify sets true).</summary>
    public static bool WrapLikeExe = false;

    static byte Add(int a, int b) => WrapLikeExe ? (byte)(a + b) : (byte)Math.Min(255, a + b);

    static int S16(int x) => (x & 0x8000) != 0 ? x - 0x10000 : x;

    // ---------------------------------------------------------------- tracker (tracker.py)

    static void ApplyEffect(SongState s, Voice v, int cmd, int param)
    {
        switch (cmd)
        {
            case 0x0A:                                     // volume slide (handler 232F)
                {
                    int up = param >> 4;
                    v.VolSlide = up != 0 ? up : (-param) & 0xFFFF;
                    break;
                }
            case 0x0B: s.OrderPos = param; s.Row = 0xFFFF; break;           // position jump (2348): ++ below -> row 0
            case 0x0C: v.Volume = Math.Min(param, VolMax); break;           // set volume (2354)
            case 0x0D: s.Row = (param - 1) & 0xFFFF; s.OrderPos += 1; break; // pattern break (2362)
            case 0x0F: s.Speed = param; break;                              // set speed (236D)
        }                                                                    // 0..9 and E: no-ops
    }

    static void ProcessNote(SongState s, Voice v, byte[] pat, int off)
    {
        v.VolSlide = 0;                                    // [22AF]
        int b0 = Get(pat, off), b1 = Get(pat, off + 1), b2 = Get(pat, off + 2), b3 = Get(pat, off + 3);
        if ((b0 | b1 | b2 | b3) == 0) return;
        int cmd = b2 & 0x0F, param = b3;
        v.Effect = (cmd << 8) | param;
        int sampleNum = (b2 >> 4) | (b1 & 0x10);
        if (sampleNum != 0 && sampleNum - 1 < s.InstrLength.Length)   // trigger [22E2-2305] (a sample past the module = ignored; Python would raise)
        {
            int idx = sampleNum - 1;
            v.Instrument = idx;
            v.Pos = 0;
            v.End = s.InstrLength[idx];
            v.Volume = s.InstrVolume[idx];
            v.Frac = 0;
        }
        int period = (b0 | (b1 << 8)) & 0x7FFF;           // [230B-231C]
        if (period != 0)
        {
            v.NotePeriod = period;
            v.Period = s.Step[period];
        }
        ApplyEffect(s, v, cmd, param);                     // [2320-232A]
    }

    static int Get(byte[] a, int i) => (uint)i < (uint)a.Length ? a[i] : 0;

    static void TrackerTick(SongState s, byte[] pattern)
    {
        foreach (var v in s.Voices)                        // volume slides [221C-223D]
        {
            if (v.VolSlide == 0) continue;
            int nv = v.Volume + S16(v.VolSlide);
            v.Volume = nv < 0 ? 0 : Math.Min(nv, VolMax);
        }
        s.Tick = (s.Tick - 1) & 0xFF;                      // [223F]
        if (s.Tick != 0) return;
        s.Tick = s.Speed;                                  // [2245-2248]
        int row = s.Row;                                   // row pointer latched before the channel loop
        for (int ch = 0; ch < 4; ch++)
            ProcessNote(s, s.Voices[ch], pattern, row * 16 + ch * 4);
        s.Row = (s.Row + 1) & 0xFFFF;                      // [228D-22AC]
        if (s.Row == 0x40)
        {
            int nxt = s.OrderPos + 1;
            s.OrderPos = s.SongLength >= nxt ? nxt : 0;    // [229C] cmp song_len, order+1 / jae
            s.Row = 0;
        }
    }

    // ---------------------------------------------------------------- mixer (mixer.py)

    /// <summary>[1030:218F] add one resampled, volume-scaled voice into the block (byte-wrapping add) and advance pos/end/frac.</summary>
    static void MixChannel(byte[] buf, Voice ch, MixInstrument ins, byte[] vol)
    {
        if (ch.Pos == ChannelOff) return;
        int si = ch.Pos, end = ch.End, period = ch.Period, frac = ch.Frac & 0xFFFF;
        int volRow = (ch.Volume << 6) & 0xFFFF;
        int newPos = -1;                                   // -1 = not set
        int cx = BlockLen, di = 0, wraps = 0;
        while (cx > 0)
        {
            int smp = si < ins.WinLen ? ins.Arena[ins.Base + si] : 0;           // [21E6 lodsb]
            si = (si + 1) & 0xFFFF;
            int vi = (volRow + smp) & 0xFFFF;                                    // [21E8 xlatb]
            int scaled = vi < vol.Length ? vol[vi] : 0;
            // DELIBERATE DEVIATION from the original: [21E9 add [di],al] wraps at 8 bits; here the sum saturates at 255
            // (user decision: wrap made the effects sound horrible). Output centre 0x80 stays in the output conversion only.
            if (di < BlockLen) buf[di] = Add(buf[di], scaled);
            di++;
            frac = (frac + period) & 0xFFFF;                                     // [21EC]
            int step = (frac >> 8) & 0xFF;
            frac &= 0xFF;
            if (step >= 0x80) step -= 0x100;                                     // [21F2 cbw]
            si = (si + step) & 0xFFFF;
            if (si > end)                                                        // [21F5]
            {
                if (ins.LoopLen >= LoopMin)
                {
                    si = (ins.LoopStart - ins.PtrOff) & 0xFFFF;                  // [221A] (cx NOT decremented)
                    wraps++;
                    if (wraps > BlockLen) { newPos = ChannelOff; break; }        // anti-freeze watchdog
                }
                else
                {
                    int al = di - 1 < BlockLen ? buf[di - 1] : 0;                // release fade [222A]
                    while (cx > 0 && al > 0)
                    {
                        if (di < BlockLen) buf[di] = Add(buf[di], al);   // saturating (deliberate deviation, see above)
                        di++; al--; cx--;
                    }
                    newPos = ChannelOff;
                    break;
                }
            }
            else cx--;                                                           // [21F9 loop]
        }
        if (newPos < 0)                                                          // writeback end-check [2257-2277]
        {
            if (si >= end)
            {
                if (ins.LoopLen >= LoopMin) { newPos = ins.LoopStart; end = (ins.LoopStart + ins.LoopLen) & 0xFFFF; }
                else newPos = ChannelOff;
            }
            else newPos = si;
        }
        ch.Pos = newPos & 0xFFFF;
        ch.End = end & 0xFFFF;
        ch.Frac = frac & 0xFF;
    }

    /// <summary>[20AB-20F3] block base: the SFX bytes copied in (+ zero padding), or all silence.</summary>
    static void MixSfx(byte[] buf, SfxPlay sfx)
    {
        int n = Math.Min(Math.Min(sfx.Remaining, BlockLen), sfx.Data.Length - sfx.Off);
        if (n < 0) n = 0;
        for (int i = 0; i < n; i++) buf[i] = sfx.Data[sfx.Off + i];
        for (int i = n; i < BlockLen; i++) buf[i] = 0;
        if (sfx.Remaining == 0) return;
        sfx.Off += n;
        sfx.Remaining -= n;
    }

    /// <summary>
    /// One audio block (AudioSystem.next_block): tick the sequencer once (music on), then SFX base + voices 0-2 (voice 3 only without SFX).
    /// <paramref name="song"/> null = no song loaded: only the SFX base.
    /// </summary>
    public static byte[] NextBlock(SongState? song, SfxPlay sfx)
    {
        var buf = new byte[BlockLen];
        if (song == null) { MixSfx(buf, sfx); return buf; }
        if (song.MusicOn)
        {
            if (song.Order.Length == 0 || song.Speed <= 0) return buf;           // defensive silence (no SFX), as the Python engine
            int oi = song.OrderPos;
            if (oi >= song.Order.Length) oi = song.OrderPos = oi % song.Order.Length;
            int pi = song.Order[oi];
            if (pi >= song.Patterns.Length) return buf;
            TrackerTick(song, song.Patterns[pi]);
        }
        MixSfx(buf, sfx);
        if (song.MusicOn)                                                         // [20F5] music flag
        {
            int n = sfx.Remaining > 0 ? 3 : 4;                                    // [210F] SFX borrows voice 3
            for (int c = 0; c < n; c++)
            {
                var v = song.Voices[c];
                int ii = v.Instrument;
                if ((uint)ii >= (uint)song.Instr.Length) continue;
                MixChannel(buf, v, song.Instr[ii], song.VolTable);
            }
        }
        return buf;
    }
}
