using System.Security.Cryptography;

namespace Pre2Port.Native;

/// <summary>
/// Initial native memory image the VM-less game starts from.
/// Port of <c>pre2/native/boot_data.py</c>: 1 MB address space holding the 64 KB
/// DGROUP (program data segment, incl. boot-loaded font band) and the CS table windows.
/// File: <c>files/boot_image.bin</c>, generated at install time from the user's PRE2.EXE by <see cref="BootBuilder"/> (identical to what <c>build_boot_memory()</c> exported).
/// </summary>
public static class BootImage
{
    public const int DsBase = 0x1A0F << 4;   // DGROUP paragraph (GOG build)
    public const int CsBase = 0x1030 << 4;   // code segment base
    public const int ImageLen = 0x140000;    // 1 MB + 4 x 64 KB EGA plane shadow
    public const string ExpectedSha256 = "abb867cabd357578a07684ad50f66f9568a0de27d47ad6fc0cc9757ebe3db8da";

    /// <summary>Load the boot image from <paramref name="path"/> and check it against the reference hash.</summary>
    public static byte[] Load(string path)
    {
        byte[] image = File.ReadAllBytes(path);
        if (image.Length != ImageLen)
            throw new InvalidDataException($"boot image is {image.Length} bytes, expected {ImageLen}");

        string hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        if (hash != ExpectedSha256)
            throw new InvalidDataException($"boot image hash {hash} != reference {ExpectedSha256}");

        return image;
    }
}
