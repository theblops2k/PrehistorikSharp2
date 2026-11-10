using System.Numerics;
using Pre2Port.Codecs;
using Pre2Port.Native;
using Raylib_cs;
using static Pre2Port.Game.Launcher.LauncherArt;

namespace Pre2Port.Game.Launcher;

/// <summary>
/// First-run screen shown when the original game is not in <see cref="GameData.Root"/>. States: NotFound (message + EXAMINAR / SALIR),
/// Browsing (folder dialog open), Copying (progress bar, validation, boot image generated from PRE2.EXE), Finishing (LISTO), Fatal (unrecoverable message).
/// Runs inside the normal <see cref="Presenter"/> (640x400 canvas, same 8:5 proportion as the 320x200 game, so FULLSCREEN modes scale it as usual).
/// Keyboard: arrows move the focus, ENTER / SPACE accepts; mouse: hover and click (read straight from Raylib, mapped through the same fit-scaling the Presenter uses).
/// </summary>
public sealed class LauncherScene : IScene
{
    public enum Mode { NotFound, Browsing, Copying, Finishing, Fatal }

    public double LogicHz => 60.0;
    public int FbWidth => W;
    public int FbHeight => H;

    /// <summary>Set when the copy was validated and the "LISTO" beat is over: the caller can start the game.</summary>
    public bool Ready { get; private set; }
    /// <summary>The user chose SALIR (the window is also asked to close).</summary>
    public bool ExitRequested { get; private set; }

    public Mode State => _mode;
    public string? ErrorText => _error;
    public GameInstaller? Installer => _installer;

    /// <summary>Folder dialog; replaced by tests.</summary>
    public Func<Task<string?>> Picker { get; set; } = () => FolderPicker.PickAsync("Elige la carpeta del Prehistorik 2 original");

    readonly byte[] _static;
    Mode _mode;
    int _tick;
    int _focus;                       // 0 = EXAMINAR, 1 = SALIR
    string? _error, _errorDetail;
    Task<string?>? _pick;
    GameInstaller? _installer;
    double _shown;                    // progress as displayed (eased towards the real one)
    int _finishTicks;
    string _srcFolder = "";
    readonly Flame _torchL = new(12, 22, 1), _torchR = new(12, 22, 2), _bonfire = new(20, 20, 3);
    readonly List<Ember> _embers = new();
    readonly Random _rng = new(7);
    int _mx = -1, _my = -1, _lastMx = -2, _lastMy = -2;
    bool _mouseDown, _mouseWasDown;

    /// <summary>No boot-image dependency: the image is generated from the picked folder's PRE2.EXE (or PRE2.SQZ, unpacked by the installer). <paramref name="fatalMessage"/> non-null = unrecoverable screen (SALIR only).</summary>
    public LauncherScene(string? fatalMessage = null, string? note = null)
    {
        _static = BuildStatic();
        if (fatalMessage is not null) { _mode = Mode.Fatal; _focus = 1; _error = fatalMessage; }
        else { _mode = Mode.NotFound; _focus = 0; }
        _errorDetail = note;
        // let the torches burn a moment before the first frame
        for (int i = 0; i < 40; i++) { _torchL.Step(_rng); _torchR.Step(_rng); _bonfire.Step(_rng); }
    }

    /// <summary>Show a failure on the first screen (e.g. the game could not start after the copy).</summary>
    public void Fail(string message, string? detail = null)
    {
        _mode = Mode.NotFound; _focus = 0; _error = message; _errorDetail = detail; Ready = false;
    }

    // ------------------------------------------------------------------ logic
    public void Update(Input input)
    {
        _tick++;
        _torchL.Step(_rng); _torchR.Step(_rng); _bonfire.Step(_rng);
        for (int i = _embers.Count - 1; i >= 0; i--)
        {
            var e = _embers[i];
            e.Y += e.Vy; e.X += e.Vx; e.Life--;
            if (e.Life <= 0) _embers.RemoveAt(i); else _embers[i] = e;
        }
        ReadMouse();

        switch (_mode)
        {
            case Mode.NotFound:
            case Mode.Fatal:
                UpdateButtons(input);
                break;
            case Mode.Browsing:
                if (_pick is { IsCompleted: true })
                {
                    string? path = null;
                    try { path = _pick.Result; }
                    catch (Exception e) { _error = "No se pudo abrir el selector de carpetas"; _errorDetail = e.InnerException?.Message ?? e.Message; }
                    _pick = null;
                    if (path is not null) Evaluate(path);
                    else { _mode = Mode.NotFound; }
                }
                break;
            case Mode.Copying:
                UpdateCopy();
                break;
            case Mode.Finishing:
                if (++_finishTicks >= 50) Ready = true;
                break;
        }
    }

    void UpdateButtons(Input input)
    {
        bool fatal = _mode == Mode.Fatal;
        if (!fatal)
        {
            if (input.IsKeyPressed(Input.Key.Left) || input.IsKeyPressed(Input.Key.Up)) _focus = 0;
            if (input.IsKeyPressed(Input.Key.Right) || input.IsKeyPressed(Input.Key.Down)) _focus = 1;
        }
        else _focus = 1;

        int hover = HoverButton();
        if (hover >= 0 && (_mx != _lastMx || _my != _lastMy)) _focus = hover;
        bool click = _mouseDown && !_mouseWasDown && hover >= 0;
        if (click) _focus = hover;
        if (click || input.IsKeyPressed(Input.Key.Enter) || input.IsKeyPressed(Input.Key.Space))
        {
            if (_focus == 1) { ExitRequested = true; QuitWindow(); }
            else BeginBrowse();
        }
    }

    static void QuitWindow()
    {
        Presenter.QuitRequested = true;
    }

    void BeginBrowse()
    {
        _error = null; _errorDetail = null;
        try
        {
            _pick = Picker();
            _mode = Mode.Browsing;
        }
        catch (Exception e)
        {
            _error = "No se pudo abrir el selector de carpetas"; _errorDetail = e.Message;
        }
    }

    /// <summary>Check the picked folder and start the copy (or show what is missing).</summary>
    public void Evaluate(string picked)
    {
        string folder = GameInstaller.FindGameFolder(picked, out var missing);
        if (missing.Count > 0)
        {
            _mode = Mode.NotFound; _focus = 0;
            _error = "Esa carpeta no tiene el juego";
            _errorDetail = "Falta: " + string.Join(", ", missing.Select(m => m == GameData.ExeName ? GameData.ExeName + " o " + GameData.ExeSqzName : m).Take(3)) + (missing.Count > 3 ? $" (+{missing.Count - 3})" : "");
            return;
        }
        _error = null; _errorDetail = null;
        _srcFolder = folder;
        _shown = 0;
        _installer = new GameInstaller();
        _installer.Start(folder, GameData.Root);
        _mode = Mode.Copying;
    }

    void UpdateCopy()
    {
        var inst = _installer!;
        double real = inst.Progress;
        if (inst.State == GameInstaller.Phase.Failed)
        {
            _mode = Mode.NotFound; _focus = 0;
            _error = "No se pudo copiar el juego";
            _errorDetail = inst.Error;
            return;
        }
        // eased display, at most 1/80 per tick (the copy of ~1 MB takes milliseconds; this keeps the bar readable)
        double step = Math.Clamp((real - _shown) * 0.12, 0, 1.0 / 80);
        if (real > _shown) _shown = Math.Min(real, _shown + Math.Max(step, 1.0 / 400));
        if (_shown > 0.01 && _shown < 1)
        {
            int headX = 120 + (int)(_shown * 400);
            for (int k = 0; k < 2; k++)
                _embers.Add(new Ember { X = headX - 1 + _rng.Next(3), Y = 288 + _rng.Next(10), Vx = (float)(_rng.NextDouble() - 0.5) * 0.7f, Vy = -0.6f - (float)_rng.NextDouble() * 0.9f, Life = 18 + _rng.Next(22) });
        }
        if (inst.State == GameInstaller.Phase.Done && _shown >= 0.999)
        {
            _shown = 1;
            _mode = Mode.Finishing; _finishTicks = 0;
        }
    }

    // ------------------------------------------------------------------ mouse
    void ReadMouse()
    {
        _lastMx = _mx; _lastMy = _my;
        _mouseWasDown = _mouseDown;
        _mx = _my = -1; _mouseDown = false;
        if (!Raylib.IsWindowReady()) return;
        Vector2 p = Raylib.GetMousePosition();
        float sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        if (sw <= 0 || sh <= 0) return;
        float k = MathF.Min(sw / W, sh / H);
        float ox = (sw - W * k) / 2f, oy = (sh - H * k) / 2f;
        _mx = (int)MathF.Floor((p.X - ox) / k);
        _my = (int)MathF.Floor((p.Y - oy) / k);
        _mouseDown = Raylib.IsMouseButtonDown(MouseButton.Left);
    }

    // Button geometry
    const int BtnY = 262, BtnW = 180, BtnH = 44;
    static (int X, int Y) BtnPos(int i, bool single) => single ? ((W - BtnW) / 2, BtnY) : (i == 0 ? 120 : 340, BtnY);

    int HoverButton()
    {
        bool single = _mode == Mode.Fatal;
        for (int i = single ? 1 : 0; i < 2; i++)
        {
            var (x, y) = BtnPos(i, single);
            if (_mx >= x && _mx < x + BtnW && _my >= y && _my < y + BtnH) return i;
        }
        return -1;
    }

    // ------------------------------------------------------------------ drawing
    public void Draw(Framebuffer fb)
    {
        ApplyPalette(fb);
        Array.Copy(_static, fb.Pixels, Math.Min(_static.Length, fb.Pixels.Length));
        _torchL.Draw(fb, 40 - 24, 226, 4);
        _torchR.Draw(fb, W - 40 - 24, 226, 4);
        // light spill of the torches on the slab edges is implicit in the dither; embers drawn last

        switch (_mode)
        {
            case Mode.NotFound: DrawNotFound(fb); break;
            case Mode.Fatal: DrawNotFound(fb); break;
            case Mode.Browsing: DrawBrowsing(fb); break;
            case Mode.Copying: DrawCopying(fb); break;
            case Mode.Finishing: DrawFinishing(fb); break;
        }
        foreach (var e in _embers)
            fb.FillRect((int)e.X, (int)e.Y, 2, 2, F(15 - Math.Clamp((22 - e.Life) / 2, 0, 9)));
    }

    static string Fit(string s, int maxChars, int scale)
    {
        // maxChars computed by the caller from the pixel width
        if (s.Length <= maxChars) return s;
        return "..." + s[^(maxChars - 3)..];
    }

    void DrawNotFound(Framebuffer fb)
    {
        bool fatal = _mode == Mode.Fatal;
        string head = fatal ? "NO SE PUEDE INICIAR" : "NO SE ENCONTRÓ EL JUEGO";
        TextCentered(fb, head, W / 2, 144, 3, fatal ? B(7) : S(14));
        if (!fatal)
        {
            TextCentered(fb, "ELIGE LA CARPETA DEL", W / 2, 186, 2, S(12));
            TextCentered(fb, "PREHISTORIK 2 ORIGINAL", W / 2, 204, 2, S(12));
        }
        if (_error is not null)
        {
            // error banner (replaces the hint line)
            TextCentered(fb, _error.ToUpperInvariant(), W / 2, fatal ? 190 : 228, 2, B(7));
            if (_errorDetail is not null) TextCentered(fb, Fit(_errorDetail.ToUpperInvariant(), 66, 1), W / 2, fatal ? 214 : 248, 1, B(7));
        }
        else
        {
            TextCentered(fb, "SE COPIARÁN A LA CARPETA " + GameData.FolderName.ToUpperInvariant(), W / 2, 232, 1, S(9));
        }
        var (bx0, by0) = BtnPos(0, fatal);
        int hover = HoverButton();
        if (!fatal) Button(fb, bx0, by0, BtnW, BtnH, "EXAMINAR", _focus == 0, hover == 0 && _mouseDown, true, _tick);
        var (bx1, by1) = BtnPos(1, fatal);
        Button(fb, bx1, by1, BtnW, BtnH, "SALIR", _focus == 1, hover == 1 && _mouseDown, true, _tick);
        string hint = fatal ? "ENTER: SALIR" : "< > ELEGIR     ENTER ACEPTAR     ESC SALIR";
        TextCentered(fb, hint, W / 2, 328, 1, S(8));
    }

    void DrawBrowsing(Framebuffer fb)
    {
        TextCentered(fb, "ELIGE LA CARPETA", W / 2, 150, 3, S(14));
        TextCentered(fb, "EN LA VENTANA QUE SE ABRIÓ", W / 2, 192, 2, S(12));
        // three "dots" bouncing like a torch flicker
        int n = (_tick / 14) % 4;
        string dots = new string('.', n);
        TextCentered(fb, "ESPERANDO" + dots.PadRight(3), W / 2 + 0, 232, 2, F(12));
        Button(fb, 120, BtnY, BtnW, BtnH, "EXAMINAR", false, false, false, _tick);
        Button(fb, 340, BtnY, BtnW, BtnH, "SALIR", false, false, false, _tick);
        TextCentered(fb, "CANCELA LA VENTANA PARA VOLVER", W / 2, 328, 1, S(8));
    }

    void DrawCopying(Framebuffer fb)
    {
        // clean progress bar: launcher primitives + own font only, no game art
        var inst = _installer!;
        TextCentered(fb, "COPIANDO EL JUEGO", W / 2, 150, 3, S(14));
        string cur = inst.CurrentFile;
        int done = inst.FilesDone;
        string count = $"{Math.Min(done + (cur.Length > 0 ? 1 : 0), inst.FilesTotal)}/{inst.FilesTotal}";
        Text(fb, cur.Length > 0 ? cur : "...", 120, 214, 2, S(13));
        Text(fb, count, 520 - TextWidth(count, 2), 214, 2, S(11));
        ProgressBar(fb, 120, 238, 400, 22, _shown, _tick);
        string pct = $"{(int)Math.Round(_shown * 100)}%";
        TextCentered(fb, pct, W / 2, 242, 2, S(15));
        double mb = inst.BytesTotal / 1048576.0;
        TextCentered(fb, $"{mb:F1} MB   COPIA Y VALIDACION", W / 2, 286, 1, S(9));
    }

    void DrawFinishing(Framebuffer fb)
    {
        TextCentered(fb, "LISTO", W / 2, 150, 4, M(7));
        TextCentered(fb, "JUEGO COPIADO Y VALIDADO", W / 2, 204, 2, S(13));
        ProgressBar(fb, 120, 250, 400, 22, 1.0, _tick);
        TextCentered(fb, "INICIANDO...", W / 2, 296, 2, F(12));
    }

    // ------------------------------------------------------------------ helpers
    struct Ember { public float X, Y, Vx, Vy; public int Life; }

    /// <summary>Classic fire: heat diffuses upwards from a hot bottom row and cools; rendered with the fire palette.</summary>
    sealed class Flame
    {
        readonly int _w, _h;
        readonly float[] _heat;
        public Flame(int w, int h, int seed) { _w = w; _h = h; _heat = new float[w * h]; }

        public void Step(Random rng)
        {
            for (int y = 0; y < _h - 1; y++)
                for (int x = 0; x < _w; x++)
                {
                    int xl = Math.Max(0, x - 1), xr = Math.Min(_w - 1, x + 1);
                    int y2 = Math.Min(_h - 1, y + 2);
                    float v = (_heat[(y + 1) * _w + xl] + _heat[(y + 1) * _w + x] + _heat[(y + 1) * _w + xr] + _heat[y2 * _w + x]) * 0.25f;
                    v -= 0.035f + (float)rng.NextDouble() * 0.045f;
                    _heat[y * _w + x] = Math.Max(0, v);
                }
            for (int x = 0; x < _w; x++)
            {
                float c = 1f - Math.Abs((x + 0.5f) / _w * 2f - 1f);       // 0 edge .. 1 centre
                _heat[(_h - 1) * _w + x] = Math.Clamp(c * 1.5f - 0.25f + (float)rng.NextDouble() * 0.35f, 0f, 1f);
            }
        }

        public void Draw(Framebuffer fb, int ox, int baseY, int cell, float limit = 1f)
        {
            for (int y = 0; y < _h; y++)
                for (int x = 0; x < _w; x++)
                {
                    int c = (int)(_heat[y * _w + x] * 15.99f);
                    if (c < 3) continue;
                    fb.FillRect(ox + x * cell, baseY - (_h - y) * cell, cell, cell, F(c));
                }
        }
    }
}
