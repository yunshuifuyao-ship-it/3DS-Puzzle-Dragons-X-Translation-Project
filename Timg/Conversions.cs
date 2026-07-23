using System.Text;

namespace TimgTool;

/// <summary>
/// TIMG → PNG 转换。
/// </summary>
public static class TimgToPng
{
    /// <summary>
    /// 将 .timg 文件解码为 PNG。返回值为输出 PNG 路径。
    /// </summary>
    public static string Convert(string timgPath, string? outputPngPath = null)
    {
        var data = File.ReadAllBytes(timgPath);
        var timg = TimgFile.Parse(data);

        // 格式判定：优先 nw4c_tfm
        var tfmName = timg.GetTfmName() ?? throw new InvalidDataException($"No nw4c_tfm chunk in {timgPath}");
        var fmt = PixelFormats.ByTfmName(tfmName) ?? throw new NotSupportedException($"Unknown Tfm format: {tfmName}");

        var txd = timg.FindChunk("nw4c_txd") ?? throw new InvalidDataException($"No nw4c_txd chunk in {timgPath}");
        int paddedW = timg.Header.Width;
        int paddedH = timg.Header.Height;

        // 解码像素 → 线性 RGBA8888（padded 尺寸）
        var linearRgba = PixelCodec.Decode(fmt, txd.Data, paddedW, paddedH);

        // 裁剪到 crop 尺寸
        int cropW = timg.Header.CropWidth > 0 ? timg.Header.CropWidth : paddedW;
        int cropH = timg.Header.CropHeight > 0 ? timg.Header.CropHeight : paddedH;
        var cropped = CropRgba(linearRgba, paddedW, paddedH, cropW, cropH);

        // 输出文件名：追加 _<TFM> 后缀
        var inPath = timgPath;
        var dir = Path.GetDirectoryName(inPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(inPath);
        var outName = $"{baseName}_{fmt.TfmName}.png";
        var outPath = outputPngPath ?? Path.Combine(dir, outName);

        PngCodec.Write(outPath, cropW, cropH, cropped);
        return outPath;
    }

    private static byte[] CropRgba(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        if (dstW == srcW && dstH == srcH) return src;
        var dst = new byte[dstW * dstH * 4];
        for (int y = 0; y < dstH; y++)
        {
            int srcOff = y * srcW * 4;
            int dstOff = y * dstW * 4;
            int copy = dstW * 4;
            Buffer.BlockCopy(src, srcOff, dst, dstOff, copy);
        }
        return dst;
    }
}

/// <summary>
/// PNG → TIMG 转换。
/// </summary>
public static class PngToTimg
{
    public sealed class Options
    {
        public string? Format; // 强制格式（--format）
        public string? OutputTimgPath;
    }

    /// <summary>
    /// 将 .png 文件编码为 TIMG。返回值为输出 TIMG 路径。
    /// </summary>
    public static string Convert(string pngPath, Options? options = null)
    {
        options ??= new Options();
        var img = PngCodec.Read(pngPath);

        // 格式判定优先级：① --format ② 文件名后缀 ③ 自动分析
        var fmt = ResolveFormat(options.Format, pngPath, img);

        // pad 到 2 的幂
        int paddedW = NextPow2(img.Width);
        int paddedH = NextPow2(img.Height);
        var padded = PadRgba(img.RgbaData, img.Width, img.Height, paddedW, paddedH);

        // 编码像素 → txd 数据
        var txdData = PixelCodec.Encode(fmt, padded, paddedW, paddedH);

        // 构造 TIMG
        var timg = BuildTimg(fmt, txdData, paddedW, paddedH, img.Width, img.Height, pngPath);

        // 输出文件名：去除 _<TFM> 后缀
        var inPath = pngPath;
        var dir = Path.GetDirectoryName(inPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(inPath);
        // 移除 _<TFM> 后缀
        baseName = StripTfmSuffix(baseName);
        var outName = $"{baseName}.timg";
        var outPath = options.OutputTimgPath ?? Path.Combine(dir, outName);

        File.WriteAllBytes(outPath, timg.ToBytes());
        return outPath;
    }

    private static PixelFormatInfo ResolveFormat(string? forcedFmt, string pngPath, PngCodec.PngImage img)
    {
        // ① --format
        if (!string.IsNullOrEmpty(forcedFmt))
        {
            var f = PixelFormats.ByTfmName(forcedFmt);
            if (f == null)
                throw new NotSupportedException($"Unknown --format: {forcedFmt}");
            return f;
        }

        // ② 文件名 _<TFM> 后缀
        var baseName = Path.GetFileNameWithoutExtension(pngPath);
        var suffixFmt = TryParseTfmSuffix(baseName);
        if (suffixFmt != null) return suffixFmt;

        // ③ 自动分析
        bool hasAlpha = HasAlpha(img.RgbaData);
        bool isGray = IsGrayscale(img.RgbaData);
        if (hasAlpha && isGray) return PixelFormats.ByKind(PixelFormatKind.La4);
        return PixelFormats.ByKind(PixelFormatKind.Rgba8);
    }

    private static PixelFormatInfo? TryParseTfmSuffix(string baseName)
    {
        // 遍历所有格式名（含下划线的如 Etc1_a4），检查 baseName 是否以 _<格式名> 结尾
        foreach (var fmt in PixelFormats.All)
        {
            var suffix = "_" + fmt.TfmName;
            if (baseName.Length > suffix.Length && baseName.EndsWith(suffix, StringComparison.Ordinal))
                return fmt;
        }
        return null;
    }

    public static string StripTfmSuffix(string baseName)
    {
        // 遍历所有格式名（含下划线的如 Etc1_a4），检查 baseName 是否以 _<格式名> 结尾
        foreach (var fmt in PixelFormats.All)
        {
            var suffix = "_" + fmt.TfmName;
            if (baseName.Length > suffix.Length && baseName.EndsWith(suffix, StringComparison.Ordinal))
                return baseName.Substring(0, baseName.Length - suffix.Length);
        }
        return baseName;
    }

    private static bool HasAlpha(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 255) return true;
        return false;
    }

    private static bool IsGrayscale(byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] != rgba[i + 1] || rgba[i] != rgba[i + 2]) return false;
        }
        return true;
    }

    public static int NextPow2(int n)
    {
        if (n <= 0) return 1;
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    private static byte[] PadRgba(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW == dstW && srcH == dstH) return src;
        var dst = new byte[dstW * dstH * 4];
        // 初始化为透明黑
        for (int y = 0; y < srcH; y++)
        {
            int srcOff = y * srcW * 4;
            int dstOff = y * dstW * 4;
            int copy = srcW * 4;
            Buffer.BlockCopy(src, srcOff, dst, dstOff, copy);
        }
        return dst;
    }

    private static TimgFile BuildTimg(PixelFormatInfo fmt, byte[] txdData, int paddedW, int paddedH, int cropW, int cropH, string srcPngPath)
    {
        var header = new TimgHeader
        {
            Magic = 0x474D4954u, // "TIMG"
            Version = 1,
            // FileSize 由 TimgFile.ToBytes() 动态回填，此处不设置
            HeaderSize = 0x100u, // 实测所有样本均为 0x100（游戏不使用此字段，chunk 固定从 0xA0 开始）
            Hash1 = 0, // 0x50: NW4C 专有哈希（算法未知，游戏不检查，写 0）
            PixelFormat = (uint)fmt.PixelFormatValue, // 0x54: 原版实测值（区别于 Pica200Id）
            Hash2 = 0, // 0x58: NW4C 专有哈希（算法未知，游戏不检查，写 0）
            Width = (ushort)paddedW,
            Height = (ushort)paddedH,
            CropWidth = (ushort)cropW,
            CropHeight = (ushort)cropH,
        };
        // filename 字段：PNG 去除 _<TFM> 后缀后加 .timg 扩展名（与原版一致，最多 63 字节 ASCII）
        var pngBase = Path.GetFileNameWithoutExtension(srcPngPath);
        pngBase = StripTfmSuffix(pngBase);
        var nameBytes = Encoding.ASCII.GetBytes(pngBase + ".timg");
        int copyName = Math.Min(nameBytes.Length, 63);
        Array.Copy(nameBytes, 0, header.Filename, 0, copyName);

        // 构造 chunk 序列：tfm → txd → gnm → (gvr) → psh → end
        // nw4c_mps 可选（仅 97/10488 文件有），不写时游戏默认 v8=1 通过检查
        // 若写 mps，byte 必须是 ASCII '1' (0x31)，因为 IDA: v8 = byte - 0x30，需 v8==1
        var chunks = new List<Nw4cChunk>();
        chunks.Add(MakeChunk("nw4c_tfm", Encoding.ASCII.GetBytes(fmt.TfmName)));
        chunks.Add(MakeChunk("nw4c_txd", txdData));
        chunks.Add(MakeChunk("nw4c_gnm", MakeGnmData(fmt)));
        // nw4c_gvr：源工具版本号 ASCII 字符串（无 NUL 终止，5B）
        // 实测样本：L4/La4 无 gvr；其他格式有 gvr="1.1.0" 或 "1.1.4"
        // 游戏不依赖此字段，但为与原版一致，非 L4/La4 格式均写入
        if (fmt.Kind != PixelFormatKind.L4 && fmt.Kind != PixelFormatKind.La4)
        {
            chunks.Add(MakeChunk("nw4c_gvr", Encoding.ASCII.GetBytes("1.1.0")));
        }
        chunks.Add(MakeChunk("nw4c_psh", MakePshData(fmt, paddedW, paddedH)));
        chunks.Add(MakeChunk("nw4c_end", Array.Empty<byte>()));

        return new TimgFile { Header = header, Chunks = chunks };
    }

    private static Nw4cChunk MakeChunk(string sig, byte[] data)
    {
        return new Nw4cChunk { Signature = sig, Size = (uint)(12 + data.Length), Data = data };
    }

    private static byte[] MakeGnmData(PixelFormatInfo fmt)
    {
        // nw4c_gnm：源工具名 ASCII 字符串（无 NUL 终止）
        // 实测样本：L4/La4 用 "NW4C_TextureConverter"（21B），
        //          其他用 "NW4C_Tga for Photoshop X.Y.Z"（29B）
        // 版本号不重要，用固定值 "15.2.2"
        if (fmt.Kind == PixelFormatKind.L4 || fmt.Kind == PixelFormatKind.La4)
            return Encoding.ASCII.GetBytes("NW4C_TextureConverter");
        return Encoding.ASCII.GetBytes("NW4C_Tga for Photoshop 15.2.2");
    }

    private static byte[] MakePshData(PixelFormatInfo fmt, int w, int h)
    {
        // nw4c_psh：TLV 结构，每项 = 8B type + 4B size(LE) + value
        // 实测所有格式均含 "psh_pver" 项（size=15, value="1.0"）
        // Etc1/Etc1_a4 额外含 "psh_etco" 项（size=13, value=1B：Etc1=0x04, Etc1_a4=0x07）
        // Rgb565 额外含 "psh_xppl" 项（size=12, value=0B）
        using var ms = new MemoryStream();
        // psh_pver: 8 + 4 + 3 = 15
        ms.Write(Encoding.ASCII.GetBytes("psh_pver"), 0, 8);
        ms.Write(BitConverter.GetBytes(15u), 0, 4);
        ms.Write(Encoding.ASCII.GetBytes("1.0"), 0, 3);

        if (fmt.Kind == PixelFormatKind.Etc1 || fmt.Kind == PixelFormatKind.Etc1_a4)
        {
            // psh_etco: 8 + 4 + 1 = 13
            ms.Write(Encoding.ASCII.GetBytes("psh_etco"), 0, 8);
            ms.Write(BitConverter.GetBytes(13u), 0, 4);
            byte etcoVal = (fmt.Kind == PixelFormatKind.Etc1) ? (byte)4 : (byte)7;
            ms.WriteByte(etcoVal);
        }
        else if (fmt.Kind == PixelFormatKind.Rgb565)
        {
            // psh_xppl: 8 + 4 + 0 = 12
            ms.Write(Encoding.ASCII.GetBytes("psh_xppl"), 0, 8);
            ms.Write(BitConverter.GetBytes(12u), 0, 4);
        }
        return ms.ToArray();
    }
}
