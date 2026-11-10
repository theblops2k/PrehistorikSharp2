using System.Numerics;
using Pre2Port.Game.Frontend;
using Pre2Port.Game.Options;
using Raylib_cs;

namespace Pre2Port.Game;

/// <summary>
/// Window + loop shared by all scenes: 960x600 window (3x) or fullscreen per <see cref="Settings"/>, Point filter,
/// the 320x200 image scaled to fit the window (aspect kept, centred), redraw at RenderHz (60) paced with
/// <see cref="Raylib.GetTime"/>; logic ticks run at the scene's LogicHz from a real-time accumulator.
/// ESC (or the pad's Start in joystick mode) no longer quits: it pauses the scene (no more ticks) and opens the <see cref="OptionsMenu"/> overlay on top of it.
/// Keys / pad buttons reach the scenes through <see cref="InputMapper"/> (configurable controls), and the presented image goes through <see cref="PostFilters"/>.
/// </summary>
public sealed class Presenter
{
    /// <summary>Window size multiplier when <c>FULLSCREEN=NO</c>.</summary>
    public const int WindowScale = 3;
    /// <summary>Window redraw rate in Hz (default from <c>FPS</c> in the cfg). Logic runs at each scene's <see cref="IScene.LogicHz"/>.</summary>
    public double RenderHz { get; set; } = Settings.Current.Fps;

    /// <summary>Debug: false (--no-interp) presents the plain tick image (alpha always 1).</summary>
    public bool Interpolate { get; init; } = true;

    static readonly KeyboardKey[] NavKeys =
    {
        KeyboardKey.Up, KeyboardKey.Down, KeyboardKey.Left, KeyboardKey.Right, KeyboardKey.Kp8, KeyboardKey.Kp2, KeyboardKey.Kp4, KeyboardKey.Kp6,
        KeyboardKey.Enter, KeyboardKey.KpEnter, KeyboardKey.Space,
    };

    /// <summary>Stop after this many LOGIC ticks (0 = run until the window closes / Exit to Windows).</summary>
    public int MaxFrames { get; init; }

    /// <summary>Absolute PNG path to write at tick <see cref="CaptureFrame"/> (1-based). Null = no capture.</summary>
    public string? CapturePath { get; init; }

    /// <summary>1-based tick at which to capture; 0 means the last tick (MaxFrames).</summary>
    public int CaptureFrame { get; init; }

    public string Title { get; init; } = "Prehistorik 2 PC";

    /// <summary>A scene asks the loop to end (the launcher's SALIR button, the overlay's Exit to Windows); same effect as closing the window.</summary>
    public static volatile bool QuitRequested;

    /// <summary>Ticks executed by the last <see cref="Run"/>.</summary>
    public int TicksRun { get; private set; }

    /// <summary>0 on success, 1 if a requested capture failed or never happened.</summary>
    public int Run(IScene scene)
    {
        QuitRequested = false;
        var fb = new Framebuffer();
        var input = new Input();
        var rgba = new byte[fb.Width * fb.Height * 4];
        int rc = 0;
        int captureAt = CapturePath is null ? -1 : (CaptureFrame > 0 ? CaptureFrame : MaxFrames);
        bool captured = false;

        Raylib.SetTraceLogLevel(TraceLogLevel.Warning);
        // NO: fixed 3x window (not resizable), canvas 320x200. ORIGINAL / WIDESCREEN: fullscreen (widescreen canvas only in WIDESCREEN)
        Raylib.InitWindow(fb.Width * WindowScale, fb.Height * WindowScale, Title);
        bool isFullscreen = Settings.Current.Fullscreen;
        if (isFullscreen) Raylib.ToggleFullscreen();   // window becomes the monitor size
        Raylib.SetExitKey(KeyboardKey.Null);   // ESC opens the overlay (handled below); the loop exits on window close / QuitRequested.

        Image img = Raylib.GenImageColor(fb.Width, fb.Height, Color.Black);   // R8G8B8A8
        Texture2D tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        Raylib.SetTextureFilter(tex, TextureFilter.Point);

        // Widescreen level canvas follows the real proportion of what is drawn on: fullscreen = the screen, windowed = the window itself
        // (GetScreenWidth/Height; the windowed mode is user-resizable, so the canvas follows the window shape; never the monitor).
        void UpdateAspect()
        {
            if (!Settings.Current.WidescreenActive) return;   // NO / ORIGINAL: nothing, aspect stays 320/200
            double aw = Raylib.GetScreenWidth(), ah = Raylib.GetScreenHeight();
            if (aw > 0 && ah > 0) Pre2Port.Recovered.LevelRender.TargetAspect = aw / ah;
            string f = Environment.GetEnvironmentVariable("PRE2_ASPECT");   // TEST TOOL (see docs/hallazgos-port.md): forces the proportion, e.g. 1.7778; unset = normal game
            if (double.TryParse(f, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fa) && fa > 0)
                Pre2Port.Recovered.LevelRender.TargetAspect = fa;
        }
        UpdateAspect();

        // ---- options overlay + controls ----
        double lastFps = 0;
        var opt = GameOptions.Current;
        var cfg = Settings.Current;
        var cc = ControllerConfig.Current;
        var menu = new OptionsMenu(opt, cfg, cc);
        var view = new OptionsMenuView();
        bool fpsChanged = false;
        void ApplyVsync(bool on) { if (on) Raylib.SetWindowState(ConfigFlags.VSyncHint); else Raylib.ClearWindowState(ConfigFlags.VSyncHint); fpsChanged = true; }   // rebase the frame schedule too
        ApplyVsync(cfg.Vsync);                                            // Vsync=T/F in Prehistorik2.cfg (default F)
        menu.OnFps = f => { RenderHz = f; fpsChanged = true; };            // Max FPS applies at once
        menu.OnVsync = ApplyVsync;                                        // V-Sync applies at once
        menu.OnVolume = v => RaylibGameAudio.MasterVolume = v / 100f;       // Volume applies at once
        menu.OnMode = m =>
        {
            bool want = m != ScreenMode.No;
            if (want != isFullscreen)
            {
                Raylib.ToggleFullscreen();
                isFullscreen = want;
                if (!want) Raylib.SetWindowSize(Screen.Width * WindowScale, Screen.Height * WindowScale);
                UpdateAspect();
                ApplyVsync(cfg.Vsync);                                    // the window state survives the fullscreen toggle
            }
        };
        menu.CanReturnToMainMenu = () => scene.CanReturnToMainMenu;
        menu.InMainMenu = () => scene.InMainMenu;
        var mapper = new InputMapper(cc);
        var pending = new HashSet<KeyboardKey>();   // presses seen by a poll but not yet consumed by a tick
        var padSrc = new RaylibInputSource(pending);
        var ovPressed = new HashSet<KeyboardKey>(); // presses latched while the overlay is open (one entry = one physical press)
        bool escLatched = false;
        var allKeys = Enum.GetValues<KeyboardKey>().Where(k => k != KeyboardKey.Null).ToArray();
        bool holdGate = false;                      // after the overlay closes, keys held at that moment are ignored until released
        double frozenAlpha = 1.0;
        bool wasFiltered = false;

        void Present()
        {
            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Black);
            float sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
            float k = MathF.Min(sw / fb.Width, sh / fb.Height);   // fit, aspect kept
            float dw = fb.Width * k, dh = fb.Height * k;
            var src = new Rectangle(0, 0, fb.Width, fb.Height);
            var dst = new Rectangle((sw - dw) / 2f, (sh - dh) / 2f, dw, dh);
            if (PostFilters.Any(cfg)) { PostFilters.Present(tex, dst, Color.White, cfg); wasFiltered = true; }
            else
            {
                if (wasFiltered) { Raylib.SetTextureFilter(tex, TextureFilter.Point); wasFiltered = false; }
                Raylib.DrawTexturePro(tex, src, dst, Vector2.Zero, 0f, Color.White);
            }
            // FPS counter (Show FPS): top-left of the image, only in fullscreen ORIGINAL / WIDESCREEN, never in the NO window
            if (cfg.Fullscreen && opt.ShowFps) OptionsMenuView.DrawFps(lastFps, dst, (int)sh);
            if (menu.IsOpen) view.Draw(menu, (int)sw, (int)sh, Raylib.GetTime(), padHints: cc.JoystickActive);
            Raylib.EndDrawing();   // also polls input events
        }

        // Latch presses between polls: the game's keys for the next tick, plus (overlay open) the menu's keys; ESC is always watched.
        void LatchPressed()
        {
            foreach (var (_, rk) in InputMapper.KeyMap)
                if (Raylib.IsKeyPressed(rk)) pending.Add(rk);
            foreach (var rk in cc.Keys)
                if (Raylib.IsKeyPressed(rk)) pending.Add(rk);
            if (Raylib.IsKeyPressed(KeyboardKey.Escape)) escLatched = true;
            if (menu.IsOpen)
            {
                if (menu.Capturing) { foreach (var k in allKeys) if (Raylib.IsKeyPressed(k)) ovPressed.Add(k); }
                else foreach (var k in NavKeys) if (Raylib.IsKeyPressed(k)) ovPressed.Add(k);
            }
        }

        bool AnyInputDown()
        {
            foreach (var k in allKeys) if (Raylib.IsKeyDown(k)) return true;
            for (int g = 0; g < 4; g++)
                if (Raylib.IsGamepadAvailable(g))
                {
                    for (int b = 1; b <= 17; b++) if (Raylib.IsGamepadButtonDown(g, (GamepadButton)b)) return true;
                    if (Math.Abs(Raylib.GetGamepadAxisMovement(g, GamepadAxis.LeftX)) > 0.5f || Math.Abs(Raylib.GetGamepadAxisMovement(g, GamepadAxis.LeftY)) > 0.5f) return true;
                }
            return false;
        }

        // Overlay input: one physical press = one action (edge detection on keys, pad buttons and the stick, plus a 60 ms lockout per action against duplicated events).
        var padPrev = new bool[24];   // 0..3 stick/d-pad up,down,left,right; 4 Start; 5 Fire; 6 Jump; 7..23 buttons 1..17
        var lastAct = new double[6];
        bool Edge(int slot, bool now)
        {
            bool e = now && !padPrev[slot];
            padPrev[slot] = now;
            return e;
        }
        bool Debounce(int act, bool v, double t)
        {
            if (!v) return false;
            if (t - lastAct[act] < 0.06) return false;
            lastAct[act] = t;
            return true;
        }
        bool StartPressed()
        {
            if (!cc.JoystickActive) { padPrev[4] = false; return false; }
            return Edge(4, Raylib.IsGamepadButtonDown(cc.JoystickIndex, GamepadButton.MiddleRight));
        }
        MenuInput BuildMenuInput(double t)
        {
            var mi = new MenuInput();
            bool P(KeyboardKey k) => ovPressed.Remove(k);
            bool up = P(KeyboardKey.Up) | P(KeyboardKey.Kp8), down = P(KeyboardKey.Down) | P(KeyboardKey.Kp2);
            bool left = P(KeyboardKey.Left) | P(KeyboardKey.Kp4), right = P(KeyboardKey.Right) | P(KeyboardKey.Kp6);
            bool sel = P(KeyboardKey.Enter) | P(KeyboardKey.Space) | P(KeyboardKey.KpEnter), back = escLatched;
            escLatched = false;
            if (menu.Capturing)
            {
                foreach (var k in ovPressed) { mi.Key = k; break; }
                ovPressed.Clear();
                if (mi.Key == KeyboardKey.Escape) back = true;
            }
            if (cc.JoystickActive)
            {
                int g = cc.JoystickIndex;
                float ax = Raylib.GetGamepadAxisMovement(g, GamepadAxis.LeftX), ay = Raylib.GetGamepadAxisMovement(g, GamepadAxis.LeftY);
                up |= Edge(0, ay < -0.5f || Raylib.IsGamepadButtonDown(g, GamepadButton.LeftFaceUp));
                down |= Edge(1, ay > 0.5f || Raylib.IsGamepadButtonDown(g, GamepadButton.LeftFaceDown));
                left |= Edge(2, ax < -0.5f || Raylib.IsGamepadButtonDown(g, GamepadButton.LeftFaceLeft));
                right |= Edge(3, ax > 0.5f || Raylib.IsGamepadButtonDown(g, GamepadButton.LeftFaceRight));
                bool fireEdge = Edge(5, Raylib.IsGamepadButtonDown(g, cc.PadFire)), jumpEdge = Edge(6, Raylib.IsGamepadButtonDown(g, cc.PadJump));
                if (menu.Capturing)
                {
                    for (int b = 1; b <= 17; b++)   // any new button is a candidate (the d-pad is rejected by the menu)
                        if (Edge(6 + b, Raylib.IsGamepadButtonDown(g, (GamepadButton)b)) && mi.Pad == GamepadButton.Unknown) mi.Pad = (GamepadButton)b;
                }
                else { sel |= fireEdge; back |= jumpEdge; }
            }
            mi.Up = Debounce(0, up, t); mi.Down = Debounce(1, down, t); mi.Left = Debounce(2, left, t); mi.Right = Debounce(3, right, t);
            mi.Select = Debounce(4, sel, t); mi.Back = Debounce(5, back, t);
            mi.AnyDown = AnyInputDown();
            return mi;
        }

        double renderStart = Raylib.GetTime();
        double logicStart = renderStart;
        double hz = scene.LogicHz;
        int tick = 0;      // logic ticks done
        int frame = 0;     // render frames done
        int fpsFrames = 0; // frames presented in the current FPS-title interval
        double fpsStart = renderStart;
        double prevAlpha = 1.0;
        while (!Raylib.WindowShouldClose() && !QuitRequested)
        {
            double now = Raylib.GetTime();

            // ---- ESC overlay: open / drive / close. While it is open the scene is paused (no ticks) and the clock is rebased every frame.
            if (menu.IsOpen)
            {
                menu.Step(BuildMenuInput(now));
                var req = menu.TakeRequest();
                if (req == OptionsMenu.Request.ExitToWindows) QuitRequested = true;
                else if (req == OptionsMenu.Request.ReturnToMainMenu && scene.CanReturnToMainMenu) scene.ReturnToMainMenu();
                if (!menu.IsOpen)   // closed: swallow the keys still held so the press that closed it does not reach the game
                {
                    holdGate = true;
                    pending.Clear(); ovPressed.Clear(); escLatched = false;
                    lastAct = new double[6];
                }
            }
            else
            {
                bool open = escLatched | StartPressed();
                escLatched = false;
                if (open)
                {
                    menu.Open();
                    ovPressed.Clear(); pending.Clear();
                    Array.Clear(padPrev);
                    frozenAlpha = prevAlpha;
                }
            }
            bool paused = menu.IsOpen;
            if (fpsChanged) { fpsChanged = false; renderStart = Raylib.GetTime() - frame / RenderHz; }

            if (scene.LogicHz != hz || paused)   // scene changed its rate (e.g. menu -> level) or the overlay is open: keep ticks done, rebase the clock
            {
                hz = scene.LogicHz;
                logicStart = now - tick / hz;
            }
            // Ticks owed by real time (tick 0 is due at t=0, so the first frame already has an Update behind it).
            long due = paused ? 0 : FrameClock.Due(now, logicStart, hz, tick);
            // Far behind (window drag, debugger): resync instead of bursting ticks.
            if (due > 0.25 * hz)
            {
                logicStart = now - tick / hz;
                due = 1;
            }
            int stopAt = MaxFrames > 0 ? MaxFrames : int.MaxValue;   // never run past the requested tick count
            while (due-- > 0 && tick < stopAt)
            {
                // Sample right before Update: fresh events, plus presses latched since the last tick (one 'pressed' per tick).
                Raylib.PollInputEvents();
                LatchPressed();
                if (holdGate)
                {
                    if (!AnyInputDown()) holdGate = false;
                    pending.Clear();
                    for (int ki = 0; ki < Input.KeyCount; ki++) input.Set((Input.Key)ki, false, false);
                }
                else
                {
                    mapper.Fill(input, padSrc, raw: !scene.GameplayKeys);
                    pending.Clear();
                }
                if (escLatched) break;   // ESC pressed: stop ticking, the overlay opens at the top of the next frame
                scene.Update(input);
                tick++;
                if (tick == captureAt) break;   // present exactly this tick before capturing
            }

            // Presentation alpha: fraction of the logic period elapsed since the last done tick (tick-1 was due at logicStart + (tick-1)/hz).
            // Forced to 1 when the run stops/captures at an exact tick so the image is the plain tick image. Frozen while the overlay is open.
            UpdateAspect();   // a change of screen / mode rebuilds the canvas (FbWidth) from the next frame
            if (paused) scene.Alpha = frozenAlpha;
            else
            {
                prevAlpha = (!Interpolate || tick >= stopAt || tick == captureAt) ? 1.0 : FrameClock.Alpha(Raylib.GetTime(), logicStart, hz, tick);
                scene.Alpha = prevAlpha;
            }
            if (scene.FbWidth != fb.Width || scene.FbHeight != fb.Height)   // the scene changed its drawing size (level <-> rest, WIDESCREEN, the 640x480 CREATORS photo): rebuild fb, rgba and texture
            {
                var nfb = new Framebuffer(scene.FbWidth, scene.FbHeight);
                Array.Copy(fb.Palette, nfb.Palette, 256);
                fb = nfb;
                rgba = new byte[fb.Width * fb.Height * 4];
                Raylib.UnloadTexture(tex);
                Image nimg = Raylib.GenImageColor(fb.Width, fb.Height, Color.Black);
                tex = Raylib.LoadTextureFromImage(nimg);
                Raylib.UnloadImage(nimg);
                Raylib.SetTextureFilter(tex, TextureFilter.Point);
                wasFiltered = true;   // force the next Present to reset the filter if none is active
                // Windowed: the window size is NOT touched here (it is the source of the aspect; resizing it from the canvas would feed back).
            }
            CrashContext.Update(scene, tick, frame, lastFps);
            scene.Draw(fb);

            fb.ToRgba(rgba);
            Raylib.UpdateTexture(tex, rgba);
            Present();
            LatchPressed();   // EndDrawing polled: keep presses it saw for the next tick

            if (tick == captureAt)
            {
                // Same approach as ShowWindow: present once more so the back buffer certainly holds this frame.
                Present();
                Image shot = Raylib.LoadImageFromScreen();
                bool ok = Raylib.ExportImage(shot, CapturePath!);
                Raylib.UnloadImage(shot);
                Console.WriteLine(ok ? $"screenshot {CapturePath} (tick {tick})" : $"FAIL screenshot {CapturePath}");
                captured = ok;
                if (!ok) rc = 1;
            }

            if (MaxFrames > 0 && tick >= MaxFrames)
                break;

            // Render at RenderHz: absolute schedule from renderStart (no drift); sleep coarsely, then spin the last ~1 ms.
            // Always applied (a driver may ignore V-Sync); Pace rebases a stale schedule, so a rate/mode change never sleeps
            // for seconds nor bursts to catch up.
            frame++;
            fpsFrames++;
            double fpsNow = Raylib.GetTime();
            if (fpsNow - fpsStart >= 0.5)
            {
                lastFps = fpsFrames / (fpsNow - fpsStart);
                Raylib.SetWindowTitle($"{Title} - {(int)Math.Round(lastFps)}Fps");
                fpsStart = fpsNow;
                fpsFrames = 0;
            }
            var (next, pacedStart) = FrameClock.Pace(Raylib.GetTime(), renderStart, frame, RenderHz);
            renderStart = pacedStart;
            double remaining = next - Raylib.GetTime();
            if (remaining > 0.002)
                Raylib.WaitTime(remaining - 0.001);
            while (Raylib.GetTime() < next) { }
            if (Raylib.GetTime() - next > 0.25)
                renderStart = Raylib.GetTime() - frame / RenderHz;
        }

        if (CapturePath is not null && !captured)
        {
            if (rc == 0)
                Console.WriteLine($"FAIL screenshot not taken (capture tick {captureAt}, ticks run {tick})");
            rc = 1;
        }

        TicksRun = tick;
        view.Dispose();   // its texture must go before the window
        Raylib.UnloadTexture(tex);
        Raylib.CloseWindow();
        return rc;
    }
}
