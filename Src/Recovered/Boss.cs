using Pre2Port.Native;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Recovered;

/// <summary>
/// The mode-9 last-boss engine 1030:6ADD..6C0C (+ the glyph blit 6C0D..6CA6 and the bolt spawns 6CA7 / 6CF1), called by 6822 only while
/// <c>[0x2D8A] == 9</c> (LEVELA.SQZ, which has no spawner records at all: the boss IS the level). Port of <c>pre2/recovered/object_spawn.py</c>
/// (tick_mode9_spawn, boss_pre_interp, boss_script_interp, spawn_boss_bolt_1ca / _1cb, boss_hit_burst), re-read against the ASM (work\tmp\boss\asm\*).
/// <list type="bullet">
/// <item>6ADD head: first frame ([0xA517] == 0xFFFF) seeds [0xA4F7] = 0xA4F9 (attack-table cursor), [0xA519] = 0x18 (health), [0xA515] = 0 (dwell), [0xA516] = 3 (hit cadence);
/// 7585 spawns the health meter (min([0xA519] &gt;&gt; 2, 8) sprites 0x135) and requests the boss song; health word 0: glyph 2 (victory) and RET.</item>
/// <item>6B1C: dwell 0 -> next attack-script entry from the table at [0xA4F7] (0xFFFF = relative wrap), cursor [0xA517], dwell [0xA515], table += 4.
/// 6B43: the 4 thrown weapons 0x4F2E (ONLY those, the club does not hurt the boss; the weapon is not consumed) inside X [0xB9,0xEB) / Y [0x1E,0x50):
/// cursor = hit script 0xA534, health-- (byte, saturating), at 0 the 6BDB burst (4 sprites 0x2137 = collectible id 0x137 = the exit/warp item), cadence = (cadence + 1) &amp; 3,
/// 0 -> cursor = 0xA540; only the first weapon in the zone counts.</item>
/// <item>6B91 script interpreter (cursor [0xA517]): 0xFF bolt 0x1CA, 0xFE bolt 0x1CB, 0xFD sfx 2, other negative byte = relative jump + dwell-- (saturating),
/// non-negative byte = glyph: cursor++, glyph rendered by <see cref="BossGlyph"/> (derived from the state: [[0xA517] - 1]).</item>
/// </list>
/// </summary>
public static class Boss
{
    public const int Table = 0xA4F7, Dwell = 0xA515, Cadence = 0xA516, Script = 0xA517, Health = 0xA519;
    const int HitScript = 0xA534, CycleScript = 0xA540, SeedTable = 0xA4F9;
    const int ProjBase = 0x4F2E, ProjCount = 4, Stride = 0x12;
    const int Pool = 0x50A8, PoolCount = 0x20;
    const int BurstX = 0xA336, BurstY = 0xA338, BurstSprite = 0xA33A;

    /// <summary>[asm 6ADD..6B0C + 6B0F..6BDA] the whole engine for one frame (the 6822 branch for level 9).</summary>
    public static void Step(LevelState s)
    {
        if (s.Rw(Script) == 0xFFFF)                                   // [6AE7] first frame: seed
        {
            s.Ww(Table, SeedTable);                                   // [6AEE]
            s.Ww(Health, 0x18);                                       // [6AF4]
            s.Wb(Dwell, 0);                                           // [6AFA]
            s.Wb(Cadence, 3);                                         // [6AFF]
        }
        CameraEngine.InitEffectRow(s, (short)s.Rw(Health) >> 2 & 0xFFFF);   // [6B04-6B0C] 7585 with CX = [0xA519] SAR 2
        if (s.Rw(Health) == 0) return;                                // [6B0F-6B1B] boss dead: AL = 2, 6C0D (the glyph), RET
        PreInterp(s);                                                 // [6B1C-6B90]
        ScriptInterp(s);                                              // [6B91-6BDA]
    }

    /// <summary>[asm 6B1C..6B90] attack-script advance + thrown-weapon hit scan.</summary>
    static void PreInterp(LevelState s)
    {
        if (s.Rb(Dwell) == 0)                                         // [6B1C]
        {
            int bx = s.Rw(Table);                                     // [6B23]
            if (s.Rw(bx) == 0xFFFF) bx = (bx + s.Rw(bx + 2)) & 0xFFFF;   // [6B27-6B2E] wrap marker: BX += [BX+2]
            s.Ww(Script, s.Rw(bx));                                   // [6B33]
            s.Wb(Dwell, s.Rw(bx + 2) & 0xFF);                         // [6B36-6B39]
            s.Ww(Table, (bx + 4) & 0xFFFF);                           // [6B3C-6B3F]
        }
        for (int k = 0; k < ProjCount; k++)                           // [6B43-6B8F]
        {
            int si = ProjBase + k * Stride;
            if (s.Rw(si + 4) == 0xFFFF) continue;                     // [6B49]
            if (((s.Rw(si) - 0xB9) & 0xFFFF) >= 0x32) continue;       // [6B4F-6B57] unsigned
            if (((s.Rw(si + 2) - 0x1E) & 0xFFFF) >= 0x32) continue;   // [6B59-6B62]
            s.Ww(Script, HitScript);                                  // [6B64]
            int h = s.Rb(Health);                                     // [6B6A-6B6F] SUB 1 / ADC 0 = saturating decrement of the low byte
            int nh = h == 0 ? 0 : h - 1;
            s.Wb(Health, nh);
            if (nh == 0) HitBurst(s);                                 // [6B74-6B76] 6BDB
            int c = (s.Rb(Cadence) + 1) & 3;                          // [6B79-6B7D]
            s.Wb(Cadence, c);
            if (c == 0) s.Ww(Script, CycleScript);                    // [6B82-6B84]
            return;                                                   // [6B8A] -> 6B91
        }
    }

    /// <summary>[asm 6BDB..6C0C] four collectible-0x137 sprites (0x2137) from (0xB9, 0x1E) in a diamond: Xvel +/-0x20 then +/-0x10, Yvel 0xFF60 then 0xFF50.</summary>
    static void HitBurst(LevelState s)
    {
        s.Ww(BurstX, 0xB9);                                           // [6BDB-6BDE]
        s.Ww(BurstY, 0x1E);                                           // [6BE1-6BE4]
        int ax = 0x20, dx = 0xFF60;                                   // [6BEA-6BED]
        for (int k = 0; k < 4; k++)                                   // [6BE7] CX = 4
        {
            s.Ww(BurstSprite, 0x2137);                                // [6BF5]
            Pickups.Burst(s, ax, dx, 1);                              // [6BFC] 901B = 8D1B, CX = 1
            ax = (-ax) & 0xFFFF;                                      // [6BFF]
            if ((ax & 0x8000) == 0) { ax = (ax - 0x10) & 0xFFFF; dx = (dx - 0x10) & 0xFFFF; }   // [6C01-6C06] JS skips
        }
    }

    /// <summary>[asm 6B91..6BDA] run opcodes from the cursor until a glyph byte.</summary>
    static void ScriptInterp(LevelState s)
    {
        int bx = s.Rw(Script);                                        // [6B91]
        for (int guard = 0; guard < 4096; guard++)
        {
            int al = s.Rb(bx);                                        // [6B95]
            if (al == 0xFF) { SpawnBolt1CA(s); bx = (bx + 1) & 0xFFFF; continue; }     // [6B97-6B9F] 6CA7
            if (al == 0xFE) { SpawnBolt1CB(s); bx = (bx + 1) & 0xFFFF; continue; }     // [6BA1-6BA9] 6CF1
            if (al == 0xFD) { s.Sfx(2); bx = (bx + 1) & 0xFFFF; continue; }            // [6BAB-6BB6] DX = 2, 0282
            if ((al & 0x80) != 0)                                     // [6BB8-6BD1] negative: relative jump, dwell-- (saturating)
            {
                int d = s.Rb(Dwell);
                s.Wb(Dwell, d == 0 ? 0 : d - 1);                      // [6BC0-6BC5] SUB 1 / ADC 0
                bx = (bx + (al - 0x100)) & 0xFFFF;                    // [6BCA-6BCB] CBW, ADD BX,AX
                s.Ww(Script, bx);                                     // [6BCD]
                continue;
            }
            s.Ww(Script, (s.Rw(Script) + 1) & 0xFFFF);                // [6BD3] INC [0xA517]: the cursor goes past the glyph (BX itself was NOT stored by 0xFF / 0xFE / 0xFD)
            return;                                                   // [6BD7-6BDA] 6C0D(AL), RET
        }
        throw new ObjectGapException("6B91 boss-script interpreter ran 4096 opcodes without reaching a glyph (malformed script)");
    }

    /// <summary>[asm 6CA9-6CB8] first free slot of the 0x20-entry effect pool 0x50A8 (stride 0x12), or -1.</summary>
    static int FreeSlot(LevelState s)
    {
        for (int k = 0; k < PoolCount; k++)
            if (s.Rw(Pool + k * Stride + 4) == 0xFFFF) return Pool + k * Stride;
        return -1;
    }

    /// <summary>[asm 6CA7..6CF0] bolt 0x1CA at (0xC8, 0x58), anim cursor [+0xC] = 0x84, Xvel = -((39DF &amp; 0xF) &lt;&lt; 3), Yvel 0; no slot = no spawn (no RNG draw either).</summary>
    static void SpawnBolt1CA(LevelState s)
    {
        int si = FreeSlot(s);
        if (si < 0) return;                                           // [6CBA]
        s.Ww(si + 4, 0x1CA);                                          // [6CBC]
        s.Ww(si, 0xC8);                                               // [6CC1]
        s.Ww(si + 2, 0x58);                                           // [6CC5]
        s.Wb(si + 0x11, 0);                                           // [6CCA]
        s.Ww(si + 0xC, 0x84);                                         // [6CCE]
        s.Ww(si + 9, 0xFFFF);                                         // [6CD3]
        int r = Pickups.Roll(s) & 0xF;                                // [6CD8-6CDB] 39DF, AND AX,0xF
        s.Ww(si + 6, (-(r << 3)) & 0xFFFF);                           // [6CDE-6CE6] SHL x3, NEG
        s.Ww(si + 0xE, 0);                                            // [6CE9]
    }

    /// <summary>[asm 6CF1..6D33] bolt 0x1CB at (rng &amp; 0x7F) - 0x10, Y 0, anim cursor 0x42, no velocity.</summary>
    static void SpawnBolt1CB(LevelState s)
    {
        int si = FreeSlot(s);
        if (si < 0) return;                                           // [6D04]
        s.Ww(si + 4, 0x1CB);                                          // [6D06]
        int r = Pickups.Roll(s) & 0x7F;                               // [6D0B-6D0E]
        s.Ww(si, (r - 0x10) & 0xFFFF);                                // [6D11-6D14]
        s.Ww(si + 2, 0);                                              // [6D16]
        s.Wb(si + 0x11, 0);                                           // [6D1B]
        s.Ww(si + 0xC, 0x42);                                         // [6D1F]
        s.Ww(si + 9, 0xFFFF);                                         // [6D24]
        s.Ww(si + 6, 0);                                              // [6D29-6D2B]
        s.Ww(si + 0xE, 0);                                            // [6D2E]
    }
}

/// <summary>
/// [asm 6C0D..6CA6] the boss image: a 6 x 7 window of the LEVEL TILE MAP (row stride 0x100, origin [glyph * 6 + 0x14]) blitted tile by tile (16 x 16, write mode 1: all
/// four planes latched from the tile cache 0x5E80 + idx * 32) into the scroll ring at 0x3F40 + 0x18, BEFORE the scroll copy 3A27. The original hands the glyph to 6C0D in
/// AL and keeps it nowhere; here it is re-derived from the state after the 6822 step: 6BD3 increments the memory cursor [0xA517] by ONE, but the 0xFF / 0xFE / 0xFD opcodes only
/// advance BX, so the glyph is the first byte at or after [[0xA517] - 1] that is not one of those three opcodes. Glyph 2 once the boss is dead (6B16).
/// </summary>
public static class BossGlyph
{
    const int RingPage = 0x3F40, XByte = 0x18, CacheBase = 0x5E80, RowStride = 0x28;

    /// <summary>The glyph index to draw, or -1 when none yet (level 9 before its first 6822 step).</summary>
    public static int Current(LevelState s)
    {
        if (s.Rb(Off.Level) != 9) return -1;
        if (s.Rw(Boss.Health) == 0) return 2;                         // [6B16] AL = 2
        int cursor = s.Rw(Boss.Script);
        if (cursor == 0xFFFF) return -1;                              // not seeded yet
        int p = (cursor - 1) & 0xFFFF, g = s.Rb(p);
        for (int guard = 0; guard < 16 && (g == 0xFF || g == 0xFE || g == 0xFD); guard++) { p = (p + 1) & 0xFFFF; g = s.Rb(p); }   // the spawn / sfx opcodes the interpreter walked over with BX
        return (g & 0x80) != 0 ? -1 : g;
    }

    /// <summary>Blit the glyph into the four planes (ring page, before ScrollCopy).</summary>
    public static void Draw(LevelState s, byte[][] planes)
    {
        int glyph = Current(s);
        if (glyph < 0) return;
        int mapBase = (s.Rw(Off.MapSeg) << 4) & AddrMask;
        int bx = glyph * 6 + 0x14;                                    // [6C16-6C1C]
        for (int row = 0; row < 7; row++)                             // [6C1F] CH = 7
        {
            for (int col = 0; col < 6; col++)                         // [6C21] CL = 6
            {
                int idx = s.Data[mapBase + ((bx + row * 0x100 + col) & 0xFFFF)];   // [6C28-6C2A]
                int src = CacheBase + (idx << 5);                     // [6C2D-6C3C]
                int dst = RingPage + XByte + row * 16 * RowStride + col * 2;   // [6C40-6C9B]
                for (int p = 0; p < 4; p++)
                {
                    int sp = src, dp = dst;
                    for (int r = 0; r < 16; r++)                      // [6C3E-6C8B] 16 rows x 2 bytes
                    {
                        planes[p][dp] = planes[p][sp];
                        planes[p][dp + 1] = planes[p][sp + 1];
                        sp += 2; dp += RowStride;
                    }
                }
            }
        }
    }
}
