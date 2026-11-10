using Raylib_cs;

namespace Pre2Port.Game.Options;

/// <summary>Physical device state seen by <see cref="InputMapper"/> (Raylib in the game, a fake in the headless verifier).</summary>
public interface IInputSource
{
    bool KeyDown(KeyboardKey k);
    /// <summary>New press of <paramref name="k"/> since the last tick; consumes it (one 'pressed' per tick, as the Presenter always did).</summary>
    bool KeyPressedConsume(KeyboardKey k);
    bool PadAvailable(int slot);
    bool PadDown(int slot, GamepadButton b);
    float PadAxis(int slot, GamepadAxis a);
}

/// <summary>
/// Turns physical keys / pad buttons into the logical <see cref="Input.Key"/> set the scenes read.
/// Gameplay (inside a level): Left/Right/Down/Up(=Jump)/Space(=Fire) come ONLY from the configured actions (<see cref="ControllerConfig"/>); the remaining keys stay native
/// (Enter also fires, as in DC1), except a physical key that is bound to an action does not also act as its native key (WASD does not type A/D by accident).
/// Front-end (intro, menus, password, carte, tally: <c>raw</c>): the original keys (arrows, Space, Enter, digits, A..F, Q); the keyboard bindings are ignored there.
/// Joystick mode adds the pad on top of the keyboard: left stick / d-pad -> Left/Right/Down (Up too in the front-end), Fire/Jump buttons -> Space/Up (both -> Space in the front-end).
/// Escape is never produced (the Presenter owns it: it opens the overlay).
/// </summary>
public sealed class InputMapper
{
    public static readonly (Input.Key Key, KeyboardKey Raylib)[] KeyMap =
    {
        (Input.Key.Left, KeyboardKey.Left),
        (Input.Key.Right, KeyboardKey.Right),
        (Input.Key.Up, KeyboardKey.Up),
        (Input.Key.Down, KeyboardKey.Down),
        (Input.Key.Space, KeyboardKey.Space),
        (Input.Key.Enter, KeyboardKey.Enter),
        (Input.Key.One, KeyboardKey.One),
        (Input.Key.Two, KeyboardKey.Two),
        (Input.Key.A, KeyboardKey.A), (Input.Key.B, KeyboardKey.B), (Input.Key.C, KeyboardKey.C),
        (Input.Key.D, KeyboardKey.D), (Input.Key.E, KeyboardKey.E), (Input.Key.F, KeyboardKey.F),
        (Input.Key.D0, KeyboardKey.Zero), (Input.Key.D1, KeyboardKey.One), (Input.Key.D2, KeyboardKey.Two),
        (Input.Key.D3, KeyboardKey.Three), (Input.Key.D4, KeyboardKey.Four), (Input.Key.D5, KeyboardKey.Five),
        (Input.Key.D6, KeyboardKey.Six), (Input.Key.D7, KeyboardKey.Seven), (Input.Key.D8, KeyboardKey.Eight),
        (Input.Key.D9, KeyboardKey.Nine),
        (Input.Key.Q, KeyboardKey.Q),
    };

    /// <summary>Every physical key the mapper may read (for the Presenter's latch).</summary>
    public static IEnumerable<KeyboardKey> PhysicalKeys(ControllerConfig cc) => KeyMap.Select(k => k.Raylib).Concat(cc.Keys).Distinct();

    readonly ControllerConfig _cc;
    readonly bool[] _padPrev = new bool[Input.KeyCount];

    public InputMapper(ControllerConfig cc) { _cc = cc; }

    static bool IsAction(Input.Key k) => k is Input.Key.Left or Input.Key.Right or Input.Key.Up or Input.Key.Down or Input.Key.Space;

    KeyboardKey? BoundKey(Input.Key k) => k switch
    {
        Input.Key.Left => _cc.KeyOf(GameAction.Left),
        Input.Key.Right => _cc.KeyOf(GameAction.Right),
        Input.Key.Down => _cc.KeyOf(GameAction.Down),
        Input.Key.Up => _cc.KeyOf(GameAction.Jump),
        Input.Key.Space => _cc.KeyOf(GameAction.Fire),
        _ => null,
    };

    /// <summary>Computes down/pressed for every logical key and stores them in <paramref name="input"/>. Call once per logic tick.</summary>
    public void Fill(Input input, IInputSource src, bool raw)
    {
        var down = new bool[Input.KeyCount];
        var pressed = new bool[Input.KeyCount];

        foreach (var (key, rk) in KeyMap)
        {
            int i = (int)key;
            if (!raw && IsAction(key))
            {
                var bk = BoundKey(key)!.Value;
                down[i] = src.KeyDown(bk);
                pressed[i] = src.KeyPressedConsume(bk);
            }
            else if (!raw && Array.IndexOf(_cc.Keys, rk) >= 0)
            {
                // physical key owned by an action: it does not also type its native key (the press is still consumed so it is not reported later)
                src.KeyPressedConsume(rk);
            }
            else
            {
                down[i] = src.KeyDown(rk);
                pressed[i] = src.KeyPressedConsume(rk);
            }
        }

        if (_cc.UseJoystick && src.PadAvailable(_cc.JoystickIndex))
        {
            int g = _cc.JoystickIndex;
            float ax = src.PadAxis(g, GamepadAxis.LeftX), ay = src.PadAxis(g, GamepadAxis.LeftY);
            var padDown = new bool[Input.KeyCount];
            padDown[(int)Input.Key.Left] = ax < -0.5f || src.PadDown(g, GamepadButton.LeftFaceLeft);
            padDown[(int)Input.Key.Right] = ax > 0.5f || src.PadDown(g, GamepadButton.LeftFaceRight);
            padDown[(int)Input.Key.Down] = ay > 0.5f || src.PadDown(g, GamepadButton.LeftFaceDown);
            bool fire = src.PadDown(g, _cc.PadFire), jump = src.PadDown(g, _cc.PadJump);
            if (raw)
            {
                padDown[(int)Input.Key.Up] = ay < -0.5f || src.PadDown(g, GamepadButton.LeftFaceUp);
                padDown[(int)Input.Key.Space] = fire || jump;
            }
            else
            {
                padDown[(int)Input.Key.Space] = fire;
                padDown[(int)Input.Key.Up] = jump;
            }
            for (int i = 0; i < padDown.Length; i++)
            {
                bool edge = padDown[i] && !_padPrev[i];
                down[i] |= padDown[i];
                pressed[i] |= edge;
                _padPrev[i] = padDown[i];
            }
        }
        else Array.Clear(_padPrev);

        down[(int)Input.Key.Escape] = false;
        pressed[(int)Input.Key.Escape] = false;
        for (int i = 0; i < down.Length; i++) input.Set((Input.Key)i, down[i], pressed[i]);
    }
}

/// <summary>Raylib-backed <see cref="IInputSource"/>; <c>Pending</c> is the Presenter's latch of presses seen by a poll but not yet consumed by a tick.</summary>
public sealed class RaylibInputSource : IInputSource
{
    readonly HashSet<KeyboardKey> _pending;
    public RaylibInputSource(HashSet<KeyboardKey> pending) { _pending = pending; }
    public bool KeyDown(KeyboardKey k) => Raylib.IsKeyDown(k) || _pending.Contains(k);
    public bool KeyPressedConsume(KeyboardKey k) => _pending.Remove(k);
    public bool PadAvailable(int slot) => Raylib.IsGamepadAvailable(slot);
    public bool PadDown(int slot, GamepadButton b) => Raylib.IsGamepadButtonDown(slot, b);
    public float PadAxis(int slot, GamepadAxis a) => Raylib.GetGamepadAxisMovement(slot, a);
}
