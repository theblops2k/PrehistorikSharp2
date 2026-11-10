namespace Pre2Port.Game.Options;

/// <summary>
/// Options of the ESC overlay stored in <c>Prehistorik2.cfg</c> next to the executable (AppContext.BaseDirectory), the same file as <see cref="Settings"/>: <c>LivesAtStartup</c> (3..10, default 3),
/// <c>Volume</c> (0..100 %, default 100) and <c>ShowFps</c> (YES/NO, default NO). Keys are case-insensitive; a missing file is created and a missing key is
/// appended with its default; an invalid value falls back to the default without rewriting the line. The max FPS is NOT here: it is the <c>FPS</c> key read by <see cref="Settings"/>.
/// The old <c>Prehistorik.cfg</c> is no longer used: if it sits next to the cfg, <see cref="Load"/> copies its keys that the cfg does not have yet (never overwriting) and renames it to <c>Prehistorik.cfg.migrated</c>, so it is never read again.
/// </summary>
public sealed class GameOptions
{
    public const string FileName = Settings.FileName;   // Prehistorik2.cfg: the single cfg of the game (Controller.cfg stays apart)
    public const string LegacyFileName = "Prehistorik.cfg", MigratedSuffix = ".migrated";
    public const string LivesKey = "LivesAtStartup", VolumeKey = "Volume", ShowFpsKey = "ShowFps";
    public const int MinLives = 3, MaxLives = 10, DefaultLives = 3, DefaultVolume = 100;

    /// <summary>Options read once per process; the overlay edits it and saves on every change.</summary>
    public static GameOptions Current { get; } = Load(Path.Combine(AppContext.BaseDirectory, FileName));

    public string FilePath { get; private set; } = "";
    public int LivesAtStartup { get; set; } = DefaultLives;
    public int Volume { get; set; } = DefaultVolume;
    public bool ShowFps { get; set; }

    /// <summary>Lives value stored in the level state ([0x27D8]): the original start block writes 2 (= "3 lives": the game over triggers when it is already 0).</summary>
    public int StartLivesState => Math.Clamp(LivesAtStartup, MinLives, MaxLives) - 1;

    public static GameOptions Load(string path)
    {
        var f = new CfgFile(path);
        MigrateLegacy(f);
        string lives = f.Ensure(LivesKey, DefaultLives.ToString());
        string vol = f.Ensure(VolumeKey, DefaultVolume.ToString());
        string fps = f.Ensure(ShowFpsKey, "NO");
        f.Save(force: !File.Exists(path));
        return new GameOptions
        {
            FilePath = path,
            LivesAtStartup = int.TryParse(lives, out int l) && l >= MinLives && l <= MaxLives ? l : DefaultLives,
            Volume = int.TryParse(vol, out int v) && v >= 0 && v <= 100 ? v : DefaultVolume,
            ShowFps = ParseBool(fps),
        };
    }

    /// <summary>
    /// One-shot migration of the old Prehistorik.cfg (same folder as the cfg): every KEY=VALUE it has that the cfg lacks is copied (existing keys win), the cfg is saved
    /// and the old file is renamed to Prehistorik.cfg.migrated so it is not read again. If the rename fails the keys are already in the cfg, so a later read changes nothing.
    /// </summary>
    static void MigrateLegacy(CfgFile f)
    {
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(f.Path)) ?? "";
        string legacy = System.IO.Path.Combine(dir, LegacyFileName);
        if (!File.Exists(legacy) || string.Equals(System.IO.Path.GetFullPath(legacy), System.IO.Path.GetFullPath(f.Path), StringComparison.OrdinalIgnoreCase)) return;
        foreach (string line in new CfgFile(legacy).Lines)
        {
            string key = CfgFile.KeyOf(line);
            if (key.Length == 0 || f.Has(key)) continue;
            f.Set(key, line.Split('=', 2)[1].Trim());
        }
        if (!f.Save(force: true)) return;   // cfg not written: keep the old file for the next run
        try { File.Move(legacy, legacy + MigratedSuffix, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"cfg: {legacy} not renamed ({e.Message})"); }
    }

    static bool ParseBool(string s) => s.Equals("YES", StringComparison.OrdinalIgnoreCase) || s.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
        || s.Equals("T", StringComparison.OrdinalIgnoreCase) || s.Equals("ON", StringComparison.OrdinalIgnoreCase) || s == "1";

    /// <summary>Writes the three keys (other lines of the file are preserved).</summary>
    public void Save()
    {
        if (FilePath.Length == 0) return;
        var f = new CfgFile(FilePath);
        f.Set(LivesKey, Math.Clamp(LivesAtStartup, MinLives, MaxLives).ToString());
        f.Set(VolumeKey, Math.Clamp(Volume, 0, 100).ToString());
        f.Set(ShowFpsKey, ShowFps ? "YES" : "NO");
        f.Save();
    }
}
