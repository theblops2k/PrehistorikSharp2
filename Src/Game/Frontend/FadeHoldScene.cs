namespace Pre2Port.Game.Frontend;

/// <summary>
/// A frozen picture while the DAC ramps to black: the 9286 fade-out (one palette per 70 Hz retrace, <see cref="Recovered.FrontEndFade.FadeOut"/>) the main loop
/// runs between the end of the attract demo and the menu (main 011C -> 013B). The image is the last frame the host showed; only the palette changes.
/// </summary>
public sealed class FadeHoldScene
{
    readonly byte[] _pixels;
    readonly List<byte[]> _frames;
    int _i;
    byte[] _dac;

    /// <param name="shown">The framebuffer to freeze (pixels are copied; its palette is only the fade's starting point and is not used here).</param>
    /// <param name="frames">0x300-byte 6-bit DAC snapshots, one per retrace.</param>
    public FadeHoldScene(Framebuffer shown, List<byte[]> frames)
    {
        _pixels = (byte[])shown.Pixels.Clone();
        FbWidth = shown.Width;
        _frames = frames;
        _dac = frames.Count > 0 ? frames[0] : new byte[0x300];
    }

    public int FbWidth { get; }
    /// <summary>True once every fade frame has been shown (set by the Update after the last one).</summary>
    public bool Done { get; private set; }
    public int Shown => _i;

    public void Update()
    {
        if (_i < _frames.Count) _dac = _frames[_i++];
        else Done = true;
    }

    public void Draw(Framebuffer fb)
    {
        fb.SetPalette6(_dac, 0, 0, 256);
        Array.Copy(_pixels, fb.Pixels, Math.Min(_pixels.Length, fb.Pixels.Length));
    }
}
