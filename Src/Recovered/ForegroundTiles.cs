using Pre2Port.Native;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Recovered;

/// <summary>
/// Foreground-tile pass 1030:3721 (selection 3732-37F6) + 37F7 (colour-0-keyed tile blit). Main loop order (0214-0270): ... 4B8E, 26FA (all moving sprites,
/// records 0x5720 down to 0x4F0A), <b>3721</b>, 54AB ...: the tiles whose flag [0x805E + tile] has bit 0x40 are redrawn OVER the sprites 26FA just painted
/// (the caveman walks behind a bush). Selection: every record that 26FA drew this frame (26FA clears [+5] bit 0x20 at 2732 and sets it again at 28B6 when the
/// sprite survives the culls; 3721 tests exactly that bit, 3747) scans a box of cells around its (x, y); tile graphics come from the permanent FRONT.SQZ bank
/// ([0x3B], index word [0x8167 + tile*2] &lt;&lt; 7, 128 bytes plane-major: plane p row r at p*0x20 + r*2). Gates: cs:[3] bit 1 (command line, default 0x1F) and
/// [0x2DF2] bit 0x40 (OR of the tile flags of the visible grid, an optimisation only: a box never reaches a cell outside the visible columns/rows, so with the bit
/// clear no cell has the flag and the pass is a no-op; the port does not keep [0x2DF2] across the 35A1 full redraws, so it is not used).
/// </summary>
public static class ForegroundTiles
{
    const int Ds = BootImage.DsBase;
    const int ListBase = SpriteRender.ListBase, ListEnd = 0x5732, Stride = SpriteRender.RecordBytes;
    const int ViewW = 320, ViewH = 176;
    const int FlagTable = 0x805E, GraphicIndex = 0x8167, FgFlag = 0x40, WindowBytes = 0x1900, RowBytes = 0x28;

    static int S16(int v) => (short)(v & 0xFFFF);

    /// <summary>Debug A/B switch (PRE2_NOFG=1 skips the pass) and counters (tiles blitted / passes run), used by the <c>fgprobe</c> command.</summary>
    public static bool Disabled { get; set; } = Environment.GetEnvironmentVariable("PRE2_NOFG") != null;
    public static long TilesBlitted { get; set; }

    static ForegroundTiles()
    {
        if (Environment.GetEnvironmentVariable("PRE2_FGSTAT") != null)     // debug: tiles blitted by the whole process (which verifier scenarios reach the pass)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.WriteLine($"foreground tiles blitted: {TilesBlitted}");
    }

    /// <summary>[asm 3721-372F] cs:[3] bit 1 (graphics bank loaded; cleared by the /A switch, set by default in the boot image).</summary>
    public static bool Enabled(LevelState s) => !Disabled && (s.CsRb(3) & 2) != 0;

    /// <summary>
    /// [asm 3732-37F6] the (tile, cell) pairs the pass redraws for the sprites at world positions <paramref name="sprites"/> (the records 26FA drew), in ASM order
    /// (records ascending from 0x4F0A; per record rows from the sprite's bottom cell upwards, per row cols -2, -1, 0, +1). <paramref name="camCol"/> / <paramref name="camRow"/>
    /// are the camera tile cells ([0x2DE4], [0x2DE6] low bytes).
    /// </summary>
    public static IEnumerable<(int Tile, int Cell)> Select(LevelState s, IEnumerable<(int X, int Y)> sprites, int camCol, int camRow)
    {
        int mapBase = (s.Rw(Off.MapSeg) << 4) & AddrMask;
        foreach (var (x, y) in sprites)
        {
            int ax = ((S16(y) >> 4) - 1) & 0xFFFF;                           // [3751-3756] SAR AX,4; DEC AX
            ax = ((ax & 0xFF) << 8) | (ax >> 8);                             // [3757] XCHG AH,AL
            int di = ((S16(x) >> 4) + ax) & 0xFFFF;                          // [3759-375D] DI = x>>4 + swapped (full 16-bit add: no column mask)
            int ch = ((di >> 8) - camRow) & 0xFF;                            // [3761] SUB CH,[0x2DE6]
            int cl = S8((di & 0xFF) - camCol);                               // [3765] SUB CL,[0x2DE4] (compared signed)
            int dx = 3;                                                      // [3769]
            if ((y & 0xF) != 0) { di = (di + 0x100) & 0xFFFF; ch = (ch + 1) & 0xFF; dx++; }   // [376C-3778] sub-tile Y: one more row, shifted down
            for (; dx > 0; dx--, di = (di - 0x100) & 0xFFFF)                 // [37E3-37E8]
            {
                if (ch >= 0xB) continue;                                     // [3779-377C] JNC: below the viewport rows
                ch = (ch - 1) & 0xFF;                                        // [377E]
                if (cl >= 2 && Tile(s, mapBase, (di - 2) & 0xFFFF, out int t0)) yield return (t0, (di - 2) & 0xFFFF);                      // [3782-3796]
                if (cl >= 1 && Tile(s, mapBase, (di - 1) & 0xFFFF, out int t1)) yield return (t1, (di - 1) & 0xFFFF);                      // [3799-37AC]
                if (cl >= 0 && cl < 0x14 && Tile(s, mapBase, di, out int t2)) yield return (t2, di);                                        // [37AF-37C5]
                if (cl >= -1 && cl < 0x13 && Tile(s, mapBase, (di + 1) & 0xFFFF, out int t3)) yield return (t3, (di + 1) & 0xFFFF);        // [37C8-37E0]
            }
        }
    }

    static int S8(int v) => (sbyte)(v & 0xFF);

    static bool Tile(LevelState s, int mapBase, int cell, out int tile)
    {
        tile = s.Data[mapBase + cell];                                       // [3787] MOV BL,ES:[DI+k] (BH = 0)
        return (s.Data[Ds + FlagTable + tile] & FgFlag) != 0;               // [378B] TEST [BX+0x805E],0x40
    }

    /// <summary>
    /// The world positions of the records 26FA draws this frame (flag [+5] bit 0x20 after 26FA), ascending record order: the live list scan of 3732-37F3.
    /// <paramref name="playerOnly"/> = the objects-off configuration (only the player record is drawn / flagged).
    /// </summary>
    public static List<(int X, int Y)> DrawnRecords(LevelState s, int page, bool playerOnly)
    {
        var list = new List<(int X, int Y)>();
        int frame = (s.Rw(0x6BD5) + 1) & 0xFFFF;                             // 26FA's [0x6BD5] after its +1
        for (int off = ListBase; off < ListEnd; off += Stride)
        {
            if (playerOnly && off != Player.PX) continue;
            if (SpriteRender.Plan(s, off, page, frame, out _)) list.Add((s.Rw(off), s.Rw(off + 2)));
        }
        return list;
    }

    /// <summary>
    /// The whole pass for the plain tick image: select around the drawn records and blit each tile with the exact planar 37F7 over <paramref name="planes"/> at
    /// <paramref name="page"/>. Returns the number of tiles blitted (clipped ones do not count).
    /// </summary>
    public static int Apply(LevelState s, byte[][] planes, int page, bool playerOnly)
    {
        if (!Enabled(s)) return 0;
        var sprites = DrawnRecords(s, page, playerOnly);
        if (sprites.Count == 0) return 0;
        int camCol = s.Rb(Off.CamCol), camRow = s.Rb(Off.CamRow);
        int n = 0;
        foreach (var (tile, cell) in Select(s, sprites, camCol, camRow))
            if (Blit(s, planes, page, tile, cell, camCol, camRow)) n++;
        TilesBlitted += n;
        return n;
    }

    /// <summary>
    /// [asm 37F7-3921] the colour-0-keyed tile blit: phase 1 (AND ~footprint into all four planes), phase 2 (OR each plane's row word back). Destination
    /// page + (cell row - camRow) * 0x280 + (cell col - camCol) * 2 - 0x28 * [0x6BC4] (bytes taken mod 256 like the ASM); only tiles whose top falls in the
    /// first 0x1900 bytes (160 scanlines) of the page are drawn. False = clipped.
    /// </summary>
    public static bool Blit(LevelState s, byte[][] planes, int page, int tile, int cell, int camCol, int camRow)
    {
        int si = (s.Rw(GraphicIndex + tile * 2) << 7) & 0xFFFF;              // [3802-3808]
        int rowDiff = ((cell >> 8) - camRow) & 0xFF;                         // [380A] SUB AH,[0x2DE6]
        int colDiff = ((cell & 0xFF) - camCol) & 0xFF;                       // [380E] SUB AL,[0x2DE4]
        int di = ((rowDiff << 2) * 0xA0 + (colDiff << 1) + page) & 0xFFFF;   // [3812-382B] (row*4) * 0xA0 (MUL, low word) + col*2 + [0x2DD8]
        di = (di - 0x28 * s.Rb(Off.FineScroll)) & 0xFFFF;                    // [382D-3833]
        if (di < page || di >= ((page + WindowBytes) & 0xFFFF)) return false;   // [3835-3846]
        int gfx = (s.Rw(Off.FgBank) << 4) & AddrMask;
        for (int r = 0; r < 16; r++)
        {
            int rowBase = (si + r * 2) & 0xFFFF;
            Span<int> pl = stackalloc int[4];
            int mask = 0;
            for (int p = 0; p < 4; p++)
            {
                pl[p] = s.Data[gfx + ((rowBase + p * 0x20) & 0xFFFF)] | (s.Data[gfx + ((rowBase + p * 0x20 + 1) & 0xFFFF)] << 8);
                mask |= pl[p];
            }
            int keep = ~mask & 0xFFFF;
            int o = (di + r * RowBytes) & 0xFFFF, o2 = (o + 1) & 0xFFFF;
            for (int p = 0; p < 4; p++)
            {
                int cur = planes[p][o] | (planes[p][o2] << 8);
                int val = (cur & keep) | pl[p];                              // [386C-387x] AND ~footprint, then [389C-38xx] OR the plane word
                planes[p][o] = (byte)val;
                planes[p][o2] = (byte)(val >> 8);
            }
        }
        return true;
    }

    /// <summary>
    /// Presentation-only variant for the interpolated image (camera at a pixel offset <paramref name="camX"/>/<paramref name="camY"/>, sprites at interpolated
    /// positions): the selection is the ASM one (camera tile cells = pixel offset / 16), each tile is composited per pixel at its exact screen position, with the
    /// same transparency rule and the same top-of-tile window as 37F7.
    /// </summary>
    public static int ApplyPixels(LevelState s, byte[][] planes, int page, IEnumerable<(int X, int Y)> sprites, int camX, int camY)
    {
        if (!Enabled(s)) return 0;
        int camCol = (camX >> 4) & 0xFF, camRow = (camY >> 4) & 0xFF;
        int gfx = (s.Rw(Off.FgBank) << 4) & AddrMask;
        int n = 0;
        foreach (var (tile, cell) in Select(s, sprites, camCol, camRow))
        {
            int sx = (cell & 0xFF) * 16 - camX, sy = (cell >> 8) * 16 - camY;
            if (sy < 0 || sy >= WindowBytes / RowBytes) continue;            // top of the tile inside the first 160 scanlines
            int si = (s.Rw(GraphicIndex + tile * 2) << 7) & 0xFFFF;
            for (int r = 0; r < 16; r++)
            {
                int y = sy + r;
                if (y < 0 || y >= ViewH) continue;
                int rowBase = (si + r * 2) & 0xFFFF;
                for (int i = 0; i < 16; i++)
                {
                    int x = sx + i;
                    if ((uint)x >= (uint)ViewW) continue;
                    int byteIdx = i >> 3, bit = 7 - (i & 7);
                    int c = 0;
                    for (int p = 0; p < 4; p++)
                        c |= ((s.Data[gfx + ((rowBase + p * 0x20 + byteIdx) & 0xFFFF)] >> bit) & 1) << p;
                    if (c == 0) continue;                                    // colour 0 = transparent
                    int o = (page + y * RowBytes + (x >> 3)) & 0xFFFF, sh = 7 - (x & 7);
                    for (int p = 0; p < 4; p++)
                        planes[p][o] = (byte)((planes[p][o] & ~(1 << sh)) | (((c >> p) & 1) << sh));
                }
            }
            n++;
        }
        TilesBlitted += n;
        return n;
    }
}
