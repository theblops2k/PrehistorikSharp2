using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>OLDIES (240a): credits over black + scene-wait 0bbe (wait fire press, then release; no timeout).</summary>
public sealed class OldiesScene : IntroStage
{
    readonly byte[] _indices;
    readonly byte[] _boot;
    bool _pressed;

    public OldiesScene(FrontEndAssets a)
    {
        _boot = a.Boot;
        _indices = OldiesScreen.ToIndices(OldiesScreen.Build(a.Boot, a.OldiesFont, 0, a.Year));
    }

    public override string Name => "OLDIES";
    public override int PaletteEntries => 16;

    public override void Update(Input input)
    {
        if (Result != StageResult.Running) return;
        bool fire = Fire(input);
        if (!_pressed) { if (fire) _pressed = true; }      // [asm 0bcd] loop until pressed
        else if (!fire) { Result = StageResult.Next; }   // [asm 0bd9] until released
    }

    public override void Draw(Framebuffer fb)
    {
        fb.SetPalette6(_boot, BootImage.DsBase + FrontEndAssets.OldiesPalOff, 0, 16);   // [asm 0b92] table 0x287E
        Array.Copy(_indices, fb.Pixels, _indices.Length);
    }
}
