namespace Pre2Port.Game;

/// <summary>Keys the game needs, as held / newly pressed this tick.</summary>
public sealed class Input
{
    // A..F, D0..D9: the hex digits typed on the password screen ('1'/'2' reuse One/Two). Q: scancode 0x10, which the password
    // table [asm DS:0xB068] also maps to 'A' (AZERTY 'A' position). Appended: existing values unchanged.
    public enum Key
    {
        Left, Right, Up, Down, Space, Enter, One, Two, Escape,
        A, B, C, D, E, F, D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
        Q,
    }

    public const int KeyCount = 26;

    readonly bool[] _down = new bool[KeyCount];
    readonly bool[] _pressed = new bool[KeyCount];

    public bool IsKeyDown(Key k) => _down[(int)k];
    public bool IsKeyPressed(Key k) => _pressed[(int)k];

    public void Set(Key k, bool down, bool pressed)
    {
        _down[(int)k] = down;
        _pressed[(int)k] = pressed;
    }
}

/// <summary>A game screen. Update then Draw per logic tick; the Presenter schedules ticks at <see cref="LogicHz"/>.</summary>
public interface IScene
{
    /// <summary>Logic ticks per second (default: original level logic rate, 70/3 Hz).</summary>
    double LogicHz => 70.0 / 3.0;

    /// <summary>Presentation interpolation factor 0..1 within the current logic tick (1 = the tick image as is). Only the level scene and the animated tally use it.</summary>
    double Alpha { get => 1.0; set { } }

    /// <summary>Framebuffer width this scene draws into (320 except the level scene in WIDESCREEN mode). The Presenter follows it.</summary>
    int FbWidth => Screen.Width;

    /// <summary>Framebuffer height this scene draws into (200 except the 640x480 CREATORS photo shown after THE END).</summary>
    int FbHeight => Screen.Height;

    /// <summary>True while the scene is live gameplay (inside a level): the configured Left/Right/Down/Jump/Fire keys or pad buttons drive it. False (default) = front-end screens, which keep the original keys (arrows, Space, Enter, digits, A-F, Q).</summary>
    bool GameplayKeys => false;

    /// <summary>The ESC overlay's "Return to Main Menu" is available (the running game can go back to the press-1/2 menu).</summary>
    bool CanReturnToMainMenu => false;

    /// <summary>The scene is showing the press-1/2 main menu: the overlay hides "Return to Main Menu" there.</summary>
    bool InMainMenu => false;

    /// <summary>Back to the main menu of the running game (only called when <see cref="CanReturnToMainMenu"/>).</summary>
    void ReturnToMainMenu() { }

    void Update(Input input);
    void Draw(Framebuffer fb);
}
