using Pre2Port.Native;

namespace Pre2Port.Game;

/// <summary>
/// Indexed frame buffer (320x200, or wider for the widescreen level view) (one byte per pixel, 0..255) plus a 256-entry RGB palette.
/// Every scene draws into this; <see cref="Presenter"/> converts it with <see cref="ToRgba"/> and shows it.
/// </summary>
public sealed class Framebuffer
{
    /// <summary>Standard width (320). Only the level scene in WIDESCREEN mode uses a wider buffer.</summary>
    public const int BaseWidth = Screen.Width;
    /// <summary>Standard height (200). Only the CREATORS photo (25F6, VGA mode 12h) uses a taller buffer (640x480).</summary>
    public const int BaseHeight = Screen.Height;

    public Framebuffer(int width = BaseWidth, int height = BaseHeight)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height];
    }

    /// <summary>Width of this buffer in pixels (default 320).</summary>
    public int Width { get; }

    /// <summary>Height of this buffer in pixels (default 200).</summary>
    public int Height { get; }

    /// <summary>Row-major pixels, <c>y * Width + x</c>, palette indices.</summary>
    public byte[] Pixels { get; }

    /// <summary>256-entry palette, 8-bit components. Starts as the VGA power-on palette (EGA16 + grayscale ramp).</summary>
    public (byte R, byte G, byte B)[] Palette { get; } = Vga.DefaultPalette();

    public void Clear(byte index) => Array.Fill(Pixels, index);

    public void SetPixel(int x, int y, byte index)
    {
        if ((uint)x < Width && (uint)y < Height)
            Pixels[y * Width + x] = index;
    }

    /// <summary>Fill a rectangle with <paramref name="index"/>, clipped to the screen.</summary>
    public void FillRect(int x, int y, int w, int h, byte index)
    {
        int x0 = Math.Max(x, 0), y0 = Math.Max(y, 0);
        int x1 = Math.Min(x + w, Width), y1 = Math.Min(y + h, Height);
        if (x1 <= x0 || y1 <= y0)
            return;
        for (int yy = y0; yy < y1; yy++)
            Array.Fill(Pixels, index, yy * Width + x0, x1 - x0);
    }

    /// <summary>
    /// Copy a <paramref name="srcW"/> x <paramref name="srcH"/> indexed bitmap to (x, y), clipped. When
    /// <paramref name="transparent"/> is set, source pixels with that index are skipped.
    /// </summary>
    public void Blit(byte[] src, int srcW, int srcH, int x, int y, int? transparent = null)
    {
        if (src.Length < srcW * srcH)
            throw new ArgumentException("source smaller than srcW*srcH", nameof(src));
        for (int sy = 0; sy < srcH; sy++)
        {
            int dy = y + sy;
            if ((uint)dy >= Height)
                continue;
            for (int sx = 0; sx < srcW; sx++)
            {
                int dx = x + sx;
                if ((uint)dx >= Width)
                    continue;
                byte p = src[sy * srcW + sx];
                if (transparent.HasValue && p == transparent.Value)
                    continue;
                Pixels[dy * Width + dx] = p;
            }
        }
    }

    /// <summary>Blit a full-screen indexed image (Width x Height of this buffer) at (x, y).</summary>
    public void Blit(byte[] src, int x, int y) => Blit(src, Width, Height, x, y);

    /// <summary>Set palette entries from 8-bit RGB triples starting at <paramref name="start"/>.</summary>
    public void SetPalette((byte R, byte G, byte B)[] colors, int start = 0)
    {
        for (int i = 0; i < colors.Length && start + i < 256; i++)
            Palette[start + i] = colors[i];
    }

    /// <summary>
    /// Copy the whole DAC of a <see cref="NativeVga"/>. Its <c>VgaPalette</c> is already 8-bit (TrackVgaDacPorts
    /// applies <see cref="Vga.Dac8"/> when the DAC is written), so no further expansion is done here.
    /// </summary>
    public void LoadFromVga(NativeVga vga) => SetPalette(vga.VgaPalette, 0);

    /// <summary>
    /// Load raw 6-bit DAC bytes (R,G,B per colour, as stored in the game data) expanding with
    /// <see cref="Vga.Dac8"/>, i.e. (v&lt;&lt;2)|(v&gt;&gt;4).
    /// </summary>
    public void SetPalette6(byte[] data, int offset, int start, int count)
    {
        for (int i = 0; i < count && start + i < 256; i++)
        {
            int p = offset + i * 3;
            Palette[start + i] = ((byte)Vga.Dac8(data[p]), (byte)Vga.Dac8(data[p + 1]), (byte)Vga.Dac8(data[p + 2]));
        }
    }

    /// <summary>Convert to RGBA8888 (alpha 255) into <paramref name="dest"/> (length Width*Height*4).</summary>
    public void ToRgba(byte[] dest)
    {
        if (dest.Length < Width * Height * 4)
            throw new ArgumentException("dest too small", nameof(dest));
        for (int i = 0; i < Pixels.Length; i++)
        {
            var c = Palette[Pixels[i]];
            int o = i * 4;
            dest[o] = c.R;
            dest[o + 1] = c.G;
            dest[o + 2] = c.B;
            dest[o + 3] = 255;
        }
    }

    public byte[] ToRgba()
    {
        var rgba = new byte[Width * Height * 4];
        ToRgba(rgba);
        return rgba;
    }
}
