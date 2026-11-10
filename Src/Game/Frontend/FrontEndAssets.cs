using Pre2Port.Codecs;
using Pre2Port.Native;

namespace Pre2Port.Game.Frontend;

/// <summary>
/// Audio extension point. The intro only announces what the original loads; nothing is played yet.
/// TODO(audio): implement with the SAMPLE.SQZ SFX bank and the PRESENTA.TRK module player.
/// </summary>
public interface IFrontEndAudio
{
    /// <summary>[asm 07C9] after OLDIES: load the SFX sample bank (SAMPLE.SQZ).</summary>
    void LoadSfxBank(string file);
    /// <summary>[asm 02cc] at the PRESENT title: load the title song (PRESENTA.TRK).</summary>
    void LoadSong(string file);
}

public sealed class NullFrontEndAudio : IFrontEndAudio
{
    public void LoadSfxBank(string file) { }
    public void LoadSong(string file) { }
}

/// <summary>Assets of the intro stretch, decoded from the Original folder with <see cref="Sqz"/>.</summary>
public sealed class FrontEndAssets
{
    public const int ImageOff = 0x300, ImageLen = 64000, LogoOff = 0x10300, LogoLen = 29760;
    public const int OldiesPalOff = 0x287E, MorphSrcOff = 0xACE7;

    public byte[] Boot { get; }
    /// <summary>FRONT.SQZ (HUD front panel), permanently loaded by main() before the sprite bank; kept for later stretches.</summary>
    public byte[] Front { get; }
    /// <summary>ALLFONTS.SQZ decoded (glyph fonts used by draw_string; not used by the intro itself).</summary>
    public byte[] AllFonts { get; }
    public byte[] Titus { get; }
    public byte[] Present { get; }
    /// <summary>OLDIES 8x12 font as the VM has it in DGROUP[0x3D] (== ALLFONTS[0..0x1800] except 2 patched bytes).</summary>
    public byte[] OldiesFont { get; }

    /// <summary>Year drawn by OLDIES. Game mode: the real clock; verification injects 2026 (the boot image's value).</summary>
    public int Year { get; }

    readonly string _dir;
    byte[]? _theEnd, _levelH, _levelI;
    byte[] LoadLazy(string name) => Sqz.Unpack(File.ReadAllBytes(Path.Combine(_dir, name)));
    /// <summary>THEEND.SQZ decoded ([asm 5034] DX = 0x6BA0): the same 16-colour 13h layout as TITUS.SQZ (6-bit palette 0x300 bytes + 64000 pixels).</summary>
    public byte[] TheEnd => _theEnd ??= LoadLazy("THEEND.SQZ");
    /// <summary>LEVELH.SQZ / LEVELI.SQZ decoded: the CREATORS photo planes (25F6: H = planes 0,1, I = planes 2,3; 0x81B0 bytes each).</summary>
    public byte[] LevelH => _levelH ??= LoadLazy("LEVELH.SQZ");
    public byte[] LevelI => _levelI ??= LoadLazy("LEVELI.SQZ");

    FrontEndAssets(byte[] boot, string dir, int? year)
    {
        _dir = dir;
        Boot = boot;
        Year = year ?? DateTime.Now.Year;
        byte[] Load(string n) => Sqz.Unpack(File.ReadAllBytes(Path.Combine(dir, n)));
        Front = Load("FRONT.SQZ");
        AllFonts = Load("ALLFONTS.SQZ");
        Titus = Load("TITUS.SQZ");
        Present = Load("PRESENT.SQZ");
        OldiesFont = OldiesScreen_BootFont(boot);
    }

    static byte[] OldiesScreen_BootFont(byte[] boot) => Recovered.OldiesScreen.BootFont(boot);

    public static FrontEndAssets Load(byte[] boot, string originalDir, int? year = null) => new(boot, originalDir, year);

    /// <summary>Number of OLDIES-font bytes that differ from ALLFONTS.SQZ (2 observed: glyph '1', bytes 13..14).</summary>
    public int FontPatchDiffs()
    {
        int n = 0;
        for (int i = 0; i < OldiesFont.Length && i < AllFonts.Length; i++)
            if (OldiesFont[i] != AllFonts[i]) n++;
        return n;
    }

    public byte[] Pal6(byte[] decoded)
    {
        var p = new byte[0x300];
        for (int i = 0; i < p.Length; i++) p[i] = (byte)(decoded[i] & 0x3F);
        return p;
    }

    public byte[] Image(byte[] decoded) => decoded.AsSpan(ImageOff, ImageLen).ToArray();

    public byte[] PresentFull()
    {
        var img = Image(Present);
        Array.Copy(Present, LogoOff, img, 0, LogoLen);     // [asm 9090] logo-top over the first 93 rows
        return img;
    }

    /// <summary>6-bit morph target palette at DGROUP 0xACE7.</summary>
    public byte[] MorphTarget()
    {
        var t = new byte[0x300];
        for (int i = 0; i < t.Length; i++) t[i] = (byte)(Boot[BootImage.DsBase + MorphSrcOff + i] & 0x3F);
        return t;
    }
}
