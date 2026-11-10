using System.Runtime.InteropServices;

namespace Pre2Port.Game.Launcher;

/// <summary>
/// Double click (no arguments) must not show the console window of the console-subsystem exe. Called ONLY from the no-argument branch of
/// Program.cs; the development commands (play, level, verify-*, launcher-test...) never call it and keep printing to the console.
/// The console is detached only when this process is the sole owner of it (double click / Explorer): when started with no arguments from
/// an existing terminal the terminal is shared with other processes and is left alone.
/// </summary>
internal static class ConsoleHider
{
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    const int SW_HIDE = 0;

    public static void HideIfOwned()
    {
        try
        {
            if (GetConsoleWindow() == IntPtr.Zero) return;                  // no console at all
            if (GetConsoleProcessList(new uint[4], 4) != 1) return;         // shared with a terminal / parent shell: leave it
            Console.SetOut(TextWriter.Null);                                // nothing may write to the freed console afterwards
            Console.SetError(TextWriter.Null);
            ShowWindow(GetConsoleWindow(), SW_HIDE);
            FreeConsole();
        }
        catch (Exception) { /* cosmetic only */ }
    }
}
