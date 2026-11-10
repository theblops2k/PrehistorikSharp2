namespace Pre2Port.Game;

/// <summary>
/// Startup settings read from <c>Prehistorik2.cfg</c> in the folder of <c>Pre2Port.exe</c>.
/// Keys are case-insensitive. A missing file is created, and a missing key line is appended
/// with its default. Invalid values fall back to the default without rewriting the line.
/// <list type="bullet">
/// <item><c>FULLSCREEN=NO|ORIGINAL|WIDESCREEN</c>: <c>NO</c> (default; also missing/empty/unrecognised) 960x600 window (3x, not resizable, 320x200); <c>ORIGINAL</c> fullscreen fit keeping 320x200 proportions (explicit only);
/// <c>WIDESCREEN</c> fullscreen with a wider level view, OLDIES, TITUS, PRESENT, main menu, mode-select/password map, CARTE, the attract title, GAME OVER / tally and THE END (the demo fade keeps the width of the frame it freezes; only the CREATORS photo stays 640x480). Case-insensitive.</item>
/// <item><c>FPS=n</c>: window redraw rate (default 60, 1..1000). Logic keeps its original 70/3 ticks per second. The ESC overlay (Graphics > Max FPS) reads and writes this same key.</item>
/// <item><c>FILTERSCALING=OFF|BILINEAR|SHARPBILINEAR|FSR</c>, <c>FILTERPIXELART=OFF|XBR|SCALE3X</c>, <c>FILTEREFFECTS=OFF|VIBRANCE|SCANLINES</c>: graphic filters
/// (ESC overlay &gt; Graphics), one per group, default OFF. Same filters as CoolSpotPort.</item>
/// <item><c>Vsync=ON|OFF</c>: vertical sync (ESC overlay &gt; Graphics &gt; V-Sync), default OFF. The line is appended as <c>Vsync=OFF</c> when missing.</item>
/// <item><c>level=n</c> (hidden: never written by the port or shown in the overlay): level where a new game from the menu starts, 1 = first. Absent / non-numeric / &lt;= 0 -> 1; above the last playable level -> that level
/// (see <see cref="ParseStartLevel"/>). Only the plain "1-START" path uses it, not a password, the demo or the CARTE.</item>
/// <item><c>LivesAtStartup</c>, <c>Volume</c>, <c>ShowFps</c>: read and written by <see cref="Options.GameOptions"/> in this same file (they used to live in Prehistorik.cfg, migrated on first run).</item>
/// </list>
/// </summary>
public enum ScreenMode { No, Original, Widescreen }

public sealed class Settings
{
    public const string FileName = "Prehistorik2.cfg";
    const string FullscreenKey = "FULLSCREEN", FpsKey = "FPS";
    const string FilterScalingKey = "FILTERSCALING", FilterPixelArtKey = "FILTERPIXELART", FilterEffectsKey = "FILTEREFFECTS";
    const string VsyncKey = "Vsync";
    public const int DefaultFps = 60, MaxFps = 1000;

    /// <summary>Settings read once per process from the cfg next to the executable.</summary>
    public static Settings Current { get; } = Load();

    /// <summary>Screen mode from FULLSCREEN (NO / ORIGINAL / WIDESCREEN).</summary>
    public ScreenMode Mode { get; set; } = ScreenMode.No;

    /// <summary>Fullscreen (ORIGINAL or WIDESCREEN).</summary>
    public bool Fullscreen => Mode != ScreenMode.No;

    /// <summary>Effective widescreen: only FULLSCREEN=WIDESCREEN. Everything must use this.</summary>
    public bool WidescreenActive => Mode == ScreenMode.Widescreen;

    /// <summary>Window redraw rate in Hz.</summary>
    public int Fps { get; set; } = DefaultFps;

    /// <summary>Graphic filters (one per group), edited by the overlay.</summary>
    public Options.FilterScaling FilterScaling { get; set; } = Options.FilterScaling.Off;
    public Options.FilterPixelArt FilterPixelArt { get; set; } = Options.FilterPixelArt.Off;
    public Options.FilterEffects FilterEffects { get; set; } = Options.FilterEffects.Off;

    /// <summary>Vertical sync (Vsync=ON/OFF, default OFF). Applied to the window by the Presenter.</summary>
    public bool Vsync { get; set; }

    /// <summary>Raw text of the hidden <c>level</c> key (null when the line is absent). Never appended to the file.</summary>
    public string? StartLevelRaw { get; private set; }

    /// <summary>
    /// 1-based start level from the hidden <c>level</c> key: 1 when <paramref name="raw"/> is null / not an integer / &lt;= 0, <paramref name="maxLevel"/> when it is larger (also for a number that overflows int), otherwise the value.
    /// </summary>
    public static int ParseStartLevel(string? raw, int maxLevel)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw)) return 1;
        if (!System.Numerics.BigInteger.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n)) return 1;
        if (n <= 0) return 1;
        return n > maxLevel ? maxLevel : (int)n;
    }

    /// <summary>Full path of the cfg this instance was read from (the overlay writes FPS and the filter keys back to it).</summary>
    public string FilePath { get; private set; } = "";

    /// <summary>Writes FPS, FULLSCREEN and the three filter keys, keeping every other line (comments).</summary>
    public void SaveOverlayKeys()
    {
        if (FilePath.Length == 0) return;
        var f = new Options.CfgFile(FilePath);
        f.Set(FpsKey, Fps.ToString());
        f.Set(FullscreenKey, Mode.ToString().ToUpperInvariant());
        f.Set(FilterScalingKey, FilterScaling.ToString().ToUpperInvariant());
        f.Set(FilterPixelArtKey, FilterPixelArt.ToString().ToUpperInvariant());
        f.Set(FilterEffectsKey, FilterEffects.ToString().ToUpperInvariant());
        f.Set(VsyncKey, Vsync ? "ON" : "OFF");
        f.Save();
    }

    static Settings Load() => Load(Path.Combine(AppContext.BaseDirectory, FileName));

    public static Settings Load(string path)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();

        // Obsolete key from older versions: dropped from the file on load (never written, never read).
        bool removed = lines.RemoveAll(l => KeyOf(l).Equals("WIDESCREEN", StringComparison.OrdinalIgnoreCase)) > 0;
        int before = lines.Count;
        string fullscreenRaw = Ensure(lines, FullscreenKey, "NO");
        string fpsRaw = Ensure(lines, FpsKey, DefaultFps.ToString());
        string fsRaw = Ensure(lines, FilterScalingKey, "OFF");
        string faRaw = Ensure(lines, FilterPixelArtKey, "OFF");
        string feRaw = Ensure(lines, FilterEffectsKey, "OFF");
        string vsRaw = Ensure(lines, VsyncKey, "OFF");
        bool normalized = false;   // legacy T/F -> ON/OFF, written back once
        if (vsRaw.Equals("T", StringComparison.OrdinalIgnoreCase) || vsRaw.Equals("F", StringComparison.OrdinalIgnoreCase))
        {
            vsRaw = vsRaw.Equals("T", StringComparison.OrdinalIgnoreCase) ? "ON" : "OFF";
            int vi = lines.FindIndex(l => KeyOf(l).Equals(VsyncKey, StringComparison.OrdinalIgnoreCase));
            if (vi >= 0) lines[vi] = $"{VsyncKey}={vsRaw}";
            normalized = true;
        }
        if (removed || normalized || lines.Count != before || !File.Exists(path))
        {
            try
            {
                File.WriteAllLines(path, lines);
            }
            catch (IOException e)
            {
                Console.Error.WriteLine($"settings: {path} not written ({e.Message}); using defaults for missing keys");
            }
        }

        var mode = fullscreenRaw.Equals("ORIGINAL", StringComparison.OrdinalIgnoreCase) ? ScreenMode.Original
            : fullscreenRaw.Equals("WIDESCREEN", StringComparison.OrdinalIgnoreCase) ? ScreenMode.Widescreen
            : ScreenMode.No;   // NO, empty or unrecognised
        int fps = int.TryParse(fpsRaw, out int f) && f >= 1 && f <= MaxFps ? f : DefaultFps;
        int li = lines.FindIndex(l => KeyOf(l).Equals("level", StringComparison.OrdinalIgnoreCase));   // hidden key: read only, never ensured/appended
        return new Settings
        {
            Mode = mode, Fps = fps, FilePath = path,
            StartLevelRaw = li >= 0 ? lines[li].Split('=', 2)[1].Trim() : null,
            FilterScaling = ParseEnum(fsRaw, Options.FilterScaling.Off),
            FilterPixelArt = ParseEnum(faRaw, Options.FilterPixelArt.Off),
            FilterEffects = ParseEnum(feRaw, Options.FilterEffects.Off),
            Vsync = vsRaw.Equals("ON", StringComparison.OrdinalIgnoreCase) || vsRaw.Equals("T", StringComparison.OrdinalIgnoreCase)
                || vsRaw.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || vsRaw.Equals("YES", StringComparison.OrdinalIgnoreCase) || vsRaw == "1",
        };
    }

    static T ParseEnum<T>(string v, T def) where T : struct, Enum => Enum.TryParse<T>(v.Replace(" ", ""), true, out var r) && Enum.IsDefined(r) ? r : def;

    /// <summary>Return the value of <paramref name="key"/>; if the key is absent, append "key=default" and return the default.</summary>
    static string Ensure(List<string> lines, string key, string defaultValue)
    {
        int idx = lines.FindIndex(l => KeyOf(l).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) return lines[idx].Split('=', 2)[1].Trim();
        lines.Add($"{key}={defaultValue}");
        return defaultValue;
    }

    static string KeyOf(string line)
    {
        var parts = line.Split('=', 2);
        return parts.Length == 2 ? parts[0].Trim() : string.Empty;
    }
}
