using Pre2Port.Native;

namespace Pre2Port.Recovered;

/// <summary>
/// The demo (attract) buffer and the two halves of DC1 (1030:0DC1) that touch it. Port of <c>pre2/recovered/input_decode.py decode_input</c>
/// (modes 0 and 1; the record/playback halves are byte-exact against the Python reference, see work/demo_ref.txt).
/// The buffer lives in DGROUP at DS:0x3F (RLE entries of 2 bytes: packed input byte, repeat count; end sentinel 0x55AA), the header bytes
/// [0x83D] (mode) and [0x83E] (level) follow it. Cursor [0x287A] (byte offset into the buffer), current byte [0x287C], repeat count [0x287D].
/// Input source [0x2879]: 0 live keyboard (+ the record tail), 1 demo playback.
/// </summary>
public static class DemoInput
{
    public const int Mode = 0x2879, Ptr = 0x287A, CurByte = 0x287C, CurCnt = 0x287D, EndSignal = 0x6BE5, PendingKey = 0x2874;
    public const int BufStart = 0x3F, HdrMode = 0x83D, HdrLevel = 0x83E, RegionLen = 0x800, RecordLimit = 0x7FC;
    public const int Sentinel = 0x55AA;

    // The six input flags, bit0 first (unpack order of the packed byte): [27ED left, 27EC right, 27EA up, 27EB down, 27E8 fire, 27E9 aux].
    static readonly int[] Unpack = { 0x27ED, 0x27EC, 0x27EA, 0x27EB, 0x27E8, 0x27E9 };
    // Re-pack order (MSB first): the exact inverse.
    static readonly int[] Pack = { 0x27E9, 0x27E8, 0x27EB, 0x27EA, 0x27EC, 0x27ED };

    /// <summary>
    /// [asm 0DD6-0E4D + 0E51 jmp 0F75] DC1 mode 1: pending key / sentinel -> [0x6BE5]=1; RLE-decode the next packed byte and unpack it into the
    /// six flags (including the aux flag [0x27E9]).
    /// </summary>
    public static void Playback(LevelState s)
    {
        if (s.Rb(PendingKey) != 0) s.Wb(EndSignal, 1);                 // [0DD6-0DDD] any key since the level start
        int ptr = s.Rw(Ptr);
        int al;
        if (ptr != 0 && s.Rb(CurCnt) != 0)                             // [0DE6/0DEA] reuse the current byte
        {
            s.Wb(CurCnt, (s.Rb(CurCnt) - 1) & 0xFF);                   // [0E0E]
            al = s.Rb(CurByte);                                        // [0E12]
        }
        else                                                           // [0DF1] next 2-byte entry
        {
            int entry = s.Rw((ptr + BufStart) & 0xFFFF);
            ptr = (ptr + 2) & 0xFFFF;
            if (entry == Sentinel) s.Wb(EndSignal, 1);                 // [0DF7-0DFC] end of the demo
            s.Ww(Ptr, ptr);                                            // [0E01]
            s.Wb(CurByte, entry & 0xFF);                               // [0E05]
            s.Wb(CurCnt, (entry >> 8) & 0xFF);                         // [0E08]
            al = entry & 0xFF;
        }
        foreach (int off in Unpack) { s.Wb(off, al & 1); al >>= 1; }   // [0E15-0E4D]
    }

    /// <summary>
    /// [asm 0F0A-0F70] the DC1 record tail (modes 0/2), run after the live read has written the six flags: RLE-append the packed input to the buffer
    /// (stops once the cursor reaches 0x7FC). The first entry also stamps the level header [0x83E] = [0x2D8A].
    /// </summary>
    public static void Record(LevelState s)
    {
        int ptr = s.Rw(Ptr);
        if (ptr >= RecordLimit) return;                                // [0F0E]
        int al = 0;
        foreach (int off in Pack) al = ((al << 1) | (s.Rb(off) & 1)) & 0xFF;   // [0F14-0F44]
        if (ptr == 0)                                                  // [0F46-0F52] first entry: level header, then a new entry
        {
            s.Wb(HdrLevel, s.Rb(LevelState.Off.Level));
        }
        else if (s.Rb(ptr + 0x3D) == al && s.Rb(ptr + 0x3E) != 0xFF)   // [0F54-0F65] same input: bump the repeat count
        {
            s.Wb(ptr + 0x3E, s.Rb(ptr + 0x3E) + 1);
            return;
        }
        s.Wb(ptr + BufStart, al);                                      // [0F67] new entry
        s.Wb(ptr + BufStart + 1, 0);                                   // [0F6B]
        s.Ww(Ptr, (ptr + 2) & 0xFFFF);                                 // [0F70]
    }

    /// <summary>The persistent demo buffer region (DS:0x3F .. 0x83E) plus the record cursor, shared by the live sessions and the attract playback.</summary>
    public sealed class Store
    {
        /// <summary>DGROUP bytes 0x3F..0x83E (buffer, [0x83D] mode, [0x83E] level).</summary>
        public byte[] Region { get; } = new byte[RegionLen];
        /// <summary>The record cursor [0x287A] of the running live session (reset to 0 at every menu entry, asm 8E4F).</summary>
        public int Cursor { get; set; }

        public Store(byte[] boot) => Array.Copy(boot, BootImage.DsBase + BufStart, Region, 0, RegionLen);

        public int Level => Region[HdrLevel - BufStart];
        public int ModeByte => Region[HdrMode - BufStart];

        /// <summary>[asm 8E4F-8E55] menu entry: the cursor, current byte and count restart at 0.</summary>
        public void ResetCursor() => Cursor = 0;

        /// <summary>[asm 8EFD-8F18] a level was chosen from the menu: the buffer is filled with the 0x55AA sentinel, [0x83E] = level, [0x83D] = mode.</summary>
        public void Wipe(int level, int mode)
        {
            for (int i = 0; i < RegionLen; i += 2) { Region[i] = 0xAA; Region[i + 1] = 0x55; }
            Region[HdrLevel - BufStart] = (byte)level;
            Region[HdrMode - BufStart] = (byte)mode;
            Cursor = 0;
        }

        /// <summary>Copy the buffer into a fresh level memory and set the cursor/mode ([0x2879]) for a live recording (<paramref name="playback"/> false) or a replay.</summary>
        public void LoadInto(LevelState s, bool playback)
        {
            Array.Copy(Region, 0, s.Data, BootImage.DsBase + BufStart, RegionLen);
            s.Wb(Mode, playback ? 1 : 0);
            s.Ww(Ptr, playback ? 0 : Cursor);
            s.Wb(CurByte, 0); s.Wb(CurCnt, 0);
            s.Wb(PendingKey, 0);                                       // [asm 020F] cleared right before the main loop
            s.Wb(EndSignal, 0);
        }

        /// <summary>Copy the (recorded) buffer and cursor back from the level memory.</summary>
        public void SaveFrom(LevelState s)
        {
            Array.Copy(s.Data, BootImage.DsBase + BufStart, Region, 0, RegionLen);
            Cursor = s.Rw(Ptr);
        }
    }
}
