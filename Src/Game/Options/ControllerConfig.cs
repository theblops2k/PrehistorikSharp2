using Raylib_cs;

namespace Pre2Port.Game.Options;

/// <summary>Graphic filters (ESC overlay > Graphics), one per group like CoolSpotPort. Keys FILTERSCALING / FILTERPIXELART / FILTEREFFECTS in Prehistorik2.cfg.</summary>
public enum FilterScaling { Off, Bilinear, SharpBilinear, Fsr }
public enum FilterPixelArt { Off, Xbr, Scale3x }
public enum FilterEffects { Off, Vibrance, Scanlines }

/// <summary>The five remappable keyboard actions (the joystick only remaps Fire and Jump).</summary>
public enum GameAction { Left, Right, Down, Jump, Fire }

/// <summary>
/// Controller settings stored in <c>Controller.cfg</c> next to the executable (AppContext.BaseDirectory\Controller.cfg):
/// <c>Mode=Keyboard|Joystick</c>, <c>Joystick=slot</c>, <c>Left/Right/Down/Jump/Fire=KeyboardKey</c>, <c>PadFire/PadJump=GamepadButton</c>.
/// Defaults: arrows, Jump = Up, Fire = Space; joystick Fire = PS square (raylib RightFaceLeft), Jump = PS cross (RightFaceDown).
/// No two keyboard actions ever share a key (<see cref="Rebind"/> swaps). Parsing/saving is pure (testable headless); only the pad queries touch Raylib.
/// </summary>
public sealed class ControllerConfig
{
    public const string FileName = "Controller.cfg";
    public static readonly string[] ActionNames = { "Left", "Right", "Down", "Jump", "Fire" };

    public static ControllerConfig Current { get; } = LoadOrCreate(Path.Combine(AppContext.BaseDirectory, FileName));

    public KeyboardKey[] Keys { get; } = DefaultKeys();
    public bool UseJoystick { get; set; }
    public int JoystickIndex { get; set; }
    public GamepadButton PadFire { get; set; } = DefaultPadFire;
    public GamepadButton PadJump { get; set; } = DefaultPadJump;
    public string FilePath { get; private set; } = "";

    public const GamepadButton DefaultPadFire = GamepadButton.RightFaceLeft;   // PS square / Xbox X
    public const GamepadButton DefaultPadJump = GamepadButton.RightFaceDown;   // PS cross / Xbox A

    public static KeyboardKey[] DefaultKeys() => new[] { KeyboardKey.Left, KeyboardKey.Right, KeyboardKey.Down, KeyboardKey.Up, KeyboardKey.Space };

    public KeyboardKey KeyOf(GameAction a) => Keys[(int)a];

    public void ResetPadButtons() { PadFire = DefaultPadFire; PadJump = DefaultPadJump; }

    /// <summary>Keys the overlay / game own: never assignable to an action.</summary>
    public static bool IsReserved(KeyboardKey k) => k == KeyboardKey.Null || k == KeyboardKey.Escape || k == KeyboardKey.Enter || k == KeyboardKey.KpEnter;

    /// <summary>Assigns <paramref name="key"/> to <paramref name="action"/>. A reserved key is rejected (false); if another action already uses it, the two actions swap keys.</summary>
    public bool Rebind(GameAction action, KeyboardKey key)
    {
        if (IsReserved(key)) return false;
        int a = (int)action;
        int other = Array.IndexOf(Keys, key);
        if (other >= 0 && other != a) Keys[other] = Keys[a];
        Keys[a] = key;
        return true;
    }

    /// <summary>Sets the pad Fire/Jump button (they never coincide: swap). Direction pad buttons are rejected (they are movement).</summary>
    public bool RebindPad(bool fire, GamepadButton b)
    {
        if (b == GamepadButton.Unknown || (int)b <= (int)GamepadButton.LeftFaceLeft) return false;   // LeftFaceUp..LeftFaceLeft = d-pad
        if (fire) { if (b == PadJump) PadJump = PadFire; PadFire = b; }
        else { if (b == PadFire) PadFire = PadJump; PadJump = b; }
        return true;
    }

    // Pad classification and button names live in PadInfo.
    public PadType CurrentType => Raylib.IsGamepadAvailable(JoystickIndex) ? PadInfo.TypeOfSlot(JoystickIndex) : PadType.Compatible;
    public bool JoystickActive => UseJoystick && Raylib.IsGamepadAvailable(JoystickIndex);

    // ---------------------------------------------------------------- file
    public static ControllerConfig LoadOrCreate(string path)
    {
        var c = new ControllerConfig { FilePath = path };
        if (!File.Exists(path)) { c.Save(); return c; }
        var f = new CfgFile(path);
        for (int i = 0; i < ActionNames.Length; i++)
        {
            string? v = f.Get(ActionNames[i]);
            if (v is not null && Enum.TryParse(v, true, out KeyboardKey kk) && Enum.IsDefined(kk) && !IsReserved(kk)) c.Keys[i] = kk;
        }
        // A hand-edited file may repeat a key: keep the first action's key, give later duplicates their default (or the next free default).
        var defaults = DefaultKeys();
        for (int i = 0; i < c.Keys.Length; i++)
            for (int j = 0; j < i; j++)
                if (c.Keys[i] == c.Keys[j]) { c.Keys[i] = defaults.FirstOrDefault(d => !c.Keys.Contains(d)); break; }
        string? mode = f.Get("Mode");
        c.UseJoystick = mode is not null && mode.Equals("Joystick", StringComparison.OrdinalIgnoreCase);
        if (int.TryParse(f.Get("Joystick"), out int j2) && j2 >= 0 && j2 < 4) c.JoystickIndex = j2;
        if (Enum.TryParse(f.Get("PadFire"), true, out GamepadButton bf) && Enum.IsDefined(bf)) c.RebindPad(true, bf);
        if (Enum.TryParse(f.Get("PadJump"), true, out GamepadButton bj) && Enum.IsDefined(bj)) c.RebindPad(false, bj);
        // Fill missing keys with their defaults (same criterion as the cfg keys).
        bool missing = ActionNames.Any(n => !f.Has(n)) || !f.Has("Mode") || !f.Has("Joystick") || !f.Has("PadFire") || !f.Has("PadJump");
        if (missing) c.Save();
        return c;
    }

    public void Save()
    {
        if (FilePath.Length == 0) return;
        var f = new CfgFile(FilePath);
        if (f.Lines.Count == 0) f.Lines.Add("# Prehistorik 2 controller configuration. Mode = Keyboard | Joystick");
        f.Set("Mode", UseJoystick ? "Joystick" : "Keyboard");
        for (int i = 0; i < ActionNames.Length; i++) f.Set(ActionNames[i], Keys[i].ToString());
        f.Set("Joystick", JoystickIndex.ToString());
        f.Set("PadFire", PadFire.ToString());
        f.Set("PadJump", PadJump.ToString());
        f.Save(force: true);
    }
}
