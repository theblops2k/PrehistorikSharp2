using Pre2Port.Codecs;
using Pre2Port.Native;
using static Pre2Port.Native.LevelState;
using static Pre2Port.Recovered.SpriteDecode;

namespace Pre2Port.Recovered;

/// <summary>
/// Level loader for the first gameplay frame (background + terrain only). Port of the part of
/// <c>pre2/native/cold_boot.py native_cold_boot</c> -> <c>level_init.py native_level_start</c> that the terrain render reads:
/// <c>native_level_load</c> (3ed6: dgroup + background + 3ead secret-tile hide + 42af anim tables + 4316 local tiles +
/// 4389 shared/UNION tiles + 4232 classify), the terrain-relevant part of <c>native_5237</c>/<c>native_player_init</c>
/// (timer-block zero, player pool clear + start position) and the 3af2 camera init (<see cref="LevelCamera"/>).
/// The entity/effect sprite rebases (4182/414d) are <see cref="RebaseSprites"/> (identity unless the level's sprite bank [0x8C8B] != 0x138: LEVEL6).
/// NOT ported (no effect on background/terrain, verified equivalent in Python by skipping them): RNG decor (40bd: now done, see AssignRandomDecor), [the trigger bank save 41ca IS done: <see cref="Proximity.SaveBank"/>; the 4065 dup IS done: <see cref="Respawn.SaveBackup"/>,
/// restored by the respawn],
/// decor counts (4073), the RNG table fill, SFX bank, keyboard init,
/// the level-start block (lives/attack), 0ba0 palette (done by the renderer).
/// </summary>
/// <summary>
/// The level counters that a level change WITHOUT the tally keeps: 4C8F / 4CC1 jump straight to 4F65, skipping 4F62 (51DF = zero the item-count table [0x6C12..+0x71] and the total [0x6C9E]),
/// and the loader 3ED6 only zeroes the four collect words [0x2A74..0x2A7A] for levels &lt;= 0xA (&gt; 0xA: [0x2A74] and [0x2A78] are halved, the other two stay). So the food count of a main level
/// is carried into its bonus level (and out of it into the tally of the main level).
/// </summary>
public sealed record LevelCarry(byte[] ItemQueue, int ItemTotal, int CollectMain, int CollectCounter, int CollectDecor, int CollectLinked)
{
    public const int QueueOff = 0x6C12, QueueLen = 0x71, TotalOff = 0x6C9E;

    public static LevelCarry Capture(LevelState s)
    {
        var q = new byte[QueueLen];
        Array.Copy(s.Data, BootImage.DsBase + QueueOff, q, 0, QueueLen);
        return new LevelCarry(q, s.Rw(TotalOff), s.Rw(LevelState.Off.CollectMain), s.Rw(LevelState.Off.CollectCounter), s.Rw(LevelState.Off.CollectDecor), s.Rw(LevelState.Off.CollectLinked));
    }

    public void Apply(LevelState s)
    {
        Array.Copy(ItemQueue, 0, s.Data, BootImage.DsBase + QueueOff, QueueLen);
        s.Ww(TotalOff, ItemTotal);
        s.Ww(LevelState.Off.CollectMain, CollectMain); s.Ww(LevelState.Off.CollectCounter, CollectCounter);
        s.Ww(LevelState.Off.CollectDecor, CollectDecor); s.Ww(LevelState.Off.CollectLinked, CollectLinked);
    }
}

public static class LevelLoad
{
    /// <summary>Sum of the 0x3f3c rep-movsw block == 0x13a7 (level_load.py PROP_LEN).</summary>
    public const int PropLen = 0x13A7;
    const int Ds = BootImage.DsBase;

    /// <summary>
    /// Boot a state the way <c>native_cold_boot(game_root, level=n)</c> does for the terrain: boot image copy, FRONT.SQZ stacked
    /// permanently ([0x3B] = its segment), the shared sprite bank (SpriteBank.NativeBuild), [0x39] reset base, then the level start.
    /// <paramref name="level"/> is the 0-based level index ([0x2D8A]; 0 == LEVEL1.SQZ == "level 1", same as MenuFlow.ChosenLevel).
    /// </summary>
    public static LevelState ColdBoot(byte[] boot, string originalDir, int level, int modeCopy = 0, LevelCarry? carry = null)
    {
        var s = new LevelState(boot, originalDir);
        int front = LoadSqz(s, "FRONT.SQZ");                 // [asm 107B] permanent front-panel load (bumps [0x2875])
        s.Ww(Off.FgBank, front);                              // [asm 0123]
        SpriteBank.NativeBuild(s.Data, originalDir);          // 2dfa: SPRITES.SQZ bank at [0x2875], bumps it
        s.Ww(Off.ResetBase, s.Rw(Off.LoadTop));               // [asm 0129-012c]
        s.Wb(Off.Level, level);
        s.Wb(0xB198, modeCopy);                               // [asm 8F12-8F15 / 8EC4] the committed BEGINNER/EXPERT mode copy, read by the decor code (4112) and the spawner cull (6927)
        carry?.Apply(s);                                      // a level change without the tally keeps the counters (before 3ED6 halves / zeroes the collect words)
        Start(s);
        // level-start values the HUD draws: [asm 0141] lives=2, [asm 0146] BONUS mask=0 (main level-start block), [asm 52a8] energy=3 (5237)
        s.Wb(Hud.OffLives, 2);
        s.Wb(Hud.OffBonusMask, 0);
        s.Wb(Hud.OffEnergy, 3);
        s.Wb(0x7B19, 0x14);                                   // [asm 014B] projectile damage / hit tolerance
        s.Wb(0x7B18, 0);                                      // [asm 0150] attack phase (the boot image holds 3 there)
        s.Wb(0x6CA8, 0);                                      // [asm 0155] utensils mask
        return s;
    }

    /// <summary>native_level_init for the terrain: load (3ed6), re-init (5237 subset), player init (55fc), camera (3af2).</summary>
    public static void Start(LevelState s)
    {
        int level = s.Rb(Off.Level);
        LoadDgroup(s, level);
        RebaseSprites(s);                                     // [asm 4059 414D, 405C 4182]: before the secret-tile hide 3ead and the decor 40BD, like the loader
        LoadBackground(s);
        SelfPatchSecretTiles(s);
        AssignRandomDecor(s);
        BuildAnimTables(s);
        LoadLocalTiles(s, level);
        LoadSharedTiles(s);
        Classify(s);
        s.Ww(0x6BAD, s.Rw(Off.StartX));                       // [asm 4050/4056] checkpoint = start point (the respawn 4F6C drops the player here)
        s.Ww(0x6BAF, s.Rw(Off.StartY));
        Respawn.SaveBackup(s);                                // [asm 4065] pristine property block backup (restored by 5237/5251 on every respawn)
        Proximity.SaveBank(s);                                // [asm 4070] 41CA: the pristine map blocks under the proximity triggers (bank at [0x2875]; read by 53F6, restored by 52D2)
        CountFoodItems(s, level);                             // [asm 4073-40BB] the tally's food totals [0x2A78] / [0x2A74] (doubled when the level has a bonus level)
        ReInit(s);
        Array.Fill(s.Data, (byte)0xFF, BootImage.DsBase + 0x7DE6, 0x78);   // [asm 5295] 5237 tail (the level start calls 5237 at 01D8): the 20 point particles ...
        Array.Fill(s.Data, (byte)0xFF, BootImage.DsBase + 0x7DAF, 0x37);   // [asm 52A0] ... and the 5 boss rocks of the level-6 tree boss (TreeBoss, list 0x7DAF) start free
        for (int i = 0; i < 0x50; i++) { s.Wb(0x6EA9 + i * 2, 0xAA); s.Wb(0x6EA9 + i * 2 + 1, 0x55); }   // [asm 5287-5290] the 20 wall-impact / firefly marker slots are free (x = 0x55AA); zeros would read as live slots
        s.Ww(0x2DBC, s.Rw(0x2D40 + level * 2)); s.Ww(0x2DBE, 0);   // [asm 52B3-52C2, 4038-4047] scripted-scroll pointer of the level (table 0x2D40), counter 0 (read by 3922 after a cave arrival)
        s.Ww(Boss.Script, 0xFFFF);                            // [asm 52AD] 5237 tail: the boss-script cursor is "unseeded" (the mode-9 engine 6ADD seeds itself on its first frame)
        LevelCamera.Init(s);
        s.Ww(0xA341, 0); s.Wb(0x6BCC, 0);                     // [asm 01EF / 01F5] the 7D9B spawn-ring index and the 7F6C aura side toggle restart at every level start
        s.Wb(Pickups.RngSeedOff, 0x05); s.Wb(Pickups.RngSeedOff + 1, 0x22); s.Wb(Pickups.RngSeedOff + 2, 0x86); s.Ww(Pickups.RngSeedOff + 3, 0xE58D);   // [asm 01FA-0209] fixed level-start RNG seed (after the 5237 table fill)
    }

    /// <summary>
    /// [asm 4073-40BB] (end of the level loader, after 41CA): counts the float sources (0x8F1D, 0x46 x 7) whose WHOLE sprite word [+4] (flag bits included, so flagged ones fall out) is in 0x6E..0x75 or
    /// 0x80..0xDB (the food / collectible ids; 0x76..0x7F, the gliders, are skipped) and adds the count to [0x2A78] (the decor total). A main level (&lt; 0xA) whose warp-table entry [0x2CF6 + level] is not 0xFF
    /// (it has a bonus level) then doubles [0x2A78] and [0x2A74] (the secret-cell total from 3ead): the bonus level's items count in the tally percentage of the main level (5139 divides by [0x2A74] + [0x2A78]).
    /// </summary>
    public static void CountFoodItems(LevelState s, int level)
    {
        int dx = 0;
        for (int k = 0; k < 0x46; k++)                        // [4078-409B]
        {
            int ax = s.Rw(0x8F1D + k * 7 + 4);
            if (ax == 0xFFFF || ax < 0x6E || ax > 0xDB) continue;   // [407E-408B]
            if (ax >= 0x80 || ax <= 0x75) dx++;                // [408D-4097]
        }
        s.Ww(Off.CollectDecor, (s.Rw(Off.CollectDecor) + dx) & 0xFFFF);   // [409D]
        if (level < 0xA && s.Rb(0x2CF6 + level) != 0xFF)      // [40A1-40AC]
        {
            s.Ww(Off.CollectDecor, (s.Rw(Off.CollectDecor) << 1) & 0xFFFF);   // [40B3]
            s.Ww(Off.CollectMain, (s.Rw(Off.CollectMain) << 1) & 0xFFFF);     // [40B7]
        }
    }

    /// <summary>
    /// [asm 414D / 4159] the float-effect sources (0x8F1D, 0x46 x 7) and terrain entities (0x9107, 0x10 x 0xF): a live sprite [+4] becomes [+4] - [0x8C89] + 0x35 (level bank -> engine bank); then
    /// [asm 4182] the spawner list (0x8489, records of length [si] while [si] &lt;= 0x32): sprite [+2] &gt;= [0x8C8B] becomes - [0x8C8B] + 0x138, else &gt;= [0x8C89] becomes - [0x8C89] + 0x35 (skipped
    /// when [0x8C89] == 0xFFFF); finally [0x8C89] = 0x35, [0x8C8B] = 0x138. Identity for the levels whose banks are already 0x35 / 0x138; LEVEL6 (index 5) has [0x8C8B] = 0x137, so its spawner
    /// sprites (0x171 enemies, ...) are +1.
    /// </summary>
    public static void RebaseSprites(LevelState s)
    {
        int lo = s.Rw(0x8C89), hi = s.Rw(0x8C8B);
        for (int k = 0; k < 0x46; k++)                        // [4151-4167]
        {
            int a = 0x8F1D + k * 7 + 4;
            if (s.Rw(a) != 0xFFFF) s.Ww(a, (s.Rw(a) - lo + 0x35) & 0xFFFF);
        }
        for (int k = 0; k < 0x10; k++)                        // [4169-417F]
        {
            int a = 0x9107 + k * 0xF + 4;
            if (s.Rw(a) != 0xFFFF) s.Ww(a, (s.Rw(a) - lo + 0x35) & 0xFFFF);
        }
        if (lo != 0xFFFF)                                     // [418A]
        {
            int si = 0x8489;
            for (int guard = 0; s.Rb(si) <= 0x32 && guard < 0x1000; guard++)   // [4192] CMP byte [si],0x32 / JA 41BD
            {
                int ax = s.Rw(si + 2);                        // [4197]
                if (ax != 0xFFFF)                             // [419A]
                {
                    if (ax >= hi) s.Ww(si + 2, (ax - hi + 0x138) & 0xFFFF);        // [419F-41A8]
                    else if (ax >= lo) s.Ww(si + 2, (ax - lo + 0x35) & 0xFFFF);    // [41AA-41B3]
                }
                si = (si + (sbyte)s.Rb(si)) & 0xFFFF;         // [41B6-41BB] CWDE of AL
            }
        }
        s.Ww(0x8C89, 0x35); s.Ww(0x8C8B, 0x138);              // [41BD-41C8]
    }

    // ---- 107B: SQZ load into the stacking buffer [0x2875] ------------------------------------------------------------

    /// <summary>[asm 1030:1463-1477 / 10C4 / 1208] paragraphs the allocator advances after an asset (pre2/codecs/sqz.py sqz_bump_advance).</summary>
    public static int BumpAdvance(byte[] data)
    {
        if (data[0] == 0xB4 && data[1] == 0x4C)               // LZSS
        {
            int dl = data[14];
            int ax = (data[15] | (data[16] << 8)) & 0xFFFF;
            dl >>= 1;
            dl >>= 1;
            for (int i = 0; i < 4; i++)
            {
                int cf = dl & 1;
                dl >>= 1;
                ax = ((cf << 15) | (ax >> 1)) & 0xFFFF;
            }
            return (ax + 1) & 0xFFFF;
        }
        long reserved = data[1] == 0x10
            ? ((long)(data[0] & 15) << 16) | data[2] | ((long)data[3] << 8)
            : ((long)(data[0] | (data[1] << 8)) << 16) | data[2] | ((long)data[3] << 8);
        return (int)(((reserved >> 4) + 1) & 0xFFFF);
    }

    /// <summary>[asm 107B] decompress <paramref name="name"/> to the load top, bump it, return the segment it landed at.</summary>
    public static int LoadSqz(LevelState s, string name)
    {
        byte[] raw = File.ReadAllBytes(s.ResolvePath(name));
        byte[] outp = Sqz.Unpack(raw);
        int seg = s.Rw(Off.LoadTop);
        int b = (seg << 4) & AddrMask;
        if (b + outp.Length > s.Data.Length) throw new InvalidDataException($"{name}: decoded data exceeds the memory image");
        Array.Copy(outp, 0, s.Data, b, outp.Length);
        s.Ww(Off.LoadTop, (seg + BumpAdvance(raw)) & 0xFFFF);
        return seg;
    }

    // ---- 3ed6 pieces ---------------------------------------------------------------------------------------------------

    /// <summary>level_load.py native_level_load_dgroup: prologue scalars + clears, LEVEL&lt;n&gt;.SQZ, tile-index table, property memcpy.</summary>
    public static void LoadDgroup(LevelState s, int level)
    {
        s.Wb(Off.Level, level);
        if (level <= 0x0A)                                    // [asm 3ede..3f26]
        {
            s.Ww(Off.CollectDecor, 0); s.Ww(Off.CollectMain, 0); s.Ww(Off.CollectLinked, 0); s.Ww(Off.CollectCounter, 0);
        }
        else
        {
            s.Ww(Off.CollectDecor, s.Rw(Off.CollectDecor) >> 1);
            s.Ww(Off.CollectMain, s.Rw(Off.CollectMain) >> 1);
        }
        s.Wb(Off.GfxGroup, s.Rb(Off.GfxGroupTable + level));  // per-level graphics-group char ('0'..) -> BACK<c>.SQZ
        int al = (level + 0x31) & 0xFF;                       // filename digit: level+'1', then 'A'.. past '9'
        if (al > 0x39) al = (al + 7) & 0xFF;
        s.Wb(Off.FilenameDigit, al);
        Array.Fill(s.Data, (byte)0xFF, Ds + Off.PropDst, 0x13A5);          // [asm 3f16]
        Array.Fill(s.Data, (byte)0xFF, Ds + Off.ObjPool, 0x40B * 2);       // [asm 3f20]

        int seg = LoadSqz(s, s.CString(Off.LevelName));       // [asm 3f2b] LEVEL<n>.SQZ
        s.Ww(Off.MapSeg, seg);

        int bh = s.Rb(Off.LevelHeaderTable + level);          // header paragraphs (<<4) == map rows; also [0x2CF5] bottom limit
        s.Wb(Off.MapRows, bh);
        int baseSeg = (seg + (bh << 4)) & 0xFFFF;
        int b = (baseSeg << 4) & AddrMask;
        Array.Copy(s.Data, b, s.Data, Ds + Off.TileIndex, 0x200);   // [asm 4316..433e] 0x100-word tile index
        int bp = 0x200;                                       // property tables follow the local tile graphics (0x80 each)
        for (int i = 0; i < 0x100; i++)
            if (s.Fw(b + i * 2) < 0x100) bp += 0x80;
        Array.Copy(s.Data, b + bp, s.Data, Ds + Off.PropDst, PropLen);   // [asm 3f3c..3f78]
    }

    /// <summary>level_load.py native_level_load_background: BACK&lt;group&gt;.SQZ (name at [0x2DA6]) -> 4 planes x 0x1F40 at plane offset 0x7E80.</summary>
    public static void LoadBackground(LevelState s)
    {
        byte[] back = Sqz.Unpack(File.ReadAllBytes(s.ResolvePath(s.CString(Off.BackName))));
        if (back.Length < 4 * 0x1F40) throw new InvalidDataException("BACK image shorter than 4 planes");
        for (int p = 0; p < 4; p++)
            Array.Copy(back, p * 0x1F40, s.Data, Plane(p, 0x7E80), 0x1F40);
    }

    /// <summary>[asm 3ead] hide the secret/bonus tiles in the map grid (swap with the entry's hidden tile, stash the revealed one) and add the count to [0x2A74].</summary>
    /// <summary>[asm 40bd] level_load.py _assign_random_decor: the float items whose sprite is in [0x11B, 0x12B) (the password glyphs shown in the level) are sorted by X
    /// (bubble sort of the pointer list [0x6A88]) and, when their count is a nonzero multiple of 4, dealt sprite 0x11B + one nibble of the level code (932F, seed [0xA333]
    /// = 0x20 on the zeroed-BIOS build), MSB-first, each group of 4 reusing the code. Without it the glyphs keep the raw level-file values.</summary>
    public static void AssignRandomDecor(LevelState s)
    {
        const int Sources = 0x8F1D, PtrList = 0x6A88, Mode = 0xB198;
        var ptrs = new List<int>();
        for (int k = 0; k < 0x46; k++)
        {
            int si = Sources + k * 7;
            if ((((s.Rw(si + 4) & 0x1FFF) - 0x11B) & 0xFFFF) <= 0xF) ptrs.Add(si);
        }
        for (int i = 0; i < ptrs.Count; i++) s.Ww(PtrList + i * 2, ptrs[i]);
        int bp = ptrs.Count;
        if (bp == 0 || (bp & 3) != 0) return;
        bool swapped = true;
        while (swapped)
        {
            swapped = false;
            for (int i = 0; i < bp - 1; i++)
                if (s.Rw(ptrs[i + 1]) < s.Rw(ptrs[i])) { (ptrs[i], ptrs[i + 1]) = (ptrs[i + 1], ptrs[i]); swapped = true; }
        }
        for (int i = 0; i < bp; i++) s.Ww(PtrList + i * 2, ptrs[i]);
        int level = s.Rb(Off.Level) + (s.Rb(Mode) != 0 ? 0x0A : 0);
        int code = Password.LevelCode(level);
        for (int g = 0; g < bp; g += 4)
        {
            int dx = code;
            for (int j = 0; j < 4; j++)
            {
                dx = ((dx << 4) & 0xFFFF) | (dx >> 12);
                s.Ww(ptrs[g + j] + 4, 0x11B + (dx & 0xF));
            }
        }
    }

    public static void SelfPatchSecretTiles(LevelState s)
    {
        int mapBase = (s.Rw(Off.MapSeg) << 4) & AddrMask;
        int count = 0;
        for (int k = 0; k < 0x50; k++)                        // stride-5 records at [0x8C8D]
        {
            int e = Off.BonusCells + k * 5;
            int cell = s.Rw(e + 3);
            if (cell == 0xFFFF) continue;
            count++;
            int a = (mapBase + cell) & AddrMask;
            int hidden = s.Rb(e);                             // [+0] hidden tile
            s.Wb(e + 1, s.Data[a]);                           // [+1] revealed tile stash
            s.Data[a] = (byte)hidden;
        }
        s.Ww(Off.CollectMain, (s.Rw(Off.CollectMain) + count) & 0xFFFF);
    }

    /// <summary>[asm 42af] tile-animation lookup tables [0x6688]/[0x6788]/[0x6888]/[0x6988] from the per-tile flag [0x805E+t] bit 0x80; [0x6BBD] = any animated tile.</summary>
    public static void BuildAnimTables(LevelState s)
    {
        int t0 = Ds + Off.AnimTable0, t1 = t0 + 0x100, t2 = t0 + 0x200, t3 = t0 + 0x300;
        int flag = Ds + Off.TileFlags;
        int hasAnim = 0;
        int bx = 0;
        while (bx < 0x100)
        {
            if ((s.Data[flag + bx] & 0x80) == 0)              // static tile
            {
                s.Data[t0 + bx] = (byte)bx; s.Data[t1 + bx] = (byte)bx; s.Data[t2 + bx] = (byte)bx; s.Data[t3 + bx] = 0;
                bx += 1;
            }
            else                                              // animated: rotated 3-frame cycle
            {
                hasAnim = 1;
                byte t = (byte)bx, u = (byte)(bx + 1), v = (byte)(bx + 2);
                s.Data[t0 + bx] = t; s.Data[t0 + bx + 1] = u; s.Data[t0 + bx + 2] = v;
                s.Data[t1 + bx] = u; s.Data[t1 + bx + 1] = v; s.Data[t1 + bx + 2] = t;
                s.Data[t2 + bx] = v; s.Data[t2 + bx + 1] = t; s.Data[t2 + bx + 2] = u;
                s.Data[t3 + bx] = 1; s.Data[t3 + bx + 1] = 1; s.Data[t3 + bx + 2] = 1;
                bx += 3;
            }
        }
        s.Wb(Off.AnyAnimated, hasAnim);                       // [asm 4311]
    }

    /// <summary>Copy the 4 x 0x20-byte planes of a 0x80-byte sprite at flat <paramref name="src"/> into cache slot <paramref name="slot"/> (plane offset 0x5E80).</summary>
    static void WriteSlot(LevelState s, int slot, int src)
    {
        for (int p = 0; p < 4; p++)
            Array.Copy(s.Data, src + p * SlotBytes, s.Data, Plane(p, 0x5E80 + slot * SlotBytes), SlotBytes);
    }

    /// <summary>[asm 4316] LOCAL tiles: index-table codes &lt; 0x100 demuxed from the level's sheet (after the header) into the planar cache.</summary>
    public static void LoadLocalTiles(LevelState s, int level)
    {
        int bh = s.Rb(Off.LevelHeaderTable + level);
        int baseSeg = (s.Rw(Off.MapSeg) + (bh << 4)) & 0xFFFF;
        int b = (baseSeg << 4) & AddrMask;
        for (int slot = 0; slot < NumSlots; slot++)
        {
            int code = s.Fw(b + slot * 2);
            if (code < LocalCodeMax)
                WriteSlot(s, slot, b + PixelBase + code * SpriteBytes);
        }
    }

    /// <summary>
    /// [asm 4389] SHARED tiles: UNION.SQZ (name at [0x0A25]) loaded over the level segment as scratch (the grid is saved/restored),
    /// then index-table codes &gt;= 0x100 demuxed with the original's segment arithmetic ((code-0x100)*8 + seg, wrapped).
    /// </summary>
    public static void LoadSharedTiles(LevelState s)
    {
        int savedTop = s.Rw(Off.LoadTop);
        int lvlSeg = s.Rw(Off.MapSeg);
        int lvlBase = (lvlSeg << 4) & AddrMask;
        byte[] grid = new byte[0x12000];
        Array.Copy(s.Data, lvlBase, grid, 0, grid.Length);
        s.Ww(Off.LoadTop, lvlSeg);
        int unionSeg = LoadSqz(s, s.CString(Off.UnionName));
        s.Ww(Off.LoadTop, savedTop);
        s.Ww(Off.UnionBankSeg, unionSeg);
        for (int slot = 0; slot < NumSlots; slot++)
        {
            int code = s.Rw(Off.TileIndex + slot * 2);
            if (code >= LocalCodeMax)
            {
                int seg = ((code - LocalCodeMax) * 8 + unionSeg) & 0xFFFF;
                WriteSlot(s, slot, (seg << 4) & AddrMask);
            }
        }
        Array.Copy(grid, 0, s.Data, lvlBase, grid.Length);    // restore the level grid UNION overwrote
    }

    /// <summary>[asm 4232] classify each cache slot (0 opaque / 1 empty / &gt;=2 partial) into [0x4DF8] and save the partial masks at [0x2DF8].</summary>
    public static void Classify(LevelState s)
    {
        var cache = new SpriteCache();
        for (int p = 0; p < 4; p++)
            Array.Copy(s.Data, Plane(p, 0x5E80), cache.PlaneData[p], 0, NumSlots * SlotBytes);
        var res = SpriteClassify.ClassifySprites(cache);
        Array.Copy(res.Types, 0, s.Data, Ds + Off.BlitType, res.Types.Length);
        Array.Copy(res.Masks, 0, s.Data, Ds + Off.MaskRegion, res.PartialCount * SlotBytes);
    }

    // ---- 5237 / 55fc subset ---------------------------------------------------------------------------------------------

    /// <summary>level_init.py native_5237 (zero timer block, tokens) + native_player_init (55fc: pool clear, slots dead, start position).</summary>
    public static void ReInit(LevelState s)
    {
        Array.Clear(s.Data, Ds + Off.TimerBlock, 0x48);       // [asm 5247] fine_scroll, anim throttle, scroll gates, friction, row factor ...
        s.Ww(Off.PrevCamX, 0x55AA);                           // [asm 525f] grid-dirty token
        s.Ww(Off.TrailRing, 0x4F76);                          // [asm 5265]
        Array.Clear(s.Data, Ds + Off.ObjPool, 0x40B * 2);     // [asm 55fc] object pool clear (player record included)
        for (int k = 0; k < 0x74; k++)                        // 0x74 slots free: [+4]=0xFFFF, [+0x11]=0
        {
            int slot = 0x4F0A + k * 0x12;
            s.Ww(slot + 4, 0xFFFF);
            s.Wb(slot + 0x11, 0);
        }
        s.Ww(Off.PlayerX, s.Rw(Off.StartX));                  // start point from the property header
        s.Ww(Off.PlayerY, s.Rw(Off.StartY));
        for (int i = 0; i < 0x100; i++)                       // [asm 5274-5285] decor RNG table [0x6CA9] (also the cave flakes): 2 draws per word
        {
            Pickups.Roll(s);                                  // [527A] first 39DF draw (dh), discarded but advances the generator
            int r = Pickups.Roll(s);                          // [527F] second draw -> ah = al
            s.Ww(0x6CA9 + i * 2, (r << 8) | r);               // [5284] stosw
        }
    }
}
