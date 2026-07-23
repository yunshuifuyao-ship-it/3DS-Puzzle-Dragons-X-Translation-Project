namespace TimgTool;

/// <summary>
/// 像素格式元信息。
/// PICA200 格式 ID (v7) 来自 IDA sub_1A87F4 反编译验证。
/// </summary>
public enum PixelFormatKind
{
    Rgba8,
    Rgba4,
    Rgb565,
    Rgb8,
    La8,
    La4,
    L4,
    Etc1,
    Etc1_a4,
}

public sealed record PixelFormatInfo(
    PixelFormatKind Kind,
    string TfmName,           // 权威字符串名（与 nw4c_tfm 内容一致）
    int Pica200Id,            // PICA200 内部格式 ID（IDA sub_1A87F4 的 v7）
    int PixelFormatValue,     // header 0x54 偏移的 pixel_format 字段值（原版实测）
    int Bpp,                  // 每像素位数
    bool IsEtc1,              // 是否为 ETC1 块压缩格式
    bool IsBlockCompressed
);

public static class PixelFormats
{
    public static readonly PixelFormatInfo[] All =
    {
        new(PixelFormatKind.Rgba8,   "Rgba8",   1,  3,    32, false, false),
        new(PixelFormatKind.Rgba4,   "Rgba4",   4,  2,    16, false, false),
        new(PixelFormatKind.Rgb565,  "Rgb565",  3,  0,    16, false, false),
        new(PixelFormatKind.Rgb8,    "Rgb8",    0,  1,    24, false, false),
        new(PixelFormatKind.La8,     "La8",     5,  0x10, 16, false, false),
        new(PixelFormatKind.La4,     "La4",     9,  0x0F, 8,  false, false),
        new(PixelFormatKind.L4,      "L4",      10, 0x0D, 4,  false, false),
        new(PixelFormatKind.Etc1,    "Etc1",    12, 0x11, 4,  true,  true),
        new(PixelFormatKind.Etc1_a4, "Etc1_a4", 13, 0x12, 8,  true,  true),
    };

    public static PixelFormatInfo? ByTfmName(string name)
    {
        foreach (var f in All)
            if (f.TfmName == name) return f;
        return null;
    }

    public static PixelFormatInfo ByKind(PixelFormatKind kind)
    {
        foreach (var f in All)
            if (f.Kind == kind) return f;
        throw new ArgumentOutOfRangeException(nameof(kind));
    }
}
