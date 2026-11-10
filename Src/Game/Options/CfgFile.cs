namespace Pre2Port.Game.Options;

/// <summary>
/// Tiny KEY=VALUE text file shared by the option files (<c>Prehistorik2.cfg</c> for the game and the overlay, <c>Controller.cfg</c> for the controls).
/// Keys are case-insensitive; lines starting with '#' and unknown lines are kept untouched; <see cref="Set"/> replaces the first line of the key or appends one.
/// </summary>
public sealed class CfgFile
{
    public string Path { get; }
    public List<string> Lines { get; }

    public CfgFile(string path)
    {
        Path = path;
        Lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
    }

    public static string KeyOf(string line)
    {
        if (line.TrimStart().StartsWith('#')) return string.Empty;
        var parts = line.Split('=', 2);
        return parts.Length == 2 ? parts[0].Trim() : string.Empty;
    }

    int Find(string key) => Lines.FindIndex(l => KeyOf(l).Equals(key, StringComparison.OrdinalIgnoreCase));

    public bool Has(string key) => Find(key) >= 0;

    public string? Get(string key)
    {
        int i = Find(key);
        return i < 0 ? null : Lines[i].Split('=', 2)[1].Trim();
    }

    /// <summary>Value of <paramref name="key"/>; if absent, appends "key=default" (marks the file dirty) and returns the default.</summary>
    public string Ensure(string key, string defaultValue)
    {
        string? v = Get(key);
        if (v is not null) return v;
        Lines.Add($"{key}={defaultValue}");
        Dirty = true;
        return defaultValue;
    }

    public bool Dirty { get; private set; }

    public void Set(string key, string value)
    {
        int i = Find(key);
        string line = $"{key}={value}";
        if (i >= 0)
        {
            if (Lines[i] == line) return;
            Lines[i] = line;   // key spelling normalised, value replaced
        }
        else Lines.Add(line);
        Dirty = true;
    }

    /// <summary>Writes the file if anything changed or it did not exist. Returns false (and reports) on an IO error.</summary>
    public bool Save(bool force = false)
    {
        if (!Dirty && !force && File.Exists(Path)) return true;
        try
        {
            File.WriteAllLines(Path, Lines);
            Dirty = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"cfg: {Path} not written ({e.Message})");
            return false;
        }
    }

    /// <summary>One-shot: load <paramref name="path"/>, set the key, save.</summary>
    public static void SetKey(string path, string key, string value)
    {
        var f = new CfgFile(path);
        f.Set(key, value);
        f.Save();
    }
}
