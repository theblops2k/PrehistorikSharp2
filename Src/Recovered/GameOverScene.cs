using Pre2Port.Game;
using Pre2Port.Game.Level;
using Pre2Port.Native;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Recovered;

/// <summary>
/// The animated GAME OVER scene, port of <c>pre2/native/gameover_scene.py</c> (native_gameover_setup / native_gameover_tick) and
/// <c>pre2/views/gameover_scene.py</c> (diorama + object pass + HUD), checked against the Ghidra listing of 1030:9B23..9C86 (setup 9B35-9C5C,
/// loop 9C62: tick 9CC0, background 9C87, object pass 29FA/26FA, present 44FB, input DC1, exit on [0x27F0] >= 0x276 or fire [0x27E8], then 9586 fade).
/// Setup resets the camera, frees every render slot, spawns the 8 GAME/OVER letters, the 4 tableau characters and the 3 circling birds; each
/// iteration scrolls the diorama down to 0xB9, cycles the crying caveman, bounces the letters and orbits the birds. Not ported: the final 9586
/// palette fade-out (the scene just ends), the Ctrl+Alt+E creators photo (25C7).
/// </summary>
public static class GameOverScene
{
    const int PlayerSlot = 0x4F1C, RenderBase = 0x4F0A, LetterTable = 0xB018, BirdBase = 0x5138, BirdPairSlots = 0x4FD0;
    const int CryCaveman = 0x5088, Palette = 0xAFE8, Stride = 0x12, Sin = 0x7090, Cos = 0x6F90, ScrollCap = 0xB9;
    const int Plane = 0x1F40, Row = 0x28;

    static int S8(int v) => (sbyte)(v & 0xFF);
    static int S16(int v) => (short)(v & 0xFFFF);

    /// <summary>[asm 9B35-9C5C] build the scene state. Run once when the scene starts.</summary>
    public static void Setup(LevelState s)
    {
        s.Ww(Off.CamCol, 0); s.Ww(Off.CamRow, 0); s.Ww(Off.RowFactor, 0);   // [9B35-9B3B]
        s.Wb(Off.FineScroll, 0);                                            // [9B3E]
        for (int i = 0; i < 0x74; i++) s.Ww(RenderBase + i * Stride + 4, 0xFFFF);   // [9B8C-9B9B] free every object slot
        int di = PlayerSlot, x = 0x2C;
        for (int i = 0; i < 8; i++)                                         // [9B9D-9BF1] the 8 letters
        {
            if (i == 4) x = (x + 0x30) & 0xFFFF;                            // [9BD1] the GAME|OVER gap
            s.Ww(di + 4, (s.Rb(LetterTable + i) + 0xB0) & 0xFFFF);
            s.Ww(di, x);
            s.Ww(di + 2, 0xE0);
            int r = Pickups.Roll(s) & 7;                                    // [9BBA/9BE2] 39DF & 7
            if (i < 4 && r == 0) r = 1;                                     // [9BC0-9BC2]
            s.Ww(di + 0xE, r);
            x = (x + 0x18) & 0xFFFF;
            di += Stride;
        }
        di += 0xB4;                                                         // [9BF3] -> 0x5060
        foreach (var (tx, ty, sid) in new[] { (0x96, 0x135, 0x64), (0x97, 0x108, 0x1C4), (0x96, 0x121, 0x68), (0x96, 0x119, 0x62) })   // [9BF3-9C34] tableau
        {
            s.Ww(di, tx); s.Ww(di + 2, ty); s.Ww(di + 4, sid);
            di += Stride;
        }
        var phases = new[] { (0, 0xB020), (0x55, 0xB032), (0xAA, 0xB054) };
        for (int k = 0; k < 3; k++)                                         // [9C35-9C5B] the 3 birds
        {
            s.Wb(BirdBase + k * Stride + 8, phases[k].Item1);
            s.Ww(BirdBase + k * Stride + 0xC, phases[k].Item2);
        }
        s.Ww(0x27F0, 0);                                                    // [9C5C]
    }

    /// <summary>[asm 9CC0-9DD9] one scene iteration: scroll, caveman cry cycle, letter bounce, bird orbits + sort + near-side handoff.</summary>
    public static void Tick(LevelState s)
    {
        if (s.Rb(Off.FineScroll) < ScrollCap) s.Wb(Off.FineScroll, s.Rb(Off.FineScroll) + 1);   // [9CC6-9CCD]
        if ((s.Rw(0x6BD5) & 3) == 0)                                        // [9CD1-9CD6] every 4th frame
        {
            int ax = ((s.Rw(CryCaveman) & 0x1FFF) + 1) & 0xFFFF;            // [9CD8-9CDE]
            if (ax >= 0x6E) ax = 0x68;                                      // [9CDF-9CE4]
            s.Ww(CryCaveman, ax);
        }
        int si = PlayerSlot;
        for (int i = 0; i < 8; i++)                                         // [9CEA-9D04] letters' bounce oscillator
        {
            int ax = (s.Rw(si + 0xE) + 1) & 0xFFFF;
            if (S8(ax) >= 8) ax = (-ax + 1) & 0xFFFF;                       // [9CF8-9CFA]
            s.Ww(si + 0xE, ax);
            s.Ww(si + 2, (s.Rw(si + 2) + S16(ax)) & 0xFFFF);                // [9CFE]
            si += Stride;
        }
        si = BirdBase;
        for (int k = 0; k < 3; k++)                                         // [9D06-9D95] bird orbits
        {
            s.Ww(si - 0x164, 0xFFFF);                                       // [9D0C] free the paired render slot
            int phase = s.Rb(si + 8);
            int sin = S8(s.Rb(Cos + phase));                                // [9D12-9D19]
            s.Ww(si, (0x96 + ((sin * 0x41) >> 6)) & 0xFFFF);                // [9D1A-9D3A]
            int cos = S8(s.Rb(Sin + phase));                                // [9D3C-9D43]
            int v = (cos * 0x0A) >> 6;                                      // [9D44-9D5F]
            s.Ww(si + 6, v & 0xFFFF);                                       // [9D61]
            s.Ww(si + 2, (v + 0x135) & 0xFFFF);                             // [9D64-9D67]
            s.Wb(si + 8, (phase + 2) & 0xFF);                               // [9D6A]
            int bx = s.Rw(si + 0xC);                                        // [9D6E] anim-script advance
            int w = s.Rw(bx);
            for (int guard = 0; (w & 0x8000) != 0 && guard < 64; guard++) { bx = (bx + S16(w)) & 0xFFFF; w = s.Rw(bx); }   // [9D73-9D79] rewind marker
            w = (w + 0x138) & 0xFFFF;                                       // [9D7B]
            w |= s.Rw(si + 6) & 0x8000;                                     // [9D7E-9D85] H-flip on the far side
            s.Ww(si + 4, w);                                                // [9D87]
            s.Ww(si + 0xC, (bx + 2) & 0xFFFF);                              // [9D8A-9D8C]
            si += Stride;
        }
        for (int guard = 0; guard < 64; guard++)                            // [9D98-9DB1] bubble-sort the birds DESCENDING by Y (swap + restart)
        {
            bool swapped = false;
            for (int k = 0; k < 2 && !swapped; k++)
            {
                int a = BirdBase + k * Stride, b = a + Stride;
                if (S16(s.Rw(b + 2)) >= S16(s.Rw(a + 2)))                   // [9DA4-9DA7]
                {
                    for (int i = 0; i < Stride; i++)
                    {
                        int t = s.Rb(a + i);
                        s.Wb(a + i, s.Rb(b + i)); s.Wb(b + i, t);
                    }
                    swapped = true;                                         // [9DAC]
                }
            }
            if (!swapped) break;
        }
        for (int k = 0; k < 2; k++)                                         // [9DB3-9DD1] near-side birds -> the paired render slots
        {
            int from = BirdBase + k * Stride, to = BirdPairSlots + k * Stride;
            if (S16(s.Rw(from + 2)) >= 0x135)                               // [9DBC-9DC1]
            {
                for (int i = 0; i < Stride; i++) s.Wb(to + i, s.Rb(from + i));   // [9DF0]
                s.Ww(from + 4, 0xFFFF);                                     // [9DC6]
            }
        }
    }

    /// <summary>[asm 9C87 + 29FA + HUD] diorama window at the current scroll, every active render record (letters, tableau, birds), the status bar; palette 0xAFE8.</summary>
    public static void Draw(LevelState s, byte[] asset, HudAssets hud, Framebuffer fb)
    {
        var pl = EndScreens.NewPlanes();
        int srcOff = (Row * s.Rb(Off.FineScroll)) & 0xFFFF;
        for (int p = 0; p < 4; p++)
        {
            var stg = new byte[0x3E80 + 0x180];
            Array.Copy(asset, p * Plane, stg, Plane, Plane);
            for (int i = 0; i < 0x1B80; i++) pl[p][i] = stg[(srcOff + i) & 0xFFFF];
        }
        SpriteRender.DrawAll(s, pl, 0);
        Hud.DrawStatusBar(pl, 0, hud.Bar);
        Hud.DrawHud(pl, Hud.ReadForDraw(s), hud.Font, 0);
        EndScreens.ToFramebuffer(pl, 0, fb);
        fb.SetPalette6(s.Data, BootImage.DsBase + Palette, 0, 16);
    }
}
