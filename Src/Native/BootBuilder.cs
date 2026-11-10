using System.Security.Cryptography;

namespace Pre2Port.Native;

/// <summary>
/// Builds the boot image (what used to ship as <c>Data\boot_image.bin</c>, generated in Python by <c>build_boot_memory()</c>) straight from the
/// user's own PRE2.EXE, with no derived file shipped and no Python.
/// <para>
/// How: the boot image is the machine state at PRE2's "OLDIES" entry (1030:240A, after main()'s boot init), reduced to the 64 KB DGROUP (DS = 1A0Fh) + the four
/// CS-resident table windows the recovered code reads; everything else is zero (same layout as <c>boot_data.py</c>, 0x140000 bytes). This class runs the
/// EXE's real start-up code up to that address in a tiny real-mode 8086 interpreter (only what the boot path needs: ~150 opcode forms, DOS int 21h
/// open/read/seek/close, BIOS int 10h/12h/1Ah, the VGA DAC read-back and the joystick port) and then cuts the same windows out of the 1 MB memory.
/// The start-up code reads KEYB.SQZ and ALLFONTS.SQZ from the game folder (the fonts band and the decoder scratch of DGROUP depend on them), so those
/// two files have to be next to PRE2.EXE.
/// </para>
/// <para>
/// The only environment value that reaches DGROUP is the BIOS year (int 1Ah/04h, stored at DS:0037h). It is pinned to 2026 so the image is
/// deterministic and identical to the reference <c>work\boot_image.bin</c> (a different clock year would change that single byte).
/// </para>
/// Verified byte for byte against work\boot_image.bin (see <see cref="ExpectedSha256"/>).
/// </summary>
public static class BootBuilder
{
    /// <summary>SHA-256 of the reference boot image (generated from the GOG build of PRE2.EXE, the one this port targets).</summary>
    public const string ExpectedSha256 = BootImage.ExpectedSha256;

    public const string ExeName = "PRE2.EXE";
    /// <summary>Game files the start-up code opens besides the EXE itself.</summary>
    public static readonly string[] SideFiles = { "KEYB.SQZ", "ALLFONTS.SQZ" };

    public const int ImageLength = 0x140000;      // 1 MB address space + 4 x 64 KB EGA plane shadow (as boot_data.IMAGE_LEN)
    const int DsSeg = 0x1A0F;                     // DGROUP paragraph
    const int CsSeg = 0x1030;                     // code segment
    const int PspSeg = 0x1000, LoadSeg = 0x1010;  // where DOS (and the reference VM) put the PSP / the load module
    const ushort CaptureIp = 0x240A;              // OLDIES entry: 1030:240A
    const long MaxSteps = 5_000_000;              // the real boot takes ~216,000 instructions
    const ushort BiosYearBcd = 0x2026;            // int 1Ah AH=04h: CH=century CL=year (BCD); see class remarks
    const ushort BiosMonthDayBcd = 0x1108;        // DH=month DL=day (BCD): not stored anywhere in DGROUP, pinned only so the execution is reproducible

    /// <summary>CS-resident windows (offset, length) the recovered code reads (boot_data.CS_TABLES).</summary>
    static readonly (int Off, int Len)[] CsWindows = { (0x0000, 0x10), (0x1D60, 0x10), (0x6770, 4), (0x6AA0, 0x30) };

    public sealed class BootBuildException : Exception
    {
        public BootBuildException(string message) : base(message) { }
    }

    /// <summary>Builds the image from <paramref name="folder"/>\PRE2.EXE + KEYB.SQZ + ALLFONTS.SQZ (case-insensitive names).</summary>
    public static byte[] BuildFromFolder(string folder)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.EnumerateFiles(folder)) byName[Path.GetFileName(f)] = f;
        if (!byName.TryGetValue(ExeName, out string? exe)) throw new BootBuildException("Falta " + ExeName);
        return Build(File.ReadAllBytes(exe), name => byName.TryGetValue(Path.GetFileName(name), out string? p) ? File.ReadAllBytes(p) : null);
    }

    /// <summary>Builds the image from the EXE bytes; <paramref name="readFile"/> returns a game file by name (null = not found).</summary>
    public static byte[] Build(byte[] exe, Func<string, byte[]?> readFile)
    {
        var m = new Machine(exe, readFile);
        try { m.RunToCapture(); }
        catch (BootBuildException) { throw; }
        catch (Exception e) { throw new BootBuildException($"PRE2.EXE: ejecución inválida del arranque ({e.GetType().Name}); ¿otra versión del juego?"); }
        var img = new byte[ImageLength];
        Array.Copy(m.Mem, DsSeg << 4, img, DsSeg << 4, 0x10000);
        foreach (var (off, len) in CsWindows) Array.Copy(m.Mem, (CsSeg << 4) + off, img, (CsSeg << 4) + off, len);
        return img;
    }


    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // ------------------------------------------------------------------------------------------------------------------------------------
    sealed class Machine
    {
        public readonly byte[] Mem = new byte[0x100000];
        readonly ushort[] R = new ushort[8];     // AX CX DX BX SP BP SI DI
        readonly ushort[] S = new ushort[4];     // ES CS SS DS
        const int AX = 0, CX = 1, DX = 2, BX = 3, SP = 4, BP = 5, SI = 6, DI = 7;
        const int ES = 0, CS = 1, SS = 2, DS = 3;
        ushort ip;
        bool cf, pf, af, zf, sf, of, df, ifl = true, tf;
        readonly Func<string, byte[]?> files;

        // DOS files: handle -> (data, pos)
        readonly Dictionary<int, (byte[] Data, int Pos)> open = new();

        // VGA DAC (6-bit components), state shared by reads and writes like the reference model
        readonly byte[,] pal = new byte[256, 3];
        int dacWriteIdx, dacReadIdx, dacComp;
        readonly List<int> dacLatch = new();

        static readonly bool[] Parity = BuildParity();
        static bool[] BuildParity()
        {
            var p = new bool[256];
            for (int i = 0; i < 256; i++) { int b = i; b ^= b >> 4; b ^= b >> 2; b ^= b >> 1; p[i] = (b & 1) == 0; }
            return p;
        }

        public Machine(byte[] exe, Func<string, byte[]?> readFile)
        {
            files = readFile;
            LoadExe(exe);
            InitBios();
            InitPalette();
        }

        // ---- loading ---------------------------------------------------------------------------------------------------------------
        void LoadExe(byte[] d)
        {
            if (d.Length < 28 || d[0] != 'M' || d[1] != 'Z') throw new BootBuildException("PRE2.EXE no es un ejecutable DOS (MZ)");
            int W(int o) => d[o] | (d[o + 1] << 8);
            int cblp = W(2), cp = W(4), nrel = W(6), hdr = W(8) * 16, ss = W(14), sp = W(16), ipv = W(20), cs = W(22), relOff = W(24);
            int imageSize = cp * 512 - (cblp != 0 ? 512 - cblp : 0);
            imageSize = Math.Min(imageSize, d.Length);
            if (hdr > imageSize || relOff + nrel * 4 > d.Length) throw new BootBuildException("PRE2.EXE: cabecera MZ inválida");
            int baseLin = LoadSeg << 4;
            if (baseLin + (imageSize - hdr) > Mem.Length) throw new BootBuildException("PRE2.EXE: demasiado grande");
            Array.Copy(d, hdr, Mem, baseLin, imageSize - hdr);
            for (int i = 0; i < nrel; i++)
            {
                int ro = W(relOff + i * 4), rs = W(relOff + i * 4 + 2);
                int seg = (LoadSeg + rs) & 0xFFFF;
                Ww(seg, ro, (Rw(seg, ro) + LoadSeg) & 0xFFFF);
            }
            // PSP (as the reference loader builds it): INT 20h, memory top 9FFFh, empty command tail
            Mem[PspSeg << 4] = 0xCD; Mem[(PspSeg << 4) + 1] = 0x20;
            Ww(PspSeg, 2, 0x9FFF);
            Mem[(PspSeg << 4) + 0x80] = 0; Mem[(PspSeg << 4) + 0x81] = 0x0D;
            S[CS] = (ushort)((LoadSeg + cs) & 0xFFFF); ip = (ushort)ipv;
            S[SS] = (ushort)((LoadSeg + ss) & 0xFFFF); R[SP] = (ushort)sp;
            S[DS] = PspSeg; S[ES] = PspSeg;
        }

        void InitBios()
        {
            // power-on BIOS state the program may chain to: IRQ vectors -> IRET stub, INT 9 -> F000:E987, CRTC base port in the BDA
            Mem[0xFFF53] = 0xCF; Mem[0xFE987] = 0xCF;
            foreach (int vec in Enumerable.Range(0x08, 8).Concat(Enumerable.Range(0x70, 8)))
            {
                int o = vec * 4;
                int off = vec == 0x09 ? 0xE987 : 0xFF53;
                Mem[o] = (byte)off; Mem[o + 1] = (byte)(off >> 8); Mem[o + 2] = 0x00; Mem[o + 3] = 0xF0;
            }
            Mem[0x463] = 0xD4; Mem[0x464] = 0x03;
        }

        void InitPalette()
        {
            // default VGA state of the reference model: 16 EGA colours (6-bit), then a grey ramp
            int[,] ega =
            {
                { 0x00, 0x00, 0x00 }, { 0x00, 0x00, 0x2A }, { 0x00, 0x2A, 0x00 }, { 0x00, 0x2A, 0x2A },
                { 0x2A, 0x00, 0x00 }, { 0x2A, 0x00, 0x2A }, { 0x2A, 0x15, 0x00 }, { 0x2A, 0x2A, 0x2A },
                { 0x15, 0x15, 0x15 }, { 0x15, 0x15, 0x3F }, { 0x15, 0x3F, 0x15 }, { 0x15, 0x3F, 0x3F },
                { 0x3F, 0x15, 0x15 }, { 0x3F, 0x15, 0x3F }, { 0x3F, 0x3F, 0x15 }, { 0x3F, 0x3F, 0x3F },
            };
            for (int i = 0; i < 256; i++)
                for (int c = 0; c < 3; c++)
                    pal[i, c] = (byte)(i < 16 ? ega[i, c] : (i >> 2) & 0x3F);
        }

        public void RunToCapture()
        {
            for (long n = 0; n < MaxSteps; n++)
            {
                if (S[CS] == CsSeg && ip == CaptureIp) return;
                Step();
            }
            throw new BootBuildException("PRE2.EXE: el arranque no llegó a 1030:240A (¿otra versión del juego?)");
        }

        // ---- memory ----------------------------------------------------------------------------------------------------------------
        static int Lin(int seg, int off) => ((seg << 4) + off) & 0xFFFFF;
        int Rb(int seg, int off)
        {
            int a = Lin(seg, off);
            if (planar && a >= EgaBase && a < EgaBase + 0x10000) return EgaRead(a - EgaBase);
            return Mem[a];
        }
        int Rw(int seg, int off) => Rb(seg, off) | (Rb(seg, off + 1) << 8);
        void Wb(int seg, int off, int v)
        {
            int a = Lin(seg, off);
            if (a >= 0xF0000) return;                                  // the BIOS ROM area is read-only
            if (planar && a >= EgaBase && a < EgaBase + 0x10000) { EgaWrite(a - EgaBase, v & 0xFF); return; }
            Mem[a] = (byte)v;
        }
        void Ww(int seg, int off, int v) { Wb(seg, off, v); Wb(seg, off + 1, v >> 8); }

        // ---- minimal EGA/VGA planar model (the VGA compatibility probe of the start-up code reads latches back through A000:2000) ----
        const int EgaBase = 0xA0000;
        bool planar;                                     // set once the program touches the sequencer / graphics controller
        readonly byte[][] planes = { new byte[0x10000], new byte[0x10000], new byte[0x10000], new byte[0x10000] };
        readonly byte[] latches = new byte[4];
        int mapMask = 0x0F, readPlane, dataRotate, logicalOp, writeMode, readMode, colorCompare, colorDontCare = 0x0F;
        int seqIndex, gcIndex, videoMode;

        int EgaRead(int po)
        {
            for (int p = 0; p < 4; p++) latches[p] = planes[p][po];
            if ((readMode & 1) != 0)
            {
                int result = 0xFF;
                for (int p = 0; p < 4; p++)
                    if ((colorDontCare & (1 << p)) != 0)
                        result &= ((colorCompare >> p) & 1) != 0 ? latches[p] : ~latches[p] & 0xFF;
                return result;
            }
            return latches[readPlane & 3];
        }

        void EgaWrite(int po, int value)
        {
            int mode = writeMode & 3;
            if (mode >= 2) throw new BootBuildException($"PRE2.EXE: modo de escritura VGA {mode} no soportado");
            if (mode == 1)
            {
                for (int p = 0; p < 4; p++) if ((mapMask & (1 << p)) != 0) planes[p][po] = latches[p];
                return;
            }
            int v = value & 0xFF, rot = dataRotate & 7;
            if (rot != 0) v = ((v >> rot) | (v << (8 - rot))) & 0xFF;
            for (int p = 0; p < 4; p++)
            {
                if ((mapMask & (1 << p)) == 0) continue;
                int l = latches[p];
                planes[p][po] = (byte)(logicalOp switch { 0 => v, 1 => v & l, 2 => v | l, _ => v ^ l });
            }
        }

        void EgaPort(int port, int value)
        {
            bool planarAllowed = (videoMode & 0x7F) is not (0x13 or 0x19);
            switch (port)
            {
                case 0x3C4: if (planarAllowed) planar = true; seqIndex = value; break;
                case 0x3C5:
                    if (planarAllowed) planar = true;
                    if (seqIndex == 2) mapMask = value & 0x0F;
                    break;
                case 0x3CE: if (planarAllowed) planar = true; gcIndex = value; break;
                case 0x3CF:
                    if (planarAllowed) planar = true;
                    switch (gcIndex)
                    {
                        case 2: colorCompare = value & 0x0F; break;
                        case 3: dataRotate = value & 7; logicalOp = (value >> 3) & 3; break;
                        case 4: readPlane = value & 3; break;
                        case 5: writeMode = value & 3; readMode = (value >> 3) & 1; break;
                        case 7: colorDontCare = value & 0x0F; break;
                    }
                    break;
            }
        }

        int Fetch8() { int v = Mem[Lin(S[CS], ip)]; ip++; return v; }
        int Fetch16() { int lo = Fetch8(); return lo | (Fetch8() << 8); }

        void Push(int v) { R[SP] -= 2; Ww(S[SS], R[SP], v); }
        int Pop() { int v = Rw(S[SS], R[SP]); R[SP] += 2; return v; }

        // ---- registers -------------------------------------------------------------------------------------------------------------
        int Get8(int i) => i < 4 ? (R[i] & 0xFF) : (R[i - 4] >> 8);
        void Set8(int i, int v)
        {
            if (i < 4) R[i] = (ushort)((R[i] & 0xFF00) | (v & 0xFF));
            else R[i - 4] = (ushort)((R[i - 4] & 0x00FF) | ((v & 0xFF) << 8));
        }
        int GetR(bool w, int i) => w ? R[i] : Get8(i);
        void SetR(bool w, int i, int v) { if (w) R[i] = (ushort)v; else Set8(i, v); }

        // ---- ModRM -----------------------------------------------------------------------------------------------------------------
        int mod, regf, rmf, eaSeg, eaOff;
        bool isReg;

        void ModRM(int segOv)
        {
            int b = Fetch8();
            mod = b >> 6; regf = (b >> 3) & 7; rmf = b & 7;
            isReg = mod == 3;
            if (isReg) return;
            int ea, seg = DS;
            switch (rmf)
            {
                case 0: ea = R[BX] + R[SI]; break;
                case 1: ea = R[BX] + R[DI]; break;
                case 2: ea = R[BP] + R[SI]; seg = SS; break;
                case 3: ea = R[BP] + R[DI]; seg = SS; break;
                case 4: ea = R[SI]; break;
                case 5: ea = R[DI]; break;
                case 6:
                    if (mod == 0) ea = Fetch16();
                    else { ea = R[BP]; seg = SS; }
                    break;
                default: ea = R[BX]; break;
            }
            if (mod == 1) ea += (sbyte)Fetch8();
            else if (mod == 2) ea += Fetch16();
            eaOff = ea & 0xFFFF;
            eaSeg = S[segOv >= 0 ? segOv : seg];
        }

        int GetRM(bool w) => isReg ? GetR(w, rmf) : (w ? Rw(eaSeg, eaOff) : Rb(eaSeg, eaOff));
        void SetRM(bool w, int v)
        {
            if (isReg) SetR(w, rmf, v);
            else if (w) Ww(eaSeg, eaOff, v);
            else Wb(eaSeg, eaOff, v);
        }

        // ---- flags / ALU -----------------------------------------------------------------------------------------------------------
        int FlagsWord() =>
            (cf ? 1 : 0) | 2 | (pf ? 4 : 0) | (af ? 0x10 : 0) | (zf ? 0x40 : 0) | (sf ? 0x80 : 0) | (tf ? 0x100 : 0) | (ifl ? 0x200 : 0) | (df ? 0x400 : 0) | (of ? 0x800 : 0);
        void SetFlags(int f)
        {
            cf = (f & 1) != 0; pf = (f & 4) != 0; af = (f & 0x10) != 0; zf = (f & 0x40) != 0; sf = (f & 0x80) != 0;
            tf = (f & 0x100) != 0; ifl = (f & 0x200) != 0; df = (f & 0x400) != 0; of = (f & 0x800) != 0;
        }

        void Szp(int res, bool w)
        {
            int mask = w ? 0xFFFF : 0xFF, sign = w ? 0x8000 : 0x80;
            res &= mask;
            zf = res == 0; sf = (res & sign) != 0; pf = Parity[res & 0xFF];
        }

        /// <summary>0 add, 1 or, 2 adc, 3 sbb, 4 and, 5 sub, 6 xor, 7 cmp. Returns the result (caller skips the write-back for cmp).</summary>
        int Alu(int op, int a, int b, bool w)
        {
            int mask = w ? 0xFFFF : 0xFF, sign = w ? 0x8000 : 0x80;
            int res;
            switch (op)
            {
                case 0:
                case 2:
                {
                    int c = (op == 2 && cf) ? 1 : 0;
                    int full = a + b + c;
                    res = full & mask;
                    cf = full > mask;
                    af = ((a ^ b ^ res) & 0x10) != 0;
                    of = ((a ^ res) & (b ^ res) & sign) != 0;
                    break;
                }
                case 3:
                case 5:
                case 7:
                {
                    int c = (op == 3 && cf) ? 1 : 0;
                    int full = a - b - c;
                    res = full & mask;
                    cf = full < 0;
                    af = ((a ^ b ^ res) & 0x10) != 0;
                    of = ((a ^ b) & (a ^ res) & sign) != 0;
                    break;
                }
                case 1: res = (a | b) & mask; cf = of = false; break;      // AF is left untouched by logic ops (undefined on the 8086; same as the reference model)
                case 4: res = (a & b) & mask; cf = of = false; break;
                default: res = (a ^ b) & mask; cf = of = false; break;
            }
            Szp(res, w);
            return res;
        }

        int IncDec(int v, bool w, bool dec)
        {
            int mask = w ? 0xFFFF : 0xFF, sign = w ? 0x8000 : 0x80;
            int res = (dec ? v - 1 : v + 1) & mask;
            af = ((v ^ res) & 0x10) != 0;
            of = dec ? res == sign - 1 : res == sign;
            Szp(res, w);
            return res;
        }

        int Shift(int op, int v, int cnt, bool w)
        {
            int mask = w ? 0xFFFF : 0xFF, sign = w ? 0x8000 : 0x80;
            cnt &= 0x1F;
            bool ofKeep = of;
            for (int i = 0; i < cnt; i++)
            {
                switch (op)
                {
                    case 0: { int c = (v & sign) != 0 ? 1 : 0; v = ((v << 1) | c) & mask; cf = c != 0; of = ((v & sign) != 0) ^ cf; break; }
                    case 1: { int c = v & 1; v = (v >> 1) | (c != 0 ? sign : 0); cf = c != 0; of = ((v & sign) != 0) ^ ((v & (sign >> 1)) != 0); break; }
                    case 2: { int c = (v & sign) != 0 ? 1 : 0; v = ((v << 1) | (cf ? 1 : 0)) & mask; cf = c != 0; of = ((v & sign) != 0) ^ cf; break; }
                    case 3: { int c = v & 1; v = (v >> 1) | (cf ? sign : 0); cf = c != 0; of = ((v & sign) != 0) ^ ((v & (sign >> 1)) != 0); break; }
                    case 4:
                    case 6: cf = (v & sign) != 0; v = (v << 1) & mask; of = ((v & sign) != 0) ^ cf; Szp(v, w); break;
                    case 5: cf = (v & 1) != 0; of = (v & sign) != 0; v >>= 1; Szp(v, w); break;
                    default: cf = (v & 1) != 0; v = (v >> 1) | (v & sign); of = false; Szp(v, w); break;
                }
            }
            if (cnt != 1) of = ofKeep;      // OF is only defined for 1-bit shifts/rotates; left untouched otherwise (as the reference model does)
            return v;
        }

        bool Cond(int c)
        {
            bool r = (c >> 1) switch
            {
                0 => of,
                1 => cf,
                2 => zf,
                3 => cf || zf,
                4 => sf,
                5 => pf,
                6 => sf != of,
                _ => zf || (sf != of),
            };
            return (c & 1) != 0 ? !r : r;
        }

        // ---- execution -------------------------------------------------------------------------------------------------------------
        void Step()
        {
            int segOv = -1, rep = 0, op;
            for (; ; )
            {
                op = Fetch8();
                if (op == 0x26) segOv = ES;
                else if (op == 0x2E) segOv = CS;
                else if (op == 0x36) segOv = SS;
                else if (op == 0x3E) segOv = DS;
                else if (op == 0xF3) rep = 1;
                else if (op == 0xF2) rep = 2;
                else if (op == 0xF0 || op == 0x66 || op == 0x67) { }      // lock; 386 size prefixes (only used in a CPU probe whose 16-bit result is identical)
                else break;
            }

            if (op < 0x40 && (op & 7) < 6)
            {
                int alu = (op >> 3) & 7;
                switch (op & 7)
                {
                    case 0: { ModRM(segOv); int r = Alu(alu, GetRM(false), Get8(regf), false); if (alu != 7) SetRM(false, r); break; }
                    case 1: { ModRM(segOv); int r = Alu(alu, GetRM(true), R[regf], true); if (alu != 7) SetRM(true, r); break; }
                    case 2: { ModRM(segOv); int r = Alu(alu, Get8(regf), GetRM(false), false); if (alu != 7) Set8(regf, r); break; }
                    case 3: { ModRM(segOv); int r = Alu(alu, R[regf], GetRM(true), true); if (alu != 7) R[regf] = (ushort)r; break; }
                    case 4: { int r = Alu(alu, Get8(0), Fetch8(), false); if (alu != 7) Set8(0, r); break; }
                    default: { int r = Alu(alu, R[AX], Fetch16(), true); if (alu != 7) R[AX] = (ushort)r; break; }
                }
                return;
            }

            switch (op)
            {
                case 0x06: case 0x0E: case 0x16: case 0x1E: Push(S[(op >> 3) & 3]); return;
                case 0x07: case 0x17: case 0x1F: S[(op >> 3) & 3] = (ushort)Pop(); return;
            }
            if (op >= 0x40 && op <= 0x47) { R[op - 0x40] = (ushort)IncDec(R[op - 0x40], true, false); return; }
            if (op >= 0x48 && op <= 0x4F) { R[op - 0x48] = (ushort)IncDec(R[op - 0x48], true, true); return; }
            if (op >= 0x50 && op <= 0x57) { Push(op == 0x54 ? (R[SP] - 2) & 0xFFFF : R[op - 0x50]); return; }
            if (op >= 0x58 && op <= 0x5F) { int v = Pop(); R[op - 0x58] = (ushort)v; return; }
            if (op >= 0x70 && op <= 0x7F) { int d = (sbyte)Fetch8(); if (Cond(op & 0xF)) ip = (ushort)(ip + d); return; }
            if (op >= 0x91 && op <= 0x97) { int t = R[AX]; R[AX] = R[op - 0x90]; R[op - 0x90] = (ushort)t; return; }
            if (op >= 0xB0 && op <= 0xB7) { Set8(op - 0xB0, Fetch8()); return; }
            if (op >= 0xB8 && op <= 0xBF) { R[op - 0xB8] = (ushort)Fetch16(); return; }

            switch (op)
            {
                case 0x80: case 0x81: case 0x82: case 0x83:
                {
                    bool w = (op & 1) != 0;
                    ModRM(segOv);
                    int imm = op == 0x81 ? Fetch16() : op == 0x83 ? ((sbyte)Fetch8() & 0xFFFF) : Fetch8();
                    int r = Alu(regf, GetRM(w), w ? imm : imm & 0xFF, w);
                    if (regf != 7) SetRM(w, r);
                    return;
                }
                case 0x84: case 0x85:
                {
                    bool w = (op & 1) != 0;
                    ModRM(segOv);
                    Alu(4, GetRM(w), GetR(w, regf), w);
                    return;
                }
                case 0x86: case 0x87:
                {
                    bool w = (op & 1) != 0;
                    ModRM(segOv);
                    int a = GetRM(w), b = GetR(w, regf);
                    SetRM(w, b); SetR(w, regf, a);
                    return;
                }
                case 0x88: case 0x89: { bool w = (op & 1) != 0; ModRM(segOv); SetRM(w, GetR(w, regf)); return; }
                case 0x8A: case 0x8B: { bool w = (op & 1) != 0; ModRM(segOv); SetR(w, regf, GetRM(w)); return; }
                case 0x8C: ModRM(segOv); SetRM(true, S[regf & 3]); return;
                case 0x8D: ModRM(segOv); if (isReg) throw Bad(op); R[regf] = (ushort)eaOff; return;
                case 0x8E: ModRM(segOv); S[regf & 3] = (ushort)GetRM(true); return;
                case 0x8F: { ModRM(segOv); int v = Pop(); SetRM(true, v); return; }
                case 0x90: return;
                case 0x98: R[AX] = (ushort)(int)(sbyte)(R[AX] & 0xFF); return;
                case 0x99: R[DX] = (ushort)((R[AX] & 0x8000) != 0 ? 0xFFFF : 0); return;
                case 0x9A: { int o = Fetch16(), s = Fetch16(); Push(S[CS]); Push(ip); S[CS] = (ushort)s; ip = (ushort)o; return; }
                case 0x9B: return;
                case 0x9C: Push(FlagsWord()); return;
                case 0x9D: SetFlags(Pop()); return;
                case 0x9E: { int ah = R[AX] >> 8; cf = (ah & 1) != 0; pf = (ah & 4) != 0; af = (ah & 0x10) != 0; zf = (ah & 0x40) != 0; sf = (ah & 0x80) != 0; return; }
                case 0x9F: R[AX] = (ushort)((R[AX] & 0xFF) | ((FlagsWord() & 0xFF) << 8)); return;
                case 0xA0: { int o = Fetch16(); Set8(0, Rb(S[segOv >= 0 ? segOv : DS], o)); return; }
                case 0xA1: { int o = Fetch16(); R[AX] = (ushort)Rw(S[segOv >= 0 ? segOv : DS], o); return; }
                case 0xA2: { int o = Fetch16(); Wb(S[segOv >= 0 ? segOv : DS], o, R[AX] & 0xFF); return; }
                case 0xA3: { int o = Fetch16(); Ww(S[segOv >= 0 ? segOv : DS], o, R[AX]); return; }
                case 0xA4: case 0xA5: case 0xA6: case 0xA7: case 0xAA: case 0xAB: case 0xAC: case 0xAD: case 0xAE: case 0xAF:
                    StringOp(op, rep, segOv); return;
                case 0xA8: Alu(4, R[AX] & 0xFF, Fetch8(), false); return;
                case 0xA9: Alu(4, R[AX], Fetch16(), true); return;
                case 0xC2: { int n = Fetch16(); ip = (ushort)Pop(); R[SP] += (ushort)n; return; }
                case 0xC3: ip = (ushort)Pop(); return;
                case 0xC4: case 0xC5:
                {
                    ModRM(segOv);
                    if (isReg) throw Bad(op);
                    R[regf] = (ushort)Rw(eaSeg, eaOff);
                    S[op == 0xC4 ? ES : DS] = (ushort)Rw(eaSeg, eaOff + 2);
                    return;
                }
                case 0xC6: case 0xC7: { bool w = (op & 1) != 0; ModRM(segOv); SetRM(w, w ? Fetch16() : Fetch8()); return; }
                case 0xCA: { int n = Fetch16(); ip = (ushort)Pop(); S[CS] = (ushort)Pop(); R[SP] += (ushort)n; return; }
                case 0xCB: ip = (ushort)Pop(); S[CS] = (ushort)Pop(); return;
                case 0xCD: Interrupt(Fetch8()); return;
                case 0xCF: ip = (ushort)Pop(); S[CS] = (ushort)Pop(); SetFlags(Pop()); return;
                case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                {
                    bool w = (op & 1) != 0;
                    ModRM(segOv);
                    int cnt = op < 0xD2 ? 1 : (R[CX] & 0xFF);
                    SetRM(w, Shift(regf, GetRM(w), cnt, w));
                    return;
                }
                case 0xD7: Set8(0, Rb(S[segOv >= 0 ? segOv : DS], (R[BX] + (R[AX] & 0xFF)) & 0xFFFF)); return;
                case 0xE0: case 0xE1: case 0xE2:
                {
                    int d = (sbyte)Fetch8();
                    R[CX]--;
                    bool take = R[CX] != 0 && (op == 0xE2 || (op == 0xE1 ? zf : !zf));
                    if (take) ip = (ushort)(ip + d);
                    return;
                }
                case 0xE3: { int d = (sbyte)Fetch8(); if (R[CX] == 0) ip = (ushort)(ip + d); return; }
                case 0xE4: Set8(0, PortIn(Fetch8())); return;
                case 0xE5: { int p = Fetch8(); R[AX] = (ushort)(PortIn(p) | (PortIn(p + 1) << 8)); return; }
                case 0xE6: PortOut(Fetch8(), R[AX] & 0xFF); return;
                case 0xE7: { int p = Fetch8(); PortOut(p, R[AX] & 0xFF); PortOut(p + 1, R[AX] >> 8); return; }
                case 0xE8: { int d = (short)Fetch16(); Push(ip); ip = (ushort)(ip + d); return; }
                case 0xE9: { int d = (short)Fetch16(); ip = (ushort)(ip + d); return; }
                case 0xEA: { int o = Fetch16(), s = Fetch16(); S[CS] = (ushort)s; ip = (ushort)o; return; }
                case 0xEB: { int d = (sbyte)Fetch8(); ip = (ushort)(ip + d); return; }
                case 0xEC: Set8(0, PortIn(R[DX])); return;
                case 0xED: R[AX] = (ushort)(PortIn(R[DX]) | (PortIn(R[DX] + 1) << 8)); return;
                case 0xEE: PortOut(R[DX], R[AX] & 0xFF); return;
                case 0xEF: PortOut(R[DX], R[AX] & 0xFF); PortOut(R[DX] + 1, R[AX] >> 8); return;
                case 0xF5: cf = !cf; return;
                case 0xF6: case 0xF7: Group3((op & 1) != 0, segOv); return;
                case 0xF8: cf = false; return;
                case 0xF9: cf = true; return;
                case 0xFA: ifl = false; return;
                case 0xFB: ifl = true; return;
                case 0xFC: df = false; return;
                case 0xFD: df = true; return;
                case 0xFE: case 0xFF: Group45((op & 1) != 0, segOv); return;
            }
            throw Bad(op);
        }

        Exception Bad(int op) =>
            new BootBuildException($"PRE2.EXE: instrucción no soportada {op:X2} en {S[CS]:X4}:{(ushort)(ip - 1):X4}");

        void Group3(bool w, int segOv)
        {
            ModRM(segOv);
            int v = GetRM(w);
            int mask = w ? 0xFFFF : 0xFF, sign = w ? 0x8000 : 0x80;
            switch (regf)
            {
                case 0: case 1: Alu(4, v, w ? Fetch16() : Fetch8(), w); return;
                case 2: SetRM(w, ~v & mask); return;
                case 3:
                {
                    int res = Alu(5, 0, v, w);          // 0 - v
                    cf = v != 0;
                    SetRM(w, res);
                    return;
                }
                case 4:
                    if (w) { uint p = (uint)R[AX] * (uint)v; R[AX] = (ushort)p; R[DX] = (ushort)(p >> 16); cf = of = R[DX] != 0; }
                    else { int p = (R[AX] & 0xFF) * v; R[AX] = (ushort)p; cf = of = (p >> 8) != 0; }
                    return;
                case 5:
                    if (w) { int p = (short)R[AX] * (short)v; R[AX] = (ushort)p; R[DX] = (ushort)(p >> 16); cf = of = p != (short)p; }
                    else { int p = (sbyte)(R[AX] & 0xFF) * (sbyte)v; R[AX] = (ushort)p; cf = of = p != (sbyte)p; }
                    return;
                case 6:
                    if (v == 0) throw new BootBuildException("PRE2.EXE: división por cero");
                    if (w)
                    {
                        uint n = ((uint)R[DX] << 16) | R[AX];
                        uint q = n / (uint)v;
                        if (q > 0xFFFF) throw new BootBuildException("PRE2.EXE: desbordamiento de división");
                        R[AX] = (ushort)q; R[DX] = (ushort)(n % (uint)v);
                    }
                    else
                    {
                        int n = R[AX], q = n / v;
                        if (q > 0xFF) throw new BootBuildException("PRE2.EXE: desbordamiento de división");
                        R[AX] = (ushort)((q & 0xFF) | ((n % v) << 8));
                    }
                    return;
                default:
                    if (v == 0) throw new BootBuildException("PRE2.EXE: división por cero");
                    if (w)
                    {
                        int n = (int)(((uint)R[DX] << 16) | R[AX]);
                        int d = (short)v;
                        int q = n / d;
                        if (q != (short)q) throw new BootBuildException("PRE2.EXE: desbordamiento de división");
                        R[AX] = (ushort)q; R[DX] = (ushort)(n % d);
                    }
                    else
                    {
                        int n = (short)R[AX], d = (sbyte)v, q = n / d;
                        if (q != (sbyte)q) throw new BootBuildException("PRE2.EXE: desbordamiento de división");
                        R[AX] = (ushort)((q & 0xFF) | ((n % d) << 8));
                    }
                    return;
            }
        }

        void Group45(bool w, int segOv)
        {
            ModRM(segOv);
            switch (regf)
            {
                case 0: SetRM(w, IncDec(GetRM(w), w, false)); return;
                case 1: SetRM(w, IncDec(GetRM(w), w, true)); return;
            }
            if (!w) throw Bad(0xFE);
            switch (regf)
            {
                case 2: { int t = GetRM(true); Push(ip); ip = (ushort)t; return; }
                case 3: { if (isReg) throw Bad(0xFF); int o = Rw(eaSeg, eaOff), s = Rw(eaSeg, eaOff + 2); Push(S[CS]); Push(ip); S[CS] = (ushort)s; ip = (ushort)o; return; }
                case 4: ip = (ushort)GetRM(true); return;
                case 5: { if (isReg) throw Bad(0xFF); int o = Rw(eaSeg, eaOff), s = Rw(eaSeg, eaOff + 2); S[CS] = (ushort)s; ip = (ushort)o; return; }
                case 6: { int v = GetRM(true); Push(v); return; }
            }
            throw Bad(0xFF);
        }

        void StringOp(int op, int rep, int segOv)
        {
            bool w = (op & 1) != 0;
            int step = w ? 2 : 1;
            if (df) step = -step;
            int srcSeg = S[segOv >= 0 ? segOv : DS];
            int kind = op & 0xFE;
            bool compares = kind == 0xA6 || kind == 0xAE;
            for (; ; )
            {
                if (rep != 0 && R[CX] == 0) break;
                switch (kind)
                {
                    case 0xA4:
                        if (w) Ww(S[ES], R[DI], Rw(srcSeg, R[SI])); else Wb(S[ES], R[DI], Rb(srcSeg, R[SI]));
                        R[SI] = (ushort)(R[SI] + step); R[DI] = (ushort)(R[DI] + step);
                        break;
                    case 0xA6:
                        Alu(7, w ? Rw(srcSeg, R[SI]) : Rb(srcSeg, R[SI]), w ? Rw(S[ES], R[DI]) : Rb(S[ES], R[DI]), w);
                        R[SI] = (ushort)(R[SI] + step); R[DI] = (ushort)(R[DI] + step);
                        break;
                    case 0xAA:
                        if (w) Ww(S[ES], R[DI], R[AX]); else Wb(S[ES], R[DI], R[AX] & 0xFF);
                        R[DI] = (ushort)(R[DI] + step);
                        break;
                    case 0xAC:
                        if (w) R[AX] = (ushort)Rw(srcSeg, R[SI]); else Set8(0, Rb(srcSeg, R[SI]));
                        R[SI] = (ushort)(R[SI] + step);
                        break;
                    default:    // 0xAE scas
                        Alu(7, w ? R[AX] : R[AX] & 0xFF, w ? Rw(S[ES], R[DI]) : Rb(S[ES], R[DI]), w);
                        R[DI] = (ushort)(R[DI] + step);
                        break;
                }
                if (rep == 0) break;
                R[CX]--;
                if (compares && ((rep == 1 && !zf) || (rep == 2 && zf))) break;
            }
        }

        // ---- ports -----------------------------------------------------------------------------------------------------------------
        int PortIn(int port)
        {
            port &= 0xFFFF;
            if (port == 0x3C9)
            {
                int idx = dacReadIdx & 0xFF, comp = dacComp % 3;
                int v = pal[idx, comp];
                dacComp = (dacComp + 1) % 3;
                if (dacComp == 0) dacReadIdx = (idx + 1) & 0xFF;
                return v;
            }
            return 0;   // unmodelled ports (the joystick at 201h among them) read 0 = no joystick, like the reference model
        }

        void PortOut(int port, int value)
        {
            port &= 0xFFFF; value &= 0xFF;
            if (port is 0x3C4 or 0x3C5 or 0x3CE or 0x3CF) EgaPort(port, value);
            if (port == 0x3C8) { dacWriteIdx = value; dacComp = 0; dacLatch.Clear(); }
            else if (port == 0x3C7) { dacReadIdx = value; dacComp = 0; }
            else if (port == 0x3C9)
            {
                dacLatch.Add(value & 0x3F);
                dacComp++;
                if (dacComp >= 3)
                {
                    int idx = dacWriteIdx & 0xFF;
                    for (int c = 0; c < 3; c++) pal[idx, c] = (byte)dacLatch[c];
                    dacWriteIdx = (idx + 1) & 0xFF;
                    dacComp = 0;
                    dacLatch.Clear();
                }
            }
        }

        // ---- BIOS / DOS ------------------------------------------------------------------------------------------------------------
        void Interrupt(int n)
        {
            int ah = R[AX] >> 8, al = R[AX] & 0xFF;
            switch (n)
            {
                case 0x10:
                    if (ah == 0x10)
                    {
                        if (al == 0x12)
                        {
                            int start = R[BX] & 0xFF, count = R[CX], addr = R[DX];
                            for (int i = 0; i < count; i++)
                            {
                                for (int c = 0; c < 3; c++) pal[(start + i) & 0xFF, c] = (byte)(Rb(S[ES], addr + c) & 0x3F);
                                addr = (addr + 3) & 0xFFFF;
                            }
                        }
                        else if (al == 0x10)
                        {
                            int idx = R[BX] & 0xFF;
                            pal[idx, 0] = (byte)((R[DX] >> 8) & 0x3F); pal[idx, 1] = (byte)((R[CX] >> 8) & 0x3F); pal[idx, 2] = (byte)(R[CX] & 0x3F);
                        }
                        else if (al == 0x17)
                        {
                            int start = R[BX] & 0xFF, count = R[CX], addr = R[DX];
                            for (int i = 0; i < count; i++)
                            {
                                for (int c = 0; c < 3; c++) Wb(S[ES], addr + c, pal[(start + i) & 0xFF, c]);
                                addr = (addr + 3) & 0xFFFF;
                            }
                        }
                        return;     // other sub-functions (attribute palette ...) are no-ops
                    }
                    if (ah == 0x00) { videoMode = al; return; }
                    if (ah is 0x01 or 0x0B or 0x11 or 0x12) return;   // cursor shape / border / font load: no effect on DGROUP
                    throw new BootBuildException($"PRE2.EXE: INT 10h AH={ah:X2} no soportado");
                case 0x12: R[AX] = 640; return;
                case 0x1A:
                    if (ah != 0x04) throw new BootBuildException($"PRE2.EXE: INT 1Ah AH={ah:X2} no soportado");
                    R[CX] = BiosYearBcd; R[DX] = BiosMonthDayBcd; cf = false;
                    return;
                case 0x21: Dos21(ah, al); return;
            }
            throw new BootBuildException($"PRE2.EXE: INT {n:X2}h no soportada");
        }

        string ReadAsciiz(int seg, int off)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 128; i++)
            {
                int b = Rb(seg, off + i);
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        void Dos21(int ah, int al)
        {
            switch (ah)
            {
                case 0x3D:
                {
                    byte[]? data = files(ReadAsciiz(S[DS], R[DX]));
                    if (data is null) { R[AX] = 2; cf = true; return; }
                    int h = 5;
                    while (open.ContainsKey(h)) h++;
                    open[h] = (data, 0);
                    R[AX] = (ushort)h; cf = false;
                    return;
                }
                case 0x3E: open.Remove(R[BX]); cf = false; return;
                case 0x3F:
                {
                    if (!open.TryGetValue(R[BX], out var f)) { R[AX] = 6; cf = true; return; }
                    int n = Math.Min(R[CX], f.Data.Length - f.Pos);
                    for (int i = 0; i < n; i++) Wb(S[DS], R[DX] + i, f.Data[f.Pos + i]);
                    open[R[BX]] = (f.Data, f.Pos + n);
                    R[AX] = (ushort)n; cf = false;
                    return;
                }
                case 0x42:
                {
                    if (!open.TryGetValue(R[BX], out var f)) { R[AX] = 6; cf = true; return; }
                    long delta = (int)(((uint)R[CX] << 16) | R[DX]);
                    long pos = al == 0 ? delta : al == 1 ? f.Pos + delta : f.Data.Length + delta;
                    if (pos < 0) pos = 0;
                    open[R[BX]] = (f.Data, (int)pos);
                    R[DX] = (ushort)(pos >> 16); R[AX] = (ushort)pos; cf = false;
                    return;
                }
            }
            throw new BootBuildException($"PRE2.EXE: INT 21h AH={ah:X2} no soportado");
        }
    }
}
