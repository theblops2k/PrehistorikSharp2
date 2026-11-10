using Pre2Port.Native;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Recovered;

/// <summary>
/// The menu-idle ATTRACT title animation [asm 8F2D..9046], port of <c>pre2/native/attract_title.py native_attract_title</c> (pixel-exact vs the VM in
/// Python: 119 frames, 0 diff). Over the decoded MENU2.SQZ planar base (page 0x2000) a caveman (the player record 0x4F1C, animation stepper 638B) runs
/// right chased by a dino (record 0x4F2E, stepper 9047) until he leaves the screen (phase 1, 69 frames); then the dino record is replicated as bouncing
/// objects and the caveman runs back left until X &lt; 0 (phase 2, 50 frames). [0x2879] stays 0: a scripted scene, not the demo.
/// Each yielded frame is one 44FB present (3 retraces). The sprite pass is the ported 26FA (<see cref="SpriteRender"/>) over a level-0 cold-boot state
/// (the sprite bank and the attribute tables).
/// </summary>
public sealed class AttractTitle
{
    public const int Page = 0x2000, FrameCount = 119;
    const int Ds = BootImage.DsBase;
    const int Dino = 0x4F2E, Stride = 0x12, FrameTimer = 0x6BD5;

    readonly LevelState _s;
    readonly byte[][] _base;                       // 4 x 8000-byte MENU2 planes
    readonly byte[][] _planes = { new byte[0x10000], new byte[0x10000], new byte[0x10000], new byte[0x10000] };

    /// <param name="levelZero">A level-0 cold-boot state (<see cref="LevelLoad.ColdBoot"/>); it is mutated.</param>
    /// <param name="basePlanes">The four 8000-byte MENU2 planes.</param>
    public AttractTitle(LevelState levelZero, byte[][] basePlanes)
    {
        _s = levelZero;
        _base = basePlanes;
    }

    static int S16(int v) => (short)(v & 0xFFFF);

    /// <summary>[asm 9047] generic object animation stepper for the record at <paramref name="si"/>.</summary>
    static void AdvanceObjAnim(LevelState s, int si)
    {
        int bx = s.Rw(si + 0xC);
        int ax = s.Rw(bx);
        if ((ax & 0x8000) != 0)                               // [9051] loop marker: rewind + reload
        {
            bx = (bx + S16(ax)) & 0xFFFF;
            ax = s.Rw(bx);
        }
        ax = ((ax & 0x1FFF) + 0x138) & 0xFFFF;                // [9057] AND AH,0x1F; [905A] ADD AX,0x138
        ax |= (s.Rb(si + 9) & 0x80) << 8;                     // [905D-9063] AH |= [si+9] & 0x80
        s.Ww(si + 4, ax);                                     // [9065]
        s.Ww(si + 0xC, (bx + 2) & 0xFFFF);                    // [906A]
    }

    /// <summary>[asm 9071 + 26FA] the jungle base restored over the page, then every non-empty record from 0x5720 down to 0x4F0A painted on it.</summary>
    byte[] Compose()
    {
        for (int p = 0; p < 4; p++)
        {
            Array.Clear(_planes[p]);
            Array.Copy(_base[p], 0, _planes[p], Page, _base[p].Length);
        }
        int frame = _s.Rw(FrameTimer);                        // read_renderer_state(frame_pre_inc=False): [0x6BD5] without the 26FA +1
        for (int off = SpriteRender.ListTop; off >= SpriteRender.ListBase; off -= SpriteRender.RecordBytes)
            if (SpriteRender.Plan(_s, off, Page, frame, out var dr)) SpriteRender.Paint(_s, _planes, dr);
        return MenuScenePage.Deplanarize(_planes, Page);
    }

    /// <summary>[asm 8FBF test [0x4F21],0x20] whether 26FA draws the caveman this frame: the phase-1 loop exit gate.</summary>
    bool CavemanOnScreen() => SpriteRender.Plan(_s, Player.PX, Page, _s.Rw(FrameTimer), out _);

    /// <summary>The records the animation itself moves (the caveman and the three staggered monsters of phase 2, 8FF2..9000), in the 26FA paint order (highest address first).
    /// The five further copies made by the 8FDA <c>rep movsw</c> are never advanced and sit still at the dino's X (off screen to the right): they are NOT part of the scene.</summary>
    static readonly int[] DrivenRecords = { Dino + 2 * Stride, Dino + Stride, Dino, Player.PX };

    byte[][]? _wideBlank, _wideFull;

    /// <summary>
    /// Port-only (FULLSCREEN=WIDESCREEN, presentation only): the sprites of the frame last yielded, as they would be painted if the screen were shifted by <paramref name="dx"/> pixels
    /// (every driven record's X + dx; restored afterwards), as 320x200 indices with 0xFF = no sprite pixel. The same 26FA pass (Plan/Paint) runs over a blank and a full page and a pixel
    /// counts as sprite if either result differs from its page (sprite pixels of any index, 0 included). Used to continue the sprites through the side margins; the state is not changed.
    /// </summary>
    public byte[] SpriteLayer(int dx)
    {
        _wideBlank ??= new[] { new byte[0x10000], new byte[0x10000], new byte[0x10000], new byte[0x10000] };
        _wideFull ??= new[] { new byte[0x10000], new byte[0x10000], new byte[0x10000], new byte[0x10000] };
        for (int p = 0; p < 4; p++) { Array.Clear(_wideBlank[p]); Array.Clear(_wideFull[p]); Array.Fill(_wideFull[p], (byte)0xFF, Page, 8000); }
        int frame = _s.Rw(FrameTimer);
        foreach (int off in DrivenRecords)
        {
            if (_s.Rw(off + 4) == 0xFFFF) continue;
            int x = _s.Rw(off);
            _s.Ww(off, x + dx);
            try { if (SpriteRender.Plan(_s, off, Page, frame, out var dr)) { SpriteRender.Paint(_s, _wideBlank, dr); SpriteRender.Paint(_s, _wideFull, dr); } }
            finally { _s.Ww(off, x); }
        }
        var a = MenuScenePage.Deplanarize(_wideBlank, Page);
        var f = MenuScenePage.Deplanarize(_wideFull, Page);
        var layer = new byte[a.Length];
        for (int i = 0; i < layer.Length; i++) layer[i] = a[i] != 0 || f[i] != 15 ? a[i] : (byte)0xFF;
        return layer;
    }

    /// <summary>The 119 frames as 320x200 indexed pixel images (64000 bytes, the palette is the caller's). Mutates the state as 8F2D..9046 does.</summary>
    public IEnumerable<byte[]> Frames()
    {
        var s = _s;
        // ---- SETUP (8F46..8F8F) ----
        s.Wb(0x6C01, 0); s.Wb(0x6C02, 1); s.Wb(0x6C03, 0);     // light fade: to_dark 0, to_light 1, step 0
        s.Wb(Off.FineScroll, 0);                                // [8F55]
        s.Ww(Off.CamCol, 0); s.Ww(Off.CamRow, 0);               // [8F5C/8F5F]
        s.Ww(Player.PX, 0); s.Ww(Player.PY, 0xA9);              // caveman X=0, Y=0xA9
        s.Ww(Player.PFacing, 0); s.Ww(Player.PXvel, 0);         // facing right, Xvel 0
        s.Ww(0x4F28, 0x7BA7);                                   // anim-ptr
        s.Ww(Dino, 0x30); s.Ww(Dino + 2, 0xA9);                 // dino X, Y
        s.Ww(Dino + 9, 0); s.Ww(Dino + 6, 0);                   // [4F37] flip/source word, [4F34] Xvel
        s.Ww(Dino + 0xC, 0xAB4B);                               // dino anim-ptr
        s.Wb(0x6BC5, 0);                                        // glider off

        // ---- PHASE 1 (8F94..8FC4) ----
        int bp = 0;
        while (true)
        {
            bp = (bp + 1) & 0xFFFF;                             // [8F96]
            Player.AdvanceAnim(s);                              // [8F9B] 638B
            AdvanceObjAnim(s, Dino);                            // [8FA1] 9047
            byte[] img = Compose();                             // [8FA4 9071] + [8FA7 26FA]
            yield return img;                                   // [8FAA 44FB] present
            bool onscreen = CavemanOnScreen();
            s.Ww(Player.PX, s.Rw(Player.PX) + 5);               // [8FAD] caveman X += 5
            s.Ww(Dino, s.Rw(Dino) + ((bp & 3) != 0 ? 5 : 4));   // [8FB4-8FBB] dino += 5, or 4 every 4th frame
            if (!onscreen) break;                               // [8FBF/8FC4]
        }

        // ---- PHASE 2 setup (8FC6..9000) ----
        s.Ww(Dino + 9, 0xFFFF);                                 // [8FC8] flip byte -> H-flipped objects
        s.Ww(Dino + 0xE, 0);                                    // [8FCE] yvel
        s.Ww(Player.PFacing, 0xFFFF);                           // [8FD4] caveman faces left
        for (int w = 0; w < 0x3F; w++)                          // [8FDA] rep movsw 4F2E -> 4F40 (overlapping, forward: replicates the record)
        {
            int src = Dino + w * 2, dst = Dino + Stride + w * 2;
            s.Wb(dst, s.Rb(src)); s.Wb(dst + 1, s.Rb(src + 1));
        }
        int ax = s.Rw(Player.PX), dx = s.Rw(Player.PY);        // [8FEB/8FEE]
        for (int i = 0; i < 3; i++)                             // [8FF2..9000] stagger the first three objects
        {
            ax = (ax + 0x1E) & 0xFFFF;
            dx = (dx - 3) & 0xFFFF;
            int o = Dino + i * Stride;
            s.Ww(o, ax); s.Ww(o + 2, dx);
        }

        // ---- PHASE 2 loop (9002..9044) ----
        while (S16(s.Rw(Player.PX)) >= 0)                       // [9044 jns]
        {
            Player.AdvanceAnim(s);                              // [9002] 638B
            for (int i = 0; i < 3; i++)                         // [900F..9034]
            {
                int o = Dino + i * Stride;
                s.Ww(o, s.Rw(o) - 5);                           // [900F]
                AdvanceObjAnim(s, o);                           // [9012]
                if (s.Rw(o + 2) > 0xA9) s.Ww(o + 0xE, -S16(s.Rw(o + 0xE)));   // [9015-901C] bounce: Y > 0xA9 (unsigned) -> NEG velocity
                s.Ww(o + 0xE, s.Rw(o + 0xE) + 8);               // [901F] gravity
                s.Ww(o + 2, s.Rw(o + 2) + (S16(s.Rw(o + 0xE)) >> 4));   // [9023-902E] Y += vel >> 4 (SAR)
            }
            byte[] img = Compose();                             // [9036 9071] + [9039 26FA]
            yield return img;                                   // [903C 44FB]
            s.Ww(Player.PX, s.Rw(Player.PX) - 7);               // [903F] caveman X -= 7
        }
    }
}
