using Pre2Port.Native;

namespace Pre2Port.Game.Frontend;

/// <summary>
/// Port-only (FULLSCREEN=WIDESCREEN, presentation only): the two front-end screens whose backdrop is a flat colour or a repeating pattern,
/// widened by RULES (no image data, no reference PNG needed).
/// <list type="bullet">
/// <item><b>OLDIES</b> ("YEAAA ...", credits over black): every margin pixel is the frame's background index (the corner pixel; the screen is
/// built from blank planes plus text, so that index is the whole backdrop). Text stays in the centred 320 frame.</item>
/// <item><b>Mode-select / password map</b>: the backdrop (MOTIF.SQZ, planes 0 and 1, the cavemen) is a 320-px-wide torus: the CRTC scroll
/// wraps it horizontally every frame and the measured seam (column 319 | column 0) is as continuous as any interior pair of columns. So the
/// tile is the frame's own 320 px, repeated with period 320 and the same alignment as the frame: the margin pixel at distance d to the left of
/// the frame is background column (319 - d) mod 320, to the right it is column d mod 320, same row. The tile is the TEXT-FREE background
/// (planes 0|1 only; the text lives in planes 2|3) so "MODE / BEGINNER" is never repeated in wide margins. Same palette indices 0..3, so the
/// exit fade applies unchanged.</item>
/// </list>
/// </summary>
public static class WideMapArt
{
    /// <summary>Centres <paramref name="src"/> in <paramref name="dst"/> (palette included) and fills both margins with its corner pixel (index of the flat backdrop).</summary>
    public static void Flat(Framebuffer src, Framebuffer dst)
    {
        int w = dst.Width, sw = src.Width, m = (w - sw) / 2;
        Array.Copy(src.Palette, dst.Palette, dst.Palette.Length);
        Array.Fill(dst.Pixels, src.Pixels[0]);
        for (int y = 0; y < dst.Height; y++) Array.Copy(src.Pixels, y * sw, dst.Pixels, y * w + m, sw);
    }

    /// <summary>Centres the 320 frame <paramref name="src"/> in <paramref name="dst"/> and fills the margins from the text-free 320x200 backdrop
    /// <paramref name="bg"/> repeated with period 320 (see the class summary).</summary>
    public static void Pattern(Framebuffer src, byte[] bg, Framebuffer dst)
    {
        int w = dst.Width, sw = src.Width, m = (w - sw) / 2;
        Array.Copy(src.Palette, dst.Palette, dst.Palette.Length);
        for (int y = 0; y < dst.Height; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int ox = x - m;
                dst.Pixels[y * w + x] = ox >= 0 && ox < sw ? src.Pixels[y * sw + ox] : bg[y * sw + ((ox % sw) + sw) % sw];
            }
        }
    }
}
