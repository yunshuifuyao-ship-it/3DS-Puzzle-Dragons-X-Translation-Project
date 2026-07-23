namespace TimgTool;

public readonly struct RgbaColor(byte r, byte g, byte b, byte a)
{
    public readonly byte R = r;
    public readonly byte G = g;
    public readonly byte B = b;
    public readonly byte A = a;
}

/// <summary>
/// 像素 descriptor 接口：解码（值→RGBA8888）与编码（RGBA8888→值）。
/// 参考Kuriimu2 IPixelDescriptor。
/// </summary>
public interface IPixelDescriptor
{
    int BitDepth { get; }
    RgbaColor GetColor(long value);
    long GetValue(RgbaColor color);
}

/// <summary>
/// RGBA 系列 descriptor（可配置 r/g/b/a 位深与 componentOrder）。
/// 严格参考 Kuriimu2 RgbaPixelDescriptor.cs。
/// componentOrder 从右到左对应从低位到高位：
///   "RGBA" → A 在最低位、R 在最高位
///   "RGB"  → B 在最低位、R 在最高位（alpha depth=0）
/// </summary>
public sealed class RgbaDescriptor : IPixelDescriptor
{
    private readonly int _rDepth, _gDepth, _bDepth, _aDepth;
    private readonly string _order;
    private readonly int _bitDepth;

    public int BitDepth => _bitDepth;

    public RgbaDescriptor(int r, int g, int b, int a, string componentOrder)
    {
        _rDepth = r; _gDepth = g; _bDepth = b; _aDepth = a;
        _order = componentOrder;
        _bitDepth = r + g + b + a;
    }

    public RgbaColor GetColor(long value)
    {
        int r = 0, g = 0, b = 0, a = 0;
        int shift = 0;
        // 从右到左读取（最低位先读）
        for (int i = _order.Length - 1; i >= 0; i--)
        {
            int depth = _order[i] switch
            {
                'R' or 'r' => _rDepth,
                'G' or 'g' => _gDepth,
                'B' or 'b' => _bDepth,
                'A' or 'a' => _aDepth,
                _ => 0,
            };
            int mask = (1 << depth) - 1;
            int raw = (int)((value >> shift) & mask);
            int upscaled = depth == 0 ? 0 : BitDepthConverter.Upscale(raw, depth);
            switch (_order[i])
            {
                case 'R' or 'r': r = upscaled; break;
                case 'G' or 'g': g = upscaled; break;
                case 'B' or 'b': b = upscaled; break;
                case 'A' or 'a': a = upscaled; break;
            }
            shift += depth;
        }
        // alpha depth=0 → 强制不透明
        if (_aDepth == 0) a = 255;
        return new RgbaColor((byte)r, (byte)g, (byte)b, (byte)a);
    }

    public long GetValue(RgbaColor color)
    {
        long result = 0;
        int shift = 0;
        for (int i = _order.Length - 1; i >= 0; i--)
        {
            int depth = _order[i] switch
            {
                'R' or 'r' => _rDepth,
                'G' or 'g' => _gDepth,
                'B' or 'b' => _bDepth,
                'A' or 'a' => _aDepth,
                _ => 0,
            };
            if (depth > 0)
            {
                int raw = _order[i] switch
                {
                    'R' or 'r' => BitDepthConverter.Downscale(color.R, depth),
                    'G' or 'g' => BitDepthConverter.Downscale(color.G, depth),
                    'B' or 'b' => BitDepthConverter.Downscale(color.B, depth),
                    'A' or 'a' => BitDepthConverter.Downscale(color.A, depth),
                    _ => 0,
                };
                int mask = (1 << depth) - 1;
                result |= (long)(raw & mask) << shift;
            }
            shift += depth;
        }
        return result;
    }
}

/// <summary>
/// LA 系列 descriptor（可配置 l/a 位深与 componentOrder）。
/// 严格参考 Kuriimu2 LaPixelDescriptor.cs。
/// Luminance 用 NTSC 加权（0.299R + 0.587G + 0.114B）而非 (R+G+B)/3。
/// </summary>
public sealed class LaDescriptor : IPixelDescriptor
{
    private readonly int _lDepth, _aDepth;
    private readonly string _order;
    private readonly int _bitDepth;

    public int BitDepth => _bitDepth;

    public LaDescriptor(int l, int a, string componentOrder)
    {
        _lDepth = l; _aDepth = a;
        _order = componentOrder;
        _bitDepth = l + a;
    }

    public RgbaColor GetColor(long value)
    {
        int l = 0, a = 0;
        int shift = 0;
        for (int i = _order.Length - 1; i >= 0; i--)
        {
            int depth = _order[i] switch
            {
                'L' or 'l' => _lDepth,
                'A' or 'a' => _aDepth,
                _ => 0,
            };
            int mask = (1 << depth) - 1;
            int raw = (int)((value >> shift) & mask);
            int upscaled = depth == 0 ? 0 : BitDepthConverter.Upscale(raw, depth);
            switch (_order[i])
            {
                case 'L' or 'l': l = upscaled; break;
                case 'A' or 'a': a = upscaled; break;
            }
            shift += depth;
        }
        // luminance depth=0 → 白色
        if (_lDepth == 0) l = 255;
        // alpha depth=0 → 不透明
        if (_aDepth == 0) a = 255;
        return new RgbaColor((byte)l, (byte)l, (byte)l, (byte)a);
    }

    public long GetValue(RgbaColor color)
    {
        int l = BitDepthConverter.NtscLuminance(color.R, color.G, color.B);
        int a = color.A;
        long result = 0;
        int shift = 0;
        for (int i = _order.Length - 1; i >= 0; i--)
        {
            int depth = _order[i] switch
            {
                'L' or 'l' => _lDepth,
                'A' or 'a' => _aDepth,
                _ => 0,
            };
            if (depth > 0)
            {
                int raw = _order[i] switch
                {
                    'L' or 'l' => BitDepthConverter.Downscale(l, depth),
                    'A' or 'a' => BitDepthConverter.Downscale(a, depth),
                    _ => 0,
                };
                int mask = (1 << depth) - 1;
                result |= (long)(raw & mask) << shift;
            }
            shift += depth;
        }
        return result;
    }
}
