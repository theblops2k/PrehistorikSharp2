using System.Security.Cryptography;
using Pre2Port.Native;

namespace Pre2Port.Game.Frontend;

/// <summary>
/// Headless check of THE END (5034) and the CREATORS photo (25F6) against work/gen_theend_ref.py: every fade-in frame (palette + picture), the hold (fire pressed, then released:
/// the picture stays on the last fade-in frame), every fade-out frame, the stage ending, and the 640x480 CREATORS image with its grey palette.
/// </summary>
public static class TheEndVerify
{
    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static byte[] PalBytes(Framebuffer fb, int n)
    {
        var p = new byte[n * 3];
        for (int i = 0; i < n; i++) { p[i * 3] = fb.Palette[i].R; p[i * 3 + 1] = fb.Palette[i].G; p[i * 3 + 2] = fb.Palette[i].B; }
        return p;
    }

    public static int Run(string bootPath, string originalDir, string refFile)
    {
        var refs = File.ReadAllLines(refFile).Select(l => l.Trim().Split(':')).Where(p => p.Length > 1).ToList();
        int nIn = int.Parse(refs.First(p => p[0] == "TE" && p[1] == "IN")[2]);
        int nOut = int.Parse(refs.First(p => p[0] == "TE" && p[1] == "OUT")[2]);
        var te = refs.Where(p => p[0] == "TE" && int.TryParse(p[1], out _)).ToDictionary(p => int.Parse(p[1]));
        var to = refs.Where(p => p[0] == "TO").ToDictionary(p => int.Parse(p[1]));
        var cr = refs.First(p => p[0] == "CR");

        var assets = FrontEndAssets.Load(BootImage.Load(bootPath), originalDir, 2026);
        var scene = new TheEndScene(assets);
        var fb = new Framebuffer();
        var none = new Input(); var fire = new Input();
        fire.Set(Input.Key.Space, true, true);
        int pass = 0, fail = 0;
        void Check(string tag, string[] want)
        {
            scene.Draw(fb);
            bool ok = Hex(PalBytes(fb, 256)) == want[2] && Hex(fb.Pixels) == want[3];
            if (ok) pass++; else { fail++; Console.WriteLine($"FAIL {tag}"); }
        }
        for (int i = 0; i < nIn; i++) { scene.Update(none); Check($"THEEND fade-in {i}", te[i]); }
        for (int k = 0; k < 3; k++) { scene.Update(none); Check($"THEEND hold {k} (no fire)", te[nIn - 1]); }
        scene.Update(fire); Check("THEEND hold (fire pressed)", te[nIn - 1]);
        scene.Update(fire); Check("THEEND hold (fire still down)", te[nIn - 1]);
        scene.Update(none); Check("THEEND hold (fire released)", te[nIn - 1]);
        for (int j = 0; j < nOut; j++) { scene.Update(none); Check($"THEEND fade-out {j}", to[j]); }
        scene.Update(none);
        if (scene.Result == StageResult.Next) pass++; else { fail++; Console.WriteLine($"FAIL THEEND did not end after the last fade-out frame (result {scene.Result})"); }

        var cs = new CreatorsScene(assets);
        var cfb = new Framebuffer(CreatorsScene.Width, CreatorsScene.Height);
        cs.Update(none); cs.Draw(cfb);
        bool crOk = Hex(PalBytes(cfb, 16)) == cr[1] && Hex(cfb.Pixels) == cr[2];
        if (crOk) pass++; else { fail++; Console.WriteLine("FAIL CREATORS image/palette"); }
        cs.Update(none); cs.Update(fire);
        if (cs.Result == StageResult.Running) pass++; else { fail++; Console.WriteLine("FAIL CREATORS ended while fire was held"); }
        cs.Update(none);
        if (cs.Result == StageResult.Next) pass++; else { fail++; Console.WriteLine("FAIL CREATORS did not end after fire press + release"); }
        Console.WriteLine($"theend: {nIn} fade-in + {nOut} fade-out frames; {pass} PASS, {fail} FAIL");
        return fail == 0 ? 0 : 1;
    }
}
