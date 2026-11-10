using Pre2Port.Game.Level;
using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>
/// Chains the ported front-end scenes in one window: FrontEndIntro -> MenuFlow (menu / attract / mode-select or
/// password map / carte). The menu-idle ATTRACT is MenuFlow's title animation followed here by the CARTE of the demo level, the demo level itself
/// (<see cref="LevelScene.DemoPlayback"/>: DC1 mode 1 replays <see cref="DemoInput.Store"/>, any key or the 0x55AA sentinel ends it) and the 9286 fade-out back to the menu (asm 8E98 -> 0163 -> 01A5 -> 4C69 -> 5063 -> 012F/013B).
/// When MenuFlow is Done (the carte finished, or the expert-eater gate) a fresh MenuFlow starts,
/// i.e. back to the menu, except level 1 (ChosenLevel 0), which opens the LevelScene (first frame: background + terrain only). No audio (null seam). Transitions cost no tick.
/// </summary>
public sealed class FrontEndRunner : IScene
{
    readonly byte[] _boot;
    readonly string _originalDir;
    readonly FrontEndIntro _intro;
    MenuAssets? _menuAssets;
    MenuFlow? _menu;
    LevelScene? _level;
    EndScene? _end;
    CarteScene? _carte;
    FadeHoldScene? _fade;
    FrontEndAssets? _assets;
    /// <summary>THE END (5034) and the CREATORS photo (25F6) that follows it.</summary>
    TheEndScene? _theEnd;
    CreatorsScene? _creators;
    /// <summary>Score words [0x6C0E], [0x6C10] and the HUD's animated score [0x6C0C] kept across THE END: 5034 does not reset them (only the game-over tail 5083-508F does), so the next game starts with them.</summary>
    (int Lo, int Hi, int Shown)? _endScore;
    int _carteNext;
    /// <summary>The demo buffer DS:0x3F (shipped demo from the boot image until the first level is chosen; then each live session is recorded into it, asm DC1 0F0A).</summary>
    readonly DemoInput.Store _demo;
    /// <summary>True while the CARTE / level / tally on screen belong to the attract demo ([0x2879] = 1).</summary>
    bool _demoRun;
    /// <summary>[0xB198] the committed BEGINNER(0)/EXPERT(1) mode: [0xB197] copied at the level choice (asm 8F12), the demo header [0x83D] for the attract (8EC4). Kept for the next levels.</summary>
    int _modeCopy;
    /// <summary>The password screen's accumulator/history ([0xB1B9], [0xB1B3-B7]): static data in the original, kept across menu visits and levels.</summary>
    readonly PasswordMemory _pw = new();
    (int Lives, int Letters, int Utensils, int V19, int V18, int ScoreLo, int ScoreHi) _carry;
    /// <summary>Level indexes whose loader/objects/player are verified against Python (levels 1-5, LEVEL6 = 5 with its tree boss 6D34 (TreeVerify), LEVEL7 = 6 (pouncer AI 7A60, bonus level LEVELG = 0xF in WarpLevels), LEVEL9 = 8 (camera-engine level [91FE] = 0, no new handler: every spawner type 1-6/8-12 already ported; gapscan 8 = 0 gaps; its end -> LEVELA = 9) LEVEL8 = 7 (no new handler: every spawner type already ported; gapscan 7 = 0 gaps) and LEVELA = 9, the mode-9 last boss, so far); any other next level is an explicit gap.</summary>
    static readonly HashSet<int> PortedLevels = new() { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
    /// <summary>Levels entered through the id 0x137 item (warp table [0x2CF6]: level 2 -> 0xC, level 3 -> 0xB, level 4 -> 0xA, LEVELA -> 0xE = LEVELF, where THE END is): the terrain frame is verified against Python (level refs 10-12, 14), they have no spawners.
    /// 0xD (LEVELE) is the bonus level that the NORMAL exit of level 3 (index 2) enters ([asm 882A] [0x2D8A] 2 -> 0xC, then 4CC1 INC -> 0xD); it has spawners 12 / 6 / 8 / 9 (objects ref level 13: 1105 PASS).
    /// 0xF (LEVELG) is the bonus level that the NORMAL exit of LEVEL7 (index 6) enters ([asm 882A] [0x2D8A] 6 -> 0xE, then 4CC1 curtain + INC -> 0xF; LEVELF itself is never loaded on this path); no spawners; its exit item sends [0x2D8A] 0xF -> 6 and the tally of LEVEL7 follows.</summary>
    static readonly HashSet<int> WarpLevels = new() { 0xA, 0xB, 0xC, 0xD, 0xE, 0xF };
    /// <summary>Highest 1-based level number the port plays through the normal flow: the main levels of <see cref="PortedLevels"/> (indexes 0..9 -> 1..10). Bonus levels (<see cref="WarpLevels"/>, 0xA-0xF) are entered by warps and do not count.</summary>
    internal static int MaxStartLevel => PortedLevels.Max() + 1;

    /// <summary>0-based level index for a new game from the menu: hidden <c>level=n</c> of the cfg, clamped to 1..<see cref="MaxStartLevel"/> (invalid / absent -> 0).</summary>
    internal static int StartLevelIndex(Settings s) => Settings.ParseStartLevel(s.StartLevelRaw, MaxStartLevel) - 1;

    /// <summary>Counters kept by a level change without the tally (null after a tally, whose 51DF cleanup zeroes them).</summary>
    LevelCarry? _carryCounters;

    public FrontEndRunner(byte[] boot, string originalDir, IGameAudio? audio = null)
    {
        _boot = boot;
        _originalDir = originalDir;
        _demo = new DemoInput.Store(boot);
        _assets = FrontEndAssets.Load(boot, originalDir);
        _intro = new FrontEndIntro(_assets, skippable: true, audio: audio);
        Audio = audio ?? new NullGameAudio();
    }

    /// <summary>Audio output (music + effects); shared by the intro, the menus and the level scenes.</summary>
    public IGameAudio Audio { get; }

    /// <summary>Optional override of the level scene's logic rate (null = the scene's own).</summary>
    public double? LevelLogicHz { get; init; }

    /// <summary>70 Hz (VGA retrace) for intro/menu/map; the level scene's rate (or the override) once it is active.</summary>
    public double LogicHz => _fade is not null || _theEnd is not null || _creators is not null ? 70.0 : _end is not null ? _end.LogicHz : _carte is not null ? 70.0 : _level is null ? 70.0 : LevelLogicHz ?? ((IScene)_level).LogicHz;

    /// <summary>"INTRO", "MENU" or "LEVEL".</summary>
    public LevelScene? ActiveLevel => _level;   // crash-log context only
    public string Phase => _theEnd is not null ? "THEEND" : _creators is not null ? "CREATORS" : _fade is not null ? "FADE" : _end is not null ? "END" : _carte is not null ? "CARTE" : _level is not null ? (_demoRun ? "DEMO" : "LEVEL") : _menu is null ? "INTRO" : "MENU";
    public string SceneName => _theEnd is not null ? _theEnd.Name : _creators is not null ? _creators.Name : _fade is not null ? "FADE" : _end is not null ? _end.SceneName : _carte is not null ? _carte.Name : _level is not null ? _level.SceneName : _menu is null ? _intro.SceneName : _menu.SceneName;
    /// <summary>Times the flow returned to the menu after a finished MenuFlow.</summary>
    public int Returns { get; private set; }
    public byte LastChosenLevel { get; private set; }
    public int LastChosenMode { get; private set; }

    public void Update(Input input)
    {
        if (_theEnd is not null)
        {
            _theEnd.Update(input);
            if (_theEnd.Result == StageResult.Running) return;
            _theEnd = null;
            if (_assets!.Year >= 1994) { _creators = new CreatorsScene(_assets); _creators.Update(input); return; }   // [505B 25F6] gated on the system year [0x37] >= 0x7CA
            AfterTheEnd(input);
            return;
        }
        if (_creators is not null)
        {
            _creators.Update(input);
            if (_creators.Result == StageResult.Running) return;
            _creators = null;
            AfterTheEnd(input);
            return;
        }
        if (_fade is not null)
        {
            _fade.Update();
            if (!_fade.Done) return;
            _fade = null;
            BackToMenu(input, keepMusic: true);                                           // [asm 013B-0160] the fade is over: lives/masks reset, PRESENTA.TRK, the press-1/2 menu
            return;
        }
        if (_end is not null)
        {
            _end.Update(input);
            if (!_end.Done) return;
            if (_demoRun) { StartDemoFade(true); return; }               // [4F54] the tally of a demo that reached the level end -> [2879] != 0 -> 506C

            // [asm 5063 -> main 0x11C] game over: after the GAME OVER scene the front end restarts at the menu (menu -> map -> carte -> level 1 with a fresh
            // start block: lives 2, energy 3, score 0). A normal end loads the next level.
            if (_end.GameOver)
            {
                BackToMenu(input);
                return;
            }
            int next = 0;
            if (!_end.GameOver)
            {
                next = (_level!.State.Rb(Pre2Port.Native.LevelState.Off.Level) + 1) & 0xFF;
                if (!PortedLevels.Contains(next))
                    throw new Pre2Port.Recovered.ObjectGapException($"next level {next + 1} (index {next}): loader/objects/player are verified only for level 1; not ported");
            }
            // [4F5E-4F6B] INC [0x2D8A], 51DF, 5237, return AX=0 -> main 012F: the start block 0141-0155 (lives 2, BONUS letters/utensils masks, [7B19]=0x14, [7B18]=0) is
            // skipped (AX == 0), the score [0x6C0E] is never touched, so they all carry over; then the CARTE (9520, marker at the NEW level) and the loader 447D.
            var st = _level!.State;
            _carry = (st.Rb(0x27D8), st.Rb(0x6CA7), st.Rb(0x6CA8), st.Rb(0x7B19), st.Rb(0x7B18), st.Rw(0x6C0E), st.Rw(0x6C10));
            _carryCounters = null;                                       // [4F5E-4F62] INC level, 51DF: the item table and the total are zeroed
            _carteNext = next;
            _end = null;
            _level = null;
            _menuAssets ??= MenuAssets.Load(_boot, _originalDir);
            if (Pre2Port.Recovered.AudioFiles.SongFile(2) is string carteSong) Audio.LoadSong(carteSong);   // [9520-952D] 02CC(2) = CARTE.TRK
            _carte = new CarteScene(_menuAssets, next);
            _carte.Update(input);
            return;
        }
        if (_carte is not null)
        {
            _carte.Update(input);
            if (_carte.Result != MapResult.LevelChosen) return;
            _carte = null;
            _level = NewLevel(_carteNext, _carryCounters);
            if (!_demoRun) ApplyCarry(_level);                           // the demo is a fresh start: ColdBoot's lives 2 / energy 3 / score 0
            UpdateLevel(input);
            return;
        }
        if (_level is not null)
        {
            if (!UpdateLevel(input)) return;
            if (_level.Outcome == LevelScene.EndKind.Warp) { StartWarp(input); return; }
            if (_level.Outcome == LevelScene.EndKind.GameComplete) { StartTheEnd(input); return; }
            if (_level.Outcome != LevelScene.EndKind.None)
            {
                bool over = _level.Outcome == LevelScene.EndKind.GameOver;
                if (_demoRun && over) { StartDemoFade(false); return; }  // [5063 506C 9B2C] demo: no GAME OVER scene; 9286 and back to the menu
                _end = new EndScene(_level.State, _originalDir, over, animated: true) { Audio = Audio };
                if (over && Pre2Port.Recovered.AudioFiles.SongFile(0x11) is string song) Audio.LoadSong(song);   // [506F-5072] 02CC ax=0x11 (BOULA.TRK)
                if (!over && !_demoRun && Pre2Port.Recovered.AudioFiles.SongFile(0xF) is string bravo) Audio.LoadSong(bravo);   // [4CE4-4CE7] 02CC ax=0xF (BRAVO.TRK); a no-op in the demo (asm 02ED)
            }
            return;
        }
        if (_menu is null)
        {
            if (!_intro.Done)
            {
                _intro.Update(input);
                if (!_intro.Done) return;
                // The last intro frame was shown this tick; the menu starts on the next one.
                return;
            }
            _menuAssets ??= MenuAssets.Load(_boot, _originalDir);
            _menu = new MenuFlow(_menuAssets, Audio, pw: _pw);
        }
        else if (_menu.Done)
        {
            if (_menu.DemoRequested) { StartDemo(input); return; }
            LastChosenLevel = _menu.ChosenLevel;
            LastChosenMode = _menu.ChosenMode;
            _modeCopy = _menu.ChosenMode;
            int chosen = _menu.ChosenLevel;
            if (chosen == 0 && !_menu.ExpertEaterWall) chosen = StartLevelIndex(Settings.Current);   // hidden cfg key level=n: a plain "1-START" begins at that level (a password's level is kept)
            _demo.Wipe((byte)chosen, _menu.ChosenMode);                   // [asm 8EFD-8F18] a level was chosen: the demo buffer is wiped (0x55AA), [0x83E] level, [0x83D] mode
            if (PortedLevels.Contains(chosen) && !_menu.ExpertEaterWall)
            {
                // The map chose level 1 (index 0) or the cfg's start level: hand off to the level loader (cold boot over a copy of the boot image; lives / score / masks of a new game).
                _level = NewLevel(chosen);
                _level.State.Wb(Pre2Port.Recovered.Hud.OffLives, Options.GameOptions.Current.StartLivesState);   // LivesAtStartup (Prehistorik2.cfg, 3..10; 3 = the original start block's 2)
                if (_endScore is { } es)                                  // the game after THE END: the score was never reset (see _endScore)
                {
                    _level.State.Ww(0x6C0E, es.Lo); _level.State.Ww(0x6C10, es.Hi); _level.State.Ww(0x6C0C, es.Shown);
                    _endScore = null;
                }
                _level.Update(input);
                return;
            }
            Returns++;
            _demo.ResetCursor();                                          // [8E4F] menu entry
            _menu = new MenuFlow(_menuAssets!, Audio, pw: _pw);
        }
        _menu.Update(input);
        if (_menu.Done && _menu.DemoRequested && !_demoRun) StartDemo(input);   // the title finished this tick: the CARTE starts in the same tick (transitions cost no tick)
    }

    /// <summary>Back to the press-1/2 menu (main 011C -> 8E45): a new menu flow; the demo cursor restarts ([asm 8E4F-8E55]).</summary>
    void BackToMenu(Input input, bool keepMusic = false)
    {
        _end = null;
        _level = null;
        _carte = null;
        _demoRun = false;
        Returns++;
        _demo.ResetCursor();
        _menuAssets ??= MenuAssets.Load(_boot, _originalDir);
        _menu = new MenuFlow(_menuAssets, Audio, announceSong: !keepMusic, pw: _pw);
        _menu.Update(input);
    }

    /// <summary>[asm 8EB6 -> main 0163/01A5] the attract title is over: the CARTE of the demo level [0x83E], then the level itself with [0x2879] = 1.</summary>
    void StartDemo(Input input)
    {
        int lvl = _demo.Level;
        if (!PortedLevels.Contains(lvl)) { BackToMenu(input, keepMusic: true); return; }  // the recorded level is not ported: nothing to replay
        _demoRun = true;
        _modeCopy = _demo.ModeByte;                                       // [asm 8EC4] [0xB198] = [0x83D]
        _menuAssets ??= MenuAssets.Load(_boot, _originalDir);
        // [9520 02CC(2)] CARTE.TRK is NOT loaded: 02CC returns at once while [0x2879] = 1 (asm 02ED), the menu song keeps playing through the carte and the demo.
        _carteNext = lvl;
        _carte = new CarteScene(_menuAssets, lvl);
        _carte.Update(input);
    }

    /// <summary>
    /// Run one tick of the level; for the attract demo a port gap (an unported branch) ends the demo like the Python <c>_native_attract</c> does
    /// (returns false: the scene was replaced by the fade).
    /// </summary>
    bool UpdateLevel(Input input)
    {
        if (!_demoRun) { _level!.Update(input); return true; }
        try { _level!.Update(input); return true; }
        catch (Exception e) when (e is Pre2Port.Recovered.ObjectGapException or NotSupportedException)
        {
            Console.Error.WriteLine($"attract demo ended at an unported branch: {e.Message}");
            StartDemoFade(false);
            return false;
        }
    }

    /// <summary>
    /// [asm 5063/506C 4F54] the demo is over (death / key / sentinel / level end): BOULA.TRK (02CC 0x11, 506F), no GAME OVER scene (9B2C), then main 011C -> 013B
    /// 9286 fades the (0xA0 first) DAC entries out over the last frame (the 30C6 curtain left the viewport black) before the menu returns.
    /// </summary>
    void StartDemoFade(bool fromEnd)
    {
        // [506F 02CC(0x11)] BOULA.TRK is NOT loaded in the demo ([0x2879] = 1, asm 02ED)
        int endW = WideKind == WideFrontEnd.Kind.End ? Pre2Port.Recovered.LevelRender.WideWidth : Screen.Width;   // WIDESCREEN: the tally was wide, so is its fade
        var fb = new Framebuffer(fromEnd ? endW : _level!.FbWidth);
        if (fromEnd) { _end!.Alpha = Alpha; if (endW > Screen.Width) _end!.DrawWide(_narrow ??= new Framebuffer(), fb); else _end!.Draw(fb); }
        else _level!.DrawClosed(fb);
        var dac = new byte[0x300];
        for (int i = 0; i < 256; i++) { dac[i * 3] = (byte)(fb.Palette[i].R >> 2); dac[i * 3 + 1] = (byte)(fb.Palette[i].G >> 2); dac[i * 3 + 2] = (byte)(fb.Palette[i].B >> 2); }
        _fade = new FadeHoldScene(fb, FrontEndFade.FadeOut(dac, MenuAssets.FadeEntries));
        _end = null;
        _level = null;
        _carte = null;
        _fade.Update();
    }

    LevelScene NewLevel(int level, LevelCarry? carry = null) => new(_boot, _originalDir, level, damage: true, bonus: true, platforms: true, attack: true, modeCopy: _modeCopy, carry: carry)
    { HoldEnd = true, AnimateDeath = true, Audio = Audio, DemoStore = _demo, DemoPlayback = _demoRun };

    /// <summary>The between-levels carry of the 4F65 path (main 0x12F with AX = 0 skips the level-start block 0141-0155): lives, BONUS letters, utensils, attack values and the score.</summary>
    void ApplyCarry(LevelScene level)
    {
        var ns = level.State;
        ns.Wb(0x27D8, _carry.Lives); ns.Wb(0x6CA7, _carry.Letters); ns.Wb(0x6CA8, _carry.Utensils);
        ns.Wb(0x7B19, _carry.V19); ns.Wb(0x7B18, _carry.V18); ns.Ww(0x6C0E, _carry.ScoreLo); ns.Ww(0x6C10, _carry.ScoreHi);
    }

    /// <summary>[asm 4CAE-4CB5 -> 5034] the player fell out of level 0xE: THE END picture (fade in, wait for fire, fade out), then the CREATORS photo, then main 012F with AX = 1.</summary>
    void StartTheEnd(Input input)
    {
        var st = _level!.State;
        _endScore = (st.Rw(0x6C0E), st.Rw(0x6C10), st.Rw(0x6C0C));
        _level = null;
        _end = null;
        _theEnd = new TheEndScene(_assets!);
        _theEnd.Update(input);
    }

    /// <summary>[asm 505E -> main 012F, AX = 1 -> 013B] back to the press-1/2 menu: 9286, 447D, lives 2, BONUS letters / utensils 0, [7B19] 0x14, [7B18] 0 (the start block), song 3, the menu. The score, the level and the demo buffer are left alone.</summary>
    void AfterTheEnd(Input input) => BackToMenu(input);

    /// <summary>
    /// [asm 4C8F-4C96 / 4CC1-4CC8 -> 4F65 -> main 012F] the end of the level without the tally (the id 0x137 item of a main level = its bonus level, or the end of a bonus level): the curtain 30C6 already
    /// closed inside the level scene, 5237 re-initialises and the target level is loaded over the same persistent state. 4F5E-4F62 (INC and the 51DF cleanup) is NOT executed, so the item table and the
    /// food counters go along (<see cref="LevelCarry"/>). A target &gt;= 0xA shows no CARTE (019E) and starts its own song ([0x2D20 + level], 01AB); a main level target would get its CARTE first.
    /// </summary>
    void StartWarp(Input input)
    {
        if (_demoRun) { StartDemoFade(false); return; }                  // no recorded demo ends in a warp
        int target = _level!.WarpTarget;
        if (!PortedLevels.Contains(target) && !WarpLevels.Contains(target))
            throw new Pre2Port.Recovered.ObjectGapException($"warp to level {target + 1} (index {target}): loader/objects/player are not ported for it");
        var st = _level.State;
        _carry = (st.Rb(0x27D8), st.Rb(0x6CA7), st.Rb(0x6CA8), st.Rb(0x7B19), st.Rb(0x7B18), st.Rw(0x6C0E), st.Rw(0x6C10));
        _carryCounters = LevelCarry.Capture(st);
        _carteNext = target;
        _end = null;
        if (target < 0xA)                                                // [019E] a main level is introduced by its CARTE
        {
            _level = null;
            _menuAssets ??= MenuAssets.Load(_boot, _originalDir);
            if (Pre2Port.Recovered.AudioFiles.SongFile(2) is string carteSong) Audio.LoadSong(carteSong);
            _carte = new CarteScene(_menuAssets, target);
            _carte.Update(input);
            return;
        }
        _level = NewLevel(target, _carryCounters);
        ApplyCarry(_level);
        UpdateLevel(input);
    }

    /// <summary>Live gameplay (not the attract demo, the tally, the carte, THE END...): the configured controls apply.</summary>
    public bool GameplayKeys => _level is not null && !_demoRun && _end is null && _fade is null && _theEnd is null && _creators is null && _carte is null;

    public bool CanReturnToMainMenu => true;

    /// <summary>The press-1/2 menu is on screen (Phase "MENU"): the overlay hides "Return to Main Menu".</summary>
    public bool InMainMenu => Phase == "MENU";

    /// <summary>ESC overlay "Return to Main Menu": drops whatever is on screen (intro, level, tally, THE END...) and starts a fresh press-1/2 menu, like the game over does.</summary>
    public void ReturnToMainMenu()
    {
        _theEnd = null; _creators = null; _fade = null;
        BackToMenu(new Input());
    }

    /// <summary>Forwarded to the level scene (intro/menu/map ignore it).</summary>
    public double Alpha { get; set; } = 1.0;

    /// <summary>Wider than 320 only in WIDESCREEN: the live level scene, TITUS and the CARTE (see WideKind); everything else is 320.</summary>
    public int FbHeight => _creators is not null ? CreatorsScene.Height : Screen.Height;

    public int FbWidth => WideKind != WideFrontEnd.Kind.None ? Pre2Port.Recovered.LevelRender.WideWidth : _creators is not null ? CreatorsScene.Width : _theEnd is not null ? Screen.Width : _fade is not null ? _fade.FbWidth : _end is null && _level is not null ? ((IScene)_level).FbWidth : Screen.Width;

    /// <summary>
    /// Port-only (FULLSCREEN=WIDESCREEN, presentation only): front-end screens that can be wider than 320 without stretching (WideFrontEnd):
    /// OLDIES (flat black backdrop), TITUS (flat backdrop extended), PRESENT and the press-1/2 MENU (side margins generated by rules, WideArt / WideMenuArt), the mode-select/password map
    /// (its 320-px torus backdrop repeated by rule, WideMapArt), the CARTE (window on the 640-px master) and the two end-of-level screens GAME OVER / TALLY
    /// (Kind.End: EndScene.DrawWide, edge rows / black + sprites through the margins), THE END (Kind.TheEnd: the picture has a black 24-px frame on both sides, so WideMapArt.Flat) and the
    /// attract title (Kind.Attract: WideAttractArt = period-64 strip + edge bands from the sprite-free MENU2 base, sprites continued through the margins; its first stage still fades the
    /// menu picture = Kind.Menu). The demo fade (FadeHoldScene) is not a kind: it freezes the frame of the demo level / tally, which are already wide, and keeps that width.
    /// CREATORS (640x480) stays as is. None when NO/ORIGINAL.
    /// </summary>
    WideFrontEnd.Kind WideKind =>
        !Settings.Current.WidescreenActive || Pre2Port.Recovered.LevelRender.ExtraCols == 0 ? WideFrontEnd.Kind.None
        : _theEnd is not null ? WideFrontEnd.Kind.TheEnd                  // THEEND.SQZ: black 24-px frame on both sides, flat margins (WideMapArt.Flat)
        : _creators is not null || _fade is not null ? WideFrontEnd.Kind.None   // CREATORS 640x480; the demo fade keeps the width of the frame it freezes (level / tally, both already wide)
        : _end is not null ? WideFrontEnd.Kind.End
        : _carte is not null ? WideFrontEnd.Kind.Carte
        : _level is not null ? WideFrontEnd.Kind.None
        : _menu is null ? (_intro.SceneName == "TITUS" ? WideFrontEnd.Kind.EdgeExtend : _intro.SceneName == "PRESENT" ? WideFrontEnd.Kind.Present : _intro.SceneName == "OLDIES" ? WideFrontEnd.Kind.Flat : WideFrontEnd.Kind.None)
        : _menu.Current is CarteScene ? WideFrontEnd.Kind.Carte
        : _menu.Current is MapScene ? WideFrontEnd.Kind.MapPattern
        : _menu.Current is MenuScene ? WideFrontEnd.Kind.Menu
        : _menu.Current is AttractScene at ? (at.ShowsMenu ? WideFrontEnd.Kind.Menu : WideFrontEnd.Kind.Attract)   // stage 1 still fades the menu picture; then the MENU2 title (bands + period-64 strip)
        : WideFrontEnd.Kind.None;

    Framebuffer? _narrow;   // the original 320x200 frame of a widened front-end screen

    public void Draw(Framebuffer fb)
    {
        var wk = WideKind;
        if (wk != WideFrontEnd.Kind.None && fb.Width > Screen.Width && fb.Height == Screen.Height)
        {
            _narrow ??= new Framebuffer();
            if (wk == WideFrontEnd.Kind.Carte) (_carte ?? (CarteScene)_menu!.Current).DrawWide(_narrow, fb);
            else if (wk == WideFrontEnd.Kind.Menu) { _menu!.Draw(_narrow); WideMenuArt.Menu(_narrow, fb); }
            else if (wk == WideFrontEnd.Kind.MapPattern) ((MapScene)_menu!.Current).DrawWide(_narrow, fb);
            else if (wk == WideFrontEnd.Kind.Flat) { _intro.Draw(_narrow); WideMapArt.Flat(_narrow, fb); }
            else if (wk == WideFrontEnd.Kind.End) { _end!.Alpha = Alpha; _end!.DrawWide(_narrow, fb); }
            else if (wk == WideFrontEnd.Kind.Present) { _intro.Draw(_narrow); WideArt.Present(_narrow, fb); }
            else if (wk == WideFrontEnd.Kind.TheEnd) { _theEnd!.Draw(_narrow); WideMapArt.Flat(_narrow, fb); }
            else if (wk == WideFrontEnd.Kind.Attract) { _menu!.Draw(_narrow); var at = (AttractScene)_menu.Current; WideAttractArt.Attract(_narrow, at.Base, fb, at.SpriteLayer); }
            else { _intro.Draw(_narrow); WideFrontEnd.EdgeExtend(_narrow, fb); }
            return;
        }
        if (_theEnd is not null) _theEnd.Draw(fb);
        else if (_creators is not null) _creators.Draw(fb);
        else if (_fade is not null) _fade.Draw(fb);
        else if (_end is not null) { _end.Alpha = Alpha; _end.Draw(fb); }
        else if (_carte is not null) _carte.Draw(fb);
        else if (_level is not null) { _level.Alpha = Alpha; _level.Draw(fb); }
        else if (_menu is null) _intro.Draw(fb);
        else _menu.Draw(fb);
    }
}
