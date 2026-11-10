using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>
/// PRESENT (9090): fade-in 256 over the background, palette morph (911D) over background+logo toward DGROUP 0xACE7,
/// then (waitKey) hold for a key press, then fade-out (n=0x100). The key wait is a deliberate divergence from Python.
/// </summary>
public sealed class PresentScene : TimedTitleStage
{
    public PresentScene(FrontEndAssets a, bool skippable, bool waitKey) : base(skippable)
    {
        byte[] background = a.Image(a.Present);
        byte[] full = a.PresentFull();
        byte[] pal6 = a.Pal6(a.Present);
        var fadeIn = FrontEndFade.FadeIn(pal6, 0xFF);
        byte[] held = fadeIn[^1];
        foreach (var p in fadeIn) Frames.Add((p, background));
        int phase = held[(0xFF - 1) * 3 + 1];                  // BL left by the fade-in = green of entry 0xFE
        var morph = FrontEndFade.PaletteMorph(held, a.MorphTarget(), phase);
        foreach (var p in morph) Frames.Add((p, full));
        // DELIBERATE DIVERGENCE from the Python reference (user decision): Python goes straight from the morph into
        // the fade-out; here the revealed title is held until a fire press+release (scene-wait style) first.
        if (waitKey) WaitAfter = Frames.Count - 1;
        foreach (var p in FrontEndFade.FadeOut(morph[^1], 0x100)) Frames.Add((p, full));
    }

    public override string Name => "PRESENT";
}
