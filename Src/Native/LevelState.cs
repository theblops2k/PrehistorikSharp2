namespace Pre2Port.Native;

/// <summary>
/// The native game memory while a level runs: a private copy of the boot image (<see cref="BootImage"/>, 0x140000 bytes =
/// 1 MB address space + the 4 EGA plane shadows at 0x100000) mutated byte by byte exactly like the Python
/// <c>NativeGameState</c> (<c>pre2/native/state.py</c>). Offsets passed to <see cref="Rb"/>/<see cref="Rw"/>/<see cref="Wb"/>/<see cref="Ww"/>
/// are DGROUP-relative (DS = 0x1A0F); <see cref="Fb"/>/<see cref="Fw"/> take flat addresses.
/// </summary>
public sealed class LevelState
{
    public const int PlaneBase = 0x100000;     // pre2/native/vga.py EGA_APERTURE
    public const int PlaneStride = 0x10000;    // EGA_PLANE_STRIDE
    public const int AddrMask = 0xFFFFF;

    /// <summary>The 0x140000-byte memory image (a copy; the boot image passed in is not modified).</summary>
    public byte[] Data { get; }
    public string OriginalDir { get; }

    public LevelState(byte[] bootImage, string originalDir)
    {
        if (bootImage.Length != BootImage.ImageLen)
            throw new InvalidDataException($"image is {bootImage.Length} bytes, expected {BootImage.ImageLen}");
        Data = (byte[])bootImage.Clone();
        OriginalDir = originalDir;
    }

    /// <summary>Last HUD state drawn while alive (render_state.py mem._hud_frozen_predeath); object-level so a fresh state (cold boot) starts empty. Boxed: HudValues lives in Pre2Port.Recovered.</summary>
    public object? HudFrozenPreDeath { get; set; }

    /// <summary>play_sfx ids requested by the logic since the scene last drained them (the host forwards them to the audio engine; capped like pre2 sfx_queue).</summary>
    public List<int> SfxQueue { get; } = new();

    /// <summary>Song index requested by a recovered routine (the 7585 boss-music 02CC call, index 0xD); the scene loads it and clears it.</summary>
    public int? SongRequest { get; set; }

    /// <summary>[asm 0282] request sound effect <paramref name="id"/>.</summary>
    public void Sfx(int id)
    {
        if (SfxQueue.Count >= 64) SfxQueue.RemoveAt(0);
        SfxQueue.Add(id);
    }

    /// <summary>Flat address of plane <paramref name="plane"/> (0..3) at plane offset <paramref name="off"/>.</summary>
    public static int Plane(int plane, int off) => PlaneBase + plane * PlaneStride + off;

    public int Rb(int off) => Data[BootImage.DsBase + (off & 0xFFFF)];
    public int Rw(int off) => Data[BootImage.DsBase + (off & 0xFFFF)] | (Data[BootImage.DsBase + ((off + 1) & 0xFFFF)] << 8);
    public void Wb(int off, int v) => Data[BootImage.DsBase + (off & 0xFFFF)] = (byte)v;
    public void Ww(int off, int v)
    {
        Data[BootImage.DsBase + (off & 0xFFFF)] = (byte)v;
        Data[BootImage.DsBase + ((off + 1) & 0xFFFF)] = (byte)(v >> 8);
    }

    public int Fb(int addr) => Data[addr & AddrMask];
    public int Fw(int addr) => Data[addr & AddrMask] | (Data[(addr + 1) & AddrMask] << 8);

    /// <summary>Code-segment byte (CS = 0x1030), e.g. the 3af2 snap flag cs:[0x6771].</summary>
    public int CsRb(int off) => Data[BootImage.CsBase + (off & 0xFFFF)];
    public void CsWb(int off, int v) => Data[BootImage.CsBase + (off & 0xFFFF)] = (byte)v;

    /// <summary>NUL-terminated ASCII string at DGROUP <paramref name="off"/> (how the loader receives a file name in DX).</summary>
    public string CString(int off)
    {
        int start = BootImage.DsBase + (off & 0xFFFF);
        int end = Array.IndexOf(Data, (byte)0, start);
        if (end < 0) throw new InvalidDataException($"unterminated string at DS:{off:X4}");
        return System.Text.Encoding.ASCII.GetString(Data, start, end - start);
    }

    /// <summary>Resolve an asset name in the game directory (case-insensitive fallback).</summary>
    public string ResolvePath(string name)
    {
        string p = Path.Combine(OriginalDir, name);
        if (File.Exists(p)) return p;
        foreach (string f in Directory.EnumerateFiles(OriginalDir))
            if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
        throw new FileNotFoundException(name);
    }

    /// <summary>DGROUP offsets used by the level code (sources: pre2/native/dgroup_offsets.py, pre2/views/dgroup_view.py, pre2/views/frame.py).</summary>
    public static class Off
    {
        // loader
        public const int ResetBase = 0x0039, FgBank = 0x003B;
        public const int PalettePtrTable = 0x2D00, LevelHeaderTable = 0x2D30, Level = 0x2D8A, LevelName = 0x2D8B;
        public const int FilenameDigit = 0x2D90, GfxGroupTable = 0x2D96, BackName = 0x2DA6, GfxGroup = 0x2DAA;
        public const int UnionName = 0x0A25, LoadTop = 0x2875, MapSeg = 0x2DDA, UnionBankSeg = 0x2DDC, MapRows = 0x2CF5;
        public const int TileIndex = 0x25CE, PropDst = 0x7E5E, ObjPool = 0x4F1C;
        public const int CollectMain = 0x2A74, CollectCounter = 0x2A76, CollectDecor = 0x2A78, CollectLinked = 0x2A7A;
        public const int BonusCells = 0x8C8D;
        // tables
        public const int TileFlags = 0x805E, BlitType = 0x4DF8, MaskRegion = 0x2DF8;
        public const int AnimTable0 = 0x6688, AnimTable3 = 0x6988, AnimRemapPtr = 0x6BC2, AnimThrottle = 0x6BD4, AnyAnimated = 0x6BBD;
        // level property header (inside the 0x7E5E property block)
        public const int PropHeader = 0x815E, StartX = 0x8160, StartY = 0x8162, ScrollAccum = 0x8164, LevelFlags = 0x8166;
        // camera / scroll
        public const int CamCol = 0x2DE4, CamRow = 0x2DE6, ColRing = 0x2DE8, RowRing = 0x2DEA, PrevCamX = 0x2DE0, PrevCamY = 0x2DE2;
        public const int ScrollSrc = 0x2DBA, DisplayPage = 0x2DD6, BackPage = 0x2DD8, FineScroll = 0x6BC4, RowFactor = 0x6BF8;
        public const int TileFlagsAcc = 0x2DF2, GridDirty = 0x2DF4, ScrollAnimCtr = 0x2DF5, BgRestorePtr = 0x2DF6;
        public const int ScrollDir = 0x6BED, CamScrollIdle = 0x6BEE, ScrollTargetRow = 0x6BF1, Friction = 0x6BF6, Gate6BD9 = 0x6BD9, Window6BFE = 0x6BFE;
        public const int SpeedCurve = 0x78C6, SpeedCurvePtr = 0x78C4, TimerBlock = 0x6BC4, TrailRing = 0x6BBE;
        // player record (render slot #1) 0x4F1C
        public const int PlayerX = 0x4F1C, PlayerY = 0x4F1E, PlayerXvel = 0x4F22, PlayerMoveFlag = 0x4F23, PlayerYvel = 0x4F2A;
    }
}
