using Pre2Port.Codecs;

namespace Pre2Port.Game.Level;

/// <summary>HUD chrome + glyph font from ALLFONTS.SQZ (pre2/views/hud_chrome.py load_hud_chrome). The font is the whole decoded blob (glyphs at 0x1610).</summary>
public sealed class HudAssets
{
    const int BarOffset = 0x07B0, BarLength = 0x0E60;     // 320x23 planar, 4 planes x 0x398
    static readonly Dictionary<string, HudAssets> Cache = new();

    public byte[] Bar { get; }
    public byte[] Font { get; }

    HudAssets(byte[] decoded)
    {
        Font = decoded;
        Bar = decoded[BarOffset..(BarOffset + BarLength)];
    }

    public static HudAssets Load(string originalDir)
    {
        string path = Path.Combine(originalDir, "ALLFONTS.SQZ");
        lock (Cache)
        {
            if (!Cache.TryGetValue(path, out var a))
                Cache[path] = a = new HudAssets(Sqz.Unpack(File.ReadAllBytes(path)));
            return a;
        }
    }
}
