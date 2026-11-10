using System.Security.Cryptography;
using Pre2Port.Codecs;
using Pre2Port.Native;

namespace Pre2Port.Game.Launcher;

/// <summary>
/// Copies the required original game files (<see cref="GameData.Required"/>) from a folder the user picked into <see cref="GameData.Root"/>
/// and validates every copy. Pure logic (no Raylib): the launcher scene polls the progress properties while <see cref="Start"/> runs it on a
/// worker thread; tests call <see cref="Run"/> directly.
/// Validation per file: size and SHA-256 of the written copy equal the source's, and the SQZ/TRK container decodes (Sqz.Unpack) to a non-empty buffer (PRE2.EXE: MZ header).
/// PRE2.EXE may come as the real file or, when the source has only PRE2.SQZ (CDRUN LZW wrapper), it is unpacked with <see cref="Sqz.UnpackCdrun"/> to <c>files\PRE2.EXE</c>
/// and checked against <see cref="GameData.ExeSha256"/>; PRE2.SQZ itself is not copied.
/// Last step: <c>boot_image.bin</c> is generated from the copied PRE2.EXE with <see cref="BootBuilder"/> and checked against its known SHA-256.
/// </summary>
public sealed class GameInstaller
{
    public enum Phase { Idle, Running, Done, Failed }

    readonly object _lock = new();
    Phase _phase = Phase.Idle;
    string _current = "";
    string? _error;
    int _filesDone;
    long _bytesDone, _bytesTotal;

    public Phase State { get { lock (_lock) return _phase; } }
    public string CurrentFile { get { lock (_lock) return _current; } }
    public string? Error { get { lock (_lock) return _error; } }
    public int FilesDone { get { lock (_lock) return _filesDone; } }
    public int FilesTotal => GameData.Required.Length;
    /// <summary>0..1: each file counts once for the copy and once for the validation.</summary>
    public double Progress { get { lock (_lock) return _bytesTotal <= 0 ? 0 : Math.Clamp(_bytesDone / (double)(2 * _bytesTotal), 0, 1); } }
    public long BytesTotal { get { lock (_lock) return _bytesTotal; } }

    /// <summary>
    /// Looks for the game in <paramref name="dir"/> and, failing that, in its sub-folders (up to 2 levels), so that picking the install folder
    /// of the DOS game (or a folder that contains it) works. Returns the folder with every required file, else the best candidate (fewest
    /// missing files) with its missing list.
    /// </summary>
    public static string FindGameFolder(string dir, out List<string> missing)
    {
        string best = dir;
        missing = GameData.MissingSource(dir);
        if (missing.Count == 0 || !Directory.Exists(dir)) return best;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((dir, 0));
        while (queue.Count > 0)
        {
            var (p, depth) = queue.Dequeue();
            if (depth >= 2) continue;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(p).ToList(); }
            catch (Exception) { continue; }
            foreach (string s in subs)
            {
                var m = GameData.MissingSource(s);
                if (m.Count < missing.Count) { best = s; missing = m; }
                if (m.Count == 0) return s;
                queue.Enqueue((s, depth + 1));
            }
        }
        return best;
    }

    /// <summary>Start the copy on a worker thread. <paramref name="sourceDir"/> must already hold every required file.</summary>
    public void Start(string sourceDir, string destDir)
    {
        lock (_lock)
        {
            if (_phase == Phase.Running) return;
            _phase = Phase.Running;
            _error = null;
            _filesDone = 0;
            _bytesDone = 0;
            _current = "";
        }
        new Thread(() => Run(sourceDir, destDir)) { IsBackground = true, Name = "GameInstaller" }.Start();
    }

    /// <summary>Synchronous copy + validation. Returns true on success; on failure <see cref="Error"/> says why.</summary>
    public bool Run(string sourceDir, string destDir)
    {
        lock (_lock) { _phase = Phase.Running; _error = null; _filesDone = 0; _bytesDone = 0; _current = ""; }
        try
        {
            var missing = GameData.MissingSource(sourceDir);
            if (missing.Count > 0) throw new FileNotFoundException("Faltan archivos: " + string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? $" (+{missing.Count - 4})" : ""));
            Directory.CreateDirectory(destDir);
            // resolve the real source names (case-insensitive) and total size
            var srcFiles = new List<(string Name, string Path)>();
            var byName = Directory.EnumerateFiles(sourceDir).ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (string name in GameData.Required)
            {
                // PRE2.EXE: the real file when present, else the PRE2.SQZ wrapper (unpacked below)
                string sp = byName.TryGetValue(name, out string? found) ? found : byName[GameData.ExeSqzName];
                srcFiles.Add((name, sp));
                total += new FileInfo(sp).Length;
            }
            lock (_lock) _bytesTotal = total;

            bool same = string.Equals(Path.GetFullPath(sourceDir).TrimEnd('\\', '/'), Path.GetFullPath(destDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            foreach (var (name, sp) in srcFiles)
            {
                lock (_lock) _current = name;
                string dp = Path.Combine(destDir, name);
                string tmp = dp + ".part";
                byte[] srcHash;
                long len;
                if (string.Equals(name, GameData.ExeName, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileName(sp), GameData.ExeSqzName, StringComparison.OrdinalIgnoreCase))
                {
                    UnpackExe(sp, dp, tmp);
                    len = new FileInfo(sp).Length;
                    Add(2 * len);                    // counted as copy + validation
                    lock (_lock) _filesDone++;
                    continue;
                }
                if (same)
                {
                    len = new FileInfo(sp).Length;
                    srcHash = HashFile(sp);
                    Add(len);
                }
                else
                {
                    (srcHash, len) = CopyWithHash(sp, tmp);
                    ReplaceFile(tmp, dp);
                }
                // validation of the written copy
                if (new FileInfo(dp).Length != len) throw new InvalidDataException($"{name}: tamaño distinto tras copiar");
                byte[] dstHash = HashFile(dp);
                if (!dstHash.AsSpan().SequenceEqual(srcHash)) throw new InvalidDataException($"{name}: la copia no coincide con el original");
                if (string.Equals(name, GameData.ExeName, StringComparison.OrdinalIgnoreCase))
                {
                    byte[] exe = File.ReadAllBytes(dp);
                    if (exe.Length < 28 || exe[0] != (byte)'M' || exe[1] != (byte)'Z') throw new InvalidDataException($"{name}: no es un ejecutable DOS valido");
                }
                else
                {
                    byte[] unpacked;
                    try { unpacked = Sqz.Unpack(File.ReadAllBytes(dp)); }
                    catch (Exception e) { throw new InvalidDataException($"{name}: archivo dañado ({e.Message})"); }
                    if (unpacked.Length == 0) throw new InvalidDataException($"{name}: archivo vacío tras descomprimir");
                }
                Add(len);
                lock (_lock) _filesDone++;
            }
            // the boot image comes from the copied PRE2.EXE (+ KEYB.SQZ / ALLFONTS.SQZ): generated, checked against the known SHA-256 and written to files
            lock (_lock) _current = GameData.BootImageName;
            BuildBootImage(destDir);
            lock (_lock) { _phase = Phase.Done; _current = ""; }
            return true;
        }
        catch (Exception e)
        {
            lock (_lock) { _phase = Phase.Failed; _error = e.Message; }
            return false;
        }
    }

    /// <summary>
    /// Generates <c>boot_image.bin</c> in <paramref name="dir"/> from the PRE2.EXE (+ KEYB.SQZ, ALLFONTS.SQZ) found there, validates its SHA-256 against
    /// <see cref="BootBuilder.ExpectedSha256"/> (the image of the GOG build this port targets) and writes it atomically. Throws with a readable message otherwise.
    /// </summary>
    public static void BuildBootImage(string dir)
    {
        byte[] boot;
        try { boot = BootBuilder.BuildFromFolder(dir); }
        catch (BootBuilder.BootBuildException e) { throw new InvalidDataException($"{GameData.ExeName}: {e.Message}"); }
        string sha = BootBuilder.Sha256Hex(boot);
        if (!string.Equals(sha, BootBuilder.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{GameData.ExeName} no es la version esperada del juego (boot SHA-256 {sha[..12]}...)");
        string dp = Path.Combine(dir, GameData.BootImageName);
        string tmp = dp + ".part";
        File.WriteAllBytes(tmp, boot);
        ReplaceFile(tmp, dp);
        if (!BootBuilder.Sha256Hex(File.ReadAllBytes(dp)).Equals(BootBuilder.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{GameData.BootImageName}: la copia escrita no coincide");
    }

    /// <summary>Unpacks PRE2.SQZ (CDRUN LZW) to <paramref name="dest"/>, requiring the MZ header and <see cref="GameData.ExeSha256"/>; atomic write via <paramref name="tmp"/>.</summary>
    static void UnpackExe(string sqzPath, string dest, string tmp)
    {
        byte[] exe;
        try { exe = Sqz.UnpackCdrun(File.ReadAllBytes(sqzPath)); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new InvalidDataException($"{GameData.ExeSqzName}: archivo dañado ({e.Message})"); }
        if (exe.Length < 28 || exe[0] != (byte)'M' || exe[1] != (byte)'Z') throw new InvalidDataException($"{GameData.ExeSqzName}: no contiene un ejecutable DOS valido");
        if (!BootBuilder.Sha256Hex(exe).Equals(GameData.ExeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{GameData.ExeSqzName} no es la version esperada del juego (SHA-256 {BootBuilder.Sha256Hex(exe)[..12]}...)");
        File.WriteAllBytes(tmp, exe);
        ReplaceFile(tmp, dest);
        if (!BootBuilder.Sha256Hex(File.ReadAllBytes(dest)).Equals(GameData.ExeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{GameData.ExeName}: la copia escrita no coincide");
    }

    void Add(long n) { lock (_lock) _bytesDone += n; }

    (byte[] Hash, long Length) CopyWithHash(string src, string tmp)
    {
        using var inp = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        long len = 0;
        using (var outp = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[16 * 1024];
            int n;
            while ((n = inp.Read(buf, 0, buf.Length)) > 0)
            {
                sha.TransformBlock(buf, 0, n, null, 0);
                outp.Write(buf, 0, n);
                len += n;
                Add(n);
            }
            outp.Flush(true);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (sha.Hash!, len);
    }

    static byte[] HashFile(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(f);
    }

    static void ReplaceFile(string tmp, string dest)
    {
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }
}
