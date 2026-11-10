namespace Pre2Port.Recovered;

/// <summary>
/// Port of <c>pre2/recovered/menu_scene.py MenuScenePage</c>: the mode-select / password map's evolving 4-plane EGA page.
/// Only the seed (asm 96EC/9718) is ported in this stretch.
/// TODO(map): StampText (draw_string against the bit-shifted font, planes 2|3) and ScrollShift (scroll_shift_frame,
/// the 4-plane A000->A000 pan) belong to the map stretch; add them here.
/// </summary>
public sealed class MenuScenePage
{
    public const int PlaneLen = 0x10000;     // mirrors the VM EGA plane
    public const int BgPlane = 0x1F40;       // bytes per bg plane in the initial fill [asm 9718: rep movsw cx=0xFA0]

    public byte[][] Planes { get; private set; } = NewPlanes();
    public bool Seeded { get; private set; }

    static byte[][] NewPlanes() => new[] { new byte[PlaneLen], new byte[PlaneLen], new byte[PlaneLen], new byte[PlaneLen] };

    /// <summary>The 9718 initial fill: planes 0,1 = the bg pattern (MOTIF.SQZ[:0x3E80]); planes 2,3 black.</summary>
    public void Seed(byte[] asset)
    {
        Planes = NewPlanes();
        Array.Copy(asset, 0, Planes[0], 0, BgPlane);
        Array.Copy(asset, BgPlane, Planes[1], 0, BgPlane);
        Seeded = true;
    }

    /// <summary>
    /// Deplanarize the first 320x200 of four EGA planes (row stride 40 bytes, plane p = bit p, MSB = leftmost pixel)
    /// into 64000 indexed pixels.
    /// </summary>
    public static byte[] Deplanarize(IReadOnlyList<byte[]> planes, int baseOffset = 0)
    {
        var o = new byte[320 * 200];
        for (int y = 0; y < 200; y++)
            for (int xb = 0; xb < 40; xb++)
            {
                int off = baseOffset + y * 40 + xb;
                int b0 = planes[0][off], b1 = planes[1][off], b2 = planes[2][off], b3 = planes[3][off];
                int d = y * 320 + xb * 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    int sh = 7 - bit;
                    o[d + bit] = (byte)(((b0 >> sh) & 1) | (((b1 >> sh) & 1) << 1) | (((b2 >> sh) & 1) << 2) | (((b3 >> sh) & 1) << 3));
                }
            }
        return o;
    }
}
