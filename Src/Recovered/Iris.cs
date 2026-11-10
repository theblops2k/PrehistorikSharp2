using Pre2Port.Native;

namespace Pre2Port.Recovered;

/// <summary>
/// [asm 316F setup + 31F4..32DD shrink loop] The level-end circular IRIS close: a circle centred on the player
/// (screen space, over the frozen level frame) shrinks to black before the tally cutscene. Port of
/// <c>pre2/recovered/transition.py</c> (<c>build_scaled_columns</c> + <c>draw_scale_frame</c> + <c>clear_span</c>,
/// VERIFIED) driven like <c>pre2/native/runtime.py::native_iris_close</c>: one shrink step per logic tick.
/// The 316F object-slot clear is intentionally NOT done here; it belongs to the tally setup
/// (<see cref="TallyScene.Setup"/>) so the frozen objects stay visible behind the iris.
/// </summary>
public sealed class Iris
{
    public const int ScreenW = 0x140, ScreenH = 0xC8, RowStride = 0x28, Columns = 0x41;

    const int PlayerX = 0x4F1C, PlayerY = 0x4F1E, CamCol = 0x2DE4, CamRow = 0x2DE6, FineScroll = 0x6BC4;
    const int CosTable = 0x7090, SinTable = 0x6F90;      // signed bytes, amplitude +-64 (cos / sin)
    const int StepOff = 0x2DC0, AccelOff = 0x2DC2, ClampOff = 0x2DC4, CenterYOff = 0x2DC6, CenterXOff = 0x2DC8, RadiusOff = 0x2DD0;
    const int TableX = 0x6B14, TableY = 0x6A88;          // words filled each frame; the ASM reads the stale tail at index = count

    readonly int[] _tx = new int[Columns + 8], _ty = new int[Columns + 8];

    public int CenterX { get; private set; }
    public int CenterY { get; private set; }
    public int Clamp { get; private set; }
    public int Radius { get; private set; }
    public int Step { get; private set; }
    /// <summary>The DAC bytes 6772 left on the last composed frame (the original's DAC is persistent); null while no fade runs.</summary>
    public byte[]? Dac { get; private set; }
    int _accel;

    public bool Closed => Radius <= 0;

    static int S16(int v) => (short)(v & 0xFFFF);

    /// <summary>[asm 316F 31AC-31EE] Centre on the player's screen position, radius 0xE6, step 4, clamp max(2*Y, 0xF0).</summary>
    public static Iris Setup(LevelState s)
    {
        int x = (s.Rw(PlayerX) - (s.Rw(CamCol) << 4)) & 0xFFFF;                                    // [31AC-31B9]
        int y = ((((s.Rw(PlayerY) - (s.Rw(CamRow) << 4)) & 0xFFFF) - 0x10 - s.Rb(FineScroll))) & 0xFFFF;   // [31BC-31D1]
        int clamp = (2 * S16(y)) & 0xFFFF;                                                          // [31D4-31DF]
        if (S16(clamp) < 0xF0) clamp = 0xF0;
        var iris = new Iris { CenterX = x, CenterY = y, Clamp = clamp, Radius = 0xE6, Step = 4 };  // [31E2-31EE]
        for (int i = 0; i < iris._tx.Length; i++)
        {
            iris._tx[i] = s.Rw(TableX + i * 2);
            iris._ty[i] = s.Rw(TableY + i * 2);
        }
        iris.WriteState(s);
        return iris;
    }

    /// <summary>
    /// Compose one iris frame over <paramref name="planes"/> (base gameplay frame already drawn): clear everything outside
    /// the current circle, repaint the player on top (as the reference does), then advance the shrink counters.
    /// </summary>
    public void Compose(LevelState s, byte[][] planes, int page)
    {
        int sc = Radius & 0xFF, clamp = S16(Clamp), running = 0x7D0, count = 0;
        for (int i = 0; i < Columns; i++)                                        // [31F4-3249] scaled columns
        {
            int sx = Scaled(s.Rb(CosTable + i), sc, CenterY);                    // cos -> ROW about centre Y ([2DC6] = x_off)
            if (sx >= running) continue;                                         // strict decreasing row envelope
            if (sx >= clamp) continue;                                           // clamped to x_clamp
            running = sx;
            _tx[count] = sx;
            _ty[count] = Scaled(s.Rb(SinTable + i), sc, CenterX);                // sin -> COLUMN about centre X ([2DC8] = y_off)
            count++;
        }
        Blit(planes, count, page);                                               // [324B-32AE] clear outside the circle
        for (int i = 0; i < count; i++)
        {
            s.Ww(TableX + i * 2, _tx[i] & 0xFFFF);
            s.Ww(TableY + i * 2, _ty[i] & 0xFFFF);
        }
        SpriteRender.DrawPlayer(s, planes, page);                                // keep the player above the black-out
        var dac = LightFade.Step(s);                                             // [32CE] CALL 6772: one fade step per iris frame
        if (dac != null) Dac = dac;                                              // the DAC the original leaves on screen
        Advance(s);                                                              // [32B0-32DD] accelerate / shrink
    }

    static int Scaled(int src, int sc, int off) =>
        S16(((S16((src * sc) & 0xFFFF) >> 6) + off) & 0xFFFF);                   // [asm 320C-3239]

    void Blit(byte[][] planes, int count, int page)                              // [asm 324B-32AE]
    {
        int curY = CenterX, si = 0, bp = count, curX = Clamp & 0xFFFF;           // curY = screen COLUMN, curX = screen ROW (x_off/y_off are swapped)
        while (true)
        {
            if (_tx[si] == curX)
            {
                bp--;
                if (bp < 0) break;
                curY = _ty[si];
                si++;
            }
            int wRight = (ScreenW - curY) & 0xFFFF;
            int mrow = (2 * CenterY - curX) & 0xFFFF;
            int wLeft = (2 * CenterX - curY) & 0xFFFF;
            ClearSpan(planes, curY, wRight, curX, page);
            ClearSpan(planes, curY, wRight, mrow, page);
            ClearSpan(planes, 0, wLeft, mrow, page);
            ClearSpan(planes, 0, wLeft, curX, page);
            curX = (curX - 1) & 0xFFFF;
            if (curX == 0) break;
        }
    }

    /// <summary>[asm 32DE] Clear pixels [x, x+width) at screen row, all four EGA planes, partial-byte edge masks.</summary>
    static void ClearSpan(byte[][] planes, int x, int width, int row, int page)
    {
        if (x >= ScreenW || width > ScreenW || row >= ScreenH) return;
        int di = (row * RowStride + page + (x >> 3)) & 0xFFFF, xSub = x & 7, cl;
        if (xSub != 0 || width >= 8)
        {
            int keep = (~(0xFF >> xSub)) & 0xFF;
            for (int p = 0; p < 4; p++) planes[p][di] &= (byte)keep;
            di = (di + 1) & 0xFFFF;
            int total = (width + xSub) & 0xFFFF, full = total >> 3;
            for (int k = 0; k < (full > 0 ? full - 1 : 0); k++)
            {
                for (int p = 0; p < 4; p++) planes[p][di] = 0;
                di = (di + 1) & 0xFFFF;
            }
            cl = total & 7;
        }
        else cl = width & 7;
        if (cl != 0)
        {
            int rightKeep = (0xFF >> cl) & 0xFF;
            for (int p = 0; p < 4; p++) planes[p][di] &= (byte)rightKeep;
        }
    }

    void Advance(LevelState s)                                                   // [asm 32B0-32DD]
    {
        _accel = (_accel + 1) & 0xFFFF;
        if (_accel >= 0x14) { _accel = 0; Step = (Step + 1) & 0xFFFF; }
        Radius = (Radius - Step) & 0xFFFF;
        if (S16(Radius) <= 0) Radius = 0;                                        // loop condition uses s16
        WriteState(s);
    }

    void WriteState(LevelState s)
    {
        s.Ww(StepOff, Step); s.Ww(AccelOff, _accel); s.Ww(ClampOff, Clamp);
        s.Ww(CenterYOff, CenterY); s.Ww(CenterXOff, CenterX); s.Ww(RadiusOff, Radius);
    }
}
