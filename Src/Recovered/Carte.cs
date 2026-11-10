namespace Pre2Port.Recovered;

/// <summary>Port of <c>pre2/recovered/carte.py</c> + <c>present.scroll_blit_column</c>: the CARTE (world map) scroll-in page.</summary>
public static class Carte
{
    public const int PageLen = 0x2000;       // circular page
    public const int ScrollStart = 8;        // [asm 95E2] mov [0xB19D],8
    public const int ScrollEnd = 639;        // front_end.py _native_carte: the scroll stops at / the carte auto-advances at 639
    public const int MasterLen = 0xFA00;     // 4 planes x 0x3E80
    public const int HoldTicks = 0x46;       // [asm 96AF-96BF] mov cx,0x46 / call 990D (1 retrace) + DC1 / cmp [27E8],0 / loop: 70 retraces held on the last column before the fade

    /// <summary>
    /// The DAC (0x300 bytes, 6-bit) 9286 starts from at the end of the carte. 9286 walks <c>CS:[9153]</c> = 0xA0 entries (the menu's 919F count, see
    /// <see cref="Pre2Port.Game.Frontend.MenuAssets.FadeEntries"/>), not just the 16 the carte uses, and runs until a pass starts with all of them at 0, so
    /// the number of passes depends on the largest component of entries 0..0x9F. Entries 0..15 = the carte palette (95F0: INT 10h 1012 from [0xB0E8]).
    /// Entries 16..63 = the 64-colour EGA palette that the mode-0Dh set (447D, INT 10h AX=0Dh) loads; 64..0x9F = 0 (faded out by the previous 9286, never
    /// reloaded). ASSUMPTION (BIOS/DOSBox behaviour, the game was not run): only its maximum matters here (63 -> 17 passes; all zero would give 15).
    /// </summary>
    public static byte[] FadeStartDac(byte[] pal6)
    {
        var dac = new byte[0x300];
        for (int i = 16; i < 64; i++)
        {
            dac[i * 3] = (byte)((((i >> 2) & 1) * 2 + ((i >> 5) & 1)) * 21);
            dac[i * 3 + 1] = (byte)((((i >> 1) & 1) * 2 + ((i >> 4) & 1)) * 21);
            dac[i * 3 + 2] = (byte)(((i & 1) * 2 + ((i >> 3) & 1)) * 21);
        }
        Array.Copy(pal6, dac, Math.Min(pal6.Length, 48));
        return dac;
    }

    /// <summary>Four black 0x2000 planes (the carte enters on a cleared page).</summary>
    public static byte[][] NewPage() => new[] { new byte[PageLen], new byte[PageLen], new byte[PageLen], new byte[PageLen] };

    /// <summary>[asm 965A] blit the newly exposed column of the master into the page; no-op unless k is a multiple of 8.</summary>
    public static void ScrollBlitColumn(byte[][] planes, byte[] source, int k)
    {
        if ((k & 7) != 0) return;
        int col = ((k >> 3) - 1) & 0xFFFF;
        int si = col, di = col;
        for (int plane = 0; plane < 4; plane++)
        {
            for (int r = 0; r < 200; r++)
            {
                di &= WorldMap.PageWrap;
                planes[plane][di] = source[si];
                si = (si + 0x50) & 0xFFFF;
                di = (di + 0x28) & 0xFFFF;
            }
            di = (di - 0x1F40) & 0xFFFF;
        }
    }

    /// <summary>[asm 954D-9560] map byte offset of the per-level marker.</summary>
    public static int MarkerOffset(int xWord, int yWord) => ((((yWord - 0x20) & 0xFF) * 0x50) + (xWord >> 3)) & 0xFFFF;

    /// <summary>[asm 9543-95CD] stamp the 'you are here' marker (mask + 4 colour planes, w*h bytes each) onto a copy of the master.</summary>
    public static byte[] StampMarker(byte[] asset, byte[] marker, int di, int w, int h)
    {
        int[] planeBase = { 0x0000, 0x3E80, 0x7D00, 0xBB80 };
        int wh = w * h;
        var buf = new byte[0x10000];
        Array.Copy(asset, buf, Math.Min(asset.Length, buf.Length));
        int lin = 0, d = di & 0xFFFF;
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                int inv = ~marker[lin] & 0xFF;
                for (int p = 0; p < 4; p++)
                {
                    int off = (d + planeBase[p]) & 0xFFFF;
                    buf[off] = (byte)((buf[off] & inv) | marker[(p + 1) * wh + lin]);
                }
                lin++;
                d = (d + 1) & 0xFFFF;
            }
            d = (d + (0x50 - w)) & 0xFFFF;
        }
        return buf.AsSpan(0, asset.Length).ToArray();
    }
}
