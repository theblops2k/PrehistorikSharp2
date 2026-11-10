using Pre2Port.Native;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Recovered;

/// <summary>
/// The level object system (1030:6822): the static spawner list at DGROUP 0x8489 (variable-length records, copied there by the level
/// load with the rest of the 0x13A7-byte property block) is walked every frame by the second pass (6913..698B), which projects the
/// spawners that are inside the camera window into the 12 live slots at 0x4FD0 (stride 0x12); the first pass (684E..6912) then
/// updates every live slot (velocity, terrain collision, animation, per-type AI). Port of <c>pre2/recovered/object_tick.py</c>
/// (object_tick), <c>object_inject.py</c> (second_pass_tick_bytes and its per-type handlers) and <c>object_update.py</c> (see
/// <see cref="ObjectAi"/>), in the frame order of <c>native_object_system_step</c>: camera/boss spawners (gap), walker, second pass.
/// The slots are drawn by the moving-sprite pass (<see cref="SpriteRender.DrawAll"/>), the same path as the player.
/// Not ported (explicit gaps that throw <see cref="ObjectGapException"/> when reached): everything outside the object slots (bonus 0x8F1D and platforms 0x9107 live in Pickups.cs and Platforms.cs; LevelScene enables them via the bonus / platforms flags). The level-6 tree boss (6D34) is <see cref="TreeBoss"/>, the mode-9 last boss engine (LEVELA) <see cref="Boss"/>.
/// </summary>
public static class LevelObjects
{
    public const int ListBase = 0x8489, EndLen = 0x32;                    // [asm 6916] a record whose length byte is &gt;= 0x32 ends the list
    public const int ObjBase = 0x4FD0, ObjStride = 0x12, ObjCount = 12;  // [asm 684E / 690A / 6851]
    const int ProjSlotPtr = 0xA32E, SpawnRing = 0xA341, SpawnXTable = 0xA343, AuraToggle = 0x6BCC;
    const int FxList = 0x7DE6, FxCount = 20, FxStride = 6;

    static int S16(int v) => (short)(v & 0xFFFF);
    static int S8(int v) => (sbyte)(v & 0xFF);

    /// <summary>One spawner record (offsets relative to the record: +0 length, +1 type | 0x80 cull flag, +2 sprite, +4 mode, +5 aux, +9 X, +0xB Y).</summary>
    public readonly record struct Spawner(int Addr, int Len, int Type, bool BonusOnly, int Sprite, int Mode, int Aux, int X, int Y);

    /// <summary>Parse the spawner list currently in memory (ends at the first record with length &gt;= 0x32).</summary>
    public static List<Spawner> Spawners(LevelState s)
    {
        var list = new List<Spawner>();
        int si = ListBase;
        while (true)
        {
            int len = s.Rb(si);
            if (len >= EndLen) return list;
            if (len == 0) throw new InvalidDataException($"spawner list: zero-length record at {si:X4}");
            list.Add(new Spawner(si, len, s.Rb(si + 1) & 0x7F, (s.Rb(si + 1) & 0x80) != 0, s.Rw(si + 2), s.Rb(si + 4), s.Rb(si + 5), s.Rw(si + 9), s.Rw(si + 0xB)));
            si = (si + len) & 0xFFFF;
        }
    }

    /// <summary>[asm 6822] the whole object system for one frame.</summary>
    public static void Step(LevelState s)
    {
        if (s.Rb(0x91FE) != 0xFF) CameraEngine.Step(s);                    // [asm 6822/6827] camera active -> 70D7
        int level = s.Rb(0x2D8A);
        if (level == 5 && (s.Rb(0x8166) & 0xFE) == 0 && s.Rw(0xA326) != 3) TreeBoss.Step(s);   // [asm 682C-6841] 6D34: the level-6 (inside a tree) boss
        if (level == 9) Boss.Step(s);                                       // [asm 6844-6849] 6ADD: the mode-9 last boss (LEVELA)
        Walk(s);
        SecondPass(s);
    }

    // ---- first pass 684E..6912 ----------------------------------------------------------------------------------------------------

    /// <summary>[asm 684E-6912] per live slot: velocity, terrain collision ([def+4] &amp; 8), animation advance, AI handler cs:[(type*2)&amp;0xFF + 0x6AA9].</summary>
    public static void Walk(LevelState s)
    {
        for (int slot = 0; slot < ObjCount; slot++)
        {
            int si = ObjBase + slot * ObjStride;
            if (s.Rw(si + 4) == 0xFFFF) continue;                        // [6856-685E]
            int x = s.Rw(si), y = s.Rw(si + 2), xv = s.Rw(si + 8), yv = s.Rw(si + 0xA);
            int ny = (y + (S16(yv) >> 4)) & 0xFFFF;                      // [6861-6866]
            int nx = xv == 0xFFFF ? x : (x + (S16(xv) >> 4)) & 0xFFFF;   // [6869-6873] 0xFFFF = no X move
            s.Ww(si, nx); s.Ww(si + 2, ny);
            int d = s.Rw(si + 6);
            if ((s.Rb(d + 4) & 8) != 0)                                  // [6875-687E]
            {
                var o = Obj.Load(s, si);
                var df = Def.Load(s, d);
                ObjectAi.TerrainCollision(s, o, df);
                s.Ww(si, o.X); s.Ww(si + 2, o.Y); s.Ww(si + 8, o.Xvel); s.Ww(si + 0xA, o.Yvel); s.Ww(si + 0xC, o.AnimPtr);
                s.Wb(d + 4, df.D4);
            }
            var anim = ObjectAi.AdvanceAnimation(s, s.Rw(si + 0xC), s.Rw(si + 4), s.Rb(si + 9), s.Rw(0x6BE2));   // [6881-68E6]
            s.Ww(si + 4, anim.Id); s.Ww(si + 0xC, anim.Ptr); s.Wb(0xA340, anim.A340);
            if (anim.Shake) s.Wb(0x6BEA, 9);                             // [68B1]
            int idx = s.Rb(d + 1);                                       // [68EC]
            int tbl = (idx << 1) & 0xFF;                                 // [68EF-68F1] 8-bit: bit7 is a flag
            int tgt = s.CsRb(0x6AA9 + tbl) | (s.CsRb(0x6AA9 + tbl + 1) << 8);   // [68FC]
            var obj = Obj.Load(s, si);
            var def = Def.Load(s, d);
            var g = Glb.Read(s);
            switch (tgt)
            {
                case 0x7C90: ObjectAi.H7C90(s, obj, def, g); break;
                case 0x7C8C: ObjectAi.H7C8C(s, obj, def, g); break;
                case 0x7C2D: ObjectAi.H7C2D(s, obj, def, g); break;
                case 0x7B91: ObjectAi.H7B91(s, obj, def, g); break;
                case 0x7ADF: ObjectAi.H7ADF(s, obj, def, g); break;
                case 0x7A60: ObjectAi.H7A60(s, obj, def, g); break;
                case 0x77DE: ObjectAi.H77DE(s, obj, def, g); break;
                case 0x7898: ObjectAi.H7898(s, obj, def, g); break;
                case 0x78EC: ObjectAi.H78EC(s, obj, def, g); break;
                case 0x760F: ObjectAi.H760F(s, obj, def, g); break;
                case 0x75C4: ObjectAi.H75C4(s, obj, def, g); break;
                case 0x773D: ObjectAi.H773D(s, obj, def, g); break;
                case 0x7665: ObjectAi.H7665(s, obj, def, g); break;
                default: throw new ObjectGapException($"slot {slot}: unported AI handler idx {idx & 0x7F} @ {tgt:X4}");
            }
            obj.Store(s, si);
            def.Store(s, d);
        }
    }

    // ---- second pass 6913..698B -----------------------------------------------------------------------------------------------------

    /// <summary>[asm 806C] first free live slot (sprite word [+4] == 0xFFFF) or -1.</summary>
    static int FindFree(LevelState s)
    {
        for (int i = 0; i < ObjCount; i++)
            if (s.Rw(ObjBase + i * ObjStride + 4) == 0xFFFF) return i;
        return -1;
    }

    /// <summary>[asm 8022] is the pixel (x, y) inside the tile window [-2,22] x [-2,13] around the camera cell?</summary>
    static bool OnScreenTile(int x, int y, int camX, int camY)
    {
        int tx = S16(((S16(x) >> 4) - camX) & 0xFFFF);
        if (tx < -2 || tx > 0x16) return false;
        int ty = S16(((S16(y) >> 4) - camY) & 0xFFFF);
        return ty >= -2 && ty <= 0x0D;
    }

    /// <summary>[asm 7F26 / 7F31] project spawner <paramref name="si"/> into a free slot: x = rec[+9], y = rec[+0xB], sprite = rec[+2], def ptr = si, velocities and state 0, [+0xF] = rec[+5], [+0x10] = 0; rec[+4] = 0x17. Returns the slot base or -1.</summary>
    static int Project(LevelState s, int si, int camX, int camY, bool cull)
    {
        if (cull && !OnScreenTile(s.Rw(si + 9), s.Rw(si + 0xB), camX, camY)) return -1;   // [7F26-7F2F]
        int slot = FindFree(s);                                          // [7F31-7F34]
        if (slot < 0) return -1;
        int b = ObjBase + slot * ObjStride;
        s.Wb(b + 0x10, 0);                                               // [7F36]
        s.Ww(b, s.Rw(si + 9));                                           // [7F3E]
        s.Ww(b + 2, s.Rw(si + 0xB));                                     // [7F43]
        s.Ww(b + 4, s.Rw(si + 2));                                       // [7F49]
        s.Ww(b + 6, si);                                                 // [7F4F]
        s.Wb(si + 4, 0x17);                                              // [7F52]
        s.Ww(b + 8, 0);                                                  // [7F56]
        s.Ww(b + 0xA, 0);                                                // [7F5B]
        s.Wb(b + 0xE, 0);                                                // [7F60]
        s.Wb(b + 0xF, s.Rb(si + 5));                                     // [7F64]
        s.Ww(ProjSlotPtr, b);
        return b;
    }

    static bool ProjectMode(LevelState s, int si, int camX, int camY, int mode)
    {
        if (Project(s, si, camX, camY, true) < 0) return false;
        s.Wb(si + 4, mode);
        return true;
    }

    /// <summary>[asm 6954-6981] descriptor pointer: scan from 0xA86F for the 0x7D01 marker followed by the type, then for the word == sprite - 0x138.</summary>
    static int LookupAnimFrame(LevelState s, int entryId, int entryType)
    {
        int target = (entryId - 0x138) & 0xFFFF, bx = 0xA86F, guard = 0;
        while (true)
        {
            bx = (bx + 2) & 0xFFFF;
            if (s.Rw(bx) == 0x7D01 && s.Rw(bx + 2) == entryType) break;
            if (++guard > 0x8000) throw new ObjectGapException($"anim table: no section marker for type {entryType}");
        }
        bx = (bx + 4) & 0xFFFF;
        guard = 0;
        while (s.Rw(bx) != target)
        {
            bx = (bx + 2) & 0xFFFF;
            if (++guard > 0x8000) throw new ObjectGapException($"anim table: sprite {entryId:X4} not found for type {entryType}");
        }
        return bx;
    }

    /// <summary>[asm 6913-698B] walk the spawner list and dispatch the per-type handler cs:[((type&amp;0x7F)*2) + 0x6AC3].</summary>
    public static void SecondPass(LevelState s)
    {
        int camX = s.Rw(Off.CamCol), camY = s.Rw(Off.CamRow);
        int b198 = s.Rb(0xB198);
        int si = ListBase;
        while (true)
        {
            int stride = s.Rb(si);                                       // [6916]
            if (stride >= EndLen) return;
            if (stride == 0) throw new InvalidDataException($"spawner list: zero-length record at {si:X4}");
            bool skip = s.Rw(si + 2) == 0xFFFF                           // [691B]
                || (s.Rb(si + 4) & 4) != 0                               // [6921]
                || (b198 != 1 && (s.Rb(si + 1) & 0x80) != 0);            // [6927/692E]
            if (!skip)
            {
                int idx = s.Rb(si + 1) & 0x7F;                           // [6934/6939]
                if (Dispatch(s, idx, si, camX, camY))                    // [6952] CF == 0
                {
                    int desc = LookupAnimFrame(s, s.Rw(si + 2), idx);    // [6954-697B]
                    s.Ww(s.Rw(ProjSlotPtr) + 0xC, desc);                 // [697D-6981] [slot+0xC] = descriptor
                }
            }
            si = (si + stride) & 0xFFFF;                                 // [6984]
        }
    }

    static bool Dispatch(LevelState s, int idx, int si, int camX, int camY)
    {
        switch (idx)
        {
            case 0: return PlayerWindowAura(s, si);                      // 7F6C
            case 1: return ProjectMode(s, si, camX, camY, 0x17);         // 7F26
            case 2: return ProjectMode(s, si, camX, camY, 5);            // 7EE2
            case 3: return ProjectMode(s, si, camX, camY, 0x37);         // 7ED8
            case 4:                                                      // 7EBF
                if (Project(s, si, camX, camY, true) < 0) return false;
                s.Wb(si + 4, 5); s.Wb(si + 0xF, 0); s.Wb(si + 0x10, 0);
                return true;
            case 5: case 6: case 7: case 8: return ProjectMode(s, si, camX, camY, 5);   // 7EB5
            case 9:                                                      // 7E97
            {
                s.Wb(si + 0x11, 0);                                      // [7E97] unconditional
                int old = s.Rb(si + 4);                                  // [7E9B]
                if (Project(s, si, camX, camY, true) < 0) return false;
                int m = old | 5;                                         // [7EA5]
                if (s.Rb(0x2D8A) == 6) m |= 0x80;                        // [7EA7]
                s.Wb(si + 4, m);
                return true;
            }
            case 10: return GroundSnap(s, si);
            case 11: return ThrottleJitter(s, si, camX, camY);
            case 12: return ProximityGate(s, si, camX, camY);
            default: throw new ObjectGapException($"second-pass spawn handler idx {idx} (cs:[{idx * 2 + 0x6AC3:X4}]) not ported");
        }
    }

    /// <summary>
    /// [asm 7F6C] idx 0: player-relative window spawner. The player's tile (X, Y low bytes of pos &gt;&gt; 4) must lie in the box [+9]/[+0xA] .. +[+0xB]/[+0xC] cells (byte compares: below the origin or beyond the extent = no
    /// spawn); then a free slot (806C), [0x6BCC] is toggled and the entity appears at playerX +0xC0 (toggle 0) or -0xC0 (toggle 1), playerY - 0xB0, mode [+4] = 7, velocities 0, flip [+0xF] = [+5].
    /// Own projection: no on-screen cull, [+0x10] is cleared only once a slot was found. [0xA32E] = the slot, as 7F26/7F31 do.
    /// </summary>
    static bool PlayerWindowAura(LevelState s, int si)
    {
        int px = s.Rw(0x4F1C), py = s.Rw(0x4F1E);
        int ax = (S16(px) >> 4) & 0xFF;                                  // [7F6E-7F71] SAR 4, AL
        int ox = s.Rb(si + 9), oy = s.Rb(si + 0xA);                      // [7F73] DX = [si+9]: DL = origin X cell, DH = origin Y cell
        if (ax < ox) return false;                                       // [7F76-7F78] SUB AL,DL / JB
        if (s.Rb(si + 0xB) < ((ax - ox) & 0xFF)) return false;           // [7F7A-7F7D] CMP [si+0xB],AL / JB
        int ay = (S16(py) >> 4) & 0xFF;                                  // [7F7F-7F82]
        if (ay < oy) return false;                                       // [7F84-7F86] SUB AL,DH / JB
        if (s.Rb(si + 0xC) < ((ay - oy) & 0xFF)) return false;           // [7F88-7F8B]
        int slot = FindFree(s);                                          // [7F8D] CALL 806C / JB
        if (slot < 0) return false;
        int b = ObjBase + slot * ObjStride;
        s.Wb(b + 0x10, 0);                                               // [7F92]
        s.Ww(ProjSlotPtr, b);                                            // [7F96]
        int toggle = s.Rb(AuraToggle) ^ 1;                               // [7F9D] XOR byte [0x6BCC],1
        s.Wb(AuraToggle, toggle);
        int dx = toggle == 0 ? 0xC0 : 0xFF40;                            // [7F9A-7FA4] DX = 0xC0, NEG when the result is non-zero
        s.Ww(b, (px + dx) & 0xFFFF);                                     // [7FA6-7FAB]
        s.Ww(b + 2, (py - 0xB0) & 0xFFFF);                               // [7FAD-7FB3]
        s.Ww(b + 4, s.Rw(si + 2));                                       // [7FB6-7FB9]
        s.Ww(b + 6, si);                                                 // [7FBC]
        s.Wb(si + 4, 7);                                                 // [7FBF]
        s.Wb(b + 0xE, 0);                                                // [7FC3]
        s.Ww(b + 0xA, 0);                                                // [7FC7]
        s.Ww(b + 8, 0);                                                  // [7FCC]
        s.Wb(b + 0xF, s.Rb(si + 5));                                     // [7FD1-7FD4]
        return true;                                                     // [7FD7] CLC
    }

    /// <summary>[asm 7D6E] idx 11: saturating counter [+7] throttle ([+6] &gt; [+7] &gt;&gt; 2 = no spawn), a culled projection (7F26), mode 0x37, and the projected Y jittered up by (39DF &amp; 0x3F).</summary>
    static bool ThrottleJitter(LevelState s, int si, int camX, int camY)
    {
        int counter = Math.Min(s.Rb(si + 7) + 1, 0xFF);                  // [7D6E-7D72] ADD 1 / SBB 0: saturating
        s.Wb(si + 7, counter);
        if (s.Rb(si + 6) > (counter >> 2)) return false;                 // [7D79-7D80]
        int b = Project(s, si, camX, camY, true);                        // [7D82] CALL 7F26
        if (b < 0) return false;
        s.Wb(si + 4, 0x37);                                              // [7D87]
        int ret = Pickups.Roll(s) & 0x3F;                                // [7D8B-7D8E] 39DF, AND AL,0x3F, CBW (always positive)
        s.Ww(b + 2, (s.Rw(b + 2) - ret) & 0xFFFF);                       // [7D91]
        return true;
    }

    /// <summary>
    /// [asm 7D1B] idx 12: player-proximity gate. The player must be below the entity (signed [0x4F1E] &gt; [+0xB]); |[+9] - [0x4F1C]| (16-bit, negated when the difference is negative) &lt; 0x280, and when it is
    /// &lt; 0x140 also 0xB4 &lt; [0x4F1E] - [+0xB] &lt; 0x168 (signed). Then the saturating counter throttle and an UNCULLED projection (7F31); mode 0x8F, counter reset.
    /// </summary>
    static bool ProximityGate(LevelState s, int si, int camX, int camY)
    {
        int ey = S16(s.Rw(si + 0xB)), py = S16(s.Rw(0x4F1E));
        if (py <= ey) return false;                                      // [7D1B-7D22] CMP [4F1E],AX / JLE
        int dx = (s.Rw(si + 9) - s.Rw(0x4F1C)) & 0xFFFF;                 // [7D24-7D27]
        if ((dx & 0x8000) != 0) dx = (-dx) & 0xFFFF;                     // [7D2B-7D2D] JNS / NEG
        if (S16(dx) >= 0x280) return false;                              // [7D2F-7D32] JGE (signed)
        if (S16(dx) < 0x140)                                             // [7D34-7D37]
        {
            int dy = S16((py - ey) & 0xFFFF);                            // [7D39-7D3C]
            if (dy >= 0x168 || dy <= 0xB4) return false;                 // [7D3F-7D47]
        }
        int counter = Math.Min(s.Rb(si + 7) + 1, 0xFF);                  // [7D49-7D4D] saturating
        s.Wb(si + 7, counter);
        if (s.Rb(si + 6) > (counter >> 2)) return false;                 // [7D51-7D5B]
        if (Project(s, si, camX, camY, false) < 0) return false;         // [7D5D] CALL 7F31 (no on-screen test)
        s.Wb(si + 4, 0x8F);                                              // [7D62]
        s.Wb(si + 7, 0);                                                 // [7D66]
        return true;
    }

    /// <summary>[asm 7D9B] idx 10: throttled, player-proximity-gated spawn at playerX + ring offset, snapped onto the first standable ground found scanning up.</summary>
    static bool GroundSnap(LevelState s, int si)
    {
        int level = s.Rb(0x2D8A);
        if (level == 5)                                                  // [7D9B-7DA9]
        {
            if (s.Rb(0x6BEA) != 0) return false;
            if (s.Rw(0xA326) == 3) return false;
        }
        int counter = s.Rb(si + 7) + 1;                                  // [7DB0] saturating ++
        if (counter > 0xFF) counter = 0xFF;
        s.Wb(si + 7, counter);
        if (s.Rb(si + 6) > (counter >> 2)) return false;                 // [7DBB]
        int pxCell = (S16(s.Rw(0x4F1C)) >> 4) & 0xFF;                    // [7DC8]
        int ox = s.Rb(si + 9), oy = s.Rb(si + 0xA), ex = s.Rb(si + 0xB), ey = s.Rb(si + 0xC);
        if (pxCell < ox) return false;                                   // [7DD0]
        if (ex < ((pxCell - ox) & 0xFF)) return false;                   // [7DD4]
        int pyCell = (S16(s.Rw(0x4F1E)) >> 4) & 0xFF;                    // [7DD9]
        if (pyCell < oy) return false;                                   // [7DDE]
        if (ey < ((pyCell - oy) & 0xFF)) return false;                   // [7DE2]
        int slot = FindFree(s);                                          // [7DE7]
        if (slot < 0) return false;
        int b = ObjBase + slot * ObjStride;
        int ring = s.Rw(SpawnRing);                                      // [7DF4]
        int playerX = s.Rw(0x4F1C);
        int newX = (playerX + s.Rw(SpawnXTable + ring)) & 0xFFFF;        // asset word at 0xA343 + ring
        s.Ww(SpawnRing, (ring + 2) & 0x0F);
        int xvel = S16(playerX) >= S16(newX) ? 0 : 0xFFFF;               // [7E0C]
        s.Wb(b + 0x10, 0);                                               // [7DEC] these land even when no ground is found
        s.Ww(ProjSlotPtr, b);                                            // [7DF0]
        s.Ww(b, newX);                                                   // [7E0A]
        s.Ww(b + 8, xvel);                                               // [7E15]
        int bp = (((pyCell + 4) & 0xFF) << 8) | ((newX >> 4) & 0xFF);    // [7E18]
        int limit = s.Rb(Off.MapRows) << 8;
        int mapBase = (s.Rw(Off.MapSeg) << 4) & AddrMask;
        int Floor(int off) => s.Rb(0x7F5E + s.Data[(mapBase + (off & 0xFFFF)) & AddrMask]);
        int groundRow = -1;
        for (int tries = 0; tries < 0x0A; tries++)                       // [7E66]
        {
            if (bp < limit && Floor(bp) != 0 && Floor((bp - 0x100) & 0xFFFF) == 0 && Floor((bp - 0x200) & 0xFFFF) == 0)   // [7E3B-7E52]
            {
                groundRow = (bp >> 8) & 0xFF;
                break;
            }
            bp = (bp - 0x100) & 0xFFFF;                                  // [7E5C]
            if (bp < 0x300) break;                                       // [7E60]
        }
        if (groundRow < 0) return false;                                 // [7E95]
        s.Ww(b + 2, (groundRow << 4) & 0xFFFF);                         // [7E74]
        s.Ww(b + 4, s.Rw(si + 2));                                       // [7E77]
        s.Ww(b + 6, si);                                                 // [7E7D]
        s.Wb(b + 0xE, 0);                                                // [7E84]
        s.Ww(b + 0xA, 0);                                                // [7E88]
        s.Wb(b + 0xF, s.Rb(si + 5));                                     // [7E90]
        s.Wb(si + 4, 0x17);                                              // [7E80]
        return true;
    }

    // ---- 4B8E state half ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// [asm 4B8E state half] the point-particle list 0x7DE6 (20 x 6 bytes) is one-shot: each active slot (x != 0xFFFF) gets its Y advanced
    /// by its angle/speed and is then killed ([slot] = 0xFFFF). The pixel plot is not ported (effect particles are not drawn).
    /// </summary>
    public static void ParticleConsume(LevelState s)
    {
        for (int k = 0; k < FxCount; k++)
        {
            int b = FxList + k * FxStride;
            if (s.Rw(b) == 0xFFFF) continue;
            int ang = s.Rb(b + 4), speed = S8(s.Rb(b + 5));
            int ny = (s.Rw(b + 2) + (((S8(s.Rb(0x7090 + ang)) >> 2) * speed) >> 4)) & 0xFFFF;
            s.Ww(b + 2, ny);
            s.Ww(b, 0xFFFF);
        }
    }
}
