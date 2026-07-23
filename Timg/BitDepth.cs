namespace TimgTool;

/// <summary>
/// 位深转换，严格遵循 Kuriimu2 Conversion.cs。
/// </summary>
public static class BitDepthConverter
{
    /// <summary>
    /// N 位深 → 8 位深（解码用，无损放大）。
    /// value==fromMaxRange 时直接映射到 255，避免 (15<<8)/15 = 18 的错误。
    /// </summary>
    public static int Upscale(int value, int fromBitDepth)
    {
        int fromMaxRange = (1 << fromBitDepth) - 1;
        return value == fromMaxRange ? 255 : (value << 8) / fromMaxRange;
    }

    /// <summary>
    /// 8 位深 → N 位深（编码用，有损缩小）。
    /// </summary>
    public static int Downscale(int value, int toBitDepth)
    {
        return value >> (8 - toBitDepth);
    }

    /// <summary>
    /// 计算 NTSC 加权亮度（与 .NET Color.GetBrightness 一致），返回 0..255。
    /// 公式：0.299*R + 0.587*G + 0.114*B
    /// </summary>
    public static int NtscLuminance(byte r, byte g, byte b)
    {
        return (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
    }
}
