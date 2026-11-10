using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Level;

/// <summary>
/// End screen shown by the host after <see cref="LevelScene.Outcome"/> is set: the level-end tally (SCORE / LEVEL COMPLETED %) or GAME OVER.
/// Static mode (the headless reference verification): one frame, done on fire (Space/Enter) or after <see cref="HoldTicks"/> ticks. The hold time is the
/// game-over scene's own timeout ([asm 9C74] [0x27F0] >= 0x276 timer ticks, +3 per scene iteration = 0x276/3 = 210 iterations at the default 70/3 Hz logic
/// rate = 9 s); the Python tally has no fixed wait, so it reuses the same constant. Nothing moves: no interpolation.
/// Animated tally (<c>animated: true</c>): [asm 316F/31F4] the iris close over the frozen level frame, then the real cutscene 4CCB..4F5E (<see cref="TallyScene"/>): walk-in, food slide-in, throw, score count-up, walk-off; it cannot
/// be skipped (the ASM loops read no input) and ends by itself; the logic rate follows the original pacing (70/3 Hz, 70 Hz during the count-up).
/// </summary>
public sealed class EndScene : IScene
{
    public const int HoldTicks = 0x276 / 3;
    readonly LevelState _s;
    readonly HudAssets? _hud;
    readonly byte[]? _asset;

    readonly bool _animated;
    IEnumerator<int>? _tally;
    int _period = TallyScene.WalkVsyncs;
    bool _post;

    Iris? _iris;                                   // [asm 316F/31F4] level-end iris close, before the tally
    byte[][]? _irisPlanes;
    int _irisPage;
    byte[]? _tallyDac;                             // the DAC 6772 left when the iris closed: the original never rewrites it during the tally
    LevelState? _prev;                             // tally: the state of the frame drawn last (presentation interpolation)

    /// <summary>Presentation interpolation factor (0..1, default 1 = exact tick image). Display only; the tally's logic rate is unchanged.</summary>
    public double Alpha { get; set; } = 1.0;

    /// <param name="animated">Game over: the real 9B23 scene (scroll, letters, caveman, birds, HUD) instead of the static final frame used by the reference verification. Tally: the 4CCB cutscene instead of the static panel.</param>
    public EndScene(LevelState finished, string originalDir, bool gameOver, bool animated = false)
    {
        _s = finished;
        GameOver = gameOver;
        _animated = animated && gameOver;
        if (gameOver) _asset = EndScreens.LoadGameOverAsset(finished);
        if (!gameOver || _animated) _hud = HudAssets.Load(originalDir);
        if (_animated) GameOverScene.Setup(_s);
        if (animated && !gameOver)
        {
            // [asm 316F] freeze the level frame and start the iris close; the 4CCB tally setup runs when it ends.
            TallyScene.ArmLightFade(_s);                      // [4CCB-4CDC] lights off -> ramp back to the level palette, one 6772 step per iris frame
            _iris = Iris.Setup(_s);
            _irisPlanes = LevelRender.RenderPlanes(_s, out _irisPage, player: true, allRecords: true);
            Hud.DrawStatusBar(_irisPlanes, _irisPage, _hud!.Bar);
            Hud.DrawHud(_irisPlanes, Hud.ReadForDraw(_s), _hud.Font, _irisPage);
        }
    }

    /// <summary>Audio output for the effects the cutscene plays (the count-up's sfx 8); null = silent.</summary>
    public Pre2Port.Game.Frontend.IGameAudio? Audio { get; init; }

    public bool GameOver { get; }
    public int Ticks { get; private set; }
    public bool Done { get; private set; }
    public string SceneName => GameOver ? "GAMEOVER" : "TALLY";

    /// <summary>70/3 Hz (3 timer ticks per iteration, 44FB); the tally count-up iterations wait one vsync (4509 + 44CD) = 70 Hz.</summary>
    public double LogicHz => _iris is not null ? 70.0 / 3.0 : _tally is not null ? 70.0 / _period : 70.0 / 3.0;

    public void Update(Input input)
    {
        Ticks++;
        if (_iris is not null)
        {
            if (_iris.Closed)                                 // the closed frame was drawn on the previous tick
            {
                _tallyDac = _iris.Dac;                        // [4CE1->32CE] the DAC stays as the last 6772 left it
                _iris = null;
                _prev = null;                                 // [4CCB] clear + recentre: no interpolation across the iris -> cutscene
                TallyScene.Setup(_s);                         // [4CCB-4D3B] object clear + recentre, once the screen is black
                _tally = TallyScene.Run(_s).GetEnumerator();
            }
            else _iris.Compose(_s, _irisPlanes!, _irisPage);
            return;
        }
        if (_tally is not null)
        {
            if (_post)                                                // [26FA] tail of the frame drawn last
            {
                if (_prev == null) _prev = new LevelState(_s.Data, _s.OriginalDir);
                else Array.Copy(_s.Data, _prev.Data, _s.Data.Length);
                Player.PostFrame(_s);
            }
            _post = true;
            if (_tally.MoveNext()) _period = _tally.Current;
            else Done = true;                                         // [4F54-4F5E] cutscene over -> next level
            if (_s.SfxQueue.Count > 0)
            {
                if (Audio != null) foreach (int id in _s.SfxQueue) Audio.PlaySfx(id);
                _s.SfxQueue.Clear();
            }
            return;
        }
        if (_animated)
        {
            if (_post) Player.PostFrame(_s);                          // [9C68] 29FA of the frame drawn last ([0x6BD5]++, record mutation)
            GameOverScene.Tick(_s);                                   // [9C62] 9CC0
            _post = true;
        }
        if (input.IsKeyPressed(Input.Key.Space) || input.IsKeyPressed(Input.Key.Enter) || Ticks >= HoldTicks) Done = true;
    }

    /// <summary>
    /// Port-only (FULLSCREEN=WIDESCREEN, presentation only): the same frame <see cref="Draw"/> gives, centred in the wider <paramref name="wide"/>.
    /// GAME OVER: the diorama's edge columns are black (measured) so each row's edge pixel is extended (the status bar repeats its edge column, like the
    /// level HUD: left-aligned at x = 0, right margin = bar edge column, as LevelRender.DrawWide); the letters/birds stay centred. TALLY: black margins plus the sprite records seen through the margins (WideEnd). No scene logic is touched.
    /// </summary>
    public void DrawWide(Framebuffer narrow, Framebuffer wide)
    {
        Draw(narrow);
        if (GameOver) Pre2Port.Game.Frontend.WideEnd.GameOver(narrow, wide);
        else Pre2Port.Game.Frontend.WideEnd.Tally(_s, narrow, wide, _tally is not null, _prev, Alpha);
    }

    public void Draw(Framebuffer fb)
    {
        if (_iris is not null) { LevelRender.Present(_s, _irisPlanes!, _irisPage, fb, Screen.Height); if (_iris.Dac != null) fb.SetPalette6(_iris.Dac, 0, 0, 16); }
        else if (_tally is not null) TallyScene.Draw(_s, _hud!, fb, _tallyDac, _prev, Alpha);
        else if (_animated) GameOverScene.Draw(_s, _asset!, _hud!, fb);
        else if (GameOver) EndScreens.DrawGameOver(_s, _asset!, fb);
        else EndScreens.DrawTally(_s, _hud!, fb);
    }
}
