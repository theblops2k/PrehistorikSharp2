namespace Pre2Port.Game.Frontend;

public enum StageResult { Running, Next, Skipped }

/// <summary>
/// One intro screen. Per tick: Update(input) selects this tick's frame and then polls input (Python order: yield the
/// frame, then poll); Draw paints the selected frame.
/// </summary>
public abstract class IntroStage : IScene
{
    public double LogicHz => 70.0;   // VGA retrace
    public abstract string Name { get; }
    public StageResult Result { get; protected set; } = StageResult.Running;
    /// <summary>Palette entries that belong to this screen (OLDIES 16, 13h titles 256).</summary>
    public virtual int PaletteEntries => 256;

    public abstract void Update(Input input);
    public abstract void Draw(Framebuffer fb);

    /// <summary>fire = [0x27e8] | [0x2832]: space or enter.</summary>
    protected static bool Fire(Input i) => i.IsKeyDown(Input.Key.Space) || i.IsKeyDown(Input.Key.Enter);
}

/// <summary>
/// A timed 13h title: a list of (6-bit palette, image) frames, one per tick, with the optional
/// <c>_skippable_intro</c> behaviour (fresh fire press after having seen the key released skips the rest).
/// </summary>
public abstract class TimedTitleStage : IntroStage
{
    protected readonly List<(byte[] Pal6, byte[] Image)> Frames = new();
    readonly bool _skippable;
    int _idx, _cur;
    bool _armed, _waiting, _pressed;

    /// <summary>Index of a frame after which the stage holds it until a fire press+release (scene-wait 0bbe); -1 = none.</summary>
    protected int WaitAfter { get; set; } = -1;

    protected TimedTitleStage(bool skippable) => _skippable = skippable;

    public override void Update(Input input)
    {
        if (Result != StageResult.Running) return;
        if (_waiting)
        {
            _cur = WaitAfter;
            bool f = Fire(input);
            if (!_pressed) { if (f) _pressed = true; }
            else if (!f) { _waiting = false; _idx = WaitAfter + 1; }
            return;
        }
        _cur = _idx;
        if (_skippable)
        {
            if (!Fire(input)) _armed = true;
            else if (_armed) { Result = StageResult.Skipped; return; }
        }
        if (_idx == WaitAfter) { _waiting = true; return; }
        if (++_idx >= Frames.Count) Result = StageResult.Next;
    }

    public override void Draw(Framebuffer fb)
    {
        var (pal, img) = Frames[_cur];
        fb.SetPalette6(pal, 0, 0, 256);
        Array.Copy(img, fb.Pixels, img.Length);
    }
}
