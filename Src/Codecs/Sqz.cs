namespace Pre2Port.Codecs;

/// <summary>
/// Prehistorik 2 <c>.SQZ</c> / <c>.TRK</c> decompression.
/// Faithful port of <c>pre2/codecs/sqz.py</c> (VERIFIED there against the original
/// ASM). Three codecs, selected by the file header:
/// <list type="bullet">
/// <item>LZSS: header word <c>b4 4c</c> (graphics assets, music modules).</item>
/// <item>LZW: <c>data[1] == 0x10</c> (keyb / castle / present / titus).</item>
/// <item>Huffman + RLE "other" format (sample / theend).</item>
/// </list>
/// </summary>
public static class Sqz
{
    private const int LzssStreamOffset = 17;
    private const int LzwStreamOffset = 4;

    /// <summary>Decompress a complete <c>.SQZ</c>/<c>.TRK</c> file, dispatching on its header.</summary>
    public static byte[] Unpack(byte[] data)
    {
        if (data[0] == 0xB4 && data[1] == 0x4C)
            return UnpackLzss(data, LzssStreamOffset);
        if (data[1] == 0x10)
            return UnpackLzw(data, LzwStreamOffset);
        return UnpackOther(data);
    }

    /// <summary>Huffman + RLE "other" format (<c>1030:10D7</c>, tree walker <c>1030:11BD</c>).</summary>
    public static byte[] UnpackOther(byte[] data)
    {
        int word0 = data[0] | (data[1] << 8);
        int word1 = data[2] | (data[3] << 8);
        int size = (word0 << 16) | word1;
        int treeSize = data[4] | (data[5] << 8);
        byte[] tree = data[6..(6 + treeSize)];
        long bitPos = (6 + treeSize) * 8L;
        var output = new List<byte>(size);

        int GetBit()
        {
            int bit = (data[(int)(bitPos >> 3)] >> (7 - (int)(bitPos & 7))) & 1; // MSB-first
            bitPos++;
            return bit;
        }

        // [asm 11BD] walk the implicit-root Huffman tree: each bit picks the left
        // (node) or right (node+2) child word; bit 15 set marks a leaf.
        int Symbol()
        {
            int node = 0;
            while (true)
            {
                if (GetBit() != 0)
                    node += 2;
                node = tree[node] | (tree[node + 1] << 8);
                if ((node & 0x8000) != 0)
                    return node & 0x7FFF;
            }
        }

        while (output.Count < size)
        {
            int sym = Symbol();
            if (sym < 0x100)
            {
                output.Add((byte)sym); // [asm 111B] literal byte
                continue;
            }

            int low = sym & 0xFF; // [asm 111F] RLE run length of the previous byte
            int count;
            if (low >= 2)
                count = low;
            else if (low == 0)
                count = Symbol(); // [asm 1138]
            else
                count = ((Symbol() & 0xFF) << 8) | (Symbol() & 0xFF); // [asm 114B]

            byte prev = output[^1];
            for (int i = 0; i < count; i++)
                output.Add(prev);
        }

        return output.Count > size ? output.GetRange(0, size).ToArray() : output.ToArray();
    }

    /// <summary>LZW (<c>1030:1208-133B</c>): 9-12 bit MSB-first codes, CLEAR=0x100, END=0x101.</summary>
    public static byte[] UnpackLzw(byte[] data, int start)
    {
        const int Clear = 0x100, End = 0x101, First = 0x102;
        var output = new List<byte>();
        var prefix = new int[4096];
        var suffix = new byte[4096];
        long bitPos = start * 8L;
        int width = 9;
        int threshold = 0x200;

        int GetCode(int nextCode)
        {
            if (threshold == nextCode && width < 12) // [asm 133E-1351] grow width
            {
                width++;
                threshold <<= 1;
            }
            int i = (int)(bitPos >> 3);
            int off = (int)(bitPos & 7);
            int b0 = data[i];
            int b1 = i + 1 < data.Length ? data[i + 1] : 0;
            int b2 = i + 2 < data.Length ? data[i + 2] : 0;
            int code = (((b0 << 16) | (b1 << 8) | b2) >> (24 - off - width)) & ((1 << width) - 1);
            bitPos += width;
            return code;
        }

        while (true) // [asm 1277] (re)entry, incl. after CLEAR
        {
            width = 9;
            threshold = 0x200; // [asm 1329] reset
            int nextCode = First;
            int code = GetCode(nextCode); // [asm 127A]
            if (code == End) // [asm 127D]
                break;

            int prev = code;
            int lastByte = code & 0xFF;
            output.Add((byte)lastByte); // [asm 1282-1288]

            while (true) // [asm 1289] main loop
            {
                code = GetCode(nextCode);
                if (code == Clear) // [asm 128C] -> outer reset
                    break;
                if (code == End) // [asm 1291 -> 130b]
                    return output.ToArray();

                int saved = code; // [asm 1296]
                var stack = new List<byte>();
                int cur;
                if (code >= nextCode) // [asm 129C] KwKwK
                {
                    stack.Add((byte)lastByte);
                    cur = prev;
                }
                else
                {
                    cur = code;
                }

                while ((cur >> 8) != 0) // [asm 12AC] walk prefix chain
                {
                    stack.Add(suffix[cur]);
                    cur = prefix[cur];
                }
                stack.Add((byte)cur); // [asm 12C1] first char
                lastByte = cur; // [asm 12C4]
                for (int k = stack.Count - 1; k >= 0; k--) // [asm 12C7-12E0] emit reversed
                    output.Add(stack[k]);

                if (nextCode < 0x1000) // [asm 12E2-12FE] add new entry
                {
                    suffix[nextCode] = (byte)lastByte;
                    prefix[nextCode] = prev;
                    nextCode++;
                }
                prev = saved; // [asm 1302]
            }
        }

        return output.ToArray();
    }

    /// <summary>LZSS bit-stream (<c>1030:1414-16E3</c>), the hot graphics path.</summary>
    public static byte[] UnpackLzss(byte[] data, int start)
    {
        var output = new List<byte>();
        int si = start;

        // [asm 14C5-14C8] prime the bit accumulator: dl = 16 bits, bp = first word.
        int bp = data[si] | (data[si + 1] << 8);
        si += 2;
        int dl = 16;

        // [asm shr bp,1 ; dec dl ; refill@16E3 when empty] LSB-first. The refill
        // happens after the bit is extracted, so the extracted bit is preserved.
        int GetBit()
        {
            int cf = bp & 1;
            bp >>= 1;
            dl--;
            if (dl == 0)
            {
                bp = data[si] | (data[si + 1] << 8);
                si += 2;
                dl = 16;
            }
            return cf;
        }

        while (true)
        {
            // [asm 14DC] control bit: 1 -> literal, 0 -> match.
            if (GetBit() != 0)
            {
                output.Add(data[si++]); // [asm 14E4 movsb]
                continue;
            }

            // [asm 14E7] match. Low distance byte; bh defaults to 0xFF so the
            // 16-bit distance is a negative (back-reference) offset.
            int b1 = GetBit();
            int bl = data[si++];
            int bh = 0xFF;
            int cl;

            if (b1 != 0)
            {
                // [asm 153A] long distance: one extra bit into bh.
                bh = ((bh << 1) | GetBit()) & 0xFF;
                if (GetBit() == 0)
                {
                    // [asm 154A] distance extension loop (up to 3 iterations).
                    int dhExt = 2;
                    int clD = 3;
                    while (true)
                    {
                        if (GetBit() != 0) // [asm 1554 jb 1562]
                            break;
                        bh = ((bh << 1) | GetBit()) & 0xFF; // [asm 1556/155C]
                        dhExt = (dhExt + dhExt) & 0xFF; // [asm 155E]
                        clD--;
                        if (clD == 0)
                            break;
                    }
                    bh = (bh - dhExt) & 0xFF; // [asm 1562 sub bh,dh]
                }

                // [asm 1564] length: unary-ish run building dh, then dh -> cl.
                int dh = 2;
                int clL = 4;
                bool jumped = false;
                while (true)
                {
                    dh = (dh + 1) & 0xFF; // [asm 1568 inc dh]
                    if (GetBit() != 0) // [asm 1570 jb 1587]
                    {
                        jumped = true;
                        break;
                    }
                    clL--;
                    if (clL == 0)
                        break;
                }

                bool longLen = false;
                cl = 0;
                if (!jumped)
                {
                    if (GetBit() != 0) // [asm 157A] -> 157C
                    {
                        dh = (dh + 1) & 0xFF;
                        if (GetBit() != 0) // [asm 1584 adc dh,0]
                            dh = (dh + 1) & 0xFF;
                    }
                    else if (GetBit() != 0) // [asm 1592 jb 15A7] explicit length byte
                    {
                        cl = data[si] + 0x11; // [asm 15A7-15AA]
                        si++;
                        longLen = true;
                    }
                    else // [asm 1594] 3 raw bits + 9
                    {
                        dh = 0;
                        for (int k = 0; k < 3; k++)
                            dh = ((dh << 1) | GetBit()) & 0xFF;
                        dh = (dh + 9) & 0xFF;
                    }
                }
                if (!longLen)
                    cl = dh; // [asm 1587 mov cl,dh]
            }
            else
            {
                // [asm 14F3] short distance.
                if (GetBit() != 0) // [asm 14F9 jb 1509]
                {
                    // [asm 1509] shift 3 extra bits into bh, then bh -= 1.
                    for (int k = 0; k < 3; k++)
                        bh = ((bh << 1) | GetBit()) & 0xFF;
                    bh = (bh - 1) & 0xFF;
                    cl = 2;
                }
                else
                {
                    // [asm 14FB cmp bl,bh ; je 15C4] end-of-stream marker.
                    if (bl == bh)
                        break;
                    cl = 2;
                }
            }

            // [asm 1501] copy cl bytes from the back-reference (overlap allowed).
            int bx = (bh << 8) | bl;
            int off = bx >= 0x8000 ? bx - 0x10000 : bx;
            int refPos = output.Count + off;
            for (int k = 0; k < cl; k++)
                output.Add(output[refPos++]);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Alternate LZW of the CDRUN wrapper (<c>PRE2.SQZ</c> = the compressed PRE2.EXE): header <c>[size hi byte][0x10][size lo word LE]</c>, then 9-12 bit
    /// MSB-first codes with END=0x100 and CLEAR=0x101 (the swapped constants are what tells it apart from <see cref="UnpackLzw"/>, which stops after the MZ header).
    /// Port of <c>Sources/pre2_port/scripts/extract_pre2_from_sqz.py</c>. Throws <see cref="InvalidDataException"/> on a bad stream or a size mismatch.
    /// </summary>
    public static byte[] UnpackCdrun(byte[] data)
    {
        if (data.Length < 8 || data[1] != 0x10) throw new InvalidDataException("not a CDRUN LZW wrapper");
        int expected = (data[0] << 16) | data[2] | (data[3] << 8);
        const int EndCode = 0x100, ClearCode = 0x101, First = 0x102, MaxTable = 0x1000;
        int pos = 4;
        int Get() => pos < data.Length ? data[pos++] : (pos++ > data.Length + 16 ? throw new InvalidDataException("CDRUN stream truncated") : 0);

        var prefix = new int[MaxTable];
        var postfix = new byte[MaxTable];
        var firstByte = new byte[MaxTable];
        int dictSize = First, nbit = 9, bitPos = 0, prev = ClearCode;
        int buf24 = (Get() << 16) | (Get() << 8) | Get();
        var output = new List<byte>(expected);
        var seq = new List<byte>();

        while (prev != EndCode)
        {
            if (prev == ClearCode) { nbit = 9; dictSize = First; }
            bitPos += nbit;
            int code = (buf24 >> (24 - bitPos)) & ((1 << nbit) - 1);
            buf24 = (buf24 << 8) | Get();
            if (bitPos >= 16) buf24 = (buf24 << 8) | Get();
            buf24 &= 0xFFFFFF;
            bitPos &= 7;

            if (code != ClearCode && code != EndCode)
            {
                byte newByte;
                if (code < dictSize)
                    newByte = code < First ? (byte)code : firstByte[code];
                else
                {
                    if (prev == ClearCode || dictSize >= MaxTable || code != dictSize) throw new InvalidDataException("invalid CDRUN LZW stream");
                    newByte = prev < First ? (byte)prev : firstByte[prev];
                }
                if (prev != ClearCode && dictSize < MaxTable)
                {
                    prefix[dictSize] = prev;
                    postfix[dictSize] = newByte;
                    firstByte[dictSize] = prev < First ? (byte)prev : firstByte[prev];
                    dictSize++;
                    if (dictSize == (1 << nbit) && nbit < 12) nbit++;
                }
                seq.Clear();
                int cur = code;
                while (cur >= First) { seq.Add(postfix[cur]); cur = prefix[cur]; }
                seq.Add((byte)cur);
                for (int i = seq.Count - 1; i >= 0; i--) output.Add(seq[i]);
                if (output.Count > expected) throw new InvalidDataException("CDRUN output larger than the header size");
            }
            prev = code;
        }
        if (output.Count != expected) throw new InvalidDataException($"CDRUN size {output.Count}, expected {expected}");
        return output.ToArray();
    }
}
