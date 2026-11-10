using Pre2Port.Native;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Recovered;

/// <summary>
/// Cave / teleport entrances. Port of <c>pre2/native/loop.py native_trigger_scan</c> (asm 52FE..5325) and the state half of
/// <c>native_cave_teleport</c> (asm 5326..53F5). Table: 20 entries at DS:0x8367, stride 7 = { u16 source tile (y&lt;&lt;8|x), u16 destination
/// camera (hi=row, lo=col), u16 destination player tile, u8 flag -> [0x6BD9] }.
/// The visual curtain (30C6 fade-out, 3054 centre-out reveal: VRAM only) lives in LevelScene.DrawCurtain. The state of 54AB (StepFireflies) and 3922 (StepScrollScript) is ported
/// here; both also run from LevelScene.FlushPostFrame (0253/0256). Their VRAM plots (marker pixels, LEVELG snow) are not drawn.
/// </summary>
public static class CaveTrigger
{
    public const int Table = 0x8367, Stride = 7, Count = 20;
    const int DropGate = 0x6BE1, Glider = 0x6BC5, TriggerFlag6BD9 = 0x6BD9, TerrainEntityBase = 0x5570;

    /// <summary>[asm 0238 -> 52FE] player packed tile coord (sar 4, low bytes): the value the trigger tables match [549A].</summary>
    public static int PlayerTile(LevelState s)
    {
        static int Sar4(int v) => ((short)(v & 0xFFFF) >> 4) & 0xFF;
        return (Sar4(s.Rw(Off.PlayerY)) << 8) | Sar4(s.Rw(Off.PlayerX));
    }

    /// <summary>[asm 52FE-5325] armed ([0x6BE1] != 0, glider [0x6BC5] == 0) and the player tile equals a table source -> the entry address (else -1). Touches nothing.</summary>
    public static int Scan(LevelState s)
    {
        if (s.Rb(DropGate) == 0 || s.Rb(Glider) != 0) return -1;
        int tile = PlayerTile(s);
        for (int k = 0; k < Count; k++)
            if (tile != 0xFFFF && s.Rw(Table + k * Stride) == tile) return Table + k * Stride;
        return -1;
    }

    /// <summary>Source tile (x, y) of table entry <paramref name="n"/> (0..Count-1); debug helper (PRE2_PLACE=cave:N).</summary>
    public static (int X, int Y) Source(LevelState s, int n)
    {
        int tile = s.Rw(Table + n * Stride);
        return (tile & 0xFF, (tile >> 8) & 0xFF);
    }

    const int Slots = 0x6EA9, SlotCount = 20, SlotSize = 8, RngRor = 0x28C1, TargetX = 0x4F1C, TargetY = 0x4F1E, Scratch = 0x6BC0, FrameGate = 0x6BD5;
    const int ScrollCounter = 0x2DBE, ScriptPtr = 0x2DBC, Wind = 0x6BF6, Flakes = 0x6CA9, CamX = 0x2DE4;

    static int S8(int v) => (sbyte)v;
    static int S16(int v) => (short)v;

    /// <summary>[asm 26CF] rotate generator: [0x28C1] = ror16([0x28C1] + 0x9248, 3); returns AL.</summary>
    static int RngRotate(LevelState s)
    {
        int ax = (s.Rw(RngRor) + 0x9248) & 0xFFFF;
        ax = ((ax >> 3) | (ax << 13)) & 0xFFFF;
        s.Ww(RngRor, ax);
        return ax & 0xFF;
    }

    /// <summary>
    /// [asm 54AB] state half of the "firefly" pass = the 20-slot marker array at 0x6EA9 (stride 8: x.w, y.w (both world&lt;&lt;3), vx.b, vy.b, timer.b, flags.b;
    /// first word 0x55AA = free; the same slots the wall-impact markers 64FA fill). Each live slot: timer--, on underflow a new timer ((26CF&amp;7)+3) and a
    /// velocity aimed at the player (two 39DF draws + one 26CF draw; vy = min(0x20, |vy|*8/|vx|)), then x += vx (undone on signed overflow), y += vy.
    /// Consumes both shared RNGs exactly like the ASM. The VRAM plot (OR pixel colour 14/15) is not drawn by the port.
    /// </summary>
    public static void StepFireflies(LevelState s)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            int b = Slots + i * SlotSize, x = s.Rw(b);
            if (x == 0x55AA) continue;                                           // [54C2] free slot
            int timer = (s.Rb(b + 6) - 1) & 0xFF;                               // [54C6] dec
            s.Wb(b + 6, timer);
            if ((timer & 0x80) != 0)                                             // [54D2] underflow: re-aim
            {
                s.Wb(b + 6, (RngRotate(s) & 7) + 3);
                bool flip = (s.Rb(b + 7) & 7) != 0;
                int al = Pickups.Roll(s) & 0xF;
                if (!(S16(s.Rw(TargetX)) >= (S16(x) >> 3))) al = -al & 0xFF;
                if (flip) al = -al & 0xFF;
                s.Wb(Scratch, al);
                al = Pickups.Roll(s) & 0xF;
                int dy = (S16(s.Rw(b + 2)) >> 3) + 0x20;
                if (!(S16(s.Rw(TargetY)) >= dy)) al = -al & 0xFF;
                if (flip) al = -al & 0xFF;
                s.Wb(Scratch + 1, al);
                al = RngRotate(s) & 0x1F;                                        // [5518] vx final
                if (S8(s.Rb(Scratch)) < 0) al = -al & 0xFF;
                s.Wb(b + 4, al);
                if (s.Rb(Scratch) == 0) al = 2;                                  // [5529] vy final
                else
                {
                    int bl = Math.Abs(S8(s.Rb(Scratch))) & 0xFF;
                    int ax = (Math.Abs(S8(s.Rb(Scratch + 1))) << 3) & 0xFFFF;
                    al = (ax / bl) & 0xFF;
                }
                if (al >= 0x20) al = 0x20;
                if (S8(s.Rb(Scratch + 1)) < 0) al = -al & 0xFF;
                s.Wb(b + 5, al);
                if ((s.Rb(FrameGate) & 0xF) == 0 && (s.Rb(b + 7) & 7) != 0) s.Wb(b + 7, (s.Rb(b + 7) - 1) & 0xFF);   // [5562]
            }
            int add = S8(s.Rb(b + 4)) & 0xFFFF, res = (x + add) & 0xFFFF;       // [5573] x += vx, undone on signed overflow
            bool of = (((x ^ res) & (add ^ res)) & 0x8000) != 0;
            s.Ww(b, of ? x : res);
            s.Ww(b + 2, (s.Rw(b + 2) + (S8(s.Rb(b + 5)) & 0xFFFF)) & 0xFFFF);
        }
    }

    /// <summary>
    /// [asm 5584-55F1] draw half of 54AB: one OR pixel per live slot. Screen pos sx = (x&gt;&gt;3) - cam_col*16, sy = (y&gt;&gt;3) - cam_row*16
    /// (arithmetic shift, camera in whole 16 px tiles: no fine scroll, as in the ASM); cull sx &gt;= 0x140 / sy &gt;= 0xB0 (unsigned). Colours follow the
    /// GC Set/Reset path: even timer -&gt; plane 0 keeps its background bit (colour 14) and planes 1..3 are ORed; odd timer -&gt; all four planes (15).
    /// (dos_re has no Set/Reset, so its VM oracle collapses every firefly to 15; here the real 14/15 flicker is restored.)
    /// </summary>
    public static void DrawFireflies(LevelState s, byte[][] planes, int page, int camX, int camY)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            int b = Slots + i * SlotSize, x = s.Rw(b);
            if (x == 0x55AA) continue;                                        // [54C2] free slot
            int sy = ((S16(s.Rw(b + 2)) >> 3) - camY) & 0xFFFF;               // [55A3] y>>3 - cam_row*16
            if (sy >= 0xB0) continue;                                         // off top/bottom (unsigned)
            int sx = ((S16(x) >> 3) - camX) & 0xFFFF;                         // x>>3 - cam_col*16
            if (sx >= 0x140) continue;                                        // off left/right (unsigned)
            int off = (page + (sy & 0xFF) * 0x28 + (sx >> 3)) & 0xFFFF;
            byte bit = (byte)(0x80 >> (sx & 7));
            int first = (s.Rb(b + 6) & 1) == 0 ? 1 : 0;                       // [5584] even timer: Set/Reset forces plane 0 to 0
            for (int p = first; p < 4; p++) planes[p][off] |= bit;
        }
    }

    /// <summary>
    /// [asm 3922] scripted-scroll pass. State: [0x2DBE]++; every 4th frame ([0x6BD5]&amp;3 == 0) the current 6-byte script entry at [0x2DBC]
    /// {threshold, delta, clamp, next} (threshold 0xFFFF = none, the case of every level except LEVELG) ramps the wind [0x6BF6] by delta,
    /// clamped to [0, clamp], and advances to the next entry. Render half (3963..39DE, LEVELG snow): with wind != 0 each of the first <c>wind</c>
    /// flakes at [0x6CA9] advances by 0x4F (camera-relative wrap) and one slot is refreshed from three 39DF draws; the white pixels are not drawn.
    /// </summary>
    public static void StepScrollScript(LevelState s)
    {
        int counter = (s.Rw(ScrollCounter) + 1) & 0xFFFF;
        s.Ww(ScrollCounter, counter);
        if ((s.Rb(FrameGate) & 3) == 0)
        {
            int ptr = s.Rw(ScriptPtr), threshold = s.Rw(ptr);
            if (threshold != 0xFFFF)
            {
                int scroll = s.Rw(Wind), delta = s.Rw(ptr + 2), clamp = s.Rw(ptr + 4), next = s.Rw(ptr + 6);
                if (threshold < counter)
                {
                    scroll = (scroll + delta) & 0xFFFF;
                    if (!(next >= counter)) s.Ww(ScriptPtr, (ptr + 6) & 0xFFFF);
                }
                s.Ww(Wind, Math.Min(Math.Max(0, S16(scroll)), S16(clamp)) & 0xFFFF);
            }
        }
        int wind = s.Rw(Wind);
        if (wind == 0) return;
        int cam = s.Rw(CamX);
        for (int i = 0; i < wind; i++)
        {
            int a = Flakes + i * 2, v = (s.Rw(a) + 0x4F) & 0xFFFF;
            s.Ww(a, v);
            int bx = ((v - cam) & 0xFFFF) & 0x1FFF;
            if (bx >= 0x1B58) s.Ww(a, bx - 0x1B58);
        }
        int r1 = Pickups.Roll(s), r2 = Pickups.Roll(s), r3 = Pickups.Roll(s);
        s.Ww(Flakes + r1 * 2, (r2 << 8) | r3);
    }

    /// <summary>
    /// [asm 5326-53D5] the state half of the transition for the matched entry <paramref name="si"/>: velocities 0, player snapped to the
    /// destination tile, the hidden camera pan (Y first with one full row per step, then X) until the camera equals the destination camera,
    /// [0x6BD9] = flag, trigger disarmed ([0x6BE1] = 0), level-6 boss re-init. The caller then runs the arrival mini-pass and the frame remainder.
    /// </summary>
    public static void Teleport(LevelState s, int si)
    {
        int destCam = s.Rw(si + 2), destTile = s.Rw(si + 4), flag = s.Rb(si + 6);
        int destX = destCam & 0xFF, destY = (destCam >> 8) & 0xFF;
        s.Ww(Off.PlayerXvel, 0);                                  // [5326]
        s.Ww(Off.PlayerYvel, 0);                                  // [532C]
        s.Wb(Off.FineScroll, 0);                                  // [5335] (30C6 fade-out is VRAM only)
        int saved = s.Rw(Off.ScrollAccum);                        // [533A]
        s.Ww(Off.ScrollAccum, 0xEC);                              // [533F] right-pan clamp -> max
        s.Ww(Off.PlayerX, (destTile & 0xFF) << 4);                // [5350]
        s.Ww(Off.PlayerY, ((destTile >> 8) & 0xFF) << 4);         // [535A]
        while (s.Rw(Off.CamRow) != destY)                         // [5361-5375]
        {
            bool ok = s.Rw(Off.CamRow) > destY ? LevelCamera.VScrollUp(s, 0x10) : LevelCamera.VScrollDown(s, 0x10);
            if (!ok) throw new ObjectGapException($"cave pan blocked vertically at row {s.Rw(Off.CamRow)} dest {destY}");
        }
        while (s.Rw(Off.CamCol) != destX)                         // [5377-5387]
            if (!LevelCamera.Pan(s, s.Rw(Off.CamCol) < destX))
                throw new ObjectGapException($"cave pan blocked horizontally at col {s.Rw(Off.CamCol)} dest {destX}");
        s.Ww(Off.ScrollAccum, saved);                             // [538A]
        s.Wb(TriggerFlag6BD9, flag);                              // [5391]
        s.Wb(DropGate, 0);                                        // [5394]
        if (s.Rb(Off.Level) == 5)                                 // [5399] level 6 (inside a tree) boss re-init
            Level6BossInit(s);
    }

    /// <summary>[asm 53A0-53D5] the level-6 boss re-init of the cave teleport (also used by the PRE2_PLACE debug hook, which warps without taking the cave).</summary>
    public static void Level6BossInit(LevelState s)
    {
        s.Wb(Off.LevelFlags, s.Rb(Off.LevelFlags) & 1);           // [53A0]
        foreach (var (off, val) in new[] { (0xA324, 0), (0xA325, 5), (0xA326, 0), (0xA328, 0), (0xA32A, 1), (0xA329, 0), (0xA32B, 0x6E), (0xA32C, 8) })
            s.Wb(off, val);                                       // [53A5-53CD]
        for (int k = 0; k < 0x69; k++) s.Wb(TerrainEntityBase + k, 0xFF);   // [53CD-53D5]
    }
}
