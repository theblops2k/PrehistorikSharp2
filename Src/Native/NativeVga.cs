namespace Pre2Port.Native;

/// <summary>
/// The stand-alone display adapter: <see cref="VgaPalette"/> + the DAC port protocol.
/// 1:1 port of <c>NativeVGA</c> in <c>pre2/native/vga.py</c>.
/// </summary>
public sealed class NativeVga
{
    /// <summary>256-entry DAC palette (8-bit components): Ega16 then grayscale ramp.</summary>
    public (byte R, byte G, byte B)[] VgaPalette { get; } = Vga.DefaultPalette();

    /// <summary>BIOS video mode read by the scene classifier; the native adapter stays in planar mode 0x0D.</summary>
    public int VideoMode { get; set; } = 0x0D;

    public int DacWriteIndex { get; private set; }
    public int DacReadIndex { get; private set; }
    public int DacComponent { get; private set; }
    public List<int> DacLatch { get; private set; } = new();

    /// <summary>The 3C7/3C8/3C9 DAC write protocol (port of <c>_track_vga_dac_ports</c>).</summary>
    public void TrackVgaDacPorts(int port, int value, int bits)
    {
        if (bits == 16)
        {
            TrackVgaDacPorts(port, value & 0xFF, 8);
            TrackVgaDacPorts((port + 1) & 0xFFFF, (value >> 8) & 0xFF, 8);
            return;
        }
        if (bits != 8)
            return;
        value &= 0xFF;
        if (port == 0x03C8)
        {
            DacWriteIndex = value;
            DacComponent = 0;
            DacLatch = new List<int>();
            return;
        }
        if (port == 0x03C7)
        {
            DacReadIndex = value;
            DacComponent = 0;
            return;
        }
        if (port != 0x03C9)
            return;
        DacLatch.Add(Vga.Dac8(value));
        DacComponent++;
        if (DacComponent >= 3)
        {
            // Python: (latch + [0,0,0])[:3]
            int r = DacLatch.Count > 0 ? DacLatch[0] : 0;
            int g = DacLatch.Count > 1 ? DacLatch[1] : 0;
            int b = DacLatch.Count > 2 ? DacLatch[2] : 0;
            int idx = DacWriteIndex & 0xFF;
            VgaPalette[idx] = ((byte)r, (byte)g, (byte)b);
            DacWriteIndex = (idx + 1) & 0xFF;
            DacComponent = 0;
            DacLatch = new List<int>();
        }
    }
}
