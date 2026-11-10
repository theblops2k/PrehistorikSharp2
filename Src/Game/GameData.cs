namespace Pre2Port.Game;

/// <summary>
/// Single point that says where the original game data is read from at run time.
/// <see cref="Root"/> = <c>files</c> next to the executable (AppContext.BaseDirectory\files). The launcher copies the files listed in
/// <see cref="Required"/> there from the folder the user picks. The dev commands (play / level / verifiers ...) keep taking their own
/// directory from the command line and do not use <see cref="Root"/>.
/// The boot image (<c>files/boot_image.bin</c>) is NOT part of the original game and nothing ships with the port for it: the launcher generates it from the
/// user's own PRE2.EXE (+ KEYB.SQZ / ALLFONTS.SQZ) with <see cref="Pre2Port.Native.BootBuilder"/> and writes it next to the copied game files.
/// </summary>
public static class GameData
{
    public const string FolderName = "files";
    public const string BootImageName = "boot_image.bin";
    /// <summary>The DOS executable whose start-up state is the boot image (copied to <c>files</c> and read by <see cref="Pre2Port.Native.BootBuilder"/>).</summary>
    public const string ExeName = "PRE2.EXE";
    /// <summary>The compressed wrapper of the executable (CDRUN LZW, <see cref="Pre2Port.Codecs.Sqz.UnpackCdrun"/>): the usual way PRE2.EXE comes in the user's install. Accepted instead of <see cref="ExeName"/> in the picked folder.</summary>
    public const string ExeSqzName = "PRE2.SQZ";
    /// <summary>SHA-256 of the PRE2.EXE this port targets (GOG build, 87452 bytes); the unpacked PRE2.SQZ must match it.</summary>
    public const string ExeSha256 = "4f25a6a32e24b577b64b93e79a161b23d5aadbb46b780d8e1928957e5be10dbb";

    /// <summary>Folder the game reads its data from (always <c>files</c> next to the exe unless a test overrides it).</summary>
    public static string Root { get; set; } = Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>Where the generated boot image lives: <c>files/boot_image.bin</c>.</summary>
    public static string BootImagePath => Path.Combine(Root, BootImageName);

    /// <summary>
    /// Runtime file set (49 files) + what the boot image needs (KEYB.SQZ and PRE2.EXE = 51). Derived from the loaders: LevelLoad (FRONT, LEVEL1..9/A..G by the filename-digit rule, BACK0..5 from the
    /// per-level group table, UNION), SpriteBank (SPRITES), EndScreens (GAMEOVER), AudioFiles/AudioEngine (SAMPLE + 12 .TRK), FrontEndAssets
    /// (FRONT, ALLFONTS, TITUS, PRESENT, THEEND, LEVELH, LEVELI), MenuAssets (MENU, MENU2, MOTIF), MapScenes (MAP).
    /// PRE2.EXE (the DOS executable, not the SQZ wrapper) + KEYB.SQZ + ALLFONTS.SQZ are the inputs of <see cref="Pre2Port.Native.BootBuilder"/>: the start-up code
    /// of the EXE loads KEYB.SQZ and ALLFONTS.SQZ into DGROUP, so KEYB.SQZ is required here although the game itself never shows the keyboard setup screen.
    /// PRE2.SQZ (the compressed EXE wrapper) is accepted in the picked folder INSTEAD of PRE2.EXE (see <see cref="MissingSource"/>); the installer unpacks it to files\PRE2.EXE and never copies it.
    /// NOT needed: CASTLE.SQZ (beginner-level screen, not ported).
    /// </summary>
    public static readonly string[] Required =
    {
        // menu / intro / map / fonts / end screens
        "MENU.SQZ", "MENU2.SQZ", "MOTIF.SQZ", "FRONT.SQZ", "ALLFONTS.SQZ", "TITUS.SQZ", "PRESENT.SQZ", "MAP.SQZ", "SPRITES.SQZ", "UNION.SQZ",
        "GAMEOVER.SQZ", "THEEND.SQZ", "LEVELH.SQZ", "LEVELI.SQZ",
        // levels
        "LEVEL1.SQZ", "LEVEL2.SQZ", "LEVEL3.SQZ", "LEVEL4.SQZ", "LEVEL5.SQZ", "LEVEL6.SQZ", "LEVEL7.SQZ", "LEVEL8.SQZ", "LEVEL9.SQZ",
        "LEVELA.SQZ", "LEVELB.SQZ", "LEVELC.SQZ", "LEVELD.SQZ", "LEVELE.SQZ", "LEVELF.SQZ", "LEVELG.SQZ",
        // level backgrounds
        "BACK0.SQZ", "BACK1.SQZ", "BACK2.SQZ", "BACK3.SQZ", "BACK4.SQZ", "BACK5.SQZ",
        // audio
        "SAMPLE.SQZ", "PRES.TRK", "CODE.TRK", "CARTE.TRK", "PRESENTA.TRK", "GLACE.TRK", "MINES.TRK", "MYSTERY.TRK", "MONSTER.TRK", "FINAL.TRK",
        "BRAVO.TRK", "KOOL.TRK", "BOULA.TRK",
        // inputs of the boot image (BootBuilder): the executable and the keyboard-setup pack its start-up code loads
        "KEYB.SQZ", "PRE2.EXE",
    };

    /// <summary>Required names that only feed the boot image; <see cref="Missing"/> lists them first so the launcher shows them.</summary>
    static readonly HashSet<string> BootInputs = new(StringComparer.OrdinalIgnoreCase) { ExeName, "KEYB.SQZ" };

    /// <summary>Required files absent from <paramref name="dir"/> (case-insensitive name match, like the loaders). Whole list when the folder does not exist.</summary>
    public static List<string> Missing(string dir)
    {
        if (!Directory.Exists(dir)) return Required.OrderBy(r => BootInputs.Contains(r) ? 0 : 1).ToList();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (string f in Directory.EnumerateFiles(dir)) present.Add(Path.GetFileName(f)); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }   // unreadable folder (e.g. a protected system folder under the one picked): counts as empty
        return Required.Where(r => !present.Contains(r)).OrderBy(r => BootInputs.Contains(r) ? 0 : 1).ToList();   // stable: PRE2.EXE / KEYB.SQZ first
    }

    /// <summary>
    /// Like <see cref="Missing"/> but for a folder the user picks as SOURCE: PRE2.EXE counts as present when the folder has PRE2.SQZ (the installer unpacks it).
    /// KEYB.SQZ and everything else stay required.
    /// </summary>
    public static List<string> MissingSource(string dir)
    {
        var m = Missing(dir);
        if (m.Contains(ExeName) && FindFile(dir, ExeSqzName) is not null) m.Remove(ExeName);
        return m;
    }

    /// <summary>Path of <paramref name="name"/> in <paramref name="dir"/> (case-insensitive); null when absent or the folder is unreadable.</summary>
    public static string? FindFile(string dir, string name)
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir))
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
        return null;
    }

    /// <summary>True when <see cref="Root"/> holds every required file and the generated boot image.</summary>
    public static bool IsComplete() => Missing(Root).Count == 0 && File.Exists(BootImagePath);

    /// <summary>The generated boot image (<c>files/boot_image.bin</c>); null when it does not exist yet.</summary>
    public static string? FindBootImage() => File.Exists(BootImagePath) ? BootImagePath : null;
}
