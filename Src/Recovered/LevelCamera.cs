using Pre2Port.Native;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Recovered;

/// <summary>
/// Camera for the level first frame. Port of <c>pre2/native/level_init.py native_3af2</c> (level-start snap-scroll onto the
/// player) with its helpers from <c>pre2/native/camera_scroll.py</c> (57A8 horizontal follow, 5663 vertical follow, 33AD/3363
/// scroll primitives), the pan state of <c>pre2/views/camera_pan.py apply_camera_pan</c> (3414/3435; the tile-column BLIT is
/// not done: the renderer rebuilds the ring from state), <c>frame_renderer.calc_scroll_source</c> (3588),
/// <c>animation.advance_animation</c> (367D) and <c>render.py native_sync_render_state</c> (per displayed frame).
/// </summary>
public static class LevelCamera
{
    const int Ds = BootImage.DsBase;
    const int ScrollDoneFlag = 0x6771;      // cs:[0x6771] snap flag (camera_scroll.py SCROLL_DONE_FLAG)
    const int RingCols = 0x14, RingRows = 0x0C;
    const int AnimEnd = 0x6988, FastThreshold = 0x14;

    static int S16(int v) => (short)(v & 0xFFFF);
    static int Sar16(int v, int n) => (S16(v) >> n) & 0xFFFF;

    /// <summary>[asm 3588] [0x2DBA] = 2*col + 0x280*row + 0x3F40 (16-bit).</summary>
    public static int CalcScrollSource(int col, int row)
    {
        int ax = (col << 1) & 0xFFFF;
        int dx = (row << 8) & 0xFFFF;
        int bx = (dx << 1) & 0xFFFF;
        dx = (dx >> 1) & 0xFFFF;
        return (ax + dx + bx + 0x3F40) & 0xFFFF;
    }

    // ---- 3af2 ---------------------------------------------------------------------------------------------------------

    /// <summary>[asm 3af2] reset the camera cells, snap-scroll the camera onto the player (5634 loop), then centre it if the player is far right.</summary>
    public static void Init(LevelState s)
    {
        s.Ww(Off.CamCol, 0); s.Ww(Off.CamRow, 0); s.Ww(Off.ColRing, 0); s.Ww(Off.RowRing, 0);   // [asm 3afe-3b07]
        for (int i = 0; i < 8000; i++)                        // [asm 3b35] 5634 run to convergence
        {
            s.CsWb(ScrollDoneFlag, 1);                        // [asm 5634] snap flag (forces dl = 0x10)
            if (s.Rb(Off.Gate6BD9) == 0 && (s.Rb(Off.LevelFlags) & 2) == 0)
                HFollow(s);                                   // [asm 565c] 57a8
            VFollow(s);                                       // [asm 565f] 5663
            s.CsWb(ScrollDoneFlag, 0);                        // [asm 563d]
            if (s.Rb(Off.ScrollDir) == 0 && s.Rb(Off.CamScrollIdle) == 0) break;   // [asm 3b38]
        }
        if ((s.Rb(Off.LevelFlags) & 2) == 0)                  // [asm 3b41]
        {
            int pxTile = Sar16(s.Rw(Off.PlayerX), 4);
            int camX = s.Rw(Off.CamCol);
            if (((pxTile - camX) & 0xFFFF) >= 0xC)            // [asm 3b53] still > 0xc cols right -> pan right x10
                for (int k = 0; k < 0xA; k++) Pan(s, true);
        }
        s.Wb(Off.GridDirty, 1);                               // [asm 3b60]
        s.Ww(Off.PrevCamX, 0x55AA);                           // [asm 3b65]
    }

    // ---- vertical scroll primitives -----------------------------------------------------------------------------------

    internal static bool VScrollDown(LevelState s, int dl)
    {
        int maxy = (s.Rb(Off.MapRows) - 0xB) & 0xFFFF;        // [33B6-33BB] bottom camera limit
        if (s.Rw(Off.CamRow) >= maxy) return false;           // [33BE]
        s.Wb(Off.ScrollAnimCtr, s.Rb(Off.ScrollAnimCtr) + 1); // [33C4]
        int acc = (s.Rb(Off.FineScroll) + (dl & 0xFF)) & 0xFF;
        if (acc < 0x10) { s.Wb(Off.FineScroll, acc); return true; }
        acc -= 0x10;
        int cy = (s.Rw(Off.CamRow) + 1) & 0xFFFF;
        s.Ww(Off.CamRow, cy);
        if (cy >= maxy) acc = 0;
        s.Wb(Off.FineScroll, acc);
        int dea = (s.Rw(Off.RowRing) + 1) & 0xFFFF;           // [33E7-33F0] wrap at 0xC
        if (dea >= 0xC) dea = 0;
        s.Ww(Off.RowRing, dea);
        s.Ww(Off.ScrollSrc, CalcScrollSource(s.Rw(Off.ColRing), dea & 0xFF));   // [33F8 -> 3588]
        return true;
    }

    internal static bool VScrollUp(LevelState s, int dl)
    {
        if (s.Rw(Off.CamRow) == 0) return false;              // [336C]
        s.Wb(Off.ScrollAnimCtr, s.Rb(Off.ScrollAnimCtr) + 1); // [3373]
        int raw = s.Rb(Off.FineScroll) - (dl & 0xFF);
        if (raw >= 0) { s.Wb(Off.FineScroll, raw & 0xFF); return true; }
        s.Wb(Off.FineScroll, (raw + 0x10) & 0xFF);
        s.Ww(Off.CamRow, (s.Rw(Off.CamRow) - 1) & 0xFFFF);
        int dea = s.Rw(Off.RowRing) - 1;                      // [3386-338C] wrap to 0xB
        if (dea < 0) dea = 0xB;
        s.Ww(Off.RowRing, dea & 0xFFFF);
        s.Ww(Off.ScrollSrc, CalcScrollSource(s.Rw(Off.ColRing), dea & 0xFF));   // [3394 -> 3588]
        return true;
    }

    // ---- horizontal follow (57A8) --------------------------------------------------------------------------------------

    static void HInit(LevelState s, int dxs)
    {
        int xvel = S16(s.Rw(Off.PlayerXvel));
        bool right;
        if (xvel != 0 && (s.Rb(Off.PlayerMoveFlag) & 0x80) != 0) right = false;   // [57FD] moving left
        else if (xvel != 0) right = true;
        else right = dxs >= 0xA;                              // [5806]
        if (right) { if (dxs >= 0x10) s.Wb(Off.ScrollDir, 1); }
        else { if (dxs <= 4) s.Wb(Off.ScrollDir, 2); }
    }

    static void HFollow(LevelState s)
    {
        int wk = LevelRender.ExtraCols;                       // port-only (FULLSCREEN=WIDESCREEN): camera beyond the widescreen limit -> pull back one column per tick
        if (wk > 0 && s.Rw(Off.CamCol) > LevelRender.CamLimit(wk)) Pan(s, false);
        int dx = (Sar16(s.Rw(Off.PlayerX), 4) - s.Rw(Off.CamCol)) & 0xFFFF;   // [57AD-57B5]
        int dxs = S16(dx);
        if (dx < 0x14 && s.Rb(Off.Window6BFE) == 0 && S16(s.Rw(Off.PlayerXvel)) == 0)   // [57B9/57BE/57C5]
        {
            s.Wb(Off.ScrollDir, 0);
            return;
        }
        int bed = s.Rb(Off.ScrollDir);
        if (bed == 0) HInit(s, dxs);                          // [57CC]
        else if (bed == 1) { if (dxs <= 5 || !Pan(s, true)) s.Wb(Off.ScrollDir, 0); }    // scrolling right
        else { if (dxs >= 0xF || !Pan(s, false)) s.Wb(Off.ScrollDir, 0); }               // scrolling left
    }

    /// <summary>
    /// 3414 (left) / 3435 (right): state side of apply_camera_pan — camera column, ring index, [0x2DBA], and the OR-accumulated
    /// flags of draw_tile_column (350C) over the revealed 12-tile column ([0x6BBD], [0x2DF2], [0x2DF4], [0x2DF6]). The plane blit is skipped.
    /// </summary>
    internal static bool Pan(LevelState s, bool right)
    {
        int colParam, de8;
        if (!right)
        {
            colParam = 0;
            if (s.Rw(Off.CamCol) == 0) return false;          // [341F-3424]
            s.Ww(Off.CamCol, (s.Rw(Off.CamCol) - 1) & 0xFFFF);
            int ax = (s.Rw(Off.ColRing) - 1) & 0xFFFF;
            de8 = (ax & 0x8000) != 0 ? 0x13 : ax;
        }
        else
        {
            colParam = 0x13;
            int px = (S16(s.Rw(Off.PlayerX)) >> 4) - 0x14;    // [343E-3449]
            int acc = s.Rw(Off.ScrollAccum);
            int clamp = S16(acc) >= px ? acc : 0xEC;          // [344C-345A]
            // Port-only (FULLSCREEN=WIDESCREEN): the extra right columns read the map up to cam+20+k -> cam <= 256-20-k (k = extra canvas columns; 234 at 16:9).
            int wk = LevelRender.ExtraCols;
            if (wk > 0) clamp = Math.Min(clamp, LevelRender.CamLimit(wk));
            if (s.Rw(Off.CamCol) >= clamp) return false;      // [345E]
            s.Ww(Off.CamCol, (s.Rw(Off.CamCol) + 1) & 0xFFFF);
            int ax = (s.Rw(Off.ColRing) + 1) & 0xFFFF;
            de8 = ax >= 0x14 ? 0 : ax;
        }
        s.Ww(Off.ColRing, de8);                               // [3472]
        int scrollSrc = CalcScrollSource(de8, s.Rb(Off.RowRing));
        s.Ww(Off.ScrollSrc, scrollSrc);
        int cell = ((s.Rb(Off.CamRow) << 8) | s.Rb(Off.CamCol)) & 0xFFFF;   // [3478-347C]

        int mapBase = (s.Rw(Off.MapSeg) << 4) & AddrMask;
        int plane = s.Rb(Off.AnyAnimated), tflags = s.Rb(Off.TileFlagsAcc), ttype = s.Rb(Off.GridDirty);
        int bg = (colParam + 0x7E80) & 0xFFFF;
        int si = (cell + colParam) & 0xFFFF;
        for (int r = 0; r < 12; r++)                          // [350C] 12 rows down the column
        {
            int tile = s.Data[mapBase + si];
            plane |= s.Data[Ds + Off.AnimTable3 + tile];      // [3547] 0x6988
            tflags |= s.Data[Ds + Off.TileFlags + tile];      // [3553] 0x805E
            ttype |= s.Data[Ds + Off.BlitType + tile];        // [355F] 0x4DF8
            bg = (bg + 0x40) & 0xFFFF;
            si = (si + 0x100) & 0xFFFF;
        }
        s.Ww(Off.BgRestorePtr, bg);
        s.Wb(Off.AnyAnimated, plane);
        s.Wb(Off.TileFlagsAcc, tflags);
        s.Wb(Off.GridDirty, ttype);
        return true;
    }

    // ---- vertical follow (5663) ----------------------------------------------------------------------------------------

    static int VSpeed(LevelState s, bool down)
    {
        if (s.CsRb(ScrollDoneFlag) != 0) return 0x10;         // snap
        int ay = (s.Rw(Off.PlayerY) - s.Rb(Off.FineScroll)) & 0xFFFF;
        int cell = ((s.Rw(Off.CamRow) + s.Rw(Off.ScrollTargetRow)) << 4) & 0xFFFF;
        int bx = (down ? ay - cell : cell - ay) & 0xFFFF;
        int baseOff = s.Rb(Off.GridDirty) == 0 ? Off.SpeedCurve : s.Rw(Off.SpeedCurvePtr);
        return s.Rb(baseOff + bx);
    }

    static void VFollow(LevelState s)
    {
        if (s.CsRb(ScrollDoneFlag) == 0 && (s.Rb(Off.LevelFlags) & 4) != 0)   // [5668-5675] forced snap-down
        {
            VScrollDown(s, 1);
            return;
        }
        int yvel = S16(s.Rw(Off.PlayerYvel));
        if (yvel == 0) s.Wb(Off.CamScrollIdle, 0);            // [567D]
        int dxs = S16((Sar16(s.Rw(Off.PlayerY), 4) - s.Rw(Off.CamRow)) & 0xFFFF);   // [5689]
        int target = -1;
        if (yvel != 0)
        {
            if (dxs >= 9) target = 3; else if (dxs <= 2) target = 8;
        }
        else if ((s.Rb(Off.LevelFlags) & 1) != 0)
        {
            if (dxs >= 8) target = 7; else if (dxs <= 5) target = 6;
        }
        else
        {
            if (dxs >= 0xA) target = 9; else if (dxs <= 3) target = 8;
        }
        if (target >= 0)                                      // [56D9]
        {
            s.Ww(Off.ScrollTargetRow, target);
            s.Wb(Off.CamScrollIdle, s.Rb(Off.CamScrollIdle) + 1);
        }
        if (s.Rb(Off.CamScrollIdle) == 0) return;             // [56E0]
        VScrollApply(s, dxs);
    }

    static void VScrollApply(LevelState s, int dxs)
    {
        int header = s.Rw(Off.PropHeader);
        int top = (header + 0xB) & 0xFFFF;
        int py = s.Rw(Off.PlayerY);
        if (py <= ((top << 4) & 0xFFFF) && S16(header) < S16((s.Rw(Off.CamRow) - 1) & 0xFFFF))   // [56EA-5701]
        {
            if (!VScrollUp(s, VSpeed(s, false))) s.Wb(Off.CamScrollIdle, 0);
            return;
        }
        if (s.Rb(Off.CamScrollIdle) == 0) return;             // [5703]
        int tgt = s.Rw(Off.ScrollTargetRow);                  // [570D]
        if (tgt == (dxs & 0xFFFF)) { s.Wb(Off.CamScrollIdle, 0); return; }
        if (S16(tgt) > dxs)                                   // [5716] target below -> scroll up
        {
            if (!VScrollUp(s, VSpeed(s, false))) s.Wb(Off.CamScrollIdle, 0);
            return;
        }
        if (py <= ((top << 4) & 0xFFFF) && s.Rw(Off.CamRow) > header)   // [5722-572C]
        {
            s.Wb(Off.CamScrollIdle, 0);
            return;
        }
        if (!VScrollDown(s, VSpeed(s, true))) s.Wb(Off.CamScrollIdle, 0);   // [5763]
    }

    // ---- per game tick (5643) --------------------------------------------------------------------------------------------

    /// <summary>
    /// [asm 5643-5662] native_camera_follow: the whole per-frame camera follow, run after the player update. Clears the snap flag,
    /// then horizontal follow (57A8, unless [0x8166]&amp;2) and vertical follow (5663); gated off entirely by [0x6BD9] != 0.
    /// </summary>
    public static void Follow(LevelState s)
    {
        s.CsWb(ScrollDoneFlag, 0);                            // [asm 5643]
        if (s.Rb(Off.Gate6BD9) != 0) return;                  // [asm 564E]
        if ((s.Rb(Off.LevelFlags) & 2) == 0) HFollow(s);      // [asm 5655-565C]
        VFollow(s);                                           // [asm 565F]
    }

    // ---- per displayed frame -------------------------------------------------------------------------------------------

    /// <summary>
    /// render.py native_sync_render_state: re-derive the render mirrors from the camera (ring indices, previous-camera cells,
    /// scroll source) and step the animated-tile remap cycle (367D) once. Run exactly once per displayed frame, tick 0 included.
    /// </summary>
    public static void SyncRenderState(LevelState s)
    {
        int camX = s.Rw(Off.CamCol), camY = s.Rw(Off.CamRow);
        s.Ww(Off.ColRing, camX % RingCols);
        s.Ww(Off.RowRing, camY % RingRows);
        s.Ww(Off.PrevCamX, camX);
        s.Ww(Off.PrevCamY, camY);
        s.Ww(Off.ScrollSrc, CalcScrollSource(s.Rw(Off.ColRing), s.Rw(Off.RowRing) & 0xFF));

        int fp = s.Rw(Off.AnimRemapPtr);
        int thr = s.Rb(Off.AnimThrottle);
        if (s.Rb(Off.AnyAnimated) != 0)                       // [asm 367D] animated tiles present
        {
            int mask = s.Rw(Off.Friction) >= FastThreshold ? 1 : 3;
            thr = (thr + 1) & 0xFF;
            if ((thr & mask) == 0)
            {
                int ax = ((((fp >> 8) + 1) & 0xFF) << 8) | (fp & 0xFF);    // [369C] inc ah
                if (ax == AnimEnd) ax = ((((ax >> 8) - 3) & 0xFF) << 8) | (ax & 0xFF);   // [36A3]
                fp = ax & 0xFFFF;
            }
        }
        s.Ww(Off.AnimRemapPtr, fp);
        s.Wb(Off.AnimThrottle, thr);
    }
}
