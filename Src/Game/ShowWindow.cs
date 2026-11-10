using System.Numerics;
using Pre2Port.Codecs;
using Pre2Port.Native;
using Raylib_cs;

namespace Pre2Port.Game;

/// <summary>First presentation milestone: BACK0.SQZ with the level-0 palette in a 640x400 (x2) Raylib window.</summary>
public static class ShowWindow
{
    const int Scale = 2;

    /// <summary>
    /// Show BACK0 with the real level palette. With a non-empty <paramref name="screenshotPath"/> it draws a
    /// frame, saves the screen to that absolute path and closes; with null/empty it stays open until ESC.
    /// </summary>
    public static int Show(string bootImagePath, string originalDir, string? screenshotPath, int level = 0)
    {
        byte[] boot = BootImage.Load(bootImagePath);
        byte[] planar = Sqz.Unpack(File.ReadAllBytes(Path.Combine(originalDir, "BACK0.SQZ")));
        byte[] rgba = Screen.IndicesToRgba(Screen.PlanarToIndices(planar), Screen.LevelPalette(boot, level));

        Raylib.SetTraceLogLevel(TraceLogLevel.Warning);
        Raylib.InitWindow(Screen.Width * Scale, Screen.Height * Scale, "Prehistorik 2 (port)");
        Raylib.SetExitKey(KeyboardKey.Escape);
        Raylib.SetTargetFPS(60);

        Image img = Raylib.GenImageColor(Screen.Width, Screen.Height, Color.Black);   // R8G8B8A8
        Texture2D tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        Raylib.UpdateTexture(tex, rgba);
        Raylib.SetTextureFilter(tex, TextureFilter.Point);

        void Frame()
        {
            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Black);
            Raylib.DrawTextureEx(tex, Vector2.Zero, 0f, Scale, Color.White);
            Raylib.EndDrawing();
        }

        int rc = 0;
        if (!string.IsNullOrEmpty(screenshotPath))
        {
            // A couple of frames so the back buffer is certainly presented before reading it.
            Frame();
            Frame();
            // Raylib's TakeScreenshot may rewrite the directory (it stores under its own base path), so the
            // absolute path is honoured by reading the screen back and exporting it explicitly.
            Image shot = Raylib.LoadImageFromScreen();
            bool ok = Raylib.ExportImage(shot, screenshotPath);
            Raylib.UnloadImage(shot);
            Console.WriteLine(ok ? $"screenshot {screenshotPath}" : $"FAIL screenshot {screenshotPath}");
            rc = ok ? 0 : 1;
        }
        else
        {
            while (!Raylib.WindowShouldClose())
                Frame();
        }

        Raylib.UnloadTexture(tex);
        Raylib.CloseWindow();
        return rc;
    }
}
