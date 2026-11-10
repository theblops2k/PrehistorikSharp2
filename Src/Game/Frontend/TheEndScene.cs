using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>
/// THE END (1030:5034), reached from 4C69 when [0x6BE5] == 0xFF (the player fell out of the bottom of level 0xE, 5B18-5B1F). In the original order: 447D (mode 0Dh), the load top
/// reset to [0x39], THEEND.SQZ (DX = 0x6BA0) into the stacking buffer, 919F with CL = 0x10 (mode 13h, black DAC, the picture, a 16-entry fade-in), the scene-wait 0BBE (fire pressed AND
/// released, no timeout), 9286 (fade-out of the same 16 entries), 25F6 (the CREATORS photo, <see cref="CreatorsScene"/>), AX = 1 / CF = 1 back to main 012F (menu). No sfx and no song
/// load in this routine: the level song of level 0xE ([0x2D20 + 0xE] = 14, FINAL.TRK) keeps playing until the menu loads PRESENTA.TRK (main 0107 via 013B..).
/// </summary>
public sealed class TheEndScene : TimedTitleStage
{
    public const int Entries = 0x10;

    public TheEndScene(FrontEndAssets a) : base(skippable: false)
    {
        byte[] image = a.Image(a.TheEnd);
        byte[] pal6 = a.Pal6(a.TheEnd);
        var fadeIn = FrontEndFade.FadeIn(pal6, Entries);                  // [919F] CL = 0x10
        foreach (var p in fadeIn) Frames.Add((p, image));
        WaitAfter = Frames.Count - 1;                                     // [5055 -> 0BBE] hold the picture until fire is pressed and released
        foreach (var p in FrontEndFade.FadeOut(fadeIn[^1], Entries)) Frames.Add((p, image));   // [5058 -> 9286]
    }

    public override string Name => "THEEND";
}

/// <summary>
/// The CREATORS photo "HOLLY DAY IN MEYRUES!" (1030:25F6, shown by 505B right after the THE END fade-out when the system year [0x37] is at least 0x7CA = 1994): VGA mode 12h (640x480, 16 colours,
/// planar), DAC entries 0..15 = a grey ramp (0, 4, ... 0x3C on R, G and B), the attribute palette registers identity (table 0x28B0). LEVELH.SQZ is copied to planes 0 and 1 and LEVELI.SQZ to
/// planes 2 and 3 (0x40D8 words = 0x81B0 bytes = 80 bytes x 415 rows each; the rest of the screen stays black), then 0BBE waits for fire pressed and released, 447D restores mode 0Dh.
/// </summary>
public sealed class CreatorsScene : IntroStage
{
    public const int Width = 640, Height = 480, RowBytes = 80, PlaneBytes = 0x81B0;
    readonly byte[] _pixels = new byte[Width * Height];
    readonly byte[] _pal6 = new byte[0x300];
    bool _pressed;

    public CreatorsScene(FrontEndAssets a)
    {
        var planes = new[]
        {
            a.LevelH.AsSpan(0, PlaneBytes).ToArray(), a.LevelH.AsSpan(PlaneBytes, PlaneBytes).ToArray(),      // [2652-2681] map mask 1, 2
            a.LevelI.AsSpan(0, PlaneBytes).ToArray(), a.LevelI.AsSpan(PlaneBytes, PlaneBytes).ToArray(),      // [268D-26BC] map mask 4, 8
        };
        for (int y = 0; y < Height; y++)
            for (int c = 0; c < RowBytes; c++)
            {
                int o = y * RowBytes + c;
                for (int bit = 0; bit < 8; bit++)
                {
                    int sh = 7 - bit, v = 0;
                    for (int p = 0; p < 4; p++)
                        if (o < PlaneBytes) v |= ((planes[p][o] >> sh) & 1) << p;
                    _pixels[y * Width + c * 8 + bit] = (byte)v;
                }
            }
        for (int i = 0; i < 16; i++)                                      // [2636-264B] DAC 0..15: 0, 4, 8 ... 0x3C
            _pal6[i * 3] = _pal6[i * 3 + 1] = _pal6[i * 3 + 2] = (byte)(i * 4);
    }

    public override string Name => "CREATORS";
    public override int PaletteEntries => 16;
    public int FbWidth => Width;
    public int FbHeight => Height;

    public override void Update(Input input)
    {
        if (Result != StageResult.Running) return;
        bool fire = Fire(input);
        if (!_pressed) { if (fire) _pressed = true; }                     // [0BC6-0BCD] loop until pressed
        else if (!fire) Result = StageResult.Next;                        // [0BD2-0BD9] until released
    }

    public override void Draw(Framebuffer fb)
    {
        fb.SetPalette6(_pal6, 0, 0, 16);
        Array.Copy(_pixels, fb.Pixels, Math.Min(_pixels.Length, fb.Pixels.Length));
    }
}
