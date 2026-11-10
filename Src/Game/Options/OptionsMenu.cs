using Raylib_cs;

namespace Pre2Port.Game.Options;

/// <summary>One menu step's worth of input, already debounced by the Presenter (one physical press = one true flag).</summary>
public struct MenuInput
{
    public bool Up, Down, Left, Right, Select, Back;
    /// <summary>First key newly pressed this step (key capture of the Controller page); Null = none.</summary>
    public KeyboardKey Key;
    /// <summary>Pad button newly pressed this step (Unknown = none).</summary>
    public GamepadButton Pad;
    /// <summary>Some key / pad button is still held (used to wait for the release of the press that started a capture).</summary>
    public bool AnyDown;
}

/// <summary>
/// ESC overlay: Game, Sound, Controller, Graphics, Return to Main Menu, Exit to Windows. Pure state machine (no drawing, no window): every change is applied and saved at once
/// (Prehistorik2.cfg: LivesAtStartup / Volume / ShowFps / FPS / Vsync + filter keys; Controller.cfg: device, keys, pad buttons). Texts are English.
/// Navigation: Up/Down move, Left/Right change a value, Select (Enter/Space/pad Fire) enters/activates, Back (Esc/pad Jump) goes up one level; Back on the root closes the overlay.
/// </summary>
public sealed class OptionsMenu
{
    public enum Page { Root, Game, Sound, Controller, Graphics, Confirm }
    public enum Request { None, ReturnToMainMenu, ExitToWindows }

    public const int VolumeStep = 5;
    public static readonly int[] FpsPresets = { 60, 75, 120, 144, 165, 240, 360 };
    public static readonly string[] RootItems = { "Game", "Sound", "Controller", "Graphics", "Return to Main Menu", "Exit to Windows" };

    readonly GameOptions _opt;
    readonly Settings _set;
    readonly ControllerConfig _cc;

    /// <summary>Connected pads (slot, class, label); replaceable for headless tests.</summary>
    public Func<List<(int Index, PadType Type, string Text)>> Pads { get; set; } = PadInfo.DetectPads;
    /// <summary>False when the running scene cannot go back to its main menu (launcher): the item is dimmed.</summary>
    public Func<bool> CanReturnToMainMenu { get; set; } = () => true;
    /// <summary>True while the press-1/2 main menu is on screen: the "Return to Main Menu" row is hidden (evaluated when the root rows are built).</summary>
    public Func<bool> InMainMenu { get; set; } = () => false;
    const string ReturnItem = "Return to Main Menu";
    public Action<int>? OnFps { get; set; }
    public Action<int>? OnVolume { get; set; }
    public Action<ScreenMode>? OnMode { get; set; }
    public Action<bool>? OnVsync { get; set; }

    public bool IsOpen { get; private set; }
    public Page Current { get; private set; } = Page.Root;
    public int Sel { get; private set; }
    public Request Pending { get; private set; }
    public bool Capturing { get; private set; }
    public string Message { get; private set; } = "";
    bool _waitRelease;
    Request _confirming;
    int _confirmSel;   // 0 = No, 1 = Yes
    readonly int[] _lastSel = new int[6];

    public OptionsMenu(GameOptions opt, Settings set, ControllerConfig cc) { _opt = opt; _set = set; _cc = cc; }

    public GameOptions Options => _opt;
    public Settings Settings => _set;
    public ControllerConfig Controller => _cc;
    public int ConfirmSel => _confirmSel;
    public Request Confirming => _confirming;

    public void Open() { IsOpen = true; Current = Page.Root; Sel = 0; Pending = Request.None; Capturing = false; Message = ""; _waitRelease = false; }
    public void Close() { IsOpen = false; Capturing = false; }
    public Request TakeRequest() { var r = Pending; Pending = Request.None; return r; }

    // ---------------------------------------------------------------- rows
    public bool ShowFpsAvailable => _set.Fullscreen;

    /// <summary>Row labels of the current page (the Back row is always last on sub-pages).</summary>
    public List<string> Rows()
    {
        switch (Current)
        {
            case Page.Root: return RootItems.Where(s => s != ReturnItem || !InMainMenu()).ToList();
            case Page.Game: return new() { "Lives at startup", "Back" };
            case Page.Sound: return new() { "Volume", "Back" };
            case Page.Graphics: return new() { "Full Screen", "Show FPS", "Max FPS", "V-Sync", "Scaling", "Pixel art", "Effects", "Back" };
            case Page.Controller:
            {
                var l = new List<string> { "Device" };
                if (_cc.UseJoystick) { l.Add("Jump"); l.Add("Fire"); }
                else { l.Add("Left"); l.Add("Right"); l.Add("Down"); l.Add("Jump"); l.Add("Fire"); }
                l.Add("Back");
                return l;
            }
            default: return new() { "No", "Yes" };
        }
    }

    public bool RowEnabled(int row) => Current switch
    {
        Page.Root => Rows()[row] != ReturnItem || CanReturnToMainMenu(),
        Page.Graphics => row != 1 || ShowFpsAvailable,
        _ => true,
    };

    /// <summary>True when Left/Right change this row's value (the view draws &lt; &gt; around it).</summary>
    public bool RowAdjustable(int row) => Current switch
    {
        Page.Game => row == 0,
        Page.Sound => row == 0,
        Page.Graphics => row < 7 && RowEnabled(row),
        Page.Controller => row == 0,
        _ => false,
    };

    public string RowValue(int row)
    {
        switch (Current)
        {
            case Page.Game: return row == 0 ? _opt.LivesAtStartup.ToString() : "";
            case Page.Sound: return row == 0 ? _opt.Volume + "%" : "";
            case Page.Graphics:
                return row switch
                {
                    0 => _set.Mode.ToString(),
                    1 => ShowFpsAvailable ? (_opt.ShowFps ? "On" : "Off") : "Fullscreen only",
                    2 => _set.Fps.ToString(),
                    3 => _set.Vsync ? "On" : "Off",
                    4 => FilterName(_set.FilterScaling),
                    5 => FilterName(_set.FilterPixelArt),
                    6 => FilterName(_set.FilterEffects),
                    _ => "",
                };
            case Page.Controller:
                if (row == 0) return DeviceText();
                if (row == Rows().Count - 1) return "";
                if (Capturing && row == Sel) return _cc.UseJoystick ? "Press a button" : "Press a key";
                if (_cc.UseJoystick) return PadInfo.ButtonName(row == 1 ? _cc.PadJump : _cc.PadFire, PadTypeOfSelected());
                return KeyName(_cc.Keys[(int)ActionOfRow(row)]);
            default: return "";
        }
    }

    public static string FilterName(FilterScaling f) => f switch { FilterScaling.Bilinear => "Bilinear", FilterScaling.SharpBilinear => "Sharp bilinear", FilterScaling.Fsr => "FSR", _ => "Off" };
    public static string FilterName(FilterPixelArt f) => f switch { FilterPixelArt.Xbr => "xBR", FilterPixelArt.Scale3x => "Scale3x", _ => "Off" };
    public static string FilterName(FilterEffects f) => f switch { FilterEffects.Vibrance => "Vibrance", FilterEffects.Scanlines => "Scanlines", _ => "Off" };

    public static string KeyName(KeyboardKey k)
    {
        string n = k.ToString();
        switch (k)
        {
            case KeyboardKey.Zero: return "0"; case KeyboardKey.One: return "1"; case KeyboardKey.Two: return "2"; case KeyboardKey.Three: return "3"; case KeyboardKey.Four: return "4";
            case KeyboardKey.Five: return "5"; case KeyboardKey.Six: return "6"; case KeyboardKey.Seven: return "7"; case KeyboardKey.Eight: return "8"; case KeyboardKey.Nine: return "9";
        }
        return System.Text.RegularExpressions.Regex.Replace(n, "(?<!^)([A-Z])", " $1");
    }

    GameAction ActionOfRow(int row) => (GameAction)(row - 1);   // keyboard: rows 1..5 = Left, Right, Down, Jump, Fire

    PadType PadTypeOfSelected()
    {
        foreach (var p in Pads()) if (p.Index == _cc.JoystickIndex) return p.Type;
        return PadType.Compatible;
    }

    public string DeviceText()
    {
        if (!_cc.UseJoystick) return "Keyboard";
        foreach (var p in Pads()) if (p.Index == _cc.JoystickIndex) return p.Text;
        return "No joystick";
    }

    public string Title => Current switch
    {
        Page.Root => "Paused",
        Page.Game => "Game",
        Page.Sound => "Sound",
        Page.Controller => "Controller",
        Page.Graphics => "Graphics",
        _ => _confirming == Request.ExitToWindows ? "Exit to Windows?" : "Return to Main Menu?",
    };

    // ---------------------------------------------------------------- logic
    public void Step(MenuInput i)
    {
        if (!IsOpen) return;
        if (Capturing) { StepCapture(i); return; }
        if (_waitRelease) { if (!i.AnyDown) _waitRelease = false; else return; }
        if (i.Back) { GoBack(); return; }
        var rows = Rows();
        if (Current == Page.Confirm)
        {
            if (i.Left || i.Up) _confirmSel = 0;
            else if (i.Right || i.Down) _confirmSel = 1;
            else if (i.Select)
            {
                if (_confirmSel == 1) { Pending = _confirming; IsOpen = false; }
                else GoBack();
            }
            return;
        }
        if (i.Up) { Sel = Next(Sel, -1, rows.Count); Message = ""; return; }
        if (i.Down) { Sel = Next(Sel, +1, rows.Count); Message = ""; return; }
        if (i.Left) { Adjust(-1, false); return; }
        if (i.Right) { Adjust(+1, false); return; }
        if (i.Select) Activate();
    }

    int Next(int s, int d, int n)   // wraps and skips disabled rows
    {
        for (int k = 0; k < n; k++)
        {
            s = ((s + d) % n + n) % n;
            if (RowEnabled(s)) return s;
        }
        return s;
    }

    void GoBack()
    {
        Message = "";
        if (Current == Page.Root) { Close(); return; }
        if (Current == Page.Confirm) { Current = Page.Root; Sel = _lastSel[(int)Page.Root]; _confirming = Request.None; return; }
        Current = Page.Root;
        Sel = _lastSel[(int)Page.Root];
    }

    void Enter(Page p)
    {
        _lastSel[(int)Page.Root] = Sel;
        Current = p;
        Sel = 0;
        Message = "";
    }

    void Activate()
    {
        var rows = Rows();
        bool isBack = Current != Page.Root && Sel == rows.Count - 1;
        if (isBack) { GoBack(); return; }
        switch (Current)
        {
            case Page.Root:
                switch (rows[Sel])
                {
                    case "Game": Enter(Page.Game); break;
                    case "Sound": Enter(Page.Sound); break;
                    case "Controller": Enter(Page.Controller); break;
                    case "Graphics": Enter(Page.Graphics); break;
                    case ReturnItem:
                        if (!CanReturnToMainMenu()) return;
                        _lastSel[(int)Page.Root] = Sel; _confirming = Request.ReturnToMainMenu; _confirmSel = 0; Current = Page.Confirm; break;
                    case "Exit to Windows":
                        _lastSel[(int)Page.Root] = Sel; _confirming = Request.ExitToWindows; _confirmSel = 0; Current = Page.Confirm; break;
                }
                return;
            case Page.Controller:
                if (Sel == 0) { Adjust(+1, true); return; }
                Capturing = true; _waitRelease = true; Message = "";
                return;
            default:
                Adjust(+1, true);
                return;
        }
    }

    /// <summary>Changes the value of the selected row and saves it. <paramref name="wrap"/>: Select cycles past the last value; Left/Right stop at the ends of numeric ranges.</summary>
    void Adjust(int d, bool wrap)
    {
        switch (Current)
        {
            case Page.Game:
                if (Sel != 0) return;
                {
                    int v = _opt.LivesAtStartup + d;
                    v = wrap && v > GameOptions.MaxLives ? GameOptions.MinLives : Math.Clamp(v, GameOptions.MinLives, GameOptions.MaxLives);
                    if (v != _opt.LivesAtStartup) { _opt.LivesAtStartup = v; _opt.Save(); }
                }
                return;
            case Page.Sound:
                if (Sel != 0) return;
                {
                    int v = _opt.Volume + d * VolumeStep;
                    v = wrap && v > 100 ? 0 : Math.Clamp(v, 0, 100);
                    if (v != _opt.Volume) { _opt.Volume = v; _opt.Save(); OnVolume?.Invoke(v); }
                }
                return;
            case Page.Graphics:
                switch (Sel)
                {
                    case 0:
                        _set.Mode = Cycle(_set.Mode, d);
                        _set.SaveOverlayKeys();
                        OnMode?.Invoke(_set.Mode);
                        return;
                    case 1:
                        if (!ShowFpsAvailable) return;
                        _opt.ShowFps = !_opt.ShowFps; _opt.Save(); return;
                    case 2:
                    {
                        int cur = _set.Fps, nv = cur;
                        if (d > 0) { nv = FpsPresets.FirstOrDefault(p => p > cur); if (nv == 0) nv = wrap ? FpsPresets[0] : cur; }
                        else { nv = FpsPresets.LastOrDefault(p => p < cur); if (nv == 0) nv = wrap ? FpsPresets[^1] : cur; }
                        if (nv != cur) { _set.Fps = nv; _set.SaveOverlayKeys(); OnFps?.Invoke(nv); }
                        return;
                    }
                    case 3:
                        _set.Vsync = !_set.Vsync; _set.SaveOverlayKeys(); OnVsync?.Invoke(_set.Vsync); return;
                    case 4: _set.FilterScaling = Cycle(_set.FilterScaling, d); _set.SaveOverlayKeys(); return;
                    case 5: _set.FilterPixelArt = Cycle(_set.FilterPixelArt, d); _set.SaveOverlayKeys(); return;
                    case 6: _set.FilterEffects = Cycle(_set.FilterEffects, d); _set.SaveOverlayKeys(); return;
                }
                return;
            case Page.Controller:
                if (Sel == 0) CycleDevice(d);
                return;
        }
    }

    static T Cycle<T>(T v, int d) where T : struct, Enum
    {
        int n = Enum.GetValues<T>().Length;
        return (T)(object)(((Convert.ToInt32(v) + d) % n + n) % n);
    }

    /// <summary>Keyboard -> pad 1 -> pad 2 ... (circular). Choosing another pad restores the default Fire/Jump buttons.</summary>
    void CycleDevice(int d)
    {
        var pads = Pads();
        int cur = !_cc.UseJoystick ? 0 : pads.FindIndex(p => p.Index == _cc.JoystickIndex) + 1;   // an unplugged chosen pad counts as the keyboard slot
        int n = pads.Count + 1, next = ((cur + d) % n + n) % n;
        if (next == 0) _cc.UseJoystick = false;
        else
        {
            if (!_cc.UseJoystick || _cc.JoystickIndex != pads[next - 1].Index) _cc.ResetPadButtons();
            _cc.UseJoystick = true; _cc.JoystickIndex = pads[next - 1].Index;
        }
        Sel = Math.Min(Sel, Rows().Count - 1);
        _cc.Save();
    }

    /// <summary>Key / pad button redefinition: waits for the activating press to be released, then takes the first new press. Esc (keyboard) cancels.</summary>
    void StepCapture(MenuInput i)
    {
        if (i.Back && !_cc.UseJoystick) { Capturing = false; Message = ""; return; }   // Esc cancels a key capture
        if (_waitRelease) { if (!i.AnyDown) _waitRelease = false; return; }
        if (_cc.UseJoystick)
        {
            if (i.Key == KeyboardKey.Escape) { Capturing = false; Message = ""; return; }
            if (i.Pad == GamepadButton.Unknown) return;
            if (!_cc.RebindPad(fire: Sel == 2, i.Pad)) { Message = "Direction buttons are movement"; return; }
            Capturing = false; Message = ""; _cc.Save(); _waitRelease = true;
            return;
        }
        if (i.Key == KeyboardKey.Null) return;
        var action = ActionOfRow(Sel);
        int other = Array.IndexOf(_cc.Keys, i.Key);
        if (!_cc.Rebind(action, i.Key)) { Message = "Reserved key"; return; }
        Message = other >= 0 && other != (int)action ? $"Swapped with {ControllerConfig.ActionNames[other]}" : "";
        Capturing = false; _cc.Save(); _waitRelease = true;
    }
}
