namespace TimgTool;

/// <summary>
/// ETC1 8 字节块结构，严格参考 Kuriimu2 Block.cs。
/// 字段：Lsb(u16) + Msb(u16) + Flags(u8) + B(u8) + G(u8) + R(u8)
/// </summary>
public struct Etc1Block
{
    public ushort Lsb;
    public ushort Msb;
    public byte Flags;
    public byte B, G, R;

    public bool FlipBit
    {
        readonly get => (Flags & 1) == 1;
        set => Flags = (byte)((Flags & ~1) | (value ? 1 : 0));
    }

    public bool DiffBit
    {
        readonly get => (Flags & 2) == 2;
        set => Flags = (byte)((Flags & ~2) | (value ? 2 : 0));
    }

    public readonly int ColorDepth => DiffBit ? 32 : 16;

    public int Table0
    {
        readonly get => (Flags >> 5) & 7;
        set => Flags = (byte)((Flags & ~(7 << 5)) | (value << 5));
    }

    public int Table1
    {
        readonly get => (Flags >> 2) & 7;
        set => Flags = (byte)((Flags & ~(7 << 2)) | (value << 2));
    }

    public readonly (int R, int G, int B) Color0 =>
        (R * ColorDepth / 256, G * ColorDepth / 256, B * ColorDepth / 256);

    public readonly (int R, int G, int B) Color1
    {
        get
        {
            if (!DiffBit) return (R % 16, G % 16, B % 16);
            var c0 = Color0;
            int rd = Sign3(R % 8), gd = Sign3(G % 8), bd = Sign3(B % 8);
            return (Math.Max(0, Math.Min(31, c0.R + rd)),
                    Math.Max(0, Math.Min(31, c0.G + gd)),
                    Math.Max(0, Math.Min(31, c0.B + bd)));
        }
    }

    /// <summary>
    /// 像素选择值（2bit）：(Msb>>i)%2 * 2 + (Lsb>>i)%2
    /// </summary>
    public readonly int this[int i] => (Msb >> i) % 2 * 2 + (Lsb >> i) % 2;

    public ulong ToUInt64()
    {
        ulong v = 0;
        v |= Lsb;
        v |= (ulong)Msb << 16;
        v |= (ulong)Flags << 32;
        v |= (ulong)B << 40;
        v |= (ulong)G << 48;
        v |= (ulong)R << 56;
        return v;
    }

    public static Etc1Block FromUInt64(ulong v) => new()
    {
        Lsb = (ushort)(v & 0xFFFF),
        Msb = (ushort)((v >> 16) & 0xFFFF),
        Flags = (byte)((v >> 32) & 0xFF),
        B = (byte)((v >> 40) & 0xFF),
        G = (byte)((v >> 48) & 0xFF),
        R = (byte)((v >> 56) & 0xFF),
    };

    private static int Sign3(int n) => (n + 4) % 8 - 4;
}

/// <summary>
/// ETC1 编解码，严格参考 Kuriimu2 Etc1Transcoder.cs 与 Constants.cs。
/// ZOrder 用于 ETC1A4，NormalOrder 用于 ETC1。
/// </summary>
public static class Etc1Codec
{
    public static readonly int[] ZOrder = [0, 4, 1, 5, 8, 12, 9, 13, 2, 6, 3, 7, 10, 14, 11, 15];
    public static readonly int[] NormalOrder = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];

    public static readonly int[][] Modifiers =
    [
        [2,   8,  -2,   -8],
        [5,  17,  -5,  -17],
        [9,  29,  -9,  -29],
        [13,  42, -13,  -42],
        [18,  60, -18,  -60],
        [24,  80, -24,  -80],
        [33, 106, -33, -106],
        [47, 183, -47, -183],
    ];

    private static int Clamp(int n) => Math.Max(0, Math.Min(n, 255));

    /// <summary>
    /// 把 packed 值（4 位或 5 位）扩展到 8 位，严格对应 Kuriimu2 Rgb.Scale(int limit)。
    /// limit=16（DiffBit=0，individual）：v * 17（4 位 → 8 位）
    /// limit=32（DiffBit=1，differential）：(v &lt;&lt; 3) | (v &gt;&gt; 2)（5 位 → 8 位）
    /// </summary>
    public static int Scale(int value, int colorDepth) => colorDepth == 16
        ? value * 17
        : (value << 3) | (value >> 2);

    /// <summary>
    /// 解码 4×4 块（16 像素），返回 16 个 RGBA 像素。
    /// useZOrder=true 用 ZOrder（ETC1A4），false 用 NormalOrder（ETC1）。
    /// alpha 为该块的 16 个 4-bit alpha（ETC1A4）；ETC1 传 null 表示全部不透明。
    /// </summary>
    public static RgbaColor[] DecodeBlock(Etc1Block block, int[]? alpha, bool useZOrder)
    {
        var order = useZOrder ? ZOrder : NormalOrder;
        var result = new RgbaColor[16];
        // Color0/Color1 返回 packed 值，必须 Scale 到 8 位才能与 modifier 相加
        // 严格对应 Kuriimu2 Etc1Transcoder.DecodeBlocks: basec0 = data.Block.Color0.Scale(data.Block.ColorDepth)
        int cd = block.ColorDepth;
        var (pr0, pg0, pb0) = block.Color0;
        var (pr1, pg1, pb1) = block.Color1;
        int r0 = Scale(pr0, cd), g0 = Scale(pg0, cd), b0 = Scale(pb0, cd);
        int r1 = Scale(pr1, cd), g1 = Scale(pg1, cd), b1 = Scale(pb1, cd);
        int flipbitmask = block.FlipBit ? 2 : 8;
        for (int i = 0; i < 16; i++)
        {
            int pi = order[i];
            bool useC0 = (pi & flipbitmask) == 0;
            int table = useC0 ? block.Table0 : block.Table1;
            int baseR = useC0 ? r0 : r1;
            int baseG = useC0 ? g0 : g1;
            int baseB = useC0 ? b0 : b1;
            int mod = Modifiers[table][block[pi]];
            int rr = Clamp(baseR + mod);
            int gg = Clamp(baseG + mod);
            int bb = Clamp(baseB + mod);
            int aa = alpha != null ? alpha[pi] : 255;
            result[i] = new RgbaColor((byte)rr, (byte)gg, (byte)bb, (byte)aa);
        }
        return result;
    }

    /// <summary>
    /// 解码 ETC1A4 alpha：8 字节 = 16 个 4-bit alpha，每个 alpha 经 *17 放大到 8 位。
    /// </summary>
    public static int[] DecodeAlpha(ulong alphaData)
    {
        var alphas = new int[16];
        for (int i = 0; i < 16; i++)
        {
            int a4 = (int)((alphaData >> (4 * i)) & 0xF);
            alphas[i] = a4 * 17;
        }
        return alphas;
    }

    /// <summary>
    /// 编码 4×4 块的 alpha（16 个 4-bit）为 8 字节 ulong。
    /// </summary>
    public static ulong EncodeAlpha(int[] alphas)
    {
        ulong v = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = alphas[i] >> 4; // 8→4 位
            v |= (ulong)(a & 0xF) << (4 * i);
        }
        return v;
    }

    /// <summary>
    /// 简易 ETC1 颜色编码：对 16 个像素搜索最佳 block。
    /// 策略：使用 individual 模式（DiffBit=0），Table0=Table1=0，颜色用块平均。
    /// 注：ETC1 为有损格式，此简化编码保证可解码、视觉接近，不保证与 Kuriimu2 编码器字节一致。
    /// </summary>
    public static Etc1Block EncodeBlock(RgbaColor[] pixels, bool useZOrder)
    {
        // 取整体平均作为 base color
        int sumR = 0, sumG = 0, sumB = 0;
        for (int i = 0; i < 16; i++)
        {
            sumR += pixels[i].R;
            sumG += pixels[i].G;
            sumB += pixels[i].B;
        }
        int avgR = sumR / 16, avgG = sumG / 16, avgB = sumB / 16;

        // individual 模式（DiffBit=0）：R 字段 8 位 = 高 4 位 color0 packed | 低 4 位 color1 packed
        // Color0 = Color1 = avg 的 4 位 packed 值，R = (packed << 4) | packed
        int packedR = avgR >> 4, packedG = avgG >> 4, packedB = avgB >> 4;
        var block = new Etc1Block
        {
            R = (byte)((packedR << 4) | packedR),
            G = (byte)((packedG << 4) | packedG),
            B = (byte)((packedB << 4) | packedB),
            Flags = 0, // DiffBit=0, FlipBit=0
            Table0 = 0,
            Table1 = 0,
        };
        // 编码时 base color 用 Scale 后的 8 位值（与解码端一致）
        int baseR = Scale(packedR, 16);
        int baseG = Scale(packedG, 16);
        int baseB = Scale(packedB, 16);
        // 选最小误差的 modifier（对每个像素比较 4 个 modifier，选使误差最小的 selector）
        // pixels[i] = 解码端 result[i] = 像素在 canonical position ZOrder[i] 的颜色
        // 编码时对 pixels[i] 搜索最佳 modifier，将 selector 写入 bit position pi=ZOrder[i]
        var order = useZOrder ? ZOrder : NormalOrder;
        ushort lsb = 0, msb = 0;
        for (int i = 0; i < 16; i++)
        {
            int pi = order[i];
            int bestSel = 0;
            int bestErr = int.MaxValue;
            for (int s = 0; s < 4; s++)
            {
                int mod = Modifiers[0][s];
                int dr = Clamp(baseR + mod) - pixels[i].R;
                int dg = Clamp(baseG + mod) - pixels[i].G;
                int db = Clamp(baseB + mod) - pixels[i].B;
                int err = dr * dr + dg * dg + db * db;
                if (err < bestErr) { bestErr = err; bestSel = s; }
            }
            // selector 2-bit：lsb=bit0, msb=bit1
            if ((bestSel & 1) != 0) lsb |= (ushort)(1 << pi);
            if ((bestSel & 2) != 0) msb |= (ushort)(1 << pi);
        }
        block.Lsb = lsb;
        block.Msb = msb;
        return block;
    }
}
