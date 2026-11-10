using Pre2Port.Native;

namespace Pre2Port.Recovered;

/// <summary>An object behaviour that the port does not implement yet (fail loud, never silently skipped).</summary>
public sealed class ObjectGapException : Exception
{
    public ObjectGapException(string message) : base(message) { }
}

/// <summary>Working copy of one live object slot (0x4FD0 + n*0x12): x +0, y +2, sprite id +4, xvel +8, yvel +0xA, anim script cursor +0xC, state +0xE.</summary>
internal sealed class Obj
{
    public int X, Y, Id, Xvel, Yvel, AnimPtr, State;

    public static Obj Load(LevelState s, int si) => new()
    {
        X = s.Rw(si), Y = s.Rw(si + 2), Id = s.Rw(si + 4), Xvel = s.Rw(si + 8), Yvel = s.Rw(si + 0xA), AnimPtr = s.Rw(si + 0xC), State = s.Rb(si + 0xE),
    };

    public void Store(LevelState s, int si)
    {
        s.Ww(si, X); s.Ww(si + 2, Y); s.Ww(si + 4, Id); s.Ww(si + 8, Xvel); s.Ww(si + 0xA, Yvel); s.Ww(si + 0xC, AnimPtr); s.Wb(si + 0xE, State);
    }
}

/// <summary>
/// Working copy of the spawner record (the "def", pointed to by slot+6) fields the AI handlers use. The record is a per-type union
/// (object_tick._DEF_BYTES / _DEF_WORDS): +2 word (sprite id, 0xFFFF = disabled), +4 mode byte, +6/+7 timer bytes, +9 word, +0xB word,
/// +0xD..+0x12 bytes. Like the Python walker every field is read before the handler and written back after it.
/// </summary>
internal sealed class Def
{
    public int D2, D4, D6, D7, D9, DB, DD, DE, DF, D10, D11, D12, D13, D14;

    public static Def Load(LevelState s, int d) => new()
    {
        D2 = s.Rw(d + 2), D4 = s.Rb(d + 4), D6 = s.Rb(d + 6), D7 = s.Rb(d + 7), D9 = s.Rw(d + 9), DB = s.Rw(d + 0xB),
        DD = s.Rb(d + 0xD), DE = s.Rb(d + 0xE), DF = s.Rb(d + 0xF), D10 = s.Rb(d + 0x10), D11 = s.Rb(d + 0x11), D12 = s.Rb(d + 0x12), D13 = s.Rb(d + 0x13), D14 = s.Rb(d + 0x14),
    };

    public void Store(LevelState s, int d)
    {
        s.Ww(d + 2, D2); s.Wb(d + 4, D4); s.Wb(d + 6, D6); s.Wb(d + 7, D7); s.Ww(d + 9, D9); s.Ww(d + 0xB, DB);
        s.Wb(d + 0xD, DD); s.Wb(d + 0xE, DE); s.Wb(d + 0xF, DF); s.Wb(d + 0x10, D10); s.Wb(d + 0x11, D11); s.Wb(d + 0x12, D12); s.Wb(d + 0x13, D13); s.Wb(d + 0x14, D14);
    }
}

/// <summary>Globals the AI handlers read (object_tick LiveWalkerMem.glb): player X/Y, frame counter byte [0x6BD5], shake [0x6BEA], anim-ready [0xA340], level [0x2D8A].</summary>
internal readonly record struct Glb(int PlayerX, int PlayerY, int Frame, int Shake, int A340, int Mode)
{
    public static Glb Read(LevelState s) => new(s.Rw(0x4F1C), s.Rw(0x4F1E), s.Rb(0x6BD5), s.Rb(0x6BEA), s.Rb(0xA340), s.Rb(0x2D8A));
}

/// <summary>
/// Per-type AI handlers of the live object slots (cs:[type*2+0x6AA9]) and their shared leaves, ported from
/// <c>pre2/recovered/object_update.py</c>: only the types that appear in level 1 (idx 1 = 7C8C passive, idx 2 = 7C2D vertical bob,
/// idx 4 = 7ADF orbit/pendulum, idx 8 = 77DE pouncer, idx 9 = 773D patrol, idx 10 = 7665 charger), plus advance_animation (6881),
/// terrain_collision (698C), despawn_check (8084 / 7CFF), the anim script seeks (8048 / 8058), the saturating timer (8001) and the
/// effect emitter (7FD9). Damage and death are NOT here (the combat pass 88D7 is not ported): nothing in these handlers hurts the player.
/// </summary>
internal static class ObjectAi
{
    static int S16(int v) => (short)(v & 0xFFFF);
    static int S8(int v) => (sbyte)(v & 0xFF);
    static int Abs16(int d) { d &= 0xFFFF; return d < 0x8000 ? d : (0x10000 - d) & 0xFFFF; }

    // ---- animation advance (6881..68E6) ----------------------------------------------------------------------------------------

    public readonly record struct Anim(int Id, int Ptr, int A340, bool Shake);

    /// <summary>[asm 6881-68E6] walk back-jumps, take the frame word, +0x138, keep the 0x6000 flags, flip from the record byte [+9] bit7; scale != 0 = 0xA801 zoom remap.</summary>
    public static Anim AdvanceAnimation(LevelState s, int scriptPtr, int oldId, int flipByte, int scale)
    {
        int bx = scriptPtr & 0xFFFF, raw = 0;
        bool found = false;
        for (int i = 0; i < 256; i++)                                    // [6884-688D]
        {
            raw = s.Rw(bx);
            if (raw < 0x8000) { found = true; break; }
            bx = (bx + (raw - 0x10000)) & 0xFFFF;
        }
        if (!found) throw new ObjectGapException("runaway animation back-jump (malformed script)");
        int a340 = ((raw >> 8) & 0xE0) | (scale & 0xFF);                 // [6891-689B]
        int frame = ((raw & 0x1FFF) + 0x138) & 0x1FFF;                   // [6891, 689F, 68B9]
        bool advance = true, shake = false;
        if (scale != 0)                                                  // [68A3] zoom remap
        {
            shake = scale == 7;                                          // [68AA-68B1]
            int di = 0xA801;
            bool done = false;
            for (int i = 0; i < 256; i++)                                // [68BC-68C8]
            {
                if (frame < s.Rw(di)) { done = true; break; }
                if (frame <= s.Rw(di + 2)) { frame = (s.Rw(di + 4) + 0x35) & 0x1FFF; advance = false; done = true; break; }
                di = (di + 6) & 0xFFFF;
            }
            if (!done) throw new ObjectGapException("runaway 0xA801 region-remap walk (malformed table)");
        }
        int val = frame | ((flipByte & 0x80) != 0 ? 0x8000 : 0);         // [68DA-68DF]
        return new Anim((oldId & 0x6000) | val, advance ? (bx + 2) & 0xFFFF : scriptPtr & 0xFFFF, a340, shake);
    }

    // ---- anim script seeks (8048 / 8058) and the saturating timer (8001) ---------------------------------------------------------

    public static int AnimRewind(LevelState s, int ptr)
    {
        int bx = ptr & 0xFFFF;
        for (int i = 0; i < 256; i++)
        {
            bx = (bx - 2) & 0xFFFF;                                      // [804C-8051]
            if (s.Rw(bx) >= 0x8000) return bx;
        }
        throw new ObjectGapException("runaway anim rewind (no loop marker)");
    }

    public static int AnimForward(LevelState s, int ptr)
    {
        int bx = ptr & 0xFFFF;
        for (int i = 0; i < 256; i++)
        {
            if (s.Rw(bx) >= 0x8000) return (bx + 2) & 0xFFFF;           // [805C-8065]
            bx = (bx + 2) & 0xFFFF;
        }
        throw new ObjectGapException("runaway anim forward (no loop marker)");
    }

    /// <summary>[asm 8001] [def+7] saturating ++; ready when ([def+7]&gt;&gt;2) &gt;= [def+6].</summary>
    static bool Saturating(Def d)
    {
        d.D7 = d.D7 < 0xFF ? d.D7 + 1 : 0xFF;
        return ((d.D7 >> 2) & 0xFF) >= (d.D6 & 0xFF);
    }

    // ---- despawn (8084 + 7CFF tail) -----------------------------------------------------------------------------------------------

    /// <summary>[asm 8084] despawn when far from the player (|dx| &gt; 0x140 or |dy| &gt; 0x12C) unless state 0xFF or drawn ([+5] bit5); (px, py) = the position tested (the object, or the orbit centre for 7ADF).</summary>
    static void Despawn8084(Obj o, Def d, Glb g, int px, int py)
    {
        if (o.State == 0xFF) return;                                     // [808C-8090]
        if (((o.Id >> 8) & 0x20) != 0) return;                           // [8092-8096]
        if (!(Abs16(px - g.PlayerX) > 0x140 || Abs16(py - g.PlayerY) > 0x12C)) return;   // [8098-80B2]
        int nd4 = d.D4 & 0xFB, nd2 = d.D2;                               // [80C2 / 7D0B]
        if (o.State >= 0x0A && (nd4 & 2) == 0) nd2 = 0xFFFF;             // [80B4 / 7D0F]
        o.Id = 0xFFFF; d.D2 = nd2; d.D4 = nd4; d.D7 = 0;
    }

    /// <summary>[asm 7CFF] unconditional full despawn.</summary>
    static void DespawnFull(Obj o, Def d)
    {
        d.D7 = 0; o.Id = 0xFFFF; d.D4 &= 0xFB;
        if ((d.D4 & 2) == 0) d.D2 = 0xFFFF;
    }

    /// <summary>[asm 7CDA] the shared dying state (state 0xFF).</summary>
    static void DyingState(Obj o, Def d, Glb g)
    {
        if ((d.D4 & 1) == 0) DespawnFull(o, d);
        else if ((o.Id & 0x2000) != 0 || S16(g.PlayerY) >= S16(o.Y))
        {
            if (S16(o.Yvel) < 0xF0) o.Yvel = (o.Yvel + 0xF) & 0xFFFF;
        }
        else DespawnFull(o, d);
    }

    // ---- effect emitter 7FD9 ------------------------------------------------------------------------------------------------------

    /// <summary>[asm 7FD9] three entries into the point-particle list 0x7DE6 (6 bytes: x w, y w, arg b, angle b) at the first free (x == 0xFFFF) slots.</summary>
    public static void SpawnEffects(LevelState s, int def9, int defB, int arg, int dl)
    {
        int step = (S8(dl) >> 2) & 0xFF;
        int y = (defB - 0x18) & 0xFFFF;
        int ang = step;
        for (int k = 0; k < 3; k++)
        {
            int di = 0x7DE6, guard = 0;
            while (s.Rw(di) != 0xFFFF)
            {
                di = (di + 6) & 0xFFFF;
                if (++guard > 0x2AAA) throw new ObjectGapException("effect list 0x7DE6 has no free slot");
            }
            s.Ww(di, def9); s.Ww(di + 2, y); s.Wb(di + 4, arg); s.Wb(di + 5, ang);
            ang = (ang + step) & 0xFF;
        }
    }

    // ---- terrain collision (698C + 6A7D) ---------------------------------------------------------------------------------------------

    static int ReadMap(LevelState s, int idx) => s.Data[(((s.Rw(LevelState.Off.MapSeg) << 4) & LevelState.AddrMask) + (idx & 0xFFFF)) & LevelState.AddrMask];

    static int SurfaceOffset(LevelState s, int objX, int tile)
    {
        int sl = s.Rb(0x8E1D + tile) & 0xFF;                              // [6A7F]
        if ((sl & 0x30) == 0) return S8(sl);                              // [6A83]
        int q = (objX & 0xF) / 3;                                        // [6A89-6A90]
        int al = (sl & 0x10) != 0 ? (q + (sl & 0xF)) & 0xFF : (((-q) & 0xFF) + (sl & 0xF)) & 0xFF;   // [6A92-6A9E]
        return S8(al);
    }

    static void Settle(LevelState s, Obj o, Def d, int tile)
    {
        int off = SurfaceOffset(s, o.X, tile);                           // [6A40]
        o.Y = ((o.Y & 0xFFF0) + off) & 0xFFFF;                           // [6A43-6A47]
        if ((d.D4 & 0x20) != 0) o.Yvel = 0;                              // [6A4D / 6A65]
        else
        {
            int dx = S16((-S16(o.Yvel)) & 0xFFFF) >> 1;                  // [6A53-6A5A]
            o.Yvel = Math.Abs(dx) > 0x20 ? dx & 0xFFFF : 0;              // [6A5E-6A67]
        }
    }

    /// <summary>[asm 698C] per-object terrain collision (called by the walker when [def+4] &amp; 8). Mutates x, y, xvel, yvel, anim ptr and def d4.</summary>
    public static void TerrainCollision(LevelState s, Obj o, Def d)
    {
        int ty = (S16(o.Y) >> 4) & 0xFF, tx = (S16(o.X) >> 4) & 0xFF;     // [6993-699E]
        int idx = (ty << 8) | tx;
        int tileHere = ReadMap(s, idx);                                  // [69A4]
        int tileAbove = ReadMap(s, (idx - 0x100) & 0xFFFF);              // [69A7]
        int xv = S16(o.Xvel);
        int dir = xv == 0 ? 0 : (xv > 0 ? 1 : -1);                       // [69AC-69B9]
        idx = (idx + dir) & 0xFFFF;                                      // [69BB]
        int tileAhead = ReadMap(s, (idx - 0x100) & 0xFFFF);              // [69BD]
        int a = s.Rb(0x7E5E + tileAhead) & 0xFF;                         // [69C2-69C5] ceiling property (prop_a)
        int flags = d.D4;
        if ((flags & 0x40) != 0 && (flags & 0x80) != 0)                  // [69C9-69D3] climbing
        {
            if (a != 0) o.X = (o.X - (S16(o.Xvel) >> 4)) & 0xFFFF;       // [69D9-69DE]
            else
            {
                o.AnimPtr = AnimRewind(s, o.AnimPtr);                    // [69E3]
                o.Yvel = 0;                                              // [69E6]
                d.D4 = flags & 0x7F;                                     // [69EB]
                o.Y = (o.Y - 0x10) & 0xFFFF;                             // [69EF]
            }
            return;
        }
        if (a != 0)                                                      // [69F6] wall ahead
        {
            if ((flags & 0x40) != 0)                                     // [69FA] start climbing
            {
                o.AnimPtr = AnimForward(s, o.AnimPtr);                   // [6A00]
                d.D4 = flags | 0x80;                                     // [6A03]
                o.Yvel = 0xFFF0;                                         // [6A07]
                o.X = (o.X + (S16(o.Xvel) >> 2)) & 0xFFFF;               // [6A0C-6A13]
                return;
            }
            o.Xvel = (-S16(o.Xvel)) & 0xFFFF;                            // [6A17]
            o.X = (o.X + (S16(o.Xvel) >> 4)) & 0xFFFF;                   // [6A1A-6A1F]
        }
        o.Y = (o.Y - 0x10) & 0xFFFF;                                     // [6A24]
        if ((s.Rb(0x7F5E + tileAbove) & 0xFF) != 0) Settle(s, o, d, tileAbove);      // [6A2A-6A3E] prop_b = floor property
        else
        {
            o.Y = (o.Y + 0x10) & 0xFFFF;                                 // [6A31]
            if ((s.Rb(0x7F5E + tileHere) & 0xFF) == 0)                   // [6A35-6A3A]
            {
                if (S16(o.Yvel) < 0x100) o.Yvel = (o.Yvel + 0x10) & 0xFFFF;   // [6A6C-6A73]
            }
            else Settle(s, o, d, tileHere);
        }
    }

    // ---- AI handlers ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// idx 0, 1030:7C90: 8084 (far despawn), then by state: 0 wait on the 8001 timer ([+0xE] is the slot state), despawn fully (7CFF) when the entity is &gt;= 0xB0 below the player (signed [+2] - [0x4F1E]), else flag [def+4] |= 8
    /// (terrain collision in the walker) and state 1; 1 once Yvel == 0 (landed): state 2, Xvel = +/-0x20 toward the player ([si] &lt; [0x4F1C] signed = +), anim forward 8058; 2 idle; 0xFF dying (7CDA); anything else returns.
    /// </summary>
    public static void H7C90(LevelState s, Obj o, Def d, Glb g)
    {
        Despawn8084(o, d, g, o.X, o.Y);                                  // [7C90] CALL 8084
        int st = o.State;                                                // [7C93]
        if (st == 0)                                                     // [7C96]
        {
            if (!Saturating(d)) return;                                  // [7C9A-7C9D] CALL 8001 / JB
            if (S16(o.Y - g.PlayerY) >= 0xB0) { DespawnFull(o, d); return; }   // [7C9F-7CA9] CMP AX,0xB0 / JGE 7CFF
            d.D4 |= 8;                                                   // [7CAB]
            o.State = 1;                                                 // [7CAF]
        }
        else if (st == 1)                                                // [7CB4]
        {
            if (o.Yvel != 0) return;                                     // [7CB8-7CBC]
            o.State = 2;                                                 // [7CBE]
            o.Xvel = S16(o.X) < S16(g.PlayerX) ? 0x20 : 0xFFE0;          // [7CC2-7CCE] JL keeps +0x20, else NEG
            o.AnimPtr = AnimForward(s, o.AnimPtr);                       // [7CD1] CALL 8058
        }
        else if (st == 0xFF) DyingState(o, d, g);                        // [7CDA]
    }

    /// <summary>idx 1, 1030:7C8C = call 8084; ret: passive, only despawns when far.</summary>
    public static void H7C8C(LevelState s, Obj o, Def d, Glb g) => Despawn8084(o, d, g, o.X, o.Y);

    /// <summary>idx 2, 1030:7C2D: vertical bob around [def+0xB] (amplitude [def+0xD], speed [def+0xE]) with a 7FD9 effect trail each frame.</summary>
    public static void H7C2D(LevelState s, Obj o, Def d, Glb g)
    {
        Despawn8084(o, d, g, o.X, o.Y);
        int st = o.State;
        if (st >= 2) { if (st == 0xFF) DyingState(o, d, g); return; }   // [7C35]
        int relY = (o.Y - d.DB) & 0xFFFF;                                // [7C3A]
        SpawnEffects(s, d.D9, d.DB, 0, relY & 0xFF);                     // [7C40-7C42]
        if (st == 0)                                                     // [7C4A] down
        {
            o.Yvel = (S8(d.DE) << 4) & 0xFFFF;
            if (S8(d.DD) < S8(relY & 0xFF)) { o.State = 1; o.AnimPtr = AnimForward(s, o.AnimPtr); }   // [7C59]
        }
        else                                                             // [7C66] up
        {
            o.Yvel = (-(S8(d.DE) << 4)) & 0xFFFF;
            if (S16(relY) < 0) { o.State = 0; o.AnimPtr = AnimRewind(s, o.AnimPtr); }    // [7C7B]
        }
    }

    /// <summary>idx 3, 1030:7B91: falling/landing enemy (state 0 wait for the player within [def+0xD] tiles, 1 fall until the tile under it is solid, 2 dash, 0xFF dying).</summary>
    public static void H7B91(LevelState s, Obj o, Def d, Glb g)
    {
        Despawn8084(o, d, g, o.X, o.Y);
        int st = o.State;
        if (st == 0)                                                     // [7B99]
        {
            if (!Saturating(d)) return;
            int distX = (Abs16(d.D9 - g.PlayerX) >> 4) & 0xFF;           // [7BA2-7BAD]
            if ((d.DD & 0xFF) < distX) return;
            d.D4 &= 0xEF; o.State = 1; o.Yvel = 0x20;                    // [7BB4-7BBC]
            o.AnimPtr = AnimForward(s, o.AnimPtr);
        }
        else if (st == 1)                                                // [7BC5]
        {
            SpawnEffects(s, d.D9, d.DB, 0, (o.Y - d.DB) & 0xFF);         // [7BC9-7BD1]
            int prop = s.Rb(0x7F5E + ReadMap(s, (((o.Y >> 4) & 0xFF) * 0x100 + ((o.X >> 4) & 0xFF)) & 0xFFFF));   // [7BD4-7BEB]
            if (prop == 0) return;
            o.Y &= 0xFFF0; o.Yvel = 0; o.State = 2; d.D4 |= 0x48;        // [7BF0-7C00]
            o.Xvel = S16(g.PlayerX) >= S16(o.X) ? 0x30 : 0xFFD0;         // [7C04-7C11]
            o.AnimPtr = AnimForward(s, o.AnimPtr);
        }
        else if (st == 2) { if (S16(o.X) < 0) o.Xvel = (-S16(o.Xvel)) & 0xFFFF; }   // [7C18]
        else if (st == 0xFF) DyingState(o, d, g);                        // [7C29]
    }

    /// <summary>idx 4, 1030:7ADF: orbit / pendulum around the spawner point ([def+9], [def+0xB]); despawn is tested on the centre (8089 entry).</summary>
    public static void H7ADF(LevelState s, Obj o, Def d, Glb g)
    {
        Despawn8084(o, d, g, d.D9, d.DB);
        void Orbit()                                                     // [7B53]
        {
            int ang = d.DF & 0xFF, r = S8(d.DD);
            o.X = (d.D9 + (((S8(s.Rb(0x6F90 + ang)) >> 2) * r) >> 4)) & 0xFFFF;   // [7B5A-7B6C]
            o.Y = (d.DB + (((S8(s.Rb(0x7090 + ang)) >> 2) * r) >> 4)) & 0xFFFF;   // [7B79-7B84]
            SpawnEffects(s, d.D9, d.DB, d.DF & 0xFF, d.DD & 0xFF);      // [7B87]
        }
        int st = o.State;
        if (st == 0)                                                     // [7AF0] descend
        {
            if (!Saturating(d)) return;
            int relY = (o.Y - d.DB) & 0xFFFF;
            SpawnEffects(s, d.D9, d.DB, 0, relY & 0xFF);                 // [7B00]
            if ((d.DD & 0xFF) <= (relY & 0xFF)) o.State = 1;             // [7B06]
            else o.Y = (o.Y + 2) & 0xFFFF;                               // [7B0B]
        }
        else if (st == 1)                                                // [7B15] spin up
        {
            Orbit();
            d.DF = (d.DF + 4) & 0xFF;                                    // [7B1C]
            if ((d.DF & 0xFF) >= (d.DE & 0xFF)) { d.DF = d.DE & 0xFF; o.State = 2; }   // [7B23]
        }
        else if (st == 2)                                                // [7B30] pendulum
        {
            Orbit();
            int dl = S8(d.DF) < 0 ? 1 : -1;                              // [7B37-7B3F]
            d.D10 = (d.D10 + dl) & 0xFF;                                 // [7B41]
            d.DF = (d.DF + S8(d.D10)) & 0xFF;                            // [7B47]
        }
        else if (st == 0xFF) DyingState(o, d, g);                        // [7B4F]
    }

    static void Pounce(LevelState s, Obj o, Def d, Glb g)                // [7827]
    {
        o.Yvel = (-(S8(d.DE) << 4)) & 0xFFFF;
        int spd = (S8(d.DF) << 4) & 0xFFFF;
        o.Xvel = S16(g.PlayerX) > S16(o.X) ? spd : (-spd) & 0xFFFF;
        o.State = 0xA;
        d.D4 = (d.D4 & 0xD3) | 0x2C;
        o.AnimPtr = AnimForward(s, o.AnimPtr);
    }

    /// <summary>idx 8, 1030:77DE: pouncing enemy (wait on the 8001 timer, leap at the player inside [def+0xD] x [def+0x10] tiles; states 0xA rise, 0xB land, 0xC cooldown).</summary>
    public static void H77DE(LevelState s, Obj o, Def d, Glb g)
    {
        Despawn8084(o, d, g, o.X, o.Y);
        if (o.Xvel == 0) o.Xvel = S16(o.X) <= S16(g.PlayerX) ? 1 : 0xFFFF;   // [77E3-77F5]
        int st = o.State;
        if (st == 0)                                                     // [77FF]
        {
            if (!Saturating(d)) return;
            int distX = (Abs16(g.PlayerX - o.X) >> 4) & 0xFF;            // [7804-780F]
            if ((d.DD & 0xFF) < distX) return;
            int distY = (Abs16(g.PlayerY - o.Y) >> 4) & 0xFF;            // [7814-7820]
            if ((d.D10 & 0xFF) >= distY) Pounce(s, o, d, g);
        }
        else if (st == 0xA)                                              // [7857]
        {
            if (S16(o.Yvel) >= 0) { o.State = 0xB; o.AnimPtr = AnimForward(s, o.AnimPtr); }
        }
        else if (st == 0xB)                                              // [7869]
        {
            if (S16(o.Yvel) <= 0)
            {
                o.State = 0xC;
                o.AnimPtr = AnimRewind(s, AnimRewind(s, o.AnimPtr));
                o.Xvel = 0;                                              // [787D]
                d.D7 = 0;                                                // [7882]
            }
        }
        else if (st == 0xC)                                              // [7887]
        {
            if (Saturating(d)) Pounce(s, o, d, g);
        }
        else if (st == 0xFF) DyingState(o, d, g);                        // [7890]
    }

    /// <summary>idx 7, 1030:7898: leaper. State 0 faces the player (Xvel = +-1) and, once within [def+0xD] tiles (low byte of |dx|&gt;&gt;4), leaps: state 0xA, Yvel = s8([def+0xE])&lt;&lt;4, Xvel = +-that; 0xFF dies.</summary>
    public static void H7898(LevelState s, Obj o, Def d, Glb g)
    {
        Despawn8084(o, d, g, o.X, o.Y);                                  // [7898] 8084
        if (o.State == 0)                                                // [789D-78A2]
        {
            o.Xvel = (S16(o.X) <= S16(g.PlayerX) ? 1 : -1) & 0xFFFF;     // [78A4-78B0]
            int dist = (Abs16(g.PlayerX - o.X) >> 4) & 0xFF;             // [78B3-78BC]
            if ((d.DD & 0xFF) < dist) return;                            // [78BE-78C1]
            o.State = 0xA;                                               // [78C3]
            int leap = (S8(d.DE) << 4) & 0xFFFF;                         // [78C7-78CB]
            o.Yvel = leap;                                               // [78CD]
            if (((o.Xvel >> 8) & 0x50) != 0) leap = (-leap) & 0xFFFF;    // [78D0-78D6]
            o.Xvel = leap;                                               // [78D8]
            o.AnimPtr = AnimForward(s, o.AnimPtr);                       // [78DB] 8058
        }
        else if (o.State == 0xFF) DyingState(o, d, g);                   // [78E4-78E8]
    }

    /// <summary>
    /// idx 6, 1030:78EC: the earthquake / screen-shake driver. 8084, then state 0 faces the player (7A50) and, within [def+0xD] tiles, arms (state 1, [def+0x10] = 0x18, shake velocities 0, position accumulators
    /// [def+0x11] / [def+0x13] = X &lt;&lt; 3 / Y &lt;&lt; 3); state 1 faces, computes dist^2 into [0xA30E:0xA310] and, close (&lt; 0x2710) with quakes enabled ([0x6BD0] != 0), kicks a directional velocity ([def+0xE] or [def+0xF]);
    /// otherwise counts [def+0x10] down and on underflow reseeds the shake ([0x6BC0] / [0x6BC1] amplitudes, [def+0xE] / [def+0xF] velocities, with 26CF and 39DF). Every state-1 path ends in 7A28: the accumulators
    /// move by the velocities and the position is accumulator &gt;&gt; 3. State 0xFF dies.
    /// </summary>
    public static void H78EC(LevelState s, Obj o, Def d, Glb g)
    {
        Despawn8084(o, d, g, o.X, o.Y);                                  // [78EC] 8084
        if (o.State == 0)                                                // [78F1-78F6]
        {
            o.Xvel = (S16(o.X) <= S16(g.PlayerX) ? 1 : -1) & 0xFFFF;     // [78F8] 7A50
            int dist = (Abs16(g.PlayerX - o.X) >> 4) & 0xFF;             // [78FB-7904]
            if ((d.DD & 0xFF) < dist) return;                            // [7906-790B] CMP [BX+0xD],AL / JNC arm
            o.State = 1;                                                 // [790E]
            d.D10 = 0x18; d.DE = 0; d.DF = 0;                            // [7912-791A]
            int ax = (o.X << 3) & 0xFFFF, ay = (o.Y << 3) & 0xFFFF;      // [791E-792C]
            d.D11 = ax & 0xFF; d.D12 = ax >> 8; d.D13 = ay & 0xFF; d.D14 = ay >> 8;
            return;
        }
        if (o.State == 1)                                                // [7930-7937]
        {
            o.Xvel = (S16(o.X) <= S16(g.PlayerX) ? 1 : -1) & 0xFFFF;     // [7937] 7A50
            int dxw = (g.PlayerX - o.X) & 0xFFFF;                        // [793A-793F] BP = signed dX
            int adx = Abs16(dxw), ady = Abs16(g.PlayerY - o.Y);          // [7941-7943] / [794F-7957]
            long dist = (long)adx * adx + (long)ady * ady;               // [7945-7961] MUL AX, then ADD/ADC
            s.Ww(0xA30E, (int)(dist & 0xFFFF)); s.Ww(0xA310, (int)((dist >> 16) & 0xFFFF));
            if (((dist >> 16) & 0xFFFF) == 0 && (dist & 0xFFFF) < 0x2710 && s.Rb(0x6BD0) != 0)   // [7965-7979]
            {
                int al = (dist & 0xFFFF) < 0xE10 ? 0x68 : 0x40;          // [797B-7985]
                if (ady >= 0x30) d.DF = 0xC0;                            // [7987-798A, 7998]
                else
                {
                    if (S16(dxw) > 0) al = (-al) & 0xFF;                 // [798C-7990] OR BP,BP / JLE
                    d.DE = al & 0xFF;                                    // [7992]
                }
            }
            else
            {
                d.D10 = (d.D10 - 1) & 0xFF;                              // [799F]
                if ((d.D10 & 0x80) != 0)                                 // [79A2] JS: underflow -> reseed
                {
                    d.D10 = ((RorRoll(s) & 0xF) + 3) & 0xFF;             // [79A7-79AE] 26CF
                    int al = Pickups.Roll(s) & 0xF;                      // [79B1-79B9] 39DF (AH = 0)
                    if (S16(g.PlayerX) < S16(o.X)) al = (-al) & 0xFF;    // [79BB-79C3]
                    s.Wb(0x6BC0, al);                                    // [79C5]
                    al = Pickups.Roll(s) & 0xF;                          // [79C8-79CB]
                    if (S16(g.PlayerY) < S16((o.Y + 0x32) & 0xFFFF)) al = (-al) & 0xFF;   // [79CD-79D9]
                    s.Wb(0x6BC1, al);                                    // [79DB]
                    al = RorRoll(s) & 0x1F;                              // [79DE-79E1]
                    int bc0 = S8(s.Rb(0x6BC0)), bc1 = S8(s.Rb(0x6BC1));
                    if (bc0 < 0) al = (-al) & 0xFF;                      // [79E3-79EA]
                    d.DE = al & 0xFF;                                    // [79EC]
                    if (bc0 == 0) al = 2;                                // [79EF-79F8]
                    else al = (((Math.Abs(bc1)) << 3) / Math.Abs(bc0)) & 0xFF;   // [79FA-7A14] DIV CH
                    if (al >= 0x20) al = 0x20;                           // [7A16-7A1A]
                    if (bc1 < 0) al = (-al) & 0xFF;                      // [7A1C-7A23]
                    d.DF = al & 0xFF;                                    // [7A25]
                }
            }
            int wx = ((d.D11 | (d.D12 << 8)) + S8(d.DE)) & 0xFFFF;       // [7A28-7A2C]
            int wy = ((d.D13 | (d.D14 << 8)) + S8(d.DF)) & 0xFFFF;       // [7A2F-7A33]
            d.D11 = wx & 0xFF; d.D12 = wx >> 8; d.D13 = wy & 0xFF; d.D14 = wy >> 8;
            o.X = (S16(wx) >> 3) & 0xFFFF;                               // [7A36-7A3D] SAR 3
            o.Y = (S16(wy) >> 3) & 0xFFFF;                               // [7A3F-7A44]
            return;
        }
        if (o.State == 0xFF) DyingState(o, d, g);                        // [7A48-7A4C]
    }

    /// <summary>[asm 26CF] the rotate generator: [0x28C1] = ROR3([0x28C1] + 0x9248); returns the new value.</summary>
    static int RorRoll(LevelState s)
    {
        int v = (s.Rw(0x28C1) + 0x9248) & 0xFFFF;
        v = ((v >> 3) | (v << 13)) & 0xFFFF;
        s.Ww(0x28C1, v);
        return v;
    }

    /// <summary>
    /// idx 11, 1030:760F: leaper (flying squirrel). 8084; state 0 waits for the animation-ready flag [0xA340], then state 1, [def+4] &amp;= 0xEF, Xvel = +-(s8([def+0xD]) &lt;&lt; 4) toward the player (player X &gt; object X = +),
    /// Yvel = -(s8([def+0xE]) &lt;&lt; 4); state 1 falls (Yvel += 8 while Yvel &lt; s8([def+0xF]) &lt;&lt; 4, signed); 0xFF dies (7712 = the 7CDA body).
    /// </summary>
    public static void H760F(LevelState s, Obj o, Def d, Glb g)
    {
        Despawn8084(o, d, g, o.X, o.Y);                                  // [7611] 8084
        if (o.State == 0)                                                // [7614-7619]
        {
            if (g.A340 == 0) return;                                     // [761B-7620]
            o.State = 1;                                                 // [7622]
            d.D4 &= 0xEF;                                                // [7626]
            int leap = (S8(d.DD) << 4) & 0xFFFF;                         // [762A-762E]
            o.Xvel = S16(g.PlayerX) > S16(o.X) ? leap : (-leap) & 0xFFFF;   // [7630-763A] CMP [4F1C],DX / JG / NEG
            o.Yvel = (-(S8(d.DE) << 4)) & 0xFFFF;                        // [763D-7645]
        }
        else if (o.State == 1)                                           // [7649]
        {
            if (S16(o.Yvel) < S16((S8(d.DF) << 4) & 0xFFFF)) o.Yvel = (o.Yvel + 8) & 0xFFFF;   // [764D-7658]
        }
        else if (o.State == 0xFF) DyingState(o, d, g);                   // [765D-7661]
    }

    /// <summary>
    /// idx 12, 1030:75C4: drifter (no 8084). State 0: Xvel = +-(s8([def+0xD]) &lt;&lt; 4) (negated while the player X &lt; object X, signed), state 1. State 1: while drawn ([+5] bit 5) [def+7] = 0, else [def+7]++ (byte, not saturating)
    /// and at 0x9A the state becomes 0xFF. State 0xFF dies.
    /// </summary>
    public static void H75C4(LevelState s, Obj o, Def d, Glb g)
    {
        if (o.State == 0)                                                // [75C9-75CE]
        {
            int ax = S8(d.DD);                                           // [75D0-75D3]
            if (S16(g.PlayerX) < S16(o.X)) ax = -ax;                     // [75D4-75DC] JGE skips the NEG
            o.Xvel = (ax << 4) & 0xFFFF;                                 // [75DE-75E0]
            o.State = 1;                                                 // [75E3]
        }
        else if (o.State == 1)                                           // [75E8-75EA]
        {
            if (((o.Id >> 8) & 0x20) != 0) d.D7 = 0;                     // [75EC-7601]
            else
            {
                d.D7 = (d.D7 + 1) & 0xFF;                                // [75F2]
                if (d.D7 >= 0x9A) o.State = 0xFF;                        // [75F5-75FB] JC keeps it below 0x9A
            }
        }
        else if (o.State == 0xFF) DyingState(o, d, g);                   // [7607-760B]
    }

    /// <summary>idx 9, 1030:773D: horizontal patrol between the 16-bit bounds [def+0xD] / [def+0xF] with its own proximity despawn (no 8084).</summary>
    public static void H773D(LevelState s, Obj o, Def d, Glb g)
    {
        int left = S16(d.DD | (d.DE << 8)), right = S16(d.DF | (d.D10 << 8));
        bool drawn = (o.Id & 0x2000) != 0;
        int st = o.State;
        if (!drawn && st != 0xFF)                                        // [7740-7784]
        {
            bool keep;
            if (Abs16(o.Y - g.PlayerY) >= 0xBE) keep = false;            // [774C-775A]
            else
            {
                int px = S16(g.PlayerX);
                keep = px >= left ? right + 0x1E0 > px : px + 0x1E0 >= left;   // [775C-7779]
            }
            if (!keep) { o.Id = 0xFFFF; d.D4 &= 0xFB; return; }          // [777B]
        }
        if (st == 0)                                                     // [7785] patrol right
        {
            int sp = S8(d.D11);
            o.Xvel = sp & 0xFFFF;                                        // [7790]
            if (sp + 3 <= (d.D12 & 0xFF)) d.D11 = (sp + 3) & 0xFF;       // [7793-779D]
            if (right < S16(o.X)) o.State = 1;                           // [77A2-77A9]
        }
        else if (st == 1)                                                // [77AE] patrol left
        {
            int sp = S8(d.D11);
            o.Xvel = sp & 0xFFFF;
            if (sp - 3 >= -(d.D12 & 0xFF)) d.D11 = (sp - 3) & 0xFF;      // [77B9-77C5]
            if (left >= S16(o.X)) o.State = 0;                           // [77CA-77D1]
        }
        else if (st == 0xFF) DyingState(o, d, g);                        // [77DA]
    }

    /// <summary>
    /// idx 5, 1030:7A60: 2D-proximity pouncer (LEVEL7 spawners 86CA / 86DA). 8084 far despawn; state 0 sets Xvel = +1 (X &lt;= playerX, signed) or -1 (0xFFFF = "no X move" to the walker) and, when
    /// (|playerX - X| &gt;&gt; 4) low byte &lt;= [def+0xD] and (|playerY - Y| &gt;&gt; 4) low byte &lt;= [def+0xE] (byte compares, CL = 4), leaps: state 0xA, Yvel = [def+0xF] (UNSIGNED byte, XOR AH,AH) &lt;&lt; 4, Xvel = that,
    /// negated when the Xvel just written has bit 15 set (facing left); state 0xA flies until |Y - playerY| &lt;= 8 (unsigned), then 8058 anim forward, state 0xB, Yvel = 0; 0xB idle; 0xFF dying (7CDA).
    /// </summary>
    public static void H7A60(LevelState s, Obj o, Def d, Glb g)
    {
        Despawn8084(o, d, g, o.X, o.Y);                                  // [7A60] CALL 8084
        int st = o.State;                                                // [7A65-7A6A]
        if (st == 0)
        {
            o.Xvel = S16(o.X) <= S16(g.PlayerX) ? 1 : 0xFFFF;            // [7A6C-7A78] DX = 1, NEG when X &gt; playerX (JLE)
            if ((d.DD & 0xFF) < ((Abs16(g.PlayerX - o.X) >> 4) & 0xFF)) return;   // [7A7B-7A89] SAR AX,4 / CMP [def+0xD],AL / JB
            if ((d.DE & 0xFF) < ((Abs16(g.PlayerY - o.Y) >> 4) & 0xFF)) return;   // [7A8B-7A9A]
            o.State = 0xA;                                               // [7A9C]
            int v = ((d.DF & 0xFF) << 4) & 0xFFFF;                       // [7AA0-7AA5]
            o.Yvel = v;                                                  // [7AA7]
            if ((o.Xvel & 0x8000) != 0) v = (-v) & 0xFFFF;               // [7AAA-7AB0] TEST byte [si+9],0x80
            o.Xvel = v;                                                  // [7AB2]
        }
        else if (st == 0xA)                                              // [7AB7]
        {
            if (Abs16(o.Y - g.PlayerY) > 8) return;                      // [7ABB-7AC9] CMP AX,8 / JA
            o.AnimPtr = AnimForward(s, o.AnimPtr);                       // [7ACB] CALL 8058
            o.State = 0xB; o.Yvel = 0;                                   // [7ACE-7AD2]
        }
        else if (st == 0xFF) DyingState(o, d, g);                        // [7ADB] JMP 7CDA
    }

    /// <summary>idx 10, 1030:7665: charger (state 0 settle, 1 arm, 2 charge at the player on a 0x1E x 4-frame timer, 3 expire, 0xFF dying).</summary>
    public static void H7665(LevelState s, Obj o, Def d, Glb g)
    {
        Despawn8084(o, d, g, o.X, o.Y);
        int st = o.State;
        if (g.Mode == 5 && g.Shake != 0 && st != 3 && st != 0xFF) d.D7 = 1;   // [766B-7684]
        if (st == 0)                                                     // [7685]
        {
            d.D4 |= 0x18;
            if (o.Yvel == 0) o.State = 1;
        }
        else if (st == 1)                                                // [7698]
        {
            if (o.Yvel != 0) o.State = 0;
            else { d.D7 = 0x1E; o.State = 2; o.AnimPtr = AnimForward(s, o.AnimPtr); }
        }
        else if (st == 2)                                                // [76B3]
        {
            if (Math.Abs(S16(o.Xvel)) < 0x10)                            // [76B7-76C3]
            {
                if (g.A340 == 0) return;                                 // [76C5]
                d.D4 = 0x0F;                                             // [76CC]
                int spd = d.DD & 0xFF;                                   // [76D0]
                o.Xvel = S16(o.X) < S16(g.PlayerX) ? spd : (-spd) & 0xFFFF;   // [76D5-76DE]
            }
            if ((g.Frame & 3) != 0) return;                              // [76E1]
            d.D7 = (d.D7 - 1) & 0xFF;                                    // [76E8]
            if (d.D7 != 0) return;
            o.State = 3;                                                 // [76ED]
            o.Yvel = 0;
            o.Xvel = (o.Xvel & 0x8000) != 0 ? 0xFFFF : 0;                // [76F8]
            d.D4 = 0x36;
            o.AnimPtr = AnimForward(s, o.AnimPtr);
        }
        else if (st == 3) { if (g.A340 != 0) DespawnFull(o, d); }        // [7703]
        else if (st == 0xFF) DyingState(o, d, g);                        // [7712]
    }
}
