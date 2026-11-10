using Pre2Port.Codecs;
using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Frontend;

/// <summary>Assets of the press-1/2 menu stretch (MENU.SQZ, MENU2.SQZ, MOTIF.SQZ, map palette at DGROUP 0xB118).</summary>
public sealed class MenuAssets
{
    public const int ImageOff = 0x300, ImageLen = 64000, FadeEntries = 0xA0, MapPalOff = 0xB118, MapPalEntries = 0x10;
    public const int MotifLen = 0x3E80, PlaneLen = 8000;

    /// <summary>MENU.SQZ 13h image (64000 px).</summary>
    public byte[] MenuImage { get; }
    /// <summary>One 0x300-byte 6-bit palette per tick of the 0xA0-entry fade-in (919F).</summary>
    public List<byte[]> MenuFade { get; }
    /// <summary>MENU2.SQZ (attract title) base, deplanarized to 64000 px.</summary>
    public byte[] AttractImage { get; }
    /// <summary>MENU2.SQZ 16-entry 6-bit palette (48 bytes).</summary>
    public byte[] AttractPal6 { get; }
    /// <summary>MENU2.SQZ as the four 8000-byte EGA planes (the animation's base, page 0x2000).</summary>
    public byte[][] AttractPlanes { get; }
    /// <summary>[asm 8E98 -> 9286] the menu fading to black before the attract: one 0x300-byte 6-bit DAC per retrace, 0xA0 entries ([0x9153] from the menu's 919F call).</summary>
    public List<byte[]> MenuFadeOut { get; }
    /// <summary>[asm 9155 -> 91CF] the MENU2 palette (16 entries, rest black) faded in from black, 0xA0 entries; the last frame is the DAC held during the animation.</summary>
    public List<byte[]> AttractFadeIn { get; }
    /// <summary>[asm 8EB3 -> 9286] the held title DAC fading to black (0xA0 entries).</summary>
    public List<byte[]> AttractFadeOut { get; }
    /// <summary>[asm 8F29 -> 9286] a second fade over the already black DAC: one pass, 5 retraces of black.</summary>
    public List<byte[]> AttractBlack { get; }
    /// <summary>MOTIF.SQZ[:0x3E80], the map background seed.</summary>
    public byte[] Motif { get; }
    /// <summary>Map 16-entry 6-bit palette from boot image DGROUP 0xB118 (48 bytes).</summary>
    public byte[] MapPal6 { get; }

    readonly Lazy<MapAssets> _map;
    readonly byte[] _boot;
    readonly string _dir;
    /// <summary>World-map stretch data (font, carte master/marker, texts); built on first use (decodes SPRITES.SQZ and MAP.SQZ).</summary>
    public MapAssets Map => _map.Value;

    MenuAssets(byte[] boot, string dir)
    {
        _boot = boot;
        _dir = dir;
        _map = new Lazy<MapAssets>(() => MapAssets.Load(boot, dir));
        byte[] Load(string n) => Sqz.Unpack(File.ReadAllBytes(Path.Combine(dir, n)));
        var menu = Load("MENU.SQZ");
        MenuImage = menu.AsSpan(ImageOff, ImageLen).ToArray();
        var pal = new byte[0x300];
        for (int i = 0; i < pal.Length; i++) pal[i] = (byte)(menu[i] & 0x3F);
        MenuFade = FrontEndFade.FadeIn(pal, FadeEntries);

        var m2 = Load("MENU2.SQZ");                          // 4 EGA planes x 8000 bytes + 48-byte palette
        var planes = new byte[4][];
        for (int p = 0; p < 4; p++) planes[p] = m2.AsSpan(p * PlaneLen, PlaneLen).ToArray();
        AttractPlanes = planes;
        AttractImage = MenuScenePage.Deplanarize(planes);
        AttractPal6 = new byte[48];
        for (int i = 0; i < 48; i++) AttractPal6[i] = (byte)(m2[4 * PlaneLen + i] & 0x3F);

        var titleDac = new byte[0x300];                      // [asm 9187-9193] the 16-entry palette followed by zeros
        Array.Copy(AttractPal6, titleDac, 48);
        MenuFadeOut = FrontEndFade.FadeOut(MenuFade[^1], FadeEntries);
        AttractFadeIn = FrontEndFade.FadeIn(titleDac, FadeEntries);
        AttractFadeOut = FrontEndFade.FadeOut(AttractFadeIn[^1], FadeEntries);
        AttractBlack = FrontEndFade.FadeOut(new byte[0x300], FadeEntries);

        Motif = Load("MOTIF.SQZ").AsSpan(0, MotifLen).ToArray();
        MapPal6 = new byte[48];
        for (int i = 0; i < 48; i++) MapPal6[i] = (byte)(boot[BootImage.DsBase + MapPalOff + i] & 0x3F);
    }

    /// <summary>A fresh level-0 cold-boot state (sprite bank + attribute tables) for the attract title animation; it is mutated by <see cref="AttractTitle"/>.</summary>
    public LevelState NewTitleState() => LevelLoad.ColdBoot(_boot, _dir, 0);

    public static MenuAssets Load(byte[] boot, string originalDir) => new(boot, originalDir);
}
