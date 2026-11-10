namespace Pre2Port.Game;

/// <summary>
/// Pure logic-clock maths shared by the <see cref="Presenter"/> and the headless verification (no real time): tick 0 is due at
/// <c>logicStart</c>, tick k at <c>logicStart + k/hz</c>, independent of the render rate. Alpha is always derived from elapsed time.
/// </summary>
public static class FrameClock
{
    /// <summary>Logic ticks owed at <paramref name="now"/> given <paramref name="tick"/> already done.</summary>
    public static long Due(double now, double logicStart, double hz, int tick)
        => (long)Math.Floor((now - logicStart) * hz) + 1 - tick;

    /// <summary>Fraction (0..1) of the logic period elapsed since the last done tick (1 when none is done yet).</summary>
    public static double Alpha(double now, double logicStart, double hz, int tick)
        => tick <= 0 ? 1.0 : Math.Clamp((now - logicStart) * hz - (tick - 1), 0.0, 1.0);

    /// <summary>
    /// Absolute time the next render frame is due at <paramref name="hz"/> plus the (possibly rebased) schedule start. The
    /// schedule is rebased when it is more than 0.1 s off real time (frames presented uncapped by a V-Sync-less driver, or a
    /// rate/mode change) so it never sleeps for seconds nor bursts to catch up.
    /// </summary>
    public static (double Next, double Start) Pace(double now, double start, int frame, double hz)
    {
        double next = start + frame / hz;
        if (Math.Abs(next - now) > 0.1) return (now, now - frame / hz);
        return (next, start);
    }
}
