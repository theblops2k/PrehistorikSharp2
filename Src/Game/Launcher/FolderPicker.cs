using System.Runtime.InteropServices;

namespace Pre2Port.Game.Launcher;

/// <summary>
/// Windows folder-selection dialog (System.Windows.Forms.FolderBrowserDialog) run on its own STA thread, owned by the game window,
/// so the Raylib loop keeps drawing while it is open.
/// </summary>
internal static class FolderPicker
{
    [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();

    sealed class Owner : System.Windows.Forms.IWin32Window
    {
        public Owner(IntPtr h) { Handle = h; }
        public IntPtr Handle { get; }
    }

    /// <summary>Opens the dialog; the task yields the chosen folder, or null if the user cancelled.</summary>
    public static Task<string?> PickAsync(string title)
    {
        IntPtr owner = GetActiveWindow();   // called on the UI (game) thread: the Raylib window
        var tcs = new TaskCompletionSource<string?>();
        var t = new Thread(() =>
        {
            try
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = title,
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false,
                    AutoUpgradeEnabled = true,
                    SelectedPath = AppContext.BaseDirectory,   // start in the folder of the executable
                };
                var r = owner != IntPtr.Zero ? dlg.ShowDialog(new Owner(owner)) : dlg.ShowDialog();
                tcs.SetResult(r == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null);
            }
            catch (Exception e) { tcs.SetException(e); }
        })
        { IsBackground = true, Name = "FolderPicker" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
