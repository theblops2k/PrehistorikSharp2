using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>Port of world_map.py level_select_dispatch [asm 8E7B-8E96].</summary>
public enum LevelSelect { Wait, ModeSelect, Password, AutoAdvance }

public static class LevelSelectDispatch
{
    public const int Timeout = 0x10E;     // [asm 8E90] cmp [0x27F0],0x10E

    public static LevelSelect Dispatch(bool flagMode, bool flagPassword, bool inputActive, int wait)
    {
        if (flagMode) return LevelSelect.ModeSelect;          // '1'  ([0x27F6])
        if (flagPassword) return LevelSelect.Password;        // '2'  ([0x27F7])
        if (inputActive) return LevelSelect.ModeSelect;       // fire ([0x282d]|[0x2810])
        return wait < Timeout ? LevelSelect.Wait : LevelSelect.AutoAdvance;
    }
}

/// <summary>One screen of the menu flow; one displayed frame per 70 Hz tick unless <see cref="HasFrame"/> is false.</summary>
public abstract class MenuStage : IScene
{
    public double LogicHz => 70.0;   // VGA retrace
    public abstract string Name { get; }
    public virtual int PaletteEntries => 16;
    /// <summary>False when this tick's Update only decided to leave (no frame shown, the flow switches within the tick).</summary>
    public bool HasFrame { get; protected set; }
    public abstract void Update(Input input);
    public abstract void Draw(Framebuffer fb);

    protected static bool Fire(Input i) => i.IsKeyDown(Input.Key.Space) || i.IsKeyDown(Input.Key.Enter);

    /// <summary>Show a 16-colour 6-bit palette (rest black) and a 64000-px image.</summary>
    protected static void Show16(Framebuffer fb, byte[] image, byte[] pal6)
    {
        fb.SetPalette6(pal6, 0, 0, 16);
        for (int i = 16; i < 256; i++) fb.Palette[i] = (0, 0, 0);
        Array.Copy(image, fb.Pixels, image.Length);
    }
}

/// <summary>
/// The "press 1/2" menu (asm 8E45): MENU.SQZ faded in over 0xA0 DAC entries (one step per tick, no input read),
/// then held while the dispatch waits. Per held tick: dispatch(latch of the PREVIOUS tick, wait) -> leave, else show a
/// frame and wait++. The latch is re-read at the end of every tick (key-table entries; same order as the Python loop).
/// </summary>
public sealed class MenuScene : MenuStage
{
    readonly MenuAssets _a;
    int _idx, _wait;
    byte[]? _pal;
    bool _k1, _k2, _fire;

    public MenuScene(MenuAssets a) => _a = a;

    public override string Name => "MENU";
    public override int PaletteEntries => 256;
    /// <summary>Set when the menu decided to leave; Running while it still owns the screen.</summary>
    public LevelSelect Outcome { get; private set; } = LevelSelect.Wait;

    public override void Update(Input input)
    {
        HasFrame = false;
        if (Outcome != LevelSelect.Wait) return;
        if (_idx < _a.MenuFade.Count)
        {
            _pal = _a.MenuFade[_idx++];
            HasFrame = true;
        }
        else
        {
            var c = LevelSelectDispatch.Dispatch(_k1, _k2, _fire, _wait);
            if (c != LevelSelect.Wait) Outcome = c;
            else { HasFrame = true; _wait++; }              // palette stays the last fade frame ("held8")
        }
        _k1 = input.IsKeyDown(Input.Key.One);
        _k2 = input.IsKeyDown(Input.Key.Two);
        _fire = Fire(input);
    }

    public override void Draw(Framebuffer fb)
    {
        if (_pal is null) return;
        fb.SetPalette6(_pal, 0, 0, 256);
        Array.Copy(_a.MenuImage, fb.Pixels, _a.MenuImage.Length);
    }
}

/// <summary>
/// Menu-idle ATTRACT, asm 8E98 .. 8F29 (the title part; the demo level that follows is run by the host). Stages, all at the 70 Hz retrace rate:
///  1. 9286 fade-to-black of the menu (0xA0 DAC entries, [0x9153] still holds the menu's 919F count): <see cref="MenuAssets.MenuFadeOut"/>, one frame per retrace.
///  2. 9155: 447D mode set + 44CD vsync (1 black tick), MENU2 planes copied while the DAC is black, then the 91CF fade-in of the MENU2 palette
///     (<see cref="MenuAssets.AttractFadeIn"/>); the last fade frame is the DAC held from then on.
///  3. 8F2D..9046 animation: 119 frames, each presented through 44FB -> 1C65 = 3 retraces (<see cref="AttractTitle"/>).
///  4. 8EB3 fade-to-black of the title (0xA0 entries), then 8F29 a second 9286 over the black DAC (one pass = 5 retraces).
/// No key is read in any of these stages (the animation cannot be skipped). Afterwards the main flow continues (0163 -> 01A5: CARTE, the demo level
/// [0x83E] with [0x2879] = 1): <see cref="Done"/> is set and <see cref="MenuFlow.DemoRequested"/> tells the host.
/// </summary>
public sealed class AttractScene : MenuStage
{
    public const int AnimFrames = AttractTitle.FrameCount, TicksPerFrame = 3;
    enum Stage { MenuFade, BlackTick, TitleIn, Anim, TitleOut, Black, Finished }

    static readonly byte[] BlackDac = new byte[0x300];
    readonly MenuAssets _a;
    readonly Task<LevelState> _title;                 // level-0 cold boot for the sprites; built while the menu fades out
    Stage _stage = Stage.MenuFade;
    int _i, _sub;
    byte[] _img;
    byte[] _dac = BlackDac;
    int _entries = 256;
    IEnumerator<byte[]>? _frames;
    AttractTitle? _anim;                              // the title animation (its state is what SpriteLayer reads)
    readonly Dictionary<int, byte[]> _layers = new();   // port-only: sprite layers of the current animation frame by dx (WIDESCREEN margins)

    public AttractScene(MenuAssets a)
    {
        _a = a;
        _img = a.MenuImage;
        _title = Task.Run(a.NewTitleState);
    }

    public override string Name => "ATTRACT";
    public override int PaletteEntries => _entries;
    public bool Done { get; private set; }
    /// <summary>Port-only (WIDESCREEN margins): the picture on screen is still the press-1/2 menu fading out (stage 1), not the title.</summary>
    public bool ShowsMenu => ReferenceEquals(_img, _a.MenuImage);
    /// <summary>Port-only (WIDESCREEN margins): the sprite-free MENU2 base the title animation is composed on (<see cref="WideAttractArt"/> derives its margins from it).</summary>
    public byte[] Base => _a.AttractImage;
    /// <summary>
    /// Port-only (WIDESCREEN margins): the sprites of the picture on screen for a screen shifted by <paramref name="dx"/> px (<see cref="AttractTitle.SpriteLayer"/>, 0xFF = none), cached per
    /// animation frame; null when no animation frame is on screen. While the title fades out the last frame's layers are kept.
    /// </summary>
    public byte[]? SpriteLayer(int dx)
    {
        if (_anim is null || ShowsMenu) return null;
        if (_layers.TryGetValue(dx, out var l)) return l;
        if (_stage != Stage.Anim) return null;                // after the last frame the state has moved on: only the cached layers belong to the picture
        return _layers[dx] = _anim.SpriteLayer(dx);
    }
    /// <summary>Retraces spent in the stage machine so far (debug / verification).</summary>
    public int Ticks { get; private set; }

    void Present(byte[] img, byte[] dac, int entries)
    {
        _img = img; _dac = dac; _entries = entries;
        HasFrame = true;
        Ticks++;
    }

    void Next(Stage s) { _stage = s; _i = 0; _sub = 0; }

    public override void Update(Input input)
    {
        HasFrame = false;
        while (!Done)
        {
            switch (_stage)
            {
                case Stage.MenuFade:
                    if (_i < _a.MenuFadeOut.Count) { Present(_a.MenuImage, _a.MenuFadeOut[_i++], 256); return; }
                    Next(Stage.BlackTick); break;
                case Stage.BlackTick:                                         // [9159] 44CD: one retrace with the DAC black
                    if (_i < 1) { _i++; Present(_a.AttractImage, BlackDac, 16); return; }
                    Next(Stage.TitleIn); break;
                case Stage.TitleIn:                                           // [91CF..9259] the MENU2 palette fades in over the drawn planes
                    if (_i < _a.AttractFadeIn.Count) { Present(_a.AttractImage, _a.AttractFadeIn[_i++], 16); return; }
                    _anim = new AttractTitle(_title.Result, _a.AttractPlanes);
                    _frames = _anim.Frames().GetEnumerator();
                    Next(Stage.Anim); break;
                case Stage.Anim:                                              // [8F94..9044] one frame per 3 retraces
                    if (_sub == 0)
                    {
                        if (!_frames!.MoveNext()) { Next(Stage.TitleOut); break; }
                        _img = _frames.Current;
                        _layers.Clear();
                    }
                    Present(_img, _a.AttractFadeIn[^1], 16);
                    _sub = (_sub + 1) % TicksPerFrame;
                    return;
                case Stage.TitleOut:                                          // [8EB3] 9286
                    if (_i < _a.AttractFadeOut.Count) { Present(_img, _a.AttractFadeOut[_i++], 16); return; }
                    Next(Stage.Black); break;
                case Stage.Black:                                             // [8F29] 9286 again: 5 retraces over the black DAC
                    if (_i < _a.AttractBlack.Count) { Present(_img, _a.AttractBlack[_i++], 16); return; }
                    Next(Stage.Finished); Done = true; break;
            }
        }
    }

    public override void Draw(Framebuffer fb)
    {
        fb.SetPalette6(_dac, 0, 0, _entries);
        for (int i = _entries; i < 256; i++) fb.Palette[i] = (0, 0, 0);
        Array.Copy(_img, fb.Pixels, _img.Length);
    }
}

public enum MapKind { ModeSelect, Password }
public enum MapResult { Running, LevelChosen, BackToMenu }

// MapScene / CarteScene (mode-select, password, carte) live in MapScenes.cs.

/// <summary>
/// native_menu_flow (front_end.py 328-381) as a scene: MENU -> ATTRACT (-> the demo, host side), MENU -> MAP (mode-select or password).
/// Audio is only announced through <see cref="IFrontEndAudio"/> (PRESENTA.TRK on entry, CODE.TRK on entering the map).
/// Transitions cost no tick: the next stage's first Update runs in the same tick.
/// </summary>
public sealed class MenuFlow : IScene
{
    public double LogicHz => 70.0;   // VGA retrace
    readonly MenuAssets _a;
    readonly IFrontEndAudio _audio;
    MenuStage _cur;

    /// <param name="announceSong">False after the attract demo: 02CC is a no-op while [0x2879] = 1 (asm 02ED), so main 015A never reloads PRESENTA.TRK and the menu music simply continues.</param>
    /// <param name="pw">The password accumulator/history, static in the original (survives leaving the screen and going to a level and back); null = a fresh one.</param>
    public MenuFlow(MenuAssets assets, IFrontEndAudio? audio = null, bool announceSong = true, PasswordMemory? pw = null)
    {
        _a = assets;
        _pw = pw ?? new PasswordMemory();
        _audio = audio ?? new NullFrontEndAudio();
        if (announceSong) _audio.LoadSong("PRESENTA.TRK");  // [asm 015a/0107] announced, not played
        _cur = new MenuScene(assets);
    }

    public string SceneName => _cur.Name;
    /// <summary>The stage on screen (read-only; the port-only WIDESCREEN front end uses it to recognise the CARTE).</summary>
    internal MenuStage Current => _cur;
    public int PaletteEntries => _cur.PaletteEntries;
    public bool HasFrame => _cur.HasFrame;
    /// <summary>True once the CARTE finished (hand-off to the level loader), the expert-eater gate was hit, or the attract title finished (<see cref="DemoRequested"/>).</summary>
    public bool Done { get; private set; }
    /// <summary>[asm 8EB6-8EC7] the menu idled out: the title animation ran and the main flow continues with the CARTE and the demo level (host side); <see cref="ChosenLevel"/> is not set.</summary>
    public bool DemoRequested { get; private set; }
    public byte ChosenLevel { get; private set; }
    public int ChosenMode { get; private set; }
    /// <summary>[asm 0163] A BEGINNER chose level 8/9: the original shows CASTLE.SQZ and returns to the menu (not ported); Done is set instead of the carte.</summary>
    public bool ExpertEaterWall { get; private set; }
    int _mode;                                              // [0xB197], persists across visits to the map
    /// <summary>[asm 8EFD -> 8F29] retraces of the SECOND 9286 a committed level runs over the already black DAC (one pass = 5).</summary>
    int _postMapHold = 5;
    readonly PasswordMemory _pw;

    public void Update(Input input)
    {
        if (Done) return;
        for (int guard = 0; guard < 8; guard++)
        {
            _cur.Update(input);
            MenuStage? next = null;
            switch (_cur)
            {
                case MenuScene m when m.Outcome == LevelSelect.AutoAdvance:
                    next = new AttractScene(_a); break;
                case MenuScene m when m.Outcome != LevelSelect.Wait:
                    _audio.LoadSong("CODE.TRK");            // [asm 96d5] map song, announced only
                    next = new MapScene(_a, m.Outcome == LevelSelect.Password ? MapKind.Password : MapKind.ModeSelect, _mode, _pw);
                    break;
                case AttractScene { Done: true }:
                    DemoRequested = true; Done = true; return;   // [asm 8EB6-8F29] the title is over: [0x2879]=1, the host runs the CARTE + the demo level
                case MapScene { Result: MapResult.BackToMenu } mb:
                    _mode = mb.Mode;
                    next = new MenuScene(_a); break;
                case MapScene { Result: MapResult.LevelChosen } ms:
                    _mode = ms.Mode;
                    ChosenLevel = ms.Level; ChosenMode = ms.Mode;
                    if (ms.Level is 8 or 9 && ms.Mode == 0) { ExpertEaterWall = true; Done = true; return; }
                    if (_postMapHold > 0) { _postMapHold--; return; }   // [asm 8EFD -> 8F29] second 9286: 5 retraces, DAC already black
                    _audio.LoadSong("CARTE.TRK");           // [asm 9520] the carte song, announced only
                    next = new CarteScene(_a, ms.Level);
                    break;
                case CarteScene { Result: MapResult.LevelChosen }:
                    Done = true; return;                    // hand-off to the level loader (not ported)
            }
            if (next is null) return;
            _cur = next;
        }
    }

    public void Draw(Framebuffer fb) => _cur.Draw(fb);
}
