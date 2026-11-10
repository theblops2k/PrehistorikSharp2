namespace Pre2Port.Native;

/// <summary>
/// Boot-constant gameplay tables (static game content) extracted from the boot image.
/// Port of <c>pre2/views/tables.py</c> (base offsets) + <c>pre2/native/asset_tables.py</c>
/// (extents from <c>scripts/gen_asset_tables.py</c>). Each table is a read-only copy of
/// <c>image[DsBase + offset .. + length]</c>; indexes are 0-based inside the table.
/// </summary>
public static class AssetTables
{
    /// <summary>Table descriptor: name, DGROUP offset and length in bytes (same order as the Python generator).</summary>
    public static readonly (string Name, int Offset, int Length)[] Spans =
    {
        ("SONG_INDEX",    0x2D20,  106),   // level -> per-level song index
        ("COS",           0x6F90,  256),   // angle byte -> signed cos
        ("SIN",           0x7090,  256),   // angle byte -> signed sin
        ("SPRITE_GEOM",   0x7190,  922),   // (id&0x1FFF)<<1 -> width byte; +1 -> height byte
        ("SPEED_CURVE",   0x78C6,  594),   // distance-from-target -> vertical scroll speed
        ("SCORE_TABLE",   0xA353,   34),   // collectible id -> score value (words LE)
        ("SCORE_SPR_LUT", 0xA375,  112),   // collectible index -> score-spawn-effect lut byte
        ("HURT_SFX",      0xA3E5,   18),   // hit count -> escalating hurt-effect id (words LE)
        ("ANIM_FRAME",    0xA86F, 2344),   // per-entity anim-frame descriptors
    };

    /// <summary>Raw bytes of the table <paramref name="name"/> read from <paramref name="image"/>.</summary>
    public static byte[] Raw(byte[] image, string name)
    {
        foreach (var (n, off, len) in Spans)
        {
            if (n != name)
                continue;
            int start = BootImage.DsBase + off;
            if (start + len > image.Length)
                throw new InvalidDataException($"{name}: span exceeds image");
            return image.AsSpan(start, len).ToArray();
        }
        throw new ArgumentException($"unknown table {name}", nameof(name));
    }

    /// <summary>Level -> per-level song index (106 bytes).</summary>
    public static byte[] SongIndex(byte[] image) => Raw(image, "SONG_INDEX");

    /// <summary>Angle -> signed cosine byte (256 entries).</summary>
    public static sbyte[] Cos(byte[] image) => Signed(Raw(image, "COS"));

    /// <summary>Angle -> signed sine byte (256 entries).</summary>
    public static sbyte[] Sin(byte[] image) => Signed(Raw(image, "SIN"));

    /// <summary>Sprite geometry: index (id&amp;0x1FFF)&lt;&lt;1 = width byte, +1 = height byte (922 bytes).</summary>
    public static byte[] SpriteGeom(byte[] image) => Raw(image, "SPRITE_GEOM");

    /// <summary>Distance-from-target -> vertical scroll speed (594 bytes).</summary>
    public static byte[] SpeedCurve(byte[] image) => Raw(image, "SPEED_CURVE");

    /// <summary>Collectible id -> score value, 17 little-endian words (34 bytes).</summary>
    public static ushort[] ScoreTable(byte[] image) => Words(Raw(image, "SCORE_TABLE"));

    /// <summary>Collectible index -> score-spawn-effect lookup byte (112 bytes).</summary>
    public static byte[] ScoreSprLut(byte[] image) => Raw(image, "SCORE_SPR_LUT");

    /// <summary>Escalating hurt-effect ids, 9 little-endian words (18 bytes).
    /// The original reads them at the raw hit count (overlapping words), so keep the byte form for that idiom.</summary>
    public static ushort[] HurtSfx(byte[] image) => Words(Raw(image, "HURT_SFX"));

    /// <summary>
    /// Overlapping-word read of the hurt-effect id: 2 bytes little-endian at DS:(0xA3E5 + hits) &amp; 0xFFFF,
    /// where <paramref name="hits"/> is the RAW hit count (NOT multiplied by 2), so word n starts at byte n.
    /// Source: Sources/pre2_port/pre2/views/tables.py Tables.hurt_sfx_word (l.159-162, HURT_SFX_TABLE l.98),
    /// consumed by Sources/pre2_port/pre2/recovered/player_interaction.py _hurt (l.112-114, even counts only).
    /// </summary>
    public static ushort HurtSfxWordAt(byte[] image, int hits)
    {
        const int HurtSfxTable = 0xA3E5;
        int off = (HurtSfxTable + hits) & 0xFFFF;
        int lo = image[BootImage.DsBase + off];
        int hi = image[BootImage.DsBase + ((off + 1) & 0xFFFF)];
        return (ushort)(lo | (hi << 8));
    }

    /// <summary>Per-entity anim-frame descriptor table (2344 bytes).</summary>
    public static byte[] AnimFrame(byte[] image) => Raw(image, "ANIM_FRAME");

    private static sbyte[] Signed(byte[] data)
    {
        var r = new sbyte[data.Length];
        for (int i = 0; i < data.Length; i++)
            r[i] = unchecked((sbyte)data[i]);
        return r;
    }

    private static ushort[] Words(byte[] data)
    {
        var r = new ushort[data.Length / 2];
        for (int i = 0; i < r.Length; i++)
            r[i] = (ushort)(data[2 * i] | (data[2 * i + 1] << 8));
        return r;
    }

    /// <summary>Serialise a table through its typed accessor back to bytes (used by verification).</summary>
    public static byte[] Typed(byte[] image, string name)
    {
        switch (name)
        {
            case "COS": return Array.ConvertAll(Cos(image), b => unchecked((byte)b));
            case "SIN": return Array.ConvertAll(Sin(image), b => unchecked((byte)b));
            case "SCORE_TABLE": return FromWords(ScoreTable(image));
            case "HURT_SFX": return FromWords(HurtSfx(image));
            default: return Raw(image, name);
        }
    }

    private static byte[] FromWords(ushort[] w)
    {
        var r = new byte[w.Length * 2];
        for (int i = 0; i < w.Length; i++)
        {
            r[2 * i] = (byte)(w[i] & 0xFF);
            r[2 * i + 1] = (byte)(w[i] >> 8);
        }
        return r;
    }
}
