using Pre2Port.Codecs;
using Pre2Port.Native;
using Pre2Port.Recovered;

namespace Pre2Port.Game.Launcher;

/// <summary>Headless dev commands for the launcher (no window, no dialog).</summary>
public static class LauncherVerify
{
    /// <summary>
    /// <c>launcher-test srcDir destDir referenceBoot</c>: copies the required set (incl. PRE2.EXE) with <see cref="GameInstaller"/>, checks byte equality, the negative cases
    /// (missing file, nested folder discovery), that the boot image GENERATED from PRE2.EXE (BootBuilder) is byte-identical to <c>referenceBoot</c> (SHA-256) and loads
    /// EVERYTHING the game reads at run time using ONLY the copy plus that generated image (levels 0..15, front-end/menu/map assets, all songs, SFX bank, game-over asset).
    /// If <c>srcDir</c> has no PRE2.EXE (the folder keeps only the SQZ wrapper) the one next to <c>referenceBoot</c> (work\PRE2.EXE) is added to a temporary copy of it.
    /// Exit code 0 = all PASS.
    /// </summary>
    public static int Test(string src, string dest, string bootPath)
    {
        string? temp = null;
        try
        {
            int sqzFail = 0;
            if (!Directory.EnumerateFiles(src).Any(f => string.Equals(Path.GetFileName(f), GameData.ExeName, StringComparison.OrdinalIgnoreCase)))
            {
                // the folder has the PRE2.SQZ wrapper instead of PRE2.EXE (the usual install): the launcher must accept it
                sqzFail = SqzScenario(src, dest + "_sqz", bootPath);
                string exe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bootPath)) ?? ".", GameData.ExeName);
                if (File.Exists(exe))
                {
                    temp = Path.Combine(Path.GetTempPath(), "pre2_src_" + Environment.ProcessId);
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    Directory.CreateDirectory(temp);
                    foreach (string f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(temp, Path.GetFileName(f)));
                    File.Copy(exe, Path.Combine(temp, GameData.ExeName));
                    Console.WriteLine($"launcher-test: {src} has no {GameData.ExeName}; using a temporary copy with {exe}");
                    src = temp;
                }
            }
            int core = TestCore(src, dest, bootPath);
            return core == 0 && sqzFail == 0 ? 0 : 1;
        }
        finally { if (temp is not null) { try { Directory.Delete(temp, true); } catch (IOException) { } } }
    }

    /// <summary>Source folder with PRE2.SQZ and no PRE2.EXE: accepted, unpacked to files\PRE2.EXE (SHA-256 checked), boot image generated; damaged / missing cases refused.</summary>
    static int SqzScenario(string src, string dest, string bootPath)
    {
        int fail = 0;
        void Check(string what, bool ok, string extra = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} [SQZ] {what}{(extra.Length > 0 ? " - " + extra : "")}");
            if (!ok) fail++;
        }
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        string? sqz = GameData.FindFile(src, GameData.ExeSqzName);
        Check("source has PRE2.SQZ and no PRE2.EXE", sqz is not null && GameData.FindFile(src, GameData.ExeName) is null);
        Check("source accepted (PRE2.EXE not reported missing)", GameData.MissingSource(src).Count == 0 && GameData.Missing(src).Contains(GameData.ExeName), string.Join(",", GameData.MissingSource(src)));
        Check("FindGameFolder accepts the PRE2.SQZ folder", GameInstaller.FindGameFolder(src, out var mm) == src && mm.Count == 0);
        byte[] reference = File.ReadAllBytes(bootPath);
        string refExe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bootPath)) ?? ".", GameData.ExeName);
        byte[]? unpacked = null;
        try { unpacked = Sqz.UnpackCdrun(File.ReadAllBytes(sqz!)); } catch (Exception e) { Check("Sqz.UnpackCdrun(PRE2.SQZ)", false, e.Message); }
        if (unpacked is not null)
        {
            Check("Sqz.UnpackCdrun SHA-256 == expected PRE2.EXE", BootBuilder.Sha256Hex(unpacked) == GameData.ExeSha256, $"{unpacked.Length} bytes {BootBuilder.Sha256Hex(unpacked)}");
            if (File.Exists(refExe)) Check("unpacked == work PRE2.EXE (byte for byte)", unpacked.AsSpan().SequenceEqual(File.ReadAllBytes(refExe)));
        }
        var inst = new GameInstaller();
        bool ok = inst.Run(src, dest);
        Check("install from PRE2.SQZ", ok && inst.State == GameInstaller.Phase.Done && inst.FilesDone == inst.FilesTotal, $"error='{inst.Error}', files {inst.FilesDone}/{inst.FilesTotal}, progress {inst.Progress:F3}");
        string exeOut = Path.Combine(dest, GameData.ExeName);
        Check("dest PRE2.EXE written, SHA-256 matches", File.Exists(exeOut) && BootBuilder.Sha256Hex(File.ReadAllBytes(exeOut)) == GameData.ExeSha256);
        Check("PRE2.SQZ not copied; no .part leftovers", GameData.FindFile(dest, GameData.ExeSqzName) is null && !Directory.EnumerateFiles(dest, "*.part").Any());
        Check("destination complete", GameData.Missing(dest).Count == 0);
        string gen = Path.Combine(dest, GameData.BootImageName);
        Check("boot_image.bin generated from the unpacked EXE == reference", File.Exists(gen) && File.ReadAllBytes(gen).AsSpan().SequenceEqual(reference));

        // whole launcher flow without a window (stub picker -> Evaluate -> copy -> LISTO -> game scene); never the NotFound ("Falta: PRE2.EXE") screen
        string flowRoot = dest + "_flow";
        string oldRoot = GameData.Root;
        try
        {
            if (Directory.Exists(flowRoot)) Directory.Delete(flowRoot, true);
            GameData.Root = flowRoot;
            byte[] boot = BootImage.Load(gen);
            var launcher = new LauncherScene() { Picker = () => Task.FromResult<string?>(src) };
            IScene? built = null;
            using var startup = new StartupScene(launcher, () => { built = new Pre2Port.Game.Frontend.FrontEndRunner(boot, GameData.Root, null); return (built, null); });
            var enter = new Input(); enter.Set(Input.Key.Enter, true, true);
            var none = new Input();
            var fbl = new Framebuffer(startup.FbWidth, startup.FbHeight);
            startup.Update(enter);
            var seen = new HashSet<LauncherScene.Mode>();
            int guard = 0;
            while (built is null && guard++ < 5000) { seen.Add(launcher.State); startup.Update(none); startup.Draw(fbl); }
            Check("launcher flow with PRE2.SQZ -> game (no NotFound)", built is not null && !seen.Contains(LauncherScene.Mode.NotFound) && seen.Contains(LauncherScene.Mode.Copying), string.Join(",", seen) + $" in {guard} ticks");
            Check("files complete + boot_image.bin == reference in GameData.Root", GameData.IsComplete() && File.ReadAllBytes(GameData.BootImagePath).AsSpan().SequenceEqual(reference));
        }
        catch (Exception e) { Check("launcher flow with PRE2.SQZ", false, e.ToString()); }
        finally { GameData.Root = oldRoot; try { Directory.Delete(flowRoot, true); } catch (Exception) { } }

        // negatives: damaged / truncated PRE2.SQZ, neither EXE nor SQZ
        string neg = Path.Combine(Path.GetTempPath(), "pre2_sqzneg_" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(neg);
            foreach (string f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(neg, Path.GetFileName(f)));
            string negSqz = GameData.FindFile(neg, GameData.ExeSqzName)!;
            byte[] good = File.ReadAllBytes(negSqz);
            var bad = (byte[])good.Clone();
            bad[bad.Length / 2] ^= 0x5A;
            File.WriteAllBytes(negSqz, bad);
            var i1 = new GameInstaller();
            Check("damaged PRE2.SQZ refused", !i1.Run(neg, Path.Combine(neg, "_o1")) && i1.State == GameInstaller.Phase.Failed && !File.Exists(Path.Combine(neg, "_o1", GameData.ExeName)), i1.Error ?? "");
            File.WriteAllBytes(negSqz, good.Take(good.Length / 2).ToArray());
            var i2 = new GameInstaller();
            Check("truncated PRE2.SQZ refused", !i2.Run(neg, Path.Combine(neg, "_o2")) && i2.State == GameInstaller.Phase.Failed, i2.Error ?? "");
            File.Delete(negSqz);
            var miss = GameData.MissingSource(neg);
            var i3 = new GameInstaller();
            Check("without PRE2.EXE nor PRE2.SQZ: reported and refused", miss.Count == 1 && miss[0] == GameData.ExeName && !i3.Run(neg, Path.Combine(neg, "_o3")), string.Join(",", miss) + " / " + (i3.Error ?? ""));
        }
        finally { try { Directory.Delete(neg, true); } catch (IOException) { } }
        Console.WriteLine(fail == 0 ? "launcher-test [SQZ]: ALL PASS" : $"launcher-test [SQZ]: {fail} FAIL");
        return fail;
    }

    static int TestCore(string src, string dest, string bootPath)
    {
        int fail = 0;
        void Check(string what, bool ok, string extra = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}{(extra.Length > 0 ? " - " + extra : "")}");
            if (!ok) fail++;
        }

        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        var missingSrc = GameData.Missing(src);
        Check("source has the required files", missingSrc.Count == 0, $"{GameData.Required.Length} required, {missingSrc.Count} missing");
        Check("empty destination is incomplete", GameData.Missing(dest).Count == GameData.Required.Length);
        Check("PRE2.EXE is required and listed first when missing", GameData.Required.Contains(GameData.ExeName) && (GameData.Missing(dest)[0] is "PRE2.EXE" or "KEYB.SQZ"));

        // BootBuilder: the image generated from PRE2.EXE is byte-identical to the reference (SHA-256)
        byte[] reference = File.ReadAllBytes(bootPath);
        string refSha = BootBuilder.Sha256Hex(reference);
        byte[]? generated = null;
        try { generated = BootBuilder.BuildFromFolder(src); }
        catch (Exception e) { Check("BootBuilder runs over PRE2.EXE", false, e.Message); }
        if (generated is not null)
            Check("BootBuilder == reference boot_image (SHA-256)", generated.AsSpan().SequenceEqual(reference) && BootBuilder.Sha256Hex(generated) == refSha && refSha == BootBuilder.ExpectedSha256, BootBuilder.Sha256Hex(generated));

        // nested folder discovery: src placed two levels down
        string nest = Path.Combine(Path.GetTempPath(), "pre2_nest_" + Environment.ProcessId);
        string nested = Path.Combine(nest, "Games", "PREHISTORIK");
        try
        {
            Directory.CreateDirectory(nested);
            foreach (string f in GameData.Required) File.Copy(Directory.EnumerateFiles(src).First(p => string.Equals(Path.GetFileName(p), f, StringComparison.OrdinalIgnoreCase)), Path.Combine(nested, f));
            string found = GameInstaller.FindGameFolder(nest, out var mm);
            Check("FindGameFolder finds the game 2 levels down", mm.Count == 0 && found == nested, found);
        }
        finally { try { Directory.Delete(nest, true); } catch (IOException) { } }

        // negative: missing file refuses to copy
        string partial = Path.Combine(Path.GetTempPath(), "pre2_partial_" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(partial);
            foreach (string f in GameData.Required.Skip(1)) File.Copy(Directory.EnumerateFiles(src).First(p => string.Equals(Path.GetFileName(p), f, StringComparison.OrdinalIgnoreCase)), Path.Combine(partial, f));
            var bad = new GameInstaller();
            bool ok = bad.Run(partial, Path.Combine(partial, "_out"));
            Check("copy refused when a file is missing", !ok && bad.State == GameInstaller.Phase.Failed, bad.Error ?? "");
            // negative: without PRE2.EXE the folder is reported as incomplete, naming it
            string noExe = Path.Combine(partial, "_noexe");
            Directory.CreateDirectory(noExe);
            foreach (string f in GameData.Required.Where(r => r != GameData.ExeName)) File.Copy(Directory.EnumerateFiles(src).First(p => string.Equals(Path.GetFileName(p), f, StringComparison.OrdinalIgnoreCase)), Path.Combine(noExe, f));
            var miss = GameData.Missing(noExe);
            Check("missing PRE2.EXE is reported", miss.Count == 1 && miss[0] == GameData.ExeName, string.Join(",", miss));
            var badExe = new GameInstaller();
            Check("copy refused without PRE2.EXE", !badExe.Run(noExe, Path.Combine(partial, "_out3")) && badExe.State == GameInstaller.Phase.Failed, badExe.Error ?? "");
            // negative: a damaged container is detected by the validation
            File.WriteAllBytes(Path.Combine(partial, GameData.Required[0]), new byte[] { 0xB4, 0x4C, 1, 2, 3 });
            var bad2 = new GameInstaller();
            ok = bad2.Run(partial, Path.Combine(partial, "_out2"));
            Check("damaged .SQZ rejected by validation", !ok && bad2.State == GameInstaller.Phase.Failed, bad2.Error ?? "");
        }
        finally { try { Directory.Delete(partial, true); } catch (IOException) { } }

        // the real copy (worker thread path, like the UI)
        var inst = new GameInstaller();
        inst.Start(src, dest);
        double last = -1; int polls = 0;
        while (inst.State == GameInstaller.Phase.Running) { last = inst.Progress; polls++; Thread.Sleep(1); }
        Check("copy + validation", inst.State == GameInstaller.Phase.Done, $"error='{inst.Error}', files {inst.FilesDone}/{inst.FilesTotal}, progress {inst.Progress:F3}, {inst.BytesTotal} bytes, last poll {last:F3}");
        Check("destination complete", GameData.Missing(dest).Count == 0);
        Check("no .part leftovers", !Directory.EnumerateFiles(dest, "*.part").Any());
        int diff = 0;
        foreach (string f in GameData.Required)
        {
            string s = Directory.EnumerateFiles(src).First(p => string.Equals(Path.GetFileName(p), f, StringComparison.OrdinalIgnoreCase));
            if (!File.ReadAllBytes(s).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dest, f)))) diff++;
        }
        Check("copied bytes identical to the source", diff == 0, $"{diff} differ");
        // idempotent re-run over existing files, and "same folder" case
        Check("second copy over existing files", new GameInstaller().Run(src, dest));
        Check("source == destination is accepted", new GameInstaller().Run(dest, dest));

        // the installer generated files/boot_image.bin from the copied PRE2.EXE: it must be the reference image
        string genPath = Path.Combine(dest, GameData.BootImageName);
        Check("files/boot_image.bin generated by the installer", File.Exists(genPath) && File.ReadAllBytes(genPath).AsSpan().SequenceEqual(reference), File.Exists(genPath) ? BootBuilder.Sha256Hex(File.ReadAllBytes(genPath)) : "missing");
        // a PRE2.EXE of another build is rejected by the SHA-256 check
        string altExe = Path.Combine(Path.GetTempPath(), "pre2_alt_" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(altExe);
            foreach (string f in Directory.EnumerateFiles(dest)) File.Copy(f, Path.Combine(altExe, Path.GetFileName(f)));
            File.Delete(Path.Combine(altExe, GameData.BootImageName));
            byte[] exeBytes = File.ReadAllBytes(Path.Combine(altExe, GameData.ExeName));
            exeBytes[1024 + 0x9FF0 + 0x2301] ^= 0xFF;   // damage a byte of DGROUP (scancode table) inside the executable
            File.WriteAllBytes(Path.Combine(altExe, GameData.ExeName), exeBytes);
            bool rejected;
            try { GameInstaller.BuildBootImage(altExe); rejected = false; } catch (InvalidDataException) { rejected = true; }
            Check("boot generation rejects a different PRE2.EXE", rejected && !File.Exists(Path.Combine(altExe, GameData.BootImageName)));
        }
        finally { try { Directory.Delete(altExe, true); } catch (IOException) { } }

        // load everything from the copy only (game files + the GENERATED boot image)
        byte[] boot = BootImage.Load(genPath);
        string only = dest;
        string? failing = null;
        void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { failing = $"{what}: {e.GetType().Name} {e.Message}"; Check(what, false, e.Message); return; }
        }
        for (int lv = 0; lv <= 15; lv++)
        {
            int l = lv;
            Try($"level {l}", () =>
            {
                var sc = new Pre2Port.Game.Level.LevelScene(boot, only, l, damage: true, bonus: true, platforms: true, attack: true);
                var fb = new Framebuffer();
                sc.Update(new Input()); sc.Draw(fb);
            });
        }
        if (failing is null) Check("levels 0..15 load and draw from the copy", true);
        Try("FrontEndAssets (+THEEND, LEVELH, LEVELI)", () =>
        {
            var fa = Pre2Port.Game.Frontend.FrontEndAssets.Load(boot, only, 2026);
            _ = fa.TheEnd.Length + fa.LevelH.Length + fa.LevelI.Length;
        });
        Try("MenuAssets (MENU, MENU2, MOTIF) + MapAssets (MAP, SPRITES)", () =>
        {
            var ma = Pre2Port.Game.Frontend.MenuAssets.Load(boot, only);
            _ = ma.Map;
        });
        Try("all 12 songs + SFX bank", () =>
        {
            var ae = new AudioEngine(boot, only);
            ae.LoadSfxBank("SAMPLE.SQZ");
            foreach (string t in GameData.Required.Where(r => r.EndsWith(".TRK", StringComparison.OrdinalIgnoreCase)))
            {
                ae.LoadSong(t);
                if (ae.CurrentSong != t) throw new InvalidDataException($"{t} not loaded");
            }
        });
        Try("GAMEOVER.SQZ", () => _ = EndScreens.LoadGameOverAsset(new LevelState(boot, only)));
        Try("FrontEndRunner (intro) 120 ticks", () =>
        {
            var r = new Pre2Port.Game.Frontend.FrontEndRunner(boot, only, null);
            var fb = new Framebuffer();
            for (int i = 0; i < 120; i++) { r.Update(new Input()); r.Draw(fb); }
        });
        // whole flow without a window: launcher (Enter on EXAMINAR with a stub picker) -> copy -> LISTO -> StartupScene builds the game -> runs ticks
        string flowRoot = dest + "_flow";
        string oldRoot = GameData.Root;
        try
        {
            if (Directory.Exists(flowRoot)) Directory.Delete(flowRoot, true);
            GameData.Root = flowRoot;
            var launcher = new LauncherScene() { Picker = () => Task.FromResult<string?>(src) };
            IScene? built = null;
            using var startup = new StartupScene(launcher, () => { built = new Pre2Port.Game.Frontend.FrontEndRunner(boot, GameData.Root, null); return (built, null); });
            var enter = new Input(); enter.Set(Input.Key.Enter, true, true);
            var none = new Input();
            var fbl = new Framebuffer(startup.FbWidth, startup.FbHeight);
            Check("launcher canvas is 640x400 (8:5 like the game)", startup.FbWidth == 640 && startup.FbHeight == 400 && Math.Abs(startup.FbWidth / (double)startup.FbHeight - 1.6) < 1e-9);
            startup.Update(enter);
            var seen = new HashSet<LauncherScene.Mode>();
            int guard = 0;
            while (built is null && guard++ < 5000) { seen.Add(launcher.State); startup.Update(none); startup.Draw(fbl); }
            Check("flow NotFound -> Browsing -> Copying -> Finishing -> game", built is not null && seen.Contains(LauncherScene.Mode.Copying) && seen.Contains(LauncherScene.Mode.Finishing), string.Join(",", seen) + $" in {guard} ticks");
            Check("game files + generated boot_image.bin now in GameData.Root", GameData.IsComplete() && File.ReadAllBytes(GameData.BootImagePath).AsSpan().SequenceEqual(reference));
            var fbg = new Framebuffer(startup.FbWidth, startup.FbHeight);
            for (int i = 0; i < 90; i++) { startup.Update(none); startup.Draw(fbg); }
            Check("game runs from the same scene (320x200, the runner's own rate)", startup.FbWidth == 320 && startup.FbHeight == 200 && Math.Abs(startup.LogicHz - built!.LogicHz) < 1e-9, $"{startup.FbWidth}x{startup.FbHeight} {startup.LogicHz:F2} Hz (runner {built!.LogicHz:F2})");
        }
        catch (Exception e) { Check("launcher flow", false, e.ToString()); }
        finally { GameData.Root = oldRoot; try { Directory.Delete(flowRoot, true); } catch (Exception) { } }
        Check("everything the game reads loaded from the copy", failing is null, failing ?? "");
        Console.WriteLine(fail == 0 ? "launcher-test: ALL PASS" : $"launcher-test: {fail} FAIL");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>launcher-shot state out.png [srcDir]</c>: renders one launcher state into a 640x400 PNG without a window. States: notfound, notfound-error, browsing,
    /// copying25, copying60, copying100, finishing, fatal. srcDir is kept for compatibility (the copying states no longer show game art).
    /// </summary>
    public static int Shot(string state, string png, string? srcDir)
    {
        bool fatal = state == "fatal";
        var sc = new LauncherScene(fatal ? "No se pudo generar el arranque" : null, fatal ? "Revisa PRE2.EXE" : null);
        var input = new Input();
        string tmp = Path.Combine(Path.GetTempPath(), "pre2_shot_" + Environment.ProcessId);
        string oldRoot = GameData.Root;
        try
        {
            if (state == "notfound-error") sc.Evaluate(Path.GetTempPath());
            else if (state == "browsing")
            {
                var tcs = new TaskCompletionSource<string?>();
                sc.Picker = () => tcs.Task;
                var press = new Input(); press.Set(Input.Key.Enter, true, true);
                sc.Update(press);
            }
            else if (state.StartsWith("copying") || state == "finishing")
            {
                if (srcDir is null) throw new ArgumentException("copying states need srcDir");
                GameData.Root = tmp;
                sc.Evaluate(srcDir);
                int target = state == "copying25" ? 25 : state == "copying60" ? 60 : 100;
                int guard = 0;
                while (sc.State == LauncherScene.Mode.Copying && guard++ < 20000)
                {
                    sc.Update(input);
                    // progress shown is private; stop on the estimated fraction through the installer + eased value: poll text via reflection-free approach
                    if (state != "copying100" && state != "finishing" && Shown(sc) * 100 >= target) break;
                }
                if (state == "finishing") for (int i = 0; i < 10; i++) sc.Update(input);
            }
            else if (state != "notfound" && state != "fatal") throw new ArgumentException("unknown state " + state);
            for (int i = 0; i < 25; i++) sc.Update(input);   // let fires/embers animate
            var fb = new Framebuffer(sc.FbWidth, sc.FbHeight);
            sc.Draw(fb);
            WritePng(fb, png);
            Console.WriteLine($"launcher-shot {state} -> {png} (mode {sc.State})");
            return 0;
        }
        finally
        {
            GameData.Root = oldRoot;
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (IOException) { }
        }
    }

    static double Shown(LauncherScene sc) =>
        (double)typeof(LauncherScene).GetField("_shown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(sc)!;

    /// <summary>Full-size RGB PNG of an indexed framebuffer (any size, all 256 palette entries).</summary>
    internal static void WritePng(Framebuffer fb, string path)
    {
        int w = fb.Width, h = fb.Height;
        var raw = new byte[h * (1 + w * 3)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < w; x++)
            {
                var c = fb.Palette[fb.Pixels[y * w + x]];
                raw[o++] = c.R; raw[o++] = c.G; raw[o++] = c.B;
            }
        }
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(raw);
        using var f = File.Create(path);
        f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string t, byte[] d)
        {
            var tb = System.Text.Encoding.ASCII.GetBytes(t);
            f.Write(new byte[] { (byte)(d.Length >> 24), (byte)(d.Length >> 16), (byte)(d.Length >> 8), (byte)d.Length });
            f.Write(tb); f.Write(d);
            uint c = 0xFFFFFFFF;
            foreach (var b in tb.Concat(d)) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1; }
            c ^= 0xFFFFFFFF;
            f.Write(new byte[] { (byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c });
        }
        Chunk("IHDR", new byte[] { (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 2, 0, 0, 0 });
        Chunk("IDAT", ms.ToArray());
        Chunk("IEND", Array.Empty<byte>());
    }
}
