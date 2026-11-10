using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>TITUS (912b): fade-in (16 entries), hold 70 retraces, fade-out; optional fresh-fire skip.</summary>
public sealed class TitusScene : TimedTitleStage
{
    public const int Entries = 0x10, Hold = 70;

    public TitusScene(FrontEndAssets a, bool skippable) : base(skippable)
    {
        byte[] image = a.Image(a.Titus);
        byte[] pal6 = a.Pal6(a.Titus);
        var fadeIn = FrontEndFade.FadeIn(pal6, Entries);
        byte[] held = fadeIn[^1];
        foreach (var p in fadeIn) Frames.Add((p, image));
        for (int i = 0; i < Hold; i++) Frames.Add((held, image));
        foreach (var p in FrontEndFade.FadeOut(held, Entries)) Frames.Add((p, image));
    }

    public override string Name => "TITUS";
}
