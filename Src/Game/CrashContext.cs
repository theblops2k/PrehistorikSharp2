using System.Text;
using Pre2Port.Game.Frontend;
using Pre2Port.Game.Level;

namespace Pre2Port.Game;

/// <summary>
/// Crash log: static game-context registry (updated by the Presenter each frame) + writer of <c>log_error.txt</c>
/// next to Pre2Port.exe (appended, one block per crash). Nothing is written unless an exception is reported.
/// Console output is NOT captured (would need a Console.Out tee; omitted on purpose).
/// </summary>
public static class CrashContext
{
    public const string FileName = "log_error.txt";
    public static IScene? Scene;
    public static int Ticks, Frames;
    public static double Fps;
    public static string[] CommandLine = Array.Empty<string>();
    static readonly bool TestCrash = Environment.GetEnvironmentVariable("PRE2_CRASHTEST") == "1";   // test hook: throws on the first level frame

    /// <summary>Called by the Presenter once per rendered frame (no game-logic effect).</summary>
    public static void Update(IScene scene, int ticks, int frames, double fps)
    {
        Scene = scene; Ticks = ticks; Frames = frames; Fps = fps;
        if (TestCrash && Describe(scene).StartsWith("LEVEL", StringComparison.Ordinal))
            throw new InvalidOperationException("PRE2_CRASHTEST=1: controlled test crash on first level frame");
    }

    static string Describe(IScene s)
    {
        try { return s is FrontEndRunner f ? f.SceneName : s is LevelScene l ? l.SceneName : s.GetType().Name; } catch { return "?"; }
    }

    static string Safe(Func<object?> f) { try { return f()?.ToString() ?? "null"; } catch (Exception e) { return "<err " + e.GetType().Name + ">"; } }

    public static void Report(Exception ex, string source)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(new string('=', 78));
            sb.AppendLine("CRASH " + Safe(() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")) + "  (" + source + ")");
            sb.AppendLine("App version: " + Safe(() => typeof(CrashContext).Assembly.GetName().Version));
            sb.AppendLine(".NET: " + Safe(() => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription));
            sb.AppendLine("OS: " + Safe(() => System.Runtime.InteropServices.RuntimeInformation.OSDescription));
            sb.AppendLine("Arch: " + Safe(() => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
            sb.AppendLine("Command line: " + Safe(() => Environment.CommandLine));
            sb.AppendLine("Args: " + Safe(() => string.Join(" ", CommandLine)));
            sb.AppendLine("--- Game context ---");
            var sc = Scene;
            sb.AppendLine("Scene: " + Safe(() => sc?.GetType().FullName) + " / " + Safe(() => sc is null ? null : Describe(sc)));
            var lv = Safe2(() => sc is FrontEndRunner f ? f.ActiveLevel : sc as LevelScene);
            sb.AppendLine("Level: " + Safe(() => lv is null ? "none" : lv.Level + 1));
            sb.AppendLine($"Logic ticks: {Ticks}  Render frames: {Frames}  FPS measured: {Fps:F1}");
            sb.AppendLine("Screen mode: " + Safe(() => Settings.Current.Mode) + "  cfg FPS: " + Safe(() => Settings.Current.Fps));
            sb.AppendLine("FbWidth: " + Safe(() => sc?.FbWidth));
            sb.AppendLine("Lives: " + Safe(() => lv!.State.Rb(0x27D8)) + "  Energy: " + Safe(() => lv!.State.Rb(0x27D6))
                + "  Player X,Y: " + Safe(() => lv!.State.Rw(Pre2Port.Native.LevelState.Off.PlayerX) + "," + lv!.State.Rw(Pre2Port.Native.LevelState.Off.PlayerY)));
            sb.AppendLine("--- Exception ---");
            int depth = 0;
            for (Exception? e = ex; e is not null && depth < 20; e = e.InnerException, depth++)
            {
                sb.AppendLine(depth == 0 ? "Type: " + e.GetType().FullName : $"InnerException[{depth}] Type: " + e.GetType().FullName);
                sb.AppendLine("Message: " + e.Message);
                sb.AppendLine("StackTrace:");
                sb.AppendLine(e.StackTrace ?? "(none)");
            }
            sb.AppendLine("--- Prehistorik2.cfg ---");
            sb.AppendLine(Safe(() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, Settings.FileName))));
            sb.AppendLine("--- Console output: not captured ---");
            sb.AppendLine();
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, FileName), sb.ToString());
        }
        catch { /* the logger must never throw */ }
    }

    static LevelScene? Safe2(Func<LevelScene?> f) { try { return f(); } catch { return null; } }
}
