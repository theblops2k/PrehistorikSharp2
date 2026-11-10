using Pre2Port.Native;

namespace Pre2Port.Game.Launcher;

/// <summary>
/// Entry scene of the double-click start when the game data is not complete: shows the <see cref="LauncherScene"/> and, once the copy is
/// validated, builds the game (front-end runner + audio, reading from <see cref="GameData.Root"/>) and keeps running in the SAME window
/// and Presenter, so the FULLSCREEN mode and the scaling stay exactly as in the game.
/// </summary>
public sealed class StartupScene : IScene, IDisposable
{
    readonly LauncherScene _launcher;
    readonly Func<(IScene Game, IDisposable? Owned)> _factory;
    IScene? _game;
    IDisposable? _owned;
    bool _paletteReset;

    public StartupScene(LauncherScene launcher, Func<(IScene Game, IDisposable? Owned)> factory)
    {
        _launcher = launcher;
        _factory = factory;
    }

    IScene Current => _game ?? _launcher;

    public double LogicHz => Current.LogicHz;
    public double Alpha { get => Current.Alpha; set => Current.Alpha = value; }
    public int FbWidth => Current.FbWidth;
    public int FbHeight => Current.FbHeight;
    public bool GameplayKeys => Current.GameplayKeys;
    public bool CanReturnToMainMenu => _game is not null && _game.CanReturnToMainMenu;
    public bool InMainMenu => _game is not null && _game.InMainMenu;
    public void ReturnToMainMenu() => _game?.ReturnToMainMenu();

    public void Update(Input input)
    {
        if (_game is not null) { _game.Update(input); return; }
        _launcher.Update(input);
        if (!_launcher.Ready) return;
        try
        {
            (_game, _owned) = _factory();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"launcher: could not start the game: {e}");
            _launcher.Fail("No se pudo iniciar el juego", e.Message);
        }
    }

    public void Draw(Framebuffer fb)
    {
        if (_game is null) { _launcher.Draw(fb); return; }
        if (!_paletteReset)
        {
            fb.SetPalette(Vga.DefaultPalette());   // the launcher palette must not leak into the game's first frames
            _paletteReset = true;
        }
        _game.Draw(fb);
    }

    public void Dispose() => _owned?.Dispose();
}

/// <summary>Builds the launcher flow for the no-argument start.</summary>
public static class LauncherEntry
{
    /// <summary>The boot image is not shipped: the launcher copies the game (incl. PRE2.EXE, or unpacks it from PRE2.SQZ) to <see cref="GameData.Root"/> and generates <c>files/boot_image.bin</c> from it.</summary>
    public static int Run()
    {
        var launcher = new LauncherScene();
        using var startup = new StartupScene(launcher, () =>
        {
            byte[] boot = BootImage.Load(GameData.BootImagePath);
            var audio = new Pre2Port.Game.Frontend.RaylibGameAudio(boot, GameData.Root);
            try
            {
                var runner = new Pre2Port.Game.Frontend.FrontEndRunner(boot, GameData.Root, audio);
                return (runner, audio);
            }
            catch { audio.Dispose(); throw; }
        });
        var presenter = new Presenter { RenderHz = Settings.Current.Fps, Interpolate = true, Title = "Prehistorik 2 PC" };
        return presenter.Run(startup);
    }
}
