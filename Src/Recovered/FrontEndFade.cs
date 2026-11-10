namespace Pre2Port.Recovered;

/// <summary>
/// Port of <c>pre2/recovered/front_end_fade.py</c>: DAC fade-in (919F), fade-out (9286) and the PRESENT palette
/// morph (911D). Each returns one 0x300-byte 6-bit palette snapshot per retrace (= per 70 Hz tick).
/// </summary>
public static class FrontEndFade
{
    const int StepIn = 2, StepOut = 4, InClamp = 0x3D, MorphStride = 0x52, MorphStep = 2;

    public static List<byte[]> FadeIn(byte[] target, int nEntries)
    {
        var dac = new byte[0x300];
        var frames = new List<byte[]>();
        while (true)
        {
            int changed = 0;
            for (int idx = 0; idx < nEntries; idx++)
            {
                if ((idx & 0x1F) == 0)
                    frames.Add((byte[])dac.Clone());                  // [asm 91E1] retrace
                int b = idx * 3;
                for (int c = 0; c < 3; c++)
                {
                    int cur = dac[b + c];
                    if (target[b + c] > cur && cur <= InClamp)
                    {
                        dac[b + c] = (byte)(cur + StepIn);
                        changed++;
                    }
                }
            }
            if (changed == 0) break;
        }
        return frames;
    }

    public static List<byte[]> FadeOut(byte[] start, int nEntries)
    {
        var dac = new byte[0x300];
        Array.Copy(start, dac, Math.Min(start.Length, 0x300));
        var frames = new List<byte[]>();
        while (true)
        {
            int changed = 0;
            for (int idx = 0; idx < nEntries; idx++)
            {
                if ((idx & 0x1F) == 0)
                    frames.Add((byte[])dac.Clone());                  // [asm 92AB] retrace
                int b = idx * 3;
                for (int c = 0; c < 3; c++)
                {
                    int cur = dac[b + c];
                    dac[b + c] = (byte)(cur > StepOut ? cur - StepOut : 0);
                    changed |= cur;
                }
            }
            if (changed == 0) break;
        }
        return frames;
    }

    /// <summary>[asm 911D] morph toward <paramref name="target"/>: retrace every 0x52 component writes, continuous across passes; display commits per DAC entry.</summary>
    public static List<byte[]> PaletteMorph(byte[] start, byte[] target, int initialPhase = 0)
    {
        var saved = new byte[0x300];
        Array.Copy(start, saved, 0x300);
        var displayed = (byte[])saved.Clone();
        var frames = new List<byte[]>();
        int bl = initialPhase & 0xFF;
        while (true)
        {
            int changed = 0, latch = 0;
            for (int i = 0; i < 0x300; i++)
            {
                int cur = saved[i];
                int diff = (target[i] - cur) & 0xFF;
                int adiff = diff < 0x80 ? diff : 256 - diff;
                if (diff == 0) { }
                else if (adiff < MorphStep) { changed++; saved[i] = target[i]; }
                else saved[i] = (byte)(target[i] > cur ? cur + MorphStep : cur - MorphStep);
                latch++;
                if (latch == 3)
                {
                    Array.Copy(saved, i - 2, displayed, i - 2, 3);
                    latch = 0;
                }
                bl++;
                if (bl == MorphStride)
                {
                    frames.Add((byte[])displayed.Clone());
                    bl = 0;
                }
            }
            if (changed == 0) break;
        }
        return frames;
    }
}
