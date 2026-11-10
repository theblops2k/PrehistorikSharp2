using Pre2Port.Game;
using Pre2Port.Native;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Recovered;

/// <summary>
/// Gameplay frame, renderer-owned layers only (scrolling tile background + parallax + terrain). Port of
/// <c>pre2/recovered/render_frame.py render_frame(rebuild=True)</c> without the moving-sprite pass (26FA), HUD and effects, over
/// the leaves of <c>frame_renderer.py</c> (build_background_ring, redraw_animated_grid, scroll_copy) and <c>renderer.py</c>
/// (blit_sprite 3B88: opaque / restore-background / masked). The planes start CLEAN: only the asset region (tile cache 0x5E80 +
/// parallax base 0x7E80, up to 0x10000) is taken from the state, exactly like RendererState.asset_planes.
/// </summary>
public static class LevelRender
{
    const int Ds = BootImage.DsBase;
    const int RowStride = 0x28, WrapAt = 0x5D40, WrapSpan = 0x1E00, CacheBase = 0x5E80;
    const int VisibleCols = 0x14, VisibleRows = 0x0C, BgPtrBias = 0x7E80;
    const int GridBgRowAdvance = 0x258, GridDiRowAdvance = 0x280, GridSiRowAdvance = 0xEC;
    const int ScrollWrapSrc = 0x3F40, ScrollHeight = 0xB0;
    public const int ViewportRows = 0xB0;                    // 320x176 gameplay viewport (HUD band below is not drawn yet)
    public const int PlaneLen = 0x10000;

    /// <summary>Compose one frame into four clean EGA planes targeted at the displayed page [0x2DD6]; returns the planes and the page.</summary>
    public static byte[][] RenderPlanes(LevelState s, out int page, bool player = true, bool allRecords = true, int bgFill = -1)
    {
        page = s.Rw(Off.DisplayPage);                          // native_render renders the CORE frame to the display page
        var planes = new byte[4][];
        for (int p = 0; p < 4; p++)
        {
            planes[p] = new byte[PlaneLen];
            Array.Copy(s.Data, Plane(p, CacheBase), planes[p], CacheBase, PlaneLen - CacheBase);   // [render_frame step 0]
            if (bgFill >= 0) Array.Fill(planes[p], (byte)bgFill, BgPtrBias, PlaneLen - BgPtrBias);   // presentation only: probe the backdrop
        }
        int mapBase = (s.Rw(Off.MapSeg) << 4) & AddrMask;
        int camX = s.Rw(Off.CamCol), camY = s.Rw(Off.CamRow);
        int colRing = s.Rb(Off.ColRing), rowRing = s.Rb(Off.RowRing);
        int fine = s.Rb(Off.FineScroll), scrollSrc = s.Rw(Off.ScrollSrc), rowFactor = s.Rb(Off.RowFactor);

        BuildBackgroundRing(s, planes, mapBase, camX, camY, scrollSrc, colRing, fine);
        RedrawAnimatedGrid(s, planes, mapBase, camX & 0xFF, camY & 0xFF, colRing, scrollSrc);
        BossGlyph.Draw(s, planes);                             // [asm 6C0D] the mode-9 boss image goes into the ring before the scroll copy (level 9 only)
        ScrollCopy(planes, scrollSrc, page, colRing, fine, rowRing, rowFactor);
        if (player)
        {
            if (allRecords) SpriteRender.DrawAll(s, planes, page); else SpriteRender.DrawPlayer(s, planes, page);     // [render_frame step 5] 26FA: every active record, objects then the player (empty slot = nothing)
            ForegroundTiles.Apply(s, planes, page, !allRecords);               // [asm 3721] right after 26FA: the flag-0x40 tiles redrawn OVER the sprites (before the fireflies 54AB and the HUD)
            CaveTrigger.DrawFireflies(s, planes, page, camX * 16, camY * 16);   // [asm 54AB] firefly/marker pixels, over everything (above 3721)
        }
        return planes;
    }

    /// <summary>Render the frame and deplanarize the 176 viewport rows into <paramref name="fb"/> (rows below stay 0), with the level palette.</summary>
    public static void Draw(LevelState s, Framebuffer fb, Pre2Port.Game.Level.HudAssets? hud = null, LevelState? prev = null, double alpha = 1.0, bool allRecords = true)
    {
        DrawCore(s, fb, hud, prev, alpha, allRecords);
        if (hud != null) ApplyHudTransparency(s, fb, allRecords);
    }

    static void DrawCore(LevelState s, Framebuffer fb, Pre2Port.Game.Level.HudAssets? hud, LevelState? prev, double alpha, bool allRecords)
    {
        int page;
        var planes = prev != null && alpha < 1.0 && TryInterpolate(prev, s, alpha, out var ip)
            ? RenderInterpolated(s, prev, ip, alpha, out page, allRecords)
            : RenderPlanes(s, out page, true, allRecords);
        fb.Clear(0);
        int rows = ViewportRows;
        if (hud != null)                                       // [render_frame step 6] after the terrain: chrome, then the values
        {
            Hud.DrawStatusBar(planes, page, hud.Bar);
            Hud.DrawHud(planes, Hud.ReadForDraw(s), hud.Font, page);
            rows = Screen.Height;                              // rows 176-199 are now drawn
        }
        Present(s, planes, page, fb, rows);
    }

    /// <summary>Deplanarize <paramref name="rows"/> rows of a composed page into <paramref name="fb"/> and set the game palette.</summary>
    public static void Present(LevelState s, byte[][] planes, int page, Framebuffer fb, int rows = ViewportRows)
    {
        fb.Clear(0);
        for (int y = 0; y < rows; y++)
        {
            int rb = (page + y * Screen.RowBytes) & 0xFFFF;
            for (int c = 0; c < Screen.RowBytes; c++)
            {
                int o = (rb + c) & 0xFFFF;
                int b0 = planes[0][o], b1 = planes[1][o], b2 = planes[2][o], b3 = planes[3][o];
                int px = y * Screen.Width + c * 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    int sh = 7 - bit;
                    fb.Pixels[px + bit] = (byte)(((b0 >> sh) & 1) | (((b1 >> sh) & 1) << 1) | (((b2 >> sh) & 1) << 2) | (((b3 >> sh) & 1) << 3));
                }
            }
        }
        SetGamePalette(s, fb);
    }

    // ---- FULLSCREEN=WIDESCREEN (level only; presentation only, logic and camera untouched) ----------------------------------------------

    /// <summary>Widescreen granularity: 16 px = one tile column (CamCol is in 16 px units). All extra width is on the right, in whole columns.</summary>
    public const int WideMargin = 16;
    /// <summary>Upper bound of extra columns (canvas up to 320 + 60*16 px; far beyond 32:9).</summary>
    public const int MaxExtraCols = 60;
    internal const int MapCols = 256;                        // the tile map has 256 columns per row (si = row*0x100 + col)

    /// <summary>Screen proportion (w/h) the widescreen canvas follows; set by the presenter each frame (fullscreen: the screen, windowed: the monitor). Default 16:9.</summary>
    public static double TargetAspect { get; set; } = 16.0 / 9.0;

    /// <summary>Extra 16 px columns for a screen proportion: floor((200 * aspect - 320) / 16), 0..MaxExtraCols.</summary>
    public static int ExtraColsFor(double aspect) =>
        Math.Clamp((int)Math.Floor((Screen.Height * aspect - Screen.Width) / WideMargin + 1e-9), 0, MaxExtraCols);

    /// <summary>Extra columns now in use (0 when FULLSCREEN=NO/ORIGINAL). The camera's right limit is MapCols - VisibleCols - ExtraCols.</summary>
    public static int ExtraCols => Pre2Port.Game.Settings.Current.WidescreenActive ? ExtraColsFor(TargetAspect) : 0;

    /// <summary>Widescreen level canvas width (320 + 16 * ExtraCols; 352 at 16:9).</summary>
    public static int WideWidth => Screen.Width + WideMargin * ExtraCols;

    /// <summary>Highest CamCol so that every visible canvas column (VisibleCols + extras) is inside the map.</summary>
    internal static int CamLimit(int extraCols) => MapCols - VisibleCols - extraCols;

    static Framebuffer? _wideC, _wideR;              // 320-wide scratch frames reused every call

    static (int Cam, int Ring, int Src) CamSave(LevelState st) => (st.Rw(Off.CamCol), st.Rw(Off.ColRing), st.Rw(Off.ScrollSrc));

    /// <summary>Move only the render-side camera cells to <paramref name="cam"/> (as LevelCamera.SyncRenderState derives them, without stepping the animation).</summary>
    static void CamSet(LevelState st, int cam)
    {
        int ring = cam % VisibleCols;
        st.Ww(Off.CamCol, cam);
        st.Ww(Off.ColRing, ring);
        st.Ww(Off.ScrollSrc, LevelCamera.CalcScrollSource(ring, st.Rw(Off.RowRing) & 0xFF));
    }

    static void CamRestore(LevelState st, (int Cam, int Ring, int Src) v)
    {
        st.Ww(Off.CamCol, v.Cam); st.Ww(Off.ColRing, v.Ring); st.Ww(Off.ScrollSrc, v.Src);
    }

    static (int Row, int Ring, int Src) CamSaveV(LevelState st) => (st.Rw(Off.CamRow), st.Rw(Off.RowRing), st.Rw(Off.ScrollSrc));

    static void CamSetV(LevelState st, int row)
    {
        int ring = row % VisibleRows;
        st.Ww(Off.CamRow, row);
        st.Ww(Off.RowRing, ring);
        st.Ww(Off.ScrollSrc, LevelCamera.CalcScrollSource(st.Rw(Off.ColRing), ring));
    }

    static void CamRestoreV(LevelState st, (int Row, int Ring, int Src) v)
    {
        st.Ww(Off.CamRow, v.Row); st.Ww(Off.RowRing, v.Ring); st.Ww(Off.ScrollSrc, v.Src);
    }

    static bool CanShiftDown(LevelState s, int d) => s.Rw(Off.CamRow) + d <= (s.Rb(Off.MapRows) - 0xB);

    static void DrawShiftedDown(LevelState s, int d, Framebuffer dst, bool allRecords)
    {
        var sv = CamSaveV(s);
        try
        {
            CamSetV(s, sv.Row + d);
            DrawCore(s, dst, null, null, 1.0, allRecords);
        }
        finally { CamRestoreV(s, sv); }
    }

    static Framebuffer? _hudWB1, _hudWB2;

    static void DrawShiftedDownWide(LevelState s, int d, Framebuffer dst, bool allRecords)
    {
        var sv = CamSaveV(s);
        try
        {
            CamSetV(s, sv.Row + d);
            DrawWide(s, dst, null, null, 1.0, allRecords);
        }
        finally { CamRestoreV(s, sv); }
    }

    static Framebuffer? _hudB1, _hudB2;

    static void ApplyHudTransparency(LevelState s, Framebuffer fb, bool allRecords)
    {
        int t = EnhancedCfg.Current.HudTransparent;
        if (t <= 0) return;
        t = Math.Clamp(t, 0, 100);
        int keep = 100 - t;
        var pal = ((byte R, byte G, byte B)[])fb.Palette.Clone();
        _hudB1 ??= new Framebuffer();
        _hudB2 ??= new Framebuffer();
        bool ok1 = CanShiftDown(s, 1);
        bool ok2 = CanShiftDown(s, 2);
        if (ok1) DrawShiftedDown(s, 1, _hudB1, allRecords);
        if (ok2) DrawShiftedDown(s, 2, _hudB2, allRecords);
        var map = new Dictionary<int, int>();
        int next = 16;
        int W = fb.Width;
        for (int y = ViewportRows; y < Screen.Height; y++)
        {
            int off = y - ViewportRows;
            int sy1 = 160 + off, sy2 = 144 + off;
            for (int x = 0; x < W; x++)
            {
                int sx = Math.Min(x, Screen.Width - 1);
                int hi = fb.Pixels[y * W + x] & 0xFF;
                int bi;
                if (off < 16) bi = ok1 ? _hudB1!.Pixels[sy1 * Screen.Width + sx] : fb.Pixels[(ViewportRows - 1) * W + x];
                else bi = ok2 ? _hudB2!.Pixels[sy2 * Screen.Width + sx] : (ok1 ? _hudB1!.Pixels[175 * Screen.Width + sx] : fb.Pixels[(ViewportRows - 1) * W + x]);
                int key = (hi & 0x0F) * 16 + (bi & 0x0F);
                if (!map.TryGetValue(key, out int idx))
                {
                    var hc = pal[hi & 0x0F]; var bc = pal[bi & 0x0F];
                    var blend = ((byte)((hc.R * keep + bc.R * t + 50) / 100), (byte)((hc.G * keep + bc.G * t + 50) / 100), (byte)((hc.B * keep + bc.B * t + 50) / 100));
                    if (next < 256)
                    {
                        fb.Palette[next] = blend;
                        idx = next++;
                    }
                    else
                    {
                        int best = 0; int bd = int.MaxValue;
                        for (int i = 0; i < next; i++)
                        {
                            var c = fb.Palette[i];
                            int dr = blend.Item1 - c.R, dg = blend.Item2 - c.G, db = blend.Item3 - c.B;
                            int d2 = dr * dr + dg * dg + db * db;
                            if (d2 < bd) { bd = d2; best = i; }
                        }
                        idx = best;
                    }
                    map[key] = idx;
                }
                fb.Pixels[y * W + x] = (byte)idx;
            }
        }
    }

    static void ApplyHudTransparencyWide(LevelState s, Framebuffer fb, bool allRecords)
    {
        int t = EnhancedCfg.Current.HudTransparent;
        if (t <= 0) return;
        t = Math.Clamp(t, 0, 100);
        int keep = 100 - t;
        var pal = ((byte R, byte G, byte B)[])fb.Palette.Clone();
        int W = fb.Width;
        if (_hudWB1 is null || _hudWB1.Width != W) _hudWB1 = new Framebuffer(W, Screen.Height);
        if (_hudWB2 is null || _hudWB2.Width != W) _hudWB2 = new Framebuffer(W, Screen.Height);
        bool ok1 = CanShiftDown(s, 1);
        bool ok2 = CanShiftDown(s, 2);
        if (ok1) DrawShiftedDownWide(s, 1, _hudWB1, allRecords);
        if (ok2) DrawShiftedDownWide(s, 2, _hudWB2, allRecords);
        var map = new Dictionary<int, int>();
        int next = 16;
        for (int y = ViewportRows; y < Screen.Height; y++)
        {
            int off = y - ViewportRows;
            int sy1 = 160 + off, sy2 = 144 + off;
            for (int x = 0; x < W; x++)
            {
                int hi = fb.Pixels[y * W + x] & 0xFF;
                int bi;
                if (off < 16) bi = ok1 ? _hudWB1!.Pixels[sy1 * W + x] : fb.Pixels[(ViewportRows - 1) * W + x];
                else bi = ok2 ? _hudWB2!.Pixels[sy2 * W + x] : (ok1 ? _hudWB1!.Pixels[175 * W + x] : fb.Pixels[(ViewportRows - 1) * W + x]);
                int key = (hi & 0x0F) * 16 + (bi & 0x0F);
                if (!map.TryGetValue(key, out int idx))
                {
                    var hc = pal[hi & 0x0F]; var bc = pal[bi & 0x0F];
                    var blend = ((byte)((hc.R * keep + bc.R * t + 50) / 100), (byte)((hc.G * keep + bc.G * t + 50) / 100), (byte)((hc.B * keep + bc.B * t + 50) / 100));
                    if (next < 256)
                    {
                        fb.Palette[next] = blend;
                        idx = next++;
                    }
                    else
                    {
                        int best = 0; int bd = int.MaxValue;
                        for (int i = 0; i < next; i++)
                        {
                            var c = fb.Palette[i];
                            int dr = blend.Item1 - c.R, dg = blend.Item2 - c.G, db = blend.Item3 - c.B;
                            int d2 = dr * dr + dg * dg + db * db;
                            if (d2 < bd) { bd = d2; best = i; }
                        }
                        idx = best;
                    }
                    map[key] = idx;
                }
                fb.Pixels[y * W + x] = (byte)idx;
            }
        }
    }

    /// <summary>Draw the camera shifted by <paramref name="d"/> tile columns (state and previous state both, restored afterwards) into a 320 frame (no HUD).</summary>
    static void DrawShifted(LevelState s, LevelState? prev, int d, Framebuffer dst, double alpha, bool allRecords)
    {
        var sv = CamSave(s);
        var pv = prev != null ? CamSave(prev) : default;
        try
        {
            CamSet(s, sv.Cam + d);
            if (prev != null) CamSet(prev, pv.Cam + d);
            DrawCore(s, dst, null, prev, alpha, allRecords);
        }
        finally
        {
            CamRestore(s, sv);
            if (prev != null) CamRestore(prev, pv);
        }
    }

    /// <summary>
    /// Widescreen level frame (<paramref name="fb"/>.Width == <see cref="WideWidth"/>). The planar engine is 320 wide, so the 16 px side strips
    /// come from two extra passes with the camera one tile column left / right (cols 0..15 of the left pass, cols 304..319 of the right one);
    /// the centre is the normal frame. HUD rows (176+) come from the centre pass only; their side strips repeat the bar's edge column.
    /// A side pass whose 20 visible map columns would leave the 256-column map (or go below 0) is replaced by colour 0 (outside the map); HUD rows always repeat the bar's edge column.
    /// </summary>
    public static void DrawWide(LevelState s, Framebuffer fb, Pre2Port.Game.Level.HudAssets? hud, LevelState? prev, double alpha, bool allRecords = true)
    {
        int W = fb.Width, B = Screen.Width, k = (W - B) / WideMargin;   // k extra columns on the right (canvas x = cam + x)
        _wideC ??= new Framebuffer(); _wideR ??= new Framebuffer();
        DrawCore(s, _wideC, hud, prev, alpha, allRecords);       // exactly the normal frame (palette included)
        int cam = s.Rw(Off.CamCol);
        int pcam = prev != null ? prev.Rw(Off.CamCol) : cam;
        int lim = MapCols - VisibleCols - Math.Max(cam, pcam);   // largest legal shift of a side pass
        byte[] c = _wideC.Pixels, r = _wideR.Pixels, o = fb.Pixels;
        for (int y = 0; y < Screen.Height; y++)
        {
            Buffer.BlockCopy(c, y * B, o, y * W, B);
            if (y >= ViewportRows) Array.Fill(o, c[y * B + B - 1], y * W + B, W - B);   // HUD rows: repeat the edge column
        }
        // Pass j: camera shifted sh = done + n columns; its last n columns are world cam+20+done.. = the next n extra columns.
        int done = 0;
        while (done < k)
        {
            int n = Math.Min(k - done, VisibleCols);
            int sh = Math.Min(done + n, lim);
            int cnt = sh - done;                             // valid columns of this pass (< n only near the map end)
            if (cnt <= 0) break;
            DrawShifted(s, prev, sh, _wideR, alpha, allRecords);
            for (int y = 0; y < ViewportRows; y++)
                Buffer.BlockCopy(r, y * B + B - cnt * 16, o, y * W + B + done * 16, cnt * 16);
            done += cnt;
            if (cnt < n) break;
        }
        int tail = (k - done) * 16;                          // beyond the map: repeat the last valid column
        if (tail > 0)
            for (int y = 0; y < ViewportRows; y++)
                Array.Fill(o, o[y * W + B + done * 16 - 1], y * W + B + done * 16, tail);
        SetGamePalette(s, fb);
        if (hud != null) ApplyHudTransparencyWide(s, fb, allRecords);
    }

    // ---- presentation interpolation (display only: never writes the logic state, alpha >= 1 == the exact tick image) ----------

    /// <summary>Presentation pose in pixels: player record X/Y (signed) and camera scroll offset (CamCol*16, CamRow*16 + fine).</summary>
    public readonly record struct Pose(int PX, int PY, int CamX, int CamY)
    {
        public static Pose Of(LevelState s) => new((short)s.Rw(Off.PlayerX), (short)s.Rw(Off.PlayerY),
            s.Rw(Off.CamCol) * 16, s.Rw(Off.CamRow) * 16 + s.Rb(Off.FineScroll));
    }

    /// <summary>a + (b - a) * alpha rounded half-up to an integer pixel.</summary>
    public static int Lerp(int a, int b, double alpha) => a + (int)Math.Floor((b - a) * alpha + 0.5);

    /// <summary>Interpolated pose between the previous and current tick; false (= draw the plain tick) on teleports / camera snaps.</summary>
    public static bool TryInterpolate(LevelState prev, LevelState cur, double alpha, out Pose pose)
    {
        var a = Pose.Of(prev); var b = Pose.Of(cur);
        pose = b;
        if (Math.Abs(b.CamX - a.CamX) >= Screen.Width || Math.Abs(b.CamY - a.CamY) >= ViewportRows
            || Math.Abs(b.PX - a.PX) >= 128 || Math.Abs(b.PY - a.PY) >= 128) return false;
        pose = new Pose(Lerp(a.PX, b.PX, alpha), Lerp(a.PY, b.PY, alpha), Lerp(a.CamX, b.CamX, alpha), Lerp(a.CamY, b.CamY, alpha));
        return true;
    }

    static byte[] Deplanarize(byte[][] planes, int page)
    {
        var img = new byte[Screen.Width * ViewportRows];
        for (int y = 0; y < ViewportRows; y++)
            for (int c = 0; c < Screen.RowBytes; c++)
            {
                int o = (page + y * Screen.RowBytes + c) & 0xFFFF;
                int b0 = planes[0][o], b1 = planes[1][o], b2 = planes[2][o], b3 = planes[3][o];
                for (int bit = 0; bit < 8; bit++)
                {
                    int sh = 7 - bit;
                    img[y * Screen.Width + c * 8 + bit] = (byte)(((b0 >> sh) & 1) | (((b1 >> sh) & 1) << 1) | (((b2 >> sh) & 1) << 2) | (((b3 >> sh) & 1) << 3));
                }
            }
        return img;
    }

    static void Planarize(byte[] img, byte[][] planes, int page)
    {
        for (int y = 0; y < ViewportRows; y++)
            for (int c = 0; c < Screen.RowBytes; c++)
            {
                int o = (page + y * Screen.RowBytes + c) & 0xFFFF;
                for (int p = 0; p < 4; p++)
                {
                    int v = 0;
                    for (int bit = 0; bit < 8; bit++)
                        v |= ((img[y * Screen.Width + c * 8 + bit] >> p) & 1) << (7 - bit);
                    planes[p][o] = (byte)v;
                }
            }
    }

    /// <summary>
    /// The level backdrop (plane offset 0x7E80, 40 bytes/row) is fixed to the SCREEN, not to the world: tile-layer pixels T move with the
    /// camera, backdrop-visible bits V (restore-background / masked tiles) show the screen-fixed backdrop B. Two renders (backdrop
    /// all 0 / all 1) split a frame into T = R0 and V = R0 ^ R1; the frame is exactly T | (B &amp; V).
    /// </summary>
    static (byte[] T, byte[] V) Layers(LevelState s)
    {
        var t = Deplanarize(RenderPlanes(s, out int p0, player: false, bgFill: 0), p0);
        var r1 = Deplanarize(RenderPlanes(s, out int p1, player: false, bgFill: 0xFF), p1);
        var v = new byte[t.Length];
        for (int i = 0; i < v.Length; i++) v[i] = (byte)(t[i] ^ r1[i]);
        return (t, v);
    }

    static byte[] Backdrop(byte[][] planes)
    {
        var b = new byte[Screen.Width * ViewportRows];
        for (int y = 0; y < ViewportRows; y++)
            for (int x = 0; x < Screen.Width; x++)
            {
                int o = BgPtrBias + y * Screen.RowBytes + (x >> 3), sh = 7 - (x & 7), v = 0;
                for (int p = 0; p < 4; p++) v |= ((planes[p][o] >> sh) & 1) << p;
                b[y * Screen.Width + x] = (byte)v;
            }
        return b;
    }

    /// <summary>Debug/verification: T | (B &amp; V) must reproduce the plain current-tick background image exactly.</summary>
    public static bool LayersReproduceFrame(LevelState s)
    {
        var planes = RenderPlanes(s, out int page, player: false);
        var want = Deplanarize(planes, page);
        var (t, v) = Layers(s); var b = Backdrop(planes);
        for (int i = 0; i < want.Length; i++) if ((t[i] | (b[i] & v[i])) != want[i]) return false;
        return true;
    }

    /// <summary>
    /// Background at the interpolated scroll offset (pixel-exact, stitched from the current and previous tick images; the tile map itself
    /// is not interpolated) + the player sprite at the interpolated position. The state is only touched transiently (player X/Y restored).
    /// </summary>
    static byte[][] RenderInterpolated(LevelState s, LevelState prev, Pose ip, double alpha, out int page, bool allRecords = true)
    {
        var planes = RenderPlanes(s, out page, player: false);
        var cp = Pose.Of(s); var pp = Pose.Of(prev);
        if (ip.CamX != cp.CamX || ip.CamY != cp.CamY)
        {
            var (tc, vc) = Layers(s); var (tp, vp) = Layers(prev);
            var bd = Backdrop(planes);
            var img = new byte[tc.Length];
            int W = Screen.Width, H = ViewportRows;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int wx = ip.CamX + x, wy = ip.CamY + y;
                    int cx = wx - cp.CamX, cy = wy - cp.CamY;
                    int ox = wx - pp.CamX, oy = wy - pp.CamY, si;
                    if ((uint)cx < (uint)W && (uint)cy < (uint)H) si = cy * W + cx;
                    else if ((uint)ox < (uint)W && (uint)oy < (uint)H) { si = oy * W + ox; img[y * W + x] = (byte)(tp[si] | (bd[y * W + x] & vp[si])); continue; }
                    else si = Math.Clamp(cy, 0, H - 1) * W + Math.Clamp(cx, 0, W - 1);   // diagonal-corner hole: nearest current pixel
                    img[y * W + x] = (byte)(tc[si] | (bd[y * W + x] & vc[si]));
                }
            Planarize(img, planes, page);
        }
        int ox0 = s.Rw(Player.PX), oy0 = s.Rw(Player.PY);
        s.Ww(Player.PX, ip.PX - (ip.CamX - cp.CamX));          // Plan: screenX = X - xOff - CamCol*16  ->  ipx - xOff - interpolated cam
        s.Ww(Player.PY, ip.PY - (ip.CamY - cp.CamY));
        var drawn = new List<(int X, int Y)>();                 // world positions of the records drawn (the foreground pass 3721 scans around them)
        if (allRecords) SpriteRender.DrawObjects(s, prev, alpha, planes, page, ip.CamX - cp.CamX, ip.CamY - cp.CamY, drawn);   // every moving record: position interpolated, frame = current
        int cx0 = s.Rw(SpriteRender.ListBase), cy0 = s.Rw(SpriteRender.ListBase + 2);
        try
        {
            if (SpriteRender.DrawPlayer(s, planes, page)) drawn.Add((ip.PX, ip.PY));                  // world position of the interpolated player
            // the club record 0x4F0A (player render slot 0, below the player in the 26FA list order) moves with the interpolated player
            s.Ww(SpriteRender.ListBase, cx0 + (s.Rw(Player.PX) - ox0)); s.Ww(SpriteRender.ListBase + 2, cy0 + (s.Rw(Player.PY) - oy0));
            if (SpriteRender.DrawClub(s, planes, page)) drawn.Add((cx0 + (ip.PX - ox0), cy0 + (ip.PY - oy0)));
        }
        finally { s.Ww(Player.PX, ox0); s.Ww(Player.PY, oy0); s.Ww(SpriteRender.ListBase, cx0); s.Ww(SpriteRender.ListBase + 2, cy0); }
        ForegroundTiles.ApplyPixels(s, planes, page, drawn, ip.CamX, ip.CamY);          // [asm 3721] over the interpolated sprites, at the interpolated camera
        CaveTrigger.DrawFireflies(s, planes, page, s.Rw(Off.CamCol) * 16, s.Rw(Off.CamRow) * 16);   // [asm 54AB] above 3721, camera in whole tiles
        return planes;
    }

    /// <summary>[asm 0ba0] 16 six-bit DAC triples at DGROUP [[0x2D00 + level*2]] -> DAC 0..15 (the lights-on resting palette of native_apply_palette_fade).</summary>
    public static void SetLevelPalette(LevelState s, Framebuffer fb)
    {
        int ptr = s.Rw(Off.PalettePtrTable + s.Rb(Off.Level) * 2);
        fb.SetPalette6(s.Data, Ds + ptr, 0, 16);
    }

    /// <summary>In-level palette: the level palette, or the 6772 light-fade ramp / resting dark palette (<see cref="LightFade.Dac"/>) while the lamp pickups changed it.</summary>
    public static void SetGamePalette(LevelState s, Framebuffer fb)
    {
        var dac = LightFade.Dac(s);
        if (dac == null) SetLevelPalette(s, fb);
        else fb.SetPalette6(dac, 0, 0, 16);
    }

    // ---- 3B88 blit (renderer.py) ---------------------------------------------------------------------------------------

    static void BlitOpaque(byte[][] pl, int idx, int di)
    {
        int src = CacheBase + idx * 0x20;
        int d = di & 0xFFFF;
        for (int r = 0; r < 16; r++)
        {
            if (d >= WrapAt) d = (d - WrapSpan) & 0xFFFF;
            for (int p = 0; p < 4; p++)
            {
                pl[p][d] = pl[p][src + r * 2];
                pl[p][d + 1] = pl[p][src + r * 2 + 1];
            }
            d = (d + RowStride) & 0xFFFF;
        }
    }

    static void RestoreBackground(byte[][] pl, int di, int bgOff)
    {
        int d = di & 0xFFFF;
        for (int r = 0; r < 16; r++)
        {
            if (d >= WrapAt) d = (d - WrapSpan) & 0xFFFF;
            int sr = (bgOff + r * RowStride) & 0xFFFF;
            for (int p = 0; p < 4; p++)
            {
                pl[p][d] = pl[p][sr];
                pl[p][d + 1] = pl[p][sr + 1];
            }
            d = (d + RowStride) & 0xFFFF;
        }
    }

    static void BlitMasked(LevelState s, byte[][] pl, int idx, int di, int bgOff, int maskAddr)
    {
        RestoreBackground(pl, di, bgOff);
        int src = CacheBase + idx * 0x20;
        int d = di & 0xFFFF;
        for (int r = 0; r < 16; r++)
        {
            if (d >= WrapAt) d = (d - WrapSpan) & 0xFFFF;
            for (int p = 0; p < 4; p++)
                for (int c = 0; c < 2; c++)
                {
                    int k = r * 2 + c;
                    pl[p][d + c] = (byte)((s.Data[maskAddr + k] & pl[p][d + c]) | pl[p][src + k]);
                }
            d = (d + RowStride) & 0xFFFF;
        }
    }

    /// <summary>[asm 3B88] dispatch on the tile's class [0x4DF8+idx]: 0 opaque, 1 restore background, &gt;=2 masked with mask (typ-2) of the [0x2DF8] region.</summary>
    static void BlitSprite(LevelState s, byte[][] pl, int idx, int di, int typ, int bgOff)
    {
        if (typ == 0) BlitOpaque(pl, idx, di);
        else if (typ == 1) RestoreBackground(pl, di, bgOff);
        else BlitMasked(s, pl, idx, di, bgOff, Ds + Off.MaskRegion + (typ - 2) * 0x20);
    }

    // ---- frame_renderer.py ---------------------------------------------------------------------------------------------

    /// <summary>Full ring rebuild: every visible tile (12x20) drawn into the scroll ring from state + assets.</summary>
    static void BuildBackgroundRing(LevelState s, byte[][] pl, int mapBase, int camX, int camY, int scrollSrc, int colRing, int fine)
    {
        int si = (camY * 0x100 + camX) & 0xFFFF;
        int di = scrollSrc & 0xFFFF;
        int bgPtr = BgPtrBias;
        for (int row = 0; row < VisibleRows; row++)
        {
            int dx = colRing;
            for (int col = 0; col < VisibleCols; col++)
            {
                int tile = s.Data[mapBase + si];
                int typ = s.Data[Ds + Off.BlitType + tile];
                if (di >= WrapAt) di = (di - WrapSpan) & 0xFFFF;   // ring wrap for ALL tiles
                int bgOff = (bgPtr - RowStride * fine) & 0xFFFF;
                BlitSprite(s, pl, tile, di, typ, bgOff);
                dx++;
                if (dx >= VisibleCols) { di = (di - RowStride) & 0xFFFF; dx = 0; }
                bgPtr = (bgPtr + 2) & 0xFFFF;
                di = (di + 2) & 0xFFFF;
                si = (si + 1) & 0xFFFF;
            }
            bgPtr = (bgPtr + GridBgRowAdvance) & 0xFFFF;
            di = (di + GridDiRowAdvance) & 0xFFFF;
            si = (si + GridSiRowAdvance) & 0xFFFF;
        }
    }

    /// <summary>[asm 3668] redraw the animated tiles ([0x6988] != 0) remapped through the current frame table [[0x6BC2]+tile]; only type-0 tiles exist.</summary>
    static void RedrawAnimatedGrid(LevelState s, byte[][] pl, int mapBase, int camCol, int camRow, int fineCol, int scrollDest)
    {
        int animPtr = s.Rw(Off.AnimRemapPtr);
        int si = (camRow * 0x100 + camCol) & 0xFFFF;
        int di = scrollDest & 0xFFFF;
        for (int row = 0; row < VisibleRows; row++)
        {
            int dx = fineCol;
            for (int col = 0; col < VisibleCols; col++)
            {
                int tile = s.Data[mapBase + si];
                if (s.Data[Ds + Off.AnimTable3 + tile] != 0)
                {
                    int remapped = s.Data[(Ds + animPtr + tile) & AddrMask];
                    if (s.Data[Ds + Off.BlitType + remapped] != 0)
                        throw new InvalidOperationException($"animated tile {tile} -> {remapped} is not type 0 (unrecovered)");
                    if (di >= WrapAt) di = (di - WrapSpan) & 0xFFFF;
                    BlitOpaque(pl, remapped, di);
                }
                dx++;
                if (dx >= VisibleCols) { di = (di - RowStride) & 0xFFFF; dx = 0; }
                di = (di + 2) & 0xFFFF;
                si = (si + 1) & 0xFFFF;
            }
            di = (di + GridDiRowAdvance) & 0xFFFF;
            si = (si + GridSiRowAdvance) & 0xFFFF;
        }
    }

    static void CopyRun(byte[][] pl, ref int si, ref int di, int n)
    {
        for (int i = 0; i < n; i++)
        {
            for (int p = 0; p < 4; p++) pl[p][di] = pl[p][si];
            si = (si + 1) & 0xFFFF;
            di = (di + 1) & 0xFFFF;
        }
    }

    /// <summary>[asm 3A27] scroll copy: the visible window of the ring buffer -> the display page (write-mode-1 latched 4-plane copy).</summary>
    static void ScrollCopy(byte[][] pl, int scrollSrc, int dest, int colRing, int fine, int rowRing, int rowFactor)
    {
        int strip = (RowStride * rowFactor) & 0xFFFF;
        int si = scrollSrc & 0xFFFF;
        int di = (dest + strip) & 0xFFFF;
        int dh = (colRing << 1) & 0xFF;
        int dl = ((0x14 - colRing) << 1) & 0xFF;

        int bp = (0xC0 - fine - (rowRing << 4)) & 0xFFFF;     // [3A3A-3A68] row split main / ring-wrap
        int bx;
        if (bp >= ScrollHeight) { bp = (ScrollHeight - rowFactor) & 0xFFFF; bx = 0; }
        else
        {
            bx = (ScrollHeight - bp - rowFactor) & 0xFFFF;
            if ((bx & 0x8000) != 0) { bp = (bp + bx) & 0xFFFF; bx = 0; }
        }
        si = (si + RowStride * fine) & 0xFFFF;                // [3A6A]
        if (bp == 0) { int t = bp; bp = bx; bx = t; }         // [3A7E-3A82] xchg bp,bx

        for (int i = 0; i < bp; i++)
        {
            CopyRun(pl, ref si, ref di, dl);
            si = (si - RowStride) & 0xFFFF;
            CopyRun(pl, ref si, ref di, dh);
            si = (si + RowStride) & 0xFFFF;
        }
        if (bx != 0)
        {
            si = (ScrollWrapSrc + dh) & 0xFFFF;
            for (int i = 0; i < bx; i++)
            {
                CopyRun(pl, ref si, ref di, dl);
                si = (si - RowStride) & 0xFFFF;
                CopyRun(pl, ref si, ref di, dh);
                si = (si + RowStride) & 0xFFFF;
            }
        }
        int words = strip >> 1;                               // [3AB2-3ACB] clear the leading strip on all planes
        for (int k = 0; k < words * 2; k++)
        {
            int off = (dest + k) & 0xFFFF;
            for (int p = 0; p < 4; p++) pl[p][off] = 0;
        }
    }
}
