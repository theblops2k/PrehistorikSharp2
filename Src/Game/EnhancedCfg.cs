namespace Pre2Port.Game;

/// <summary>
/// Optional enhancements read from <c>Enhanced.cfg</c> in the folder of the executable (never created by the port).
/// <c>HudTransparent=0..100</c>: percent of transparency of the whole bottom HUD bar over the game drawn behind it (0 = opaque original, 100 = invisible; 10 = 10% transparent). Missing file / key / invalid value => 0. Keys case-insensitive. <c>Enhaced.cfg</c> (misspelled) is accepted as a fallback if <c>Enhanced.cfg</c> is absent.
/// </summary>
public sealed class EnhancedCfg
{
    public const string FileName = "Enhanced.cfg";
    const string LegacyFileName = "Enhaced.cfg";
    const string HudTransparentKey = "HudTransparent";

    public static EnhancedCfg Current { get; } = Load();

    public int HudTransparent { get; init; }

    static EnhancedCfg Load() => Load(Path.Combine(AppContext.BaseDirectory, FileName));

    internal static EnhancedCfg Load(string path)
    {
        string actual = path;
        if (!File.Exists(actual) && Path.GetFileName(actual).Equals(FileName, StringComparison.OrdinalIgnoreCase))
        {
            string legacy = Path.Combine(Path.GetDirectoryName(actual) ?? "", LegacyFileName);
            if (File.Exists(legacy)) actual = legacy;
        }
        string? raw = null;
        if (File.Exists(actual))
        {
            try
            {
                foreach (string line in File.ReadAllLines(actual))
                {
                    string key = line.Split('=', 2)[0].Trim();
                    if (key.Equals(HudTransparentKey, StringComparison.OrdinalIgnoreCase))
                        raw = line.Split('=', 2)[1].Trim();
                }
            }
            catch (IOException) { raw = null; }
        }
        return new EnhancedCfg { HudTransparent = ParseHudTransparent(raw) };
    }

    internal static int ParseHudTransparent(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return 0;
        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v)) return 0;
        return Math.Clamp(v, 0, 100);
    }
}
