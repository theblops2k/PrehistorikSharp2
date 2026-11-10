using Pre2Port.Codecs;
using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>
/// Data of the world-map stretch (mode-select, password, carte), all taken from the boot image / original files:
/// shifted font (boot font segment [0x3D]), row-bounce sine (0x6F90), text strings, carte palette (0xB0E8), per-level marker
/// table (0xB148), the marker sprite (sprite 457 of the SPRITES.SQZ bank: far pointer [0x667A]:[0x62DA], size word [0x7522])
/// and the MAP.SQZ master. The sprite bank is built into a COPY of the boot image (SpriteBank.NativeBuild), like the Python flow.
/// </summary>
public sealed class MapAssets
{
    public const int FontSegOff = 0x3D, SineOff = 0x6F90, CartePalOff = 0xB0E8, MarkerTableOff = 0xB148, MarkerTableLen = 0x40;
    public const int MarkerSegOff = 0x667A, MarkerOffOff = 0x62DA, MarkerDimsOff = 0x7522, SeedOff = 0xA333;
    public const int EnterCodeOff = 0xB175, ModeOff = 0xB180, BeginnerOff = 0xB185, ExpertOff = 0xB18E;

    public byte[] Font { get; }                 // 0x8400, 8 sub-pixel shifted copies
    public byte[] Sine { get; }                 // 0x100 signed bytes
    public bool Bounce { get; }                 // cs:[5] != 0
    public int Rot { get; }                     // cs:[5] rotate count of the password generator
    public int Seed { get; }                    // [0xA333] or the 0x20 fallback
    public byte[] ModeText { get; }
    public byte[] BeginnerText { get; }
    public byte[] ExpertText { get; }
    public byte[] EnterCodeText { get; }
    public byte[] CartePal6 { get; }            // 16 entries, 6-bit
    public byte[] MarkerTable { get; }          // 0x40 bytes: per level (x word, y word)
    public int MarkerW { get; }                 // bytes wide
    public int MarkerH { get; }                 // rows
    public byte[] Marker { get; }               // mask + 4 colour planes, w*h each
    public byte[] Master { get; }               // MAP.SQZ[:0xFA00]

    MapAssets(byte[] boot, string dir)
    {
        var img = (byte[])boot.Clone();
        SpriteBank.NativeBuild(img, dir);       // places the marker sprite and the far-pointer tables
        int ds = BootImage.DsBase;
        int U16(int o) => img[ds + o] | (img[ds + o + 1] << 8);
        byte[] CStr(int o)
        {
            int e = ds + o;
            while (img[e] != 0) e++;
            return img.AsSpan(ds + o, e - (ds + o)).ToArray();
        }

        int fseg = U16(FontSegOff);
        Font = WorldMap.BuildShiftedFont(img.AsSpan(fseg << 4, 0x3000).ToArray());
        Sine = img.AsSpan(ds + SineOff, 0x100).ToArray();
        Rot = img[BootImage.CsBase + 5];
        Bounce = Rot != 0;
        int seed = U16(SeedOff);
        Seed = seed != 0 ? seed : Password.DefaultSeed;
        ModeText = CStr(ModeOff);
        BeginnerText = CStr(BeginnerOff);
        ExpertText = CStr(ExpertOff);
        EnterCodeText = CStr(EnterCodeOff);
        CartePal6 = new byte[48];
        for (int i = 0; i < 48; i++) CartePal6[i] = (byte)(img[ds + CartePalOff + i] & 0x3F);
        MarkerTable = img.AsSpan(ds + MarkerTableOff, MarkerTableLen).ToArray();
        int dims = U16(MarkerDimsOff);
        MarkerW = (dims & 0xFF) >> 3;           // [asm 9562-956A]
        MarkerH = dims >> 8;
        int src = (U16(MarkerSegOff) << 4) + U16(MarkerOffOff);
        Marker = img.AsSpan(src, 5 * MarkerW * MarkerH).ToArray();

        var map = Sqz.Unpack(File.ReadAllBytes(Path.Combine(dir, "MAP.SQZ")));
        Master = new byte[Carte.MasterLen];
        Array.Copy(map, Master, Math.Min(map.Length, Master.Length));
    }

    public static MapAssets Load(byte[] boot, string originalDir) => new(boot, originalDir);
}

/// <summary>
/// Password state that the original keeps in static DGROUP data and the screen's init (asm 9771-9792) never clears: the 16-bit
/// nibble accumulator [0xB1B9] and the three-group history [0xB1B3/B5/B7] (the DEAD C0DE F00D cheat builds in it). It therefore
/// survives leaving the screen and coming back (and going to a level and back); only a program restart zeroes it.
/// </summary>
public sealed class PasswordMemory
{
    public int Code, Hist0, Hist1, Hist2;
}

/// <summary>
/// The 0Dh scrolling world map in its two interactive forms (front_end.py _native_menu_map): MODE SELECT (kind ModeSelect,
/// callback 991F) and ENTER CODE (kind Password, callback 9985). Frame 0 is the seeded MOTIF page; every later tick moves the
/// bouncing camera, re-stamps the text on planes 2|3 and pans the page. Input is polled AFTER each frame (never after frame 0):
/// ModeSelect - an arrow (up/down/right/left, rising edge of "any arrow") flips BEGINNER/EXPERT; Space/Enter (level, not consumed)
/// commits level 0. Password - the callback 9985 mirrors the ASM order: wrong-code pause (status 1: 0x8C polls, fire and keys
/// ignored) / matched (status 2: leave) / last scancode [0x2874] is a break code: nothing (fire too) / fire: leave with Level still
/// 0xFF / else the make code is consumed, mapped through the DS:0xB068 table (1-0, A-F, plus Q = scancode 0x10 = 'A') and shifted
/// into the accumulator; a complete 4-digit group is checked against the DEAD C0DE F00D warp cheat then the per-level codes. A hit
/// commits (level, mode) and leaves on the NEXT poll (one more frame is shown, asm 9AAD status 2 -> 9AC4); a miss shifts the group
/// into the history and starts the pause. The scancode latch models make AND break codes: a key released before the poll is lost.
/// Every exit fades the 16-entry palette to black (FadeOut, planes frozen) and then sets <see cref="Result"/>
/// (LevelChosen / BackToMenu) on a tick without a frame. Escape does nothing here (neither does the Python flow).
/// </summary>
public sealed class MapScene : MenuStage
{
    static readonly (Input.Key Key, byte Ch)[] HexKeys =
    {
        (Input.Key.One, (byte)'1'), (Input.Key.Two, (byte)'2'), (Input.Key.D3, (byte)'3'), (Input.Key.D4, (byte)'4'),
        (Input.Key.D5, (byte)'5'), (Input.Key.D6, (byte)'6'), (Input.Key.D7, (byte)'7'), (Input.Key.D8, (byte)'8'),
        (Input.Key.D9, (byte)'9'), (Input.Key.D0, (byte)'0'), (Input.Key.D1, (byte)'1'), (Input.Key.D2, (byte)'2'), (Input.Key.A, (byte)'A'), (Input.Key.B, (byte)'B'),
        (Input.Key.C, (byte)'C'), (Input.Key.D, (byte)'D'), (Input.Key.E, (byte)'E'), (Input.Key.F, (byte)'F'),
        (Input.Key.Q, (byte)'A'),                                      // DS:[0xB068 + 0x10] = 'A' (and + 0x1E = 'A')
    };
    static readonly Input.Key[] AllKeys = Enum.GetValues<Input.Key>();
    const int ScNone = 0, ScMake = 1, ScBreak = 2;                      // what [0x2874] holds: 0 / a make code / a break code (bit 7)
    static readonly byte[] EmptyEcho = { 0x5B, 0x5B, 0x5B, 0x5B };      // "[[[[" [asm 9ACB]

    readonly MenuAssets _a;
    readonly MapAssets _m;
    readonly MenuScenePage _page = new();
    MapCamera _cam = MapCamera.Start;
    int _prevPage, _ds, _pel;
    bool _started, _prevArrow;
    byte[] _pal;
    int _aw = 320;
    // password state (PasswordScreenView)
    readonly byte[] _echo = (byte[])EmptyEcho.Clone();
    readonly PasswordMemory _mem;
    readonly bool[] _held = new bool[Input.KeyCount];
    int _cursor, _wrong, _status, _pending, _sc;
    bool _leave;                                                        // [0xB1B2] != 0: the driver loop (asm 9877) leaves after this poll
    // exit fade
    List<byte[]>? _fade;
    int _fadeIdx;
    MapResult _exit;

    /// <param name="pw">The persistent password accumulator/history (null = a fresh one, as after a program start).</param>
    public MapScene(MenuAssets a, MapKind kind, int mode = 0, PasswordMemory? pw = null)
    {
        _a = a;
        _m = a.Map;
        _mem = pw ?? new PasswordMemory();
        Kind = kind;
        Mode = mode & 1;
        _page.Seed(a.Motif);
        _pal = a.MapPal6;
        Level = kind == MapKind.Password ? (byte)0xFF : (byte)0;   // [asm 8ee9] no level chosen yet for the password map
    }

    public MapKind Kind { get; }
    public MapResult Result { get; private set; } = MapResult.Running;
    public byte Level { get; private set; }
    /// <summary>[0xB197]: 0 = BEGINNER, 1 = EXPERT. Persists across screens (the caller passes it back in).</summary>
    public int Mode { get; private set; }
    public override string Name => Kind == MapKind.Password ? "MAP_PASSWORD" : "MAP_MODE_SELECT";

    public override void Update(Input input)
    {
        HasFrame = false;
        if (Result != MapResult.Running) return;
        if (_fade != null)
        {
            if (_fadeIdx < _fade.Count) { _pal = _fade[_fadeIdx++]; HasFrame = true; }
            else Result = _exit;                                   // the generator returns: no frame, the flow switches this tick
            return;
        }
        if (!_started)
        {
            _started = true;                                       // frame 0 = the seeded page, no input poll after it
            HasFrame = true;
            LatchKeys(input);
            return;
        }
        BuildFrame();
        HasFrame = true;
        LatchKeys(input);
        Poll(input);
    }

    void BuildFrame()
    {
        _cam = _cam.Update(_m.Bounce, _m.Sine);
        var (ds, pel) = WorldMap.MapPan(_cam.X, _cam.Row);
        int pageDraw = ds, pageClear = _prevPage;
        Array.Clear(_page.Planes[2], 0, 0x2000);
        Array.Clear(_page.Planes[3], 0, 0x2000);
        if (Kind == MapKind.Password)
        {
            // draw_string(..., page_clear, page_draw): the Python call passes the pages in this (swapped) order.
            WorldMap.DrawString(_page.Planes, _m.EnterCodeText, _m.Font, _cam.BlitOff, 0x0AF2, 3, pageClear, pageDraw);
            WorldMap.DrawString(_page.Planes, _echo, _m.Font, _cam.BlitOff, 0x12C9, 4, pageClear, pageDraw);
        }
        else
        {
            WorldMap.DrawString(_page.Planes, _m.ModeText, _m.Font, _cam.BlitOff, 0x0C38, 4, pageClear, pageDraw);
            WorldMap.DrawString(_page.Planes, Mode != 0 ? _m.ExpertText : _m.BeginnerText, _m.Font, _cam.BlitOff, 0x1185, 3,
                                pageClear, pageDraw);
        }
        WorldMap.ScrollShiftFrame(_page.Planes, _cam.PrevX, _cam.X, _cam.Row, _cam.PrevRow, pageClear);
        _prevPage = pageDraw;
        _ds = ds;
        _pel = pel;
    }

    /// <summary>
    /// [asm 1823] the keyboard ISR stores EVERY scancode, make or break, in the single latch [0x2874] (the last one wins). A press this
    /// tick is a make code (a hex key carries its table char, any other key a char the table maps to '-'); a key that was down and is
    /// now up is a break code (bit 7). A release is applied before a press of the same tick, so a tap latched by the presenter
    /// (pressed and already up) still counts as a make.
    /// </summary>
    void LatchKeys(Input input)
    {
        if (Kind != MapKind.Password) return;
        bool released = false, pressedAny = false;
        foreach (var k in AllKeys)
        {
            bool down = input.IsKeyDown(k), pressed = input.IsKeyPressed(k);
            if (_held[(int)k] && !down && !pressed) released = true;
            _held[(int)k] = down;
            pressedAny |= pressed;
        }
        if (released) { _sc = ScBreak; _pending = 0; }
        if (!pressedAny) return;
        int ch = 0;
        foreach (var (key, c) in HexKeys)
            if (input.IsKeyPressed(key)) ch = c;
        _sc = ScMake;
        _pending = ch;
    }

    void Poll(Input input)
    {
        if (Kind == MapKind.ModeSelect)
        {
            bool arrow = input.IsKeyDown(Input.Key.Up) || input.IsKeyDown(Input.Key.Down)
                         || input.IsKeyDown(Input.Key.Right) || input.IsKeyDown(Input.Key.Left);
            if (arrow && !_prevArrow) Mode ^= 1;                   // [asm 994E-9977] toggle on the rising edge
            _prevArrow = arrow;
            if (Fire(input))
            {
                Level = 0;                                         // BEGINNER/EXPERT both begin at level 1
                StartFade(MapResult.LevelChosen);
            }
            return;
        }
        PasswordCallback(input);
        if (_leave)                                                // [asm 9877] [0xB1B2] != 0 -> 9286 fade and RET
            StartFade(Level != 0xFF ? MapResult.LevelChosen : MapResult.BackToMenu);   // [asm 8EF1] [0x2D8A] >= 0: a level was committed
    }

    /// <summary>
    /// [asm 9985/99AA-9ADF] the ENTER-CODE callback, one call per frame after the camera/draw part (BuildFrame). Same order as the ASM:
    /// status 1 (wrong-code pause) / status 2 (a code matched) / break code / fire / the pending make code.
    /// </summary>
    void PasswordCallback(Input input)
    {
        if (_status == 1)                                          // [asm 99AA/9AB4] wrong-code pause: no key, no fire
        {
            _wrong = (_wrong + 1) & 0xFFFF;
            if (_wrong >= Password.WrongFrames) ResetEntry();      // [asm 9AB8-9AC0 -> 9AC8]
            return;
        }
        if (_status == 2) { _leave = true; ResetEntry(); return; } // [asm 99B4/99BB -> 9AC4] the frame after a match: leave
        if (_sc == ScBreak) return;                                // [asm 99BE-99C7] the last scancode was a release: ignored, fire too
        if (Fire(input)) { _leave = true; ResetEntry(); return; }  // [asm 99CA-99D3 -> 9AC4] Space/Enter: leave (Level stays 0xFF unless matched)
        int al = _sc == ScMake ? _pending : 0;
        _sc = ScNone; _pending = 0;                                // [asm 99D6] consume
        if (al == 0) return;                                       // [asm 99E1] table char '-' (non-hex key) or nothing
        if (Password.HexValue(al) is not int nibble) return;
        _mem.Code = Password.Accumulate(_mem.Code, nibble);        // [asm 99F5-9A0D]
        int cur = _cursor;
        _echo[cur] = (byte)al;
        _cursor = cur + 1;
        if (cur + 1 < Password.Length) return;                     // [asm 9A1F]

        _wrong = 0;
        int di = _mem.Hist0, si = _mem.Hist1, bp = _mem.Hist2, code = _mem.Code;
        (int Level, bool Expert)? m = null;
        if (Password.IsCheatSequence(di, si, bp))
        {
            int idx = (code - 1) & 0xFFFF;
            if (idx <= 0x13) m = Password.WarpIndexToLevel(idx);
        }
        m ??= Password.Validate(code, _m.Seed, _m.Rot);
        if (m is { } hit)
        {
            Level = (byte)hit.Level;                               // [asm 9AA6] [0x2D8A]
            Mode = hit.Expert ? 1 : 0;                             // [asm 9AAA] [0xB197]
            _status = 2;                                           // [asm 9AAD] the screen shows one more frame, then leaves
            return;
        }
        _mem.Hist0 = si; _mem.Hist1 = bp; _mem.Hist2 = code;       // [asm 9A81-9A94] shift the group into the history
        _status = 1;
    }

    /// <summary>[asm 9AC8-9ADF] empty "[[[[" echo, status 0, cursor 0 (end of the pause, or on leaving the screen).</summary>
    void ResetEntry()
    {
        Array.Copy(EmptyEcho, _echo, 4);
        _status = 0;
        _cursor = 0;
    }

    void StartFade(MapResult result)
    {
        _exit = result;
        _fade = FrontEndFade.FadeOut(Carte.FadeStartDac(_a.MapPal6), MenuAssets.FadeEntries);   // [asm 9882 -> 9286, cs:[9153] = 0xA0 from 8E67/8E69], planes frozen
        _fadeIdx = 0;
    }

    public override void Draw(Framebuffer fb)
    {
        fb.SetPalette6(_pal, 0, 0, 16);
        for (int i = 16; i < 256; i++) fb.Palette[i] = (0, 0, 0);
        WorldMap.RenderPlanar(_page.Planes, _ds, _pel, WorldMap.PageWrap, _aw, fb.Pixels);
    }

    // Port-only (FULLSCREEN=WIDESCREEN): read-only view for WideMapArt.Pattern; logic untouched. The text-free backdrop = planes 0|1 only
    // (draw_string writes planes 2|3), rendered with the same ds/pel as the frame.
    public void DrawWide(Framebuffer narrow, Framebuffer wide)
    {
        Draw(narrow);
        var p = _page.Planes;
        var bg = new byte[320 * 200];
        WorldMap.RenderPlanar(new[] { p[0], p[1], new byte[p[0].Length], new byte[p[0].Length] }, _ds, _pel, WorldMap.PageWrap, _aw, bg);
        WideMapArt.Pattern(narrow, bg, wide);
    }
}

/// <summary>
/// The CARTE (asm 9520/9613, front_end.py _native_carte): MAP.SQZ master with the per-level 'you are here' marker stamped on,
/// revealed column by column while the CRTC pans: scroll_x starts at 8 and gains 1 per tick up to 639 (CARTE.TRK is only announced
/// by the flow). The view is 312 px wide (right 8 px border; the CRTC width set at 960A stays through the fade). Input is polled after every
/// frame: a fresh Space/Enter press (the previous poll saw it up; a fire still held from the mode-select does not count) starts the fade-out at
/// once; reaching scroll 639 first holds that last frame for <see cref="Carte.HoldTicks"/> = 0x46 retraces ([asm 96AE-96BF], the same fire poll
/// exits early). The fade is 9286 (96C7) over <c>CS:[9153]</c> = 0xA0 DAC entries ([asm 919F cl=0xA0 from the menu 8E69; nothing rewrites it before the
/// carte]): 5 retraces per pass (one per 0x20 entries) x 17 passes = 85 frames (<see cref="Carte.FadeStartDac"/>), after which <see cref="Result"/> becomes
/// LevelChosen (hand-off to the level loader, not ported).
/// </summary>
public sealed class CarteScene : MenuStage
{
    readonly byte[] _master;
    readonly byte[][] _planes = Carte.NewPage();
    readonly MapAssets _m;
    int _scrollX = Carte.ScrollStart, _ds, _pel, _aw = 312;
    bool _prevFire = true;                                         // the mode-select fire must be released first
    byte[] _pal;
    List<byte[]>? _fade;
    int _fadeIdx;
    int _endTicks;                                                 // ticks spent on the last column (the scroll-639 frame itself counts 0)

    public CarteScene(MenuAssets a, int level)
    {
        _m = a.Map;
        Level = (byte)level;
        _pal = _m.CartePal6;
        int o = level * 4;
        int x = _m.MarkerTable[o] | (_m.MarkerTable[o + 1] << 8);
        int y = _m.MarkerTable[o + 2] | (_m.MarkerTable[o + 3] << 8);
        _master = Carte.StampMarker(_m.Master, _m.Marker, Carte.MarkerOffset(x, y), _m.MarkerW, _m.MarkerH);
    }

    public byte Level { get; }
    public MapResult Result { get; private set; } = MapResult.Running;
    public override string Name => "CARTE";

    public override void Update(Input input)
    {
        HasFrame = false;
        if (Result != MapResult.Running) return;
        if (_fade != null)
        {
            if (_fadeIdx < _fade.Count) { _pal = _fade[_fadeIdx++]; HasFrame = true; }   // 9286 only touches the DAC: planes, pan and 312 px width stay
            else Result = MapResult.LevelChosen;
            return;
        }
        _ds = (_scrollX >> 3) & WorldMap.PageWrap;
        _pel = _scrollX & 7;
        HasFrame = true;
        bool fire = Fire(input);
        // [asm 969C-96A9 / 96AE-96BF] fire exits at once; the scroll-639 frame is followed by 70 held retraces (fire polled in each) before the fade
        if ((fire && !_prevFire) || (_scrollX >= Carte.ScrollEnd && _endTicks++ >= Carte.HoldTicks))
        {
            _fade = FrontEndFade.FadeOut(Carte.FadeStartDac(_m.CartePal6), MenuAssets.FadeEntries);   // [asm 96C7 -> 9286, cs:[9153] = 0xA0]
            _fadeIdx = 0;
            return;
        }
        _prevFire = fire;
        if (_scrollX < Carte.ScrollEnd)
        {
            Carte.ScrollBlitColumn(_planes, _master, _scrollX);    // reveal the column for the next frame
            _scrollX++;
        }
    }

    public override void Draw(Framebuffer fb)
    {
        fb.SetPalette6(_pal, 0, 0, 16);
        for (int i = 16; i < 256; i++) fb.Palette[i] = (0, 0, 0);
        WorldMap.RenderPlanar(_planes, _ds, _pel, WorldMap.PageWrap, _aw, fb.Pixels);
    }

    // Port-only (FULLSCREEN=WIDESCREEN): read-only view state for WideFrontEnd.CarteWindow; logic untouched.
    internal byte[] Master => _master;
    internal int Ds => _ds;
    internal int Pel => _pel;

    /// <summary>Port-only (WIDESCREEN): draws the original 320 frame into <paramref name="tmp"/> and the wider window on the master into <paramref name="wide"/>.</summary>
    internal void DrawWide(Framebuffer tmp, Framebuffer wide)
    {
        Draw(tmp);
        WideFrontEnd.CarteWindow(tmp, _master, _ds, _pel, wide);
    }
}
