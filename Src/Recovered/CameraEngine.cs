using Pre2Port.Native;

namespace Pre2Port.Recovered;

/// <summary>
/// The per-frame level camera / scroll engine 1030:70D7..7579 (called from 6822 when [0x91FE] != 0xFF): spawn cursor + scroll (70D7..7172),
/// player-follow distance (7172), the 8-state camera sequencer (71AB..7534, boundary crush 81B4/81F3/824D, state-6 finale 7491 + 94F3) and its
/// camera-script bytecode (7534: 93B2 / 93F6 / 94DC, target scan 80DE / 8182). Port of <c>pre2/recovered/object_spawn.py camera_engine</c>.
/// Memory is mutated IN PLACE in asm order; every leaf reads what the Python reads at its entry (its overlay applies writes between leaves only).
/// Audio: sfx 1/3/8 go to <see cref="LevelState.Sfx"/>, the 7585 boss-music load (song 0xD) to <see cref="LevelState.SongRequest"/>.
/// </summary>
public static class CameraEngine
{
    // spawn cursor / scroll (70D7 head)
    const int SpawnCount = 0x91FC, SpawnGatePtr = 0x564C, CursorX = 0x91FF, CursorY = 0x9201, CursorXLo = 0x91F7, CursorXHi = 0x91F9;
    const int ScrollVx = 0x6C06, ScrollVy = 0x6C08, ScrollStop = 0x6BEA, CamState = 0x91FE, ScrollPhase = 0x6C05, ScrollPush = 0x91FB;
    const int EffectRow = 0x56A2, FloorProps = 0x7F5E;
    // player / state
    const int PlayerX = 0x4F1C, PlayerY = 0x4F1E, PXvel = 0x4F22, PMode = 0x4F24, PYvel = 0x4F2A, PDeath = 0x4F2D;
    const int ClubRec = 0x4F0A, ProjBase = 0x4F2E, Stride = 0x12;
    const int AnimGate = 0x6BD0, LowGravity = 0x6BC7, Glider = 0x6BC5, Energy = 0x27D6, HurtCooldown = 0x6BC9, InUp = 0x27EA, RespawnState = 0x6BE4;
    const int FrameStamp = 0x6BD5, HitDebounce = 0xA3FD, AttackV19 = 0x7B19, HitFlag = 0xA330;
    const int BurstX = 0xA336, BurstY = 0xA338, BurstSprite = 0xA33A;
    // camera sequencer / script
    const int DistDir = 0xA3FA, DistX = 0xA3FB, CamTimer = 0xA3F7, CmdByte = 0xA3F9, ScriptCursor = 0xA3FF, ScriptPtr = 0xA401, ScriptLast = 0x6C0A;
    const int LatchX = 0xA403, LatchY = 0xA405;
    const int Param1 = 0xA407, T0X = 0xA409, T0Y = 0xA40B, Param2 = 0xA40D, T1X = 0xA40F, T1Y = 0xA411, T1P = 0xA413, T2X = 0xA415, T2Y = 0xA417, T2P = 0xA419,
              T3X = 0xA41B, T3Y = 0xA41D, T3P = 0xA41F;
    const int TargetC0 = 0xA421, TargetA = 0xA423, TargetB = 0xA425;                 // CAM_TARGET_A/B/C of the python (A421/A423/A425)
    const int ScriptIntro = 0xA427, ScriptPan = 0xA434, ScriptSpawn = 0xA43B, ScriptIdle = 0xA45E, ScriptIdleHi = 0xA460, ScriptIdleLo = 0xA462,
              Script73A4 = 0xA464, Script73D1 = 0xA46B, Script73DA = 0xA470, Script743D = 0xA475, Script7471 = 0xA47E, Script7481 = 0xA480;
    const int CmdTable = 0xA571, OffsetTable = 0xA6ED, BossTargets = 0x5648;

    static int S16(int v) => (short)(v & 0xFFFF);
    static int Sar16(int v, int n) => (S16(v) >> n) & 0xFFFF;
    static int Abs16(int d) { d &= 0xFFFF; return (d & 0x8000) != 0 ? (0x10000 - d) & 0xFFFF : d; }
    static int Cbw(int v) { v &= 0xFF; return (v & 0x80) != 0 ? (v - 0x100) & 0xFFFF : v; }

    /// <summary>[asm 70D7..7579] one frame of the camera engine.</summary>
    public static void Step(LevelState s)
    {
        TickScrollCursor(s);                                            // [70D7..7172]
        if (PlayerCursorDist(s)) return;                                // [7172..71AB] too far -> jmp 7579
        bool finale = s.Rb(CamState) == 6 && S16(s.Rw(ScrollVy)) >= 0; // [747A-748C] the state-6 finale RETs at 74E9, skipping 7534
        StateMachine(s);                                                // [71AB..7534]
        if (!finale) ScriptInterp(s);                                   // [7534..7579]
    }

    // ---- 7585 ----------------------------------------------------------------------------------------------------------------------

    /// <summary>[asm 7585] spawn min(cx,8) sprite-0x135 effects as a row (X 0xD step 5, Y 0xAA), load the boss song 0xD when cx &gt; 0, terminate the rest.</summary>
    internal static void InitEffectRow(LevelState s, int cx)
    {
        int n = cx < 8 ? cx : 8;
        int x = 0xD;
        if (n > 0) s.SongRequest = 0xD;                                 // [7599-759C] 02CC(0xD) = MONSTER.TRK
        for (int i = 0; i < 8; i++)
        {
            int p = EffectRow + i * Stride;
            if (i < n) { s.Ww(p + 4, 0x135); s.Ww(p, x); s.Ww(p + 2, 0xAA); x = (x + 5) & 0xFFFF; }
            else s.Ww(p + 4, 0xFFFF);
        }
    }

    // ---- 70D7 head -----------------------------------------------------------------------------------------------------------------

    static void TickScrollCursor(LevelState s)
    {
        int di = s.Rw(SpawnGatePtr);
        int cx = (s.Rw(SpawnCount) >> 3) & 0xFFFF;
        if (di == 0xFFFF || s.Rb(CamState) == 6 || (di & 0x2000) == 0) cx = 0;
        int vx = s.Rw(ScrollVx), vy = s.Rw(ScrollVy), curX = s.Rw(CursorX), curY = s.Rw(CursorY);   // read before 7585 touches the row (disjoint from these)
        InitEffectRow(s, cx);                                           // [70F9]

        int ax = (Sar16(vx, 4) + curX) & 0xFFFF;                        // [70FE-7103]
        if ((ax & 0x8000) == 0 && S16(s.Rw(CursorXLo)) <= S16(ax) && S16(s.Rw(CursorXHi)) >= S16(ax))
        {
            s.Ww(CursorX, ax);                                          // [7115]
            curX = ax;
        }
        int nyc = (curY + Sar16(vy, 4)) & 0xFFFF;                       // [7118]
        s.Ww(CursorY, nyc);
        bool landed = false;
        if (S16(vy) >= 0)                                               // [7121-7126]
        {
            int col = Sar16(curX, 4) & 0xFF, row = Sar16(nyc, 4) & 0xFF;
            int tile = MapRb(s, (col | (row << 8)) & 0xFFFF);           // [7128-713B]
            if (s.Rb(FloorProps + tile) != 0)                           // [7142-7144]
            {
                s.Ww(CursorY, nyc & 0xFFF0);                            // [7146]
                if (S16(vy) != 0) s.Wb(ScrollStop, 7);                  // [714B]
                s.Ww(ScrollVy, 0); s.Ww(ScrollVx, 0);                   // [7157]
                landed = true;
            }
        }
        if (!landed && S16(vy) < 0xE0) s.Ww(ScrollVy, (vy + 0x10) & 0xFFFF);   // [7165]
    }

    static int MapRb(LevelState s, int off) => s.Data[((s.Rw(LevelState.Off.MapSeg) << 4) + (off & 0xFFFF)) & LevelState.AddrMask];

    // ---- 7172 ----------------------------------------------------------------------------------------------------------------------

    /// <summary>[asm 7172..71AB] returns the cull flag (|dX| &gt; 0x190 or |dY| &gt; 0xFA).</summary>
    static bool PlayerCursorDist(LevelState s)
    {
        int px = s.Rw(PlayerX), curX = s.Rw(CursorX);
        s.Wb(DistDir, curX >= px ? 0 : 1);                              // [7177] jae (unsigned)
        int xd = Abs16((px - curX) & 0xFFFF);
        s.Ww(DistX, xd);
        if (xd > 0x190) return true;
        int yd = Abs16((s.Rw(PlayerY) - s.Rw(CursorY)) & 0xFFFF);
        return yd > 0xFA;
    }

    // ---- 757A ----------------------------------------------------------------------------------------------------------------------

    static void IncScrollPhase(LevelState s) { int v = s.Rb(ScrollPhase); s.Wb(ScrollPhase, v == 0xFF ? 0xFF : v + 1); }

    // ---- 8D7B users: 80DE / 8182 ---------------------------------------------------------------------------------------------------

    /// <summary>[asm 8182] one sprite vs the camera targets; the returned value is the ASM's CF at the RET.</summary>
    static bool TargetCollision(LevelState s, int si)
    {
        if (s.Rw(si + 4) == 0xFFFF) return false;                       // [8186]
        int di = s.Rw(TargetA);                                         // [8188]
        int ida = s.Rw(di + 4) & 0x1FFF;
        if (ida == 0x19C || ida == 0x19D)                               // [818C-819A]
        {
            bool hit = PlayerInteraction.HitboxOverlap(s, si, di);      // [819C]
            if (hit) { s.Ww(si + 4, 0xFFFF); return false; }            // [819F-81B2] projectile consumed, NOT a response hit
        }
        return PlayerInteraction.HitboxOverlap(s, si, s.Rw(TargetB));   // [81A8-81AF]
    }

    /// <summary>[asm 80DE] the camera-target collision scan + response.</summary>
    static void ScanCameraTargets(LevelState s)
    {
        int siHit = -1;
        if (TargetCollision(s, ClubRec)) siHit = ClubRec;               // [80DE-80E4] player record first
        else
            for (int k = 0; k < 4; k++)
            {
                int p = ProjBase + k * Stride;
                if (TargetCollision(s, p)) { siHit = p; break; }        // [80E6-80F4]
            }
        if (siHit < 0) return;                                          // [80F6]

        int stamp = s.Rw(FrameStamp);                                   // [80F7]
        if (Abs16((stamp - s.Rw(HitDebounce)) & 0xFFFF) <= 0x16) return;   // [80FC-8107]
        s.Ww(HitDebounce, stamp);                                       // [8109]
        s.Sfx(8);                                                       // [8110]
        for (int k = 0; k < 5; k++) { int f = BossTargets + k * Stride + 5; s.Wb(f, s.Rb(f) | 0x40); }   // [8113-8127]
        s.Wb(Glider, 0);                                                // [812C]
        IncScrollPhase(s);                                              // [8131]
        int al = s.Rb(AttackV19);                                       // [8134]
        int nfc = (s.Rw(SpawnCount) - al) & 0xFFFF;                     // [8139]
        s.Ww(SpawnCount, nfc);
        if ((nfc & 0x8000) != 0)                                        // [813D]
        {
            s.Wb(CamState, 6);                                          // [813F]
            s.Ww(ScrollVy, 0xFF80);                                     // [8144]
            return;
        }
        if (al > 0x14)                                                  // [814C]
        {
            int dx = S16(s.Rw(CursorX)) >= S16(s.Rw(PlayerX)) ? 0x30 : 0xFFD0;   // [8153-815C]
            s.Ww(ScrollVx, dx);                                         // [815E]
            s.Ww(ScrollVy, S16(s.Rw(ScrollVy)) > 0 ? 0xFF80 : 0xFFC0); // [8162-816E]
            s.Wb(CamState, 5);                                          // [8171]
        }
        s.Ww(CamTimer, 0);                                              // [8176]
        s.Ww(siHit + 4, 0xFFFF);                                        // [817C]
    }

    // ---- 824D / 81F3 / 81B4 --------------------------------------------------------------------------------------------------------

    /// <summary>[asm 824D] the crush hurt effect: damage cooldown (reload 5, energy-- ; underflow -> 65B3), then one 0x2046 hurt sprite via 8D1B.</summary>
    internal static void HurtEffect(LevelState s)
    {
        int cd = (s.Rb(HurtCooldown) - 1) & 0xFF;
        if ((cd & 0x80) != 0)                                           // [8254]
        {
            s.Wb(HurtCooldown, 5); cd = 5;
            int en = (s.Rb(Energy) - 1) & 0xFF;
            s.Wb(Energy, en);
            if ((en & 0x80) != 0) PlayerCollision.OffCameraTrigger(s);  // [825F-8261] 65B3
        }
        else s.Wb(HurtCooldown, cd);
        s.Ww(BurstX, s.Rw(PlayerX));                                    // [8264]
        s.Ww(BurstY, (s.Rw(PlayerY) - 0x30) & 0xFFFF);                  // [826A]
        int ax = (cd & 1) != 0 ? 0xFFD0 : 0x30;                         // [8276-8280]
        s.Ww(BurstSprite, 0x2046);                                      // [8285]
        Pickups.Burst(s, ax, 0xFF80, 1);                                // [828B] 8D1B
    }

    /// <summary>[asm 81F3] one boundary target vs the player: on overlap, knock the scroll phase down, hurt the player and bounce him away.</summary>
    static void TargetBounce(LevelState s, int di)
    {
        di &= 0xFFFF;
        if (s.Rw(di + 4) == 0xFFFF) return;                             // [81F3]
        if ((s.Rb(di + 5) & 0x20) == 0) return;                         // [81F9]
        if (!PlayerInteraction.HitboxOverlap(s, PlayerX, di)) return;   // [8201] 8D7B
        s.Sfx(1);
        int al = (4 - s.Rb(ScrollPush)) & 0xFF;                         // [8206]
        int r = (s.Rb(ScrollPhase) - al) & 0xFF;                        // [820C]
        s.Wb(ScrollPhase, (r & 0x80) != 0 ? 0 : r);                     // [8210]
        int px = s.Rw(PlayerX), cur = s.Rw(CursorX);
        HurtEffect(s);                                                  // [8217]
        s.Wb(PDeath, 0x2C);                                             // [8220]
        s.Wb(AnimGate, 0);                                              // [8225]
        s.Ww(PYvel, 0xFF80);                                            // [822A]
        s.Wb(PMode, 3);                                                 // [8230]
        s.Ww(PXvel, S16(px) >= S16(cur) ? 0x80 : 0xFF80);               // [8235-8244]
        s.Wb(LowGravity, 0);                                            // [8247]
    }

    /// <summary>[asm 81B4] the whole camera-boundary crush (gated off while [0x6BE4] != 0).</summary>
    static void BoundaryCollision(LevelState s)
    {
        if (s.Rb(RespawnState) != 0) return;
        TargetBounce(s, s.Rw(TargetC0));                                // [81BE]
        TargetBounce(s, s.Rw(TargetA));                                 // [81C5]
        bool hit = PlayerInteraction.HitboxOverlap(s, PlayerX, s.Rw(TargetB));   // [81D0]
        if (hit && s.Rb(HitFlag) != 0)                                  // [81D3-81D5]
        {
            bool up = s.Rb(InUp) != 0;
            if (up) s.Sfx(3);
            s.Ww(PYvel, up ? 0xFF80 : 0xFFC0);                          // [81DC-81EF]
        }
    }

    // ---- 7491 finale ---------------------------------------------------------------------------------------------------------------

    static void State6Finale(LevelState s)
    {
        s.Wb(CamState, 0xFF);                                           // [7491]
        for (int k = 0; k < 5; k++) s.Ww(BossTargets + k * Stride + 4, 0xFFFF);   // [7496-74A5]
        s.Ww(BurstX, s.Rw(BossTargets)); s.Ww(BurstY, s.Rw(BossTargets + 2));      // [74A8-74B1]
        Pickups.FoodFountain(s);                                        // [74B4] burst 1
        s.Ww(BurstX, (s.Rw(BurstX) + 0x20) & 0xFFFF); s.Ww(BurstY, (s.Rw(BurstY) + 0x20) & 0xFFFF);   // [74B7-74BC]
        Pickups.FoodFountain(s);                                        // [74C1] burst 2
        s.Ww(BurstX, (s.Rw(BurstX) - 0x40) & 0xFFFF);                   // [74C4]
        Pickups.FoodFountain(s);                                        // [74C9] burst 3
        s.Ww(BurstX, (s.Rw(BurstX) - 0x20) & 0xFFFF); s.Ww(BurstY, (s.Rw(BurstY) - 0x40) & 0xFFFF);   // [74CC-74D1]
        Pickups.FoodFountain(s);                                        // [74D6] burst 4
        s.Ww(BurstSprite, 0x63);                                        // [74E0]
        Pickups.Burst(s, 0, 0, 1);                                      // [74D9-74E6]
    }

    // ---- 71AB..7534 the camera sequencer --------------------------------------------------------------------------------------------

    /// <summary>[asm 7301-7346] ramp the scroll velocity from the cursor-player gap, capped by [0x91FB]*0x50.</summary>
    static void CamScrollVelocity(LevelState s)
    {
        int bx = (s.Rb(ScrollPush) * 0x50) & 0xFFFF;
        s.Ww(ScriptPtr, ScriptIdle);                                    // [730A]
        int diff = (s.Rw(CursorX) - s.Rw(PlayerX)) & 0xFFFF;
        int adist = Abs16(diff);
        int axv, dxv;
        if (adist <= bx)
        {
            int q = (adist / 0xE) & 0xFF;                               // [7328-732C]
            int v = (q << 4) & 0xFFFF;
            axv = (-v) & 0xFFFF; dxv = (v >> 1) & 0xFFFF;
        }
        else { axv = 0xFFA0; dxv = 0x30; }
        s.Ww(ScrollVy, axv);
        s.Ww(ScrollVx, S16(diff) < 0 ? dxv : (-dxv) & 0xFFFF);
    }

    static void StateMachine(LevelState s)
    {
        int state = s.Rb(CamState), dist = s.Rw(DistX), timer = s.Rw(CamTimer), spawn = s.Rw(SpawnCount), vy = s.Rw(ScrollVy);
        int phase = s.Rb(ScrollPhase), curX = s.Rw(CursorX), px = s.Rw(PlayerX);
        int t;
        switch (state)
        {
            case 0:                                                     // [71AB]
                if (dist >= 0xFA) { s.Wb(ScrollPhase, 0); s.Ww(ScriptPtr, ScriptIntro); }
                else { s.Ww(CamTimer, 0); s.Wb(CamState, 1); }
                return;

            case 1:                                                     // [71D6]
                if (dist >= 0xFA) { s.Wb(CamState, 0); return; }
                t = (timer + 1) & 0xFFFF;
                s.Ww(CamTimer, t);
                if (spawn < 0x3C) { s.Wb(CamState, 2); return; }
                s.Ww(ScriptPtr, ScriptPan);
                if (t >= 0x6E) { s.Wb(CamState, 2); IncScrollPhase(s); s.Ww(CamTimer, 0); return; }
                if (s.Rb(AnimGate) != 0 && (s.Rb(FrameStamp) & 7) == 0) { IncScrollPhase(s); return; }
                if (phase >= 0xA) { s.Wb(CamState, 3); s.Ww(CamTimer, 0); }
                else if (phase > 3) { s.Wb(CamState, 2); s.Ww(CamTimer, 0); }
                return;

            case 2:                                                     // [7262]
                t = (timer + 1) & 0xFFFF;
                s.Ww(CamTimer, t);
                if (t < 0x2C)
                {
                    BoundaryCollision(s);                               // [7279]
                    if (dist >= 0x4B) s.Ww(ScriptPtr, ScriptSpawn);
                    else { s.Wb(CamState, 3); s.Ww(CamTimer, 0); }
                    return;
                }
                if (t == 0x2C)
                {
                    BoundaryCollision(s);                               // [7299]
                    s.Ww(ScrollVy, 0xFF20);
                    if (spawn < 0x28 && dist < 0x50) { s.Wb(CamState, 4); s.Ww(CamTimer, 0); }
                    s.Ww(ScriptPtr, ScriptIdle);
                    return;
                }
                if (S16(vy) > 0) s.Ww(ScriptPtr, spawn >= 0x64 ? ScriptIdleHi : ScriptIdleLo);   // [72C4]
                if (dist > 0x50)                                        // [72DB]
                {
                    if (t < 0x58) return;
                    if (t == 0x58) { CamScrollVelocity(s); return; }
                    if (vy == 0) { s.Wb(ScrollPhase, 3); s.Ww(CamTimer, 0); s.Wb(CamState, 1); }
                    return;
                }
                s.Ww(CamTimer, 0); s.Wb(CamState, 3); s.Wb(ScrollPhase, 0xB);   // [72E2-72ED]
                return;

            case 3:                                                     // [7366]
                BoundaryCollision(s);                                   // [7370]
                t = (timer + 1) & 0xFFFF;
                s.Ww(CamTimer, t);
                int limit = Abs16((spawn - 0x32) & 0xFFFF) <= 0x19 ? 0x9A : 0x42;   // [7377-7389]
                if (t > limit) { s.Ww(CamTimer, 0); s.Wb(CamState, 2); return; }
                if (S16(vy) < 0) { s.Ww(ScriptPtr, ScriptIdle); return; }
                if (S16(vy) > 0) { s.Ww(ScriptPtr, Script73A4); return; }
                int a = dist;                                           // [73AD] vy == 0
                if (a >= 0x64)
                {
                    s.Ww(ScrollVy, 0xFFAF);
                    s.Ww(ScrollVx, S16(px) > S16(curX) ? 0x50 : 0xFFB0);
                }
                else if (a <= 0x19) s.Ww(ScriptPtr, Script73DA);
                else if (a <= 0x23) { s.Wb(CamState, 4); s.Ww(CamTimer, 0); }
                else
                {
                    if (s.Rb(ScrollPhase) == 0) { s.Wb(CamState, 7); s.Ww(CamTimer, 0); }   // 81B4 may have knocked it down
                    s.Ww(ScriptPtr, Script73D1);
                }
                return;

            case 4:                                                     // [741A]
                BoundaryCollision(s);                                   // [7421]
                t = (timer + 1) & 0xFFFF;
                s.Ww(CamTimer, t);
                if (t > 0x42) { s.Wb(CamState, 3); s.Ww(CamTimer, 0); return; }
                s.Ww(ScriptPtr, Script743D);
                if (vy == 0) s.Wb(ScrollStop, 4);
                return;

            case 5:                                                     // [7452]
                t = (timer + 1) & 0xFFFF;
                s.Ww(CamTimer, t);
                if (t > 0x13) { s.Ww(CamTimer, 0); s.Wb(CamState, 1); }
                else s.Ww(ScriptPtr, Script7471);
                return;

            case 6:                                                     // [747A]
                s.Ww(ScriptPtr, Script7481);
                if (S16(vy) < 0) return;
                State6Finale(s);                                        // [7491-74E9]
                return;

            case 7:                                                     // [74EA]
                t = (timer + 1) & 0xFFFF;
                s.Ww(CamTimer, t);
                if (t > 0x2C) { s.Wb(CamState, 1); return; }
                s.Ww(ScriptPtr, ScriptIdle);
                if (vy == 0)
                {
                    s.Ww(ScrollVy, 0xFF9F);
                    s.Ww(ScrollVx, S16(px) > S16(curX) ? 0xFFD0 : 0x30);
                }
                else if (S16(vy) > 0) s.Ww(ScriptPtr, ScriptIdleHi);
                return;

            default: return;                                            // [74EF] states >= 8 / 0xFF: no-op
        }
    }

    // ---- 7534 the camera-script interpreter -----------------------------------------------------------------------------------------

    /// <summary>[asm 94DC] scan the [0xA6ED] table (stride 6: dx key, ax key, value) for the (dx, ax) pair.</summary>
    static int OffsetLookup(LevelState s, int keyDx, int keyAx)
    {
        int bx = OffsetTable;
        for (int i = 0; i < 4096; i++)
        {
            if (s.Rw(bx) == keyDx && s.Rw(bx + 2) == keyAx) return s.Rw(bx + 4);
            bx = (bx + 6) & 0xFFFF;
        }
        throw new ObjectGapException("94DC camera-offset (dx, ax) not found in the [0xA6ED] table");
    }

    /// <summary>[asm 93F6] lay out the 4 camera targets from 5 script param words; returns the advanced si.</summary>
    static int TargetGeometry(LevelState s, int si)
    {
        bool face = s.Rb(DistDir) != 0;
        int Flip(int p) => face ? (p ^ 0x8000) & 0xFFFF : p & 0xFFFF;
        int cx = s.Rw(LatchX), cy = s.Rw(LatchY);

        int p1 = s.Rw(si); si = (si + 2) & 0xFFFF;                      // [93F9]
        int p2 = s.Rw(si); si = (si + 2) & 0xFFFF;                      // [93FF]
        int off = OffsetLookup(s, p1, p2);                              // [9403]
        int t0x = (cx + (face ? -Cbw(off) : Cbw(off))) & 0xFFFF;
        int t0y = (cy + Cbw(off >> 8)) & 0xFFFF;
        s.Ww(Param1, Flip(p1)); s.Ww(Param2, Flip(p2)); s.Ww(T0X, t0x); s.Ww(T0Y, t0y);

        (int p, int x, int y)[] rest = { (T1P, T1X, T1Y), (T2P, T2X, T2Y), (T3P, T3X, T3Y) };
        int[] ty = new int[3];
        for (int k = 0; k < 3; k++)
        {
            int p = s.Rw(si); si = (si + 2) & 0xFFFF;                   // [9427/944F/9477]
            off = OffsetLookup(s, p2, p);                               // [942B/9453/947B]
            int tx = (t0x + (face ? -Cbw(off) : Cbw(off))) & 0xFFFF;
            ty[k] = (t0y + Cbw(off >> 8)) & 0xFFFF;
            s.Ww(rest[k].p, Flip(p)); s.Ww(rest[k].x, tx); s.Ww(rest[k].y, ty[k]);
        }
        if ((s.Rb(CmdByte) & 0x40) != 0)                                // [949B-94AF]
        {
            s.Ww(T0Y, (t0y + 2) & 0xFFFF);
            for (int k = 0; k < 3; k++) s.Ww(rest[k].y, (ty[k] + 1) & 0xFFFF);
        }
        return si;
    }

    /// <summary>[asm 93B2] populate the 5 camera-target records from the script command struct at si.</summary>
    static void ScriptCommand(LevelState s, int si)
    {
        si = TargetGeometry(s, si);                                     // [93B9]
        int di = BossTargets;
        for (int k = 0; k < 5; k++)                                     // [93BF]
        {
            int bx = s.Rw(si); si = (si + 2) & 0xFFFF;                  // [93C2]
            if (bx == T2X) s.Ww(TargetA, di);                           // [93C4]
            if (bx == T3X) s.Ww(TargetC0, di);                           // [93CE]
            if (bx == T1X) s.Ww(TargetB, di);                          // [93D8]
            int param = s.Rw(bx), x = s.Rw(bx + 2), y = s.Rw(bx + 4);
            s.Ww(di, param); s.Ww(di + 2, x); s.Ww(di + 4, y);          // [93E4-93EE]
            di = (di + Stride) & 0xFFFF;
        }
    }

    static void ScriptInterp(LevelState s)
    {
        int ax = s.Rw(ScriptPtr);                                       // [7534]
        if (s.Rw(ScriptLast) != ax) { s.Ww(ScriptLast, ax); s.Ww(ScriptCursor, ax); }   // [7537-7540]
        int al = 0;
        bool found = false;
        for (int i = 0; i < 4096; i++)                                  // [7543] skip JUMP opcodes
        {
            int cursor = s.Rw(ScriptCursor);
            al = s.Rb(cursor);
            if ((al & 0x80) == 0) { found = true; break; }              // [7549] jns
            s.Ww(ScriptCursor, (cursor + Cbw(al)) & 0xFFFF);            // [754D-754E]
        }
        if (!found) throw new ObjectGapException("7534 camera-script JUMP loop did not terminate");
        s.Wb(CmdByte, al);                                              // [7554]
        int cmd = al & 0xBF;                                            // [7557]
        s.Ww(ScriptCursor, (s.Rw(ScriptCursor) + 1) & 0xFFFF);          // [7559]
        int si = (cmd * 0x14 + CmdTable) & 0xFFFF;                      // [755D-7563]
        s.Ww(LatchX, s.Rw(CursorX)); s.Ww(LatchY, s.Rw(CursorY));       // [7567-7570]
        ScriptCommand(s, si);                                           // [7573]
        ScanCameraTargets(s);                                           // [7576]
    }
}
