using System.IO.Compression;

namespace TimgTool;

/// <summary>
/// 轻量 PNG 读写器（RGBA8888），不依赖外部 NuGet 包。
/// 仅支持色彩类型 6（RGBA8888）和 2（RGB888，自动补 alpha=255）。
/// zlib 用 System.IO.Compression.DeflateStream（AOT 兼容）。
/// </summary>
public static class PngCodec
{
    public sealed class PngImage
    {
        public int Width;
        public int Height;
        public byte[] RgbaData = Array.Empty<byte>(); // Width*Height*4
    }

    public static void Write(string path, int width, int height, byte[] rgbaData)
    {
        using var fs = File.Create(path);
        Write(fs, width, height, rgbaData);
    }

    public static void Write(Stream stream, int width, int height, byte[] rgbaData)
    {
        // PNG signature
        stream.WriteByte(0x89);
        stream.WriteByte(0x50);
        stream.WriteByte(0x4E);
        stream.WriteByte(0x47);
        stream.WriteByte(0x0D);
        stream.WriteByte(0x0A);
        stream.WriteByte(0x1A);
        stream.WriteByte(0x0A);

        // IHDR (PNG 规范：宽度/高度为大端)
        var ihdr = new byte[13];
        BigEndianWrite(ihdr, 0, (uint)width);
        BigEndianWrite(ihdr, 4, (uint)height);
        ihdr[8] = 8;       // bit depth
        ihdr[9] = 6;       // color type: RGBA
        ihdr[10] = 0;      // compression
        ihdr[11] = 0;      // filter
        ihdr[12] = 0;      // interlace
        WriteChunk(stream, "IHDR", ihdr);

        // IDAT：每行前加 filter byte (0=None)
        int rowBytes = width * 4;
        var raw = new byte[(rowBytes + 1) * height];
        for (int y = 0; y < height; y++)
        {
            raw[y * (rowBytes + 1)] = 0; // filter None
            Buffer.BlockCopy(rgbaData, y * rowBytes, raw, y * (rowBytes + 1) + 1, rowBytes);
        }

        byte[] compressed;
        using (var cms = new MemoryStream())
        {
            // zlib header
            cms.WriteByte(0x78);
            cms.WriteByte(0x9C);
            using (var ds = new DeflateStream(cms, CompressionLevel.Optimal, leaveOpen: true))
            {
                ds.Write(raw, 0, raw.Length);
            }
            // adler32
            uint adler = Adler32(rgbaData, 0, rgbaData.Length);
            // 注：标准要求对 raw 数据计算 Adler32（含 filter bytes），不是原 rgbaData
            adler = Adler32(raw, 0, raw.Length);
            cms.WriteByte((byte)((adler >> 24) & 0xFF));
            cms.WriteByte((byte)((adler >> 16) & 0xFF));
            cms.WriteByte((byte)((adler >> 8) & 0xFF));
            cms.WriteByte((byte)(adler & 0xFF));
            compressed = cms.ToArray();
        }
        WriteChunk(stream, "IDAT", compressed);

        // IEND
        WriteChunk(stream, "IEND", Array.Empty<byte>());
    }

    public static PngImage Read(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    public static PngImage Read(Stream stream)
    {
        // signature
        var sig = new byte[8];
        if (stream.Read(sig, 0, 8) != 8) throw new InvalidDataException("PNG too short");
        if (sig[0] != 0x89 || sig[1] != 0x50 || sig[2] != 0x4E || sig[3] != 0x47)
            throw new InvalidDataException("Not a PNG");

        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        var idatBytes = new List<byte>();
        bool sawIEND = false;

        while (!sawIEND)
        {
            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4) break;
            uint len = BigEndianUInt32(lenBuf);
            var type = new byte[4];
            stream.Read(type, 0, 4);
            var data = new byte[len];
            if (len > 0) stream.Read(data, 0, (int)len);
            var crc = new byte[4];
            stream.Read(crc, 0, 4);

            string typeStr = System.Text.Encoding.ASCII.GetString(type);
            switch (typeStr)
            {
                case "IHDR":
                    width = (int)BigEndianUInt32(data, 0);
                    height = (int)BigEndianUInt32(data, 4);
                    bitDepth = data[8];
                    colorType = data[9];
                    break;
                case "IDAT":
                    idatBytes.AddRange(data);
                    break;
                case "IEND":
                    sawIEND = true;
                    break;
            }
        }

        if (bitDepth != 8)
            throw new NotSupportedException($"PNG bit depth {bitDepth} not supported");
        int channels = colorType switch
        {
            2 => 3,    // RGB
            6 => 4,    // RGBA
            0 => 1,    // Grayscale
            4 => 2,    // GA
            _ => throw new NotSupportedException($"PNG color type {colorType} not supported"),
        };

        // zlib 解压
        var compressed = idatBytes.ToArray();
        if (compressed.Length < 6) throw new InvalidDataException("IDAT too short");
        // 跳过 zlib header (2 bytes)
        var raw = new byte[(width * channels + 1) * height];
        using (var dms = new MemoryStream(compressed, 2, compressed.Length - 6))
        using (var ds = new DeflateStream(dms, CompressionMode.Decompress))
        {
            int totalRead = 0;
            while (totalRead < raw.Length)
            {
                int n = ds.Read(raw, totalRead, raw.Length - totalRead);
                if (n == 0) break;
                totalRead += n;
            }
        }

        // 反过滤：仅支持 filter 0 (None)；其他 filter 简化处理（按 None）
        // 注：本工具写入的 PNG 一律 None；读取第三方 PNG 时若遇到 filter≠0 可能失真
        var rgba = new byte[width * height * 4];
        int rowBytes = width * channels;
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (rowBytes + 1);
            byte filter = raw[rowStart];
            for (int x = 0; x < width; x++)
            {
                int srcIdx = rowStart + 1 + x * channels;
                int dstIdx = (y * width + x) * 4;
                if (filter != 0)
                {
                    // 简化处理：对 Sub/Up/Avg/Paeth 做最小实现
                    // 这里为兼容性实现 Sub(1)/Up(2)/Average(3)/Paeth(4)
                    // 大多数 PNG 由本工具写出（filter=0），此处仅作保险
                }
                switch (channels)
                {
                    case 4: // RGBA
                        rgba[dstIdx + 0] = raw[srcIdx + 0];
                        rgba[dstIdx + 1] = raw[srcIdx + 1];
                        rgba[dstIdx + 2] = raw[srcIdx + 2];
                        rgba[dstIdx + 3] = raw[srcIdx + 3];
                        break;
                    case 3: // RGB → alpha=255
                        rgba[dstIdx + 0] = raw[srcIdx + 0];
                        rgba[dstIdx + 1] = raw[srcIdx + 1];
                        rgba[dstIdx + 2] = raw[srcIdx + 2];
                        rgba[dstIdx + 3] = 255;
                        break;
                    case 1: // Grayscale → RGBA = (L, L, L, 255)
                        rgba[dstIdx + 0] = raw[srcIdx];
                        rgba[dstIdx + 1] = raw[srcIdx];
                        rgba[dstIdx + 2] = raw[srcIdx];
                        rgba[dstIdx + 3] = 255;
                        break;
                    case 2: // GA
                        rgba[dstIdx + 0] = raw[srcIdx];
                        rgba[dstIdx + 1] = raw[srcIdx];
                        rgba[dstIdx + 2] = raw[srcIdx];
                        rgba[dstIdx + 3] = raw[srcIdx + 1];
                        break;
                }
            }
            // 应用过滤器（PNG 解码要求逆推）
            if (filter != 0)
            {
                ApplyFilter(filter, raw, rowStart + 1, rowBytes, channels, y, width);
                // 重新读取
                for (int x = 0; x < width; x++)
                {
                    int srcIdx = rowStart + 1 + x * channels;
                    int dstIdx = (y * width + x) * 4;
                    switch (channels)
                    {
                        case 4:
                            rgba[dstIdx + 0] = raw[srcIdx + 0];
                            rgba[dstIdx + 1] = raw[srcIdx + 1];
                            rgba[dstIdx + 2] = raw[srcIdx + 2];
                            rgba[dstIdx + 3] = raw[srcIdx + 3];
                            break;
                        case 3:
                            rgba[dstIdx + 0] = raw[srcIdx + 0];
                            rgba[dstIdx + 1] = raw[srcIdx + 1];
                            rgba[dstIdx + 2] = raw[srcIdx + 2];
                            rgba[dstIdx + 3] = 255;
                            break;
                        case 1:
                            rgba[dstIdx + 0] = raw[srcIdx];
                            rgba[dstIdx + 1] = raw[srcIdx];
                            rgba[dstIdx + 2] = raw[srcIdx];
                            rgba[dstIdx + 3] = 255;
                            break;
                        case 2:
                            rgba[dstIdx + 0] = raw[srcIdx];
                            rgba[dstIdx + 1] = raw[srcIdx];
                            rgba[dstIdx + 2] = raw[srcIdx];
                            rgba[dstIdx + 3] = raw[srcIdx + 1];
                            break;
                    }
                }
            }
        }

        return new PngImage { Width = width, Height = height, RgbaData = rgba };
    }

    private static void ApplyFilter(byte filter, byte[] raw, int rowStart, int rowBytes, int channels, int y, int width)
    {
        int bpp = channels;
        int prevRowStart = y > 0 ? (y - 1) * (width * channels + 1) + 1 : -1;
        switch (filter)
        {
            case 0: break; // None
            case 1: // Sub
                for (int i = bpp; i < rowBytes; i++)
                    raw[rowStart + i] += raw[rowStart + i - bpp];
                break;
            case 2: // Up
                if (prevRowStart >= 0)
                    for (int i = 0; i < rowBytes; i++)
                        raw[rowStart + i] += raw[prevRowStart + i];
                break;
            case 3: // Average
                for (int i = 0; i < rowBytes; i++)
                {
                    int left = i >= bpp ? raw[rowStart + i - bpp] : 0;
                    int up = prevRowStart >= 0 ? raw[prevRowStart + i] : 0;
                    raw[rowStart + i] += (byte)((left + up) >> 1);
                }
                break;
            case 4: // Paeth
                for (int i = 0; i < rowBytes; i++)
                {
                    int left = i >= bpp ? raw[rowStart + i - bpp] : 0;
                    int up = prevRowStart >= 0 ? raw[prevRowStart + i] : 0;
                    int upLeft = (i >= bpp && prevRowStart >= 0) ? raw[prevRowStart + i - bpp] : 0;
                    int p = left + up - upLeft;
                    int pa = Math.Abs(p - left);
                    int pb = Math.Abs(p - up);
                    int pc = Math.Abs(p - upLeft);
                    int pred = (pa <= pb && pa <= pc) ? left : (pb <= pc ? up : upLeft);
                    raw[rowStart + i] += (byte)pred;
                }
                break;
        }
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        byte[] lenBuf = new byte[4];
        BigEndianWrite(lenBuf, 0, (uint)data.Length);
        s.Write(lenBuf, 0, 4);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);

        // CRC32 over type + data
        uint crc = Crc32(typeBytes, data);
        byte[] crcBuf = new byte[4];
        BigEndianWrite(crcBuf, 0, crc);
        s.Write(crcBuf, 0, 4);
    }

    private static uint BigEndianUInt32(byte[] b, int off = 0)
    {
        return (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);
    }

    private static void BigEndianWrite(byte[] b, int off, uint v)
    {
        b[off] = (byte)((v >> 24) & 0xFF);
        b[off + 1] = (byte)((v >> 16) & 0xFF);
        b[off + 2] = (byte)((v >> 8) & 0xFF);
        b[off + 3] = (byte)(v & 0xFF);
    }

    private static uint[]? _crcTable;
    private static uint[] CrcTable
    {
        get
        {
            if (_crcTable != null) return _crcTable;
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            _crcTable = t;
            return t;
        }
    }

    private static uint Crc32(byte[] typeBytes, byte[] data)
    {
        uint c = 0xFFFFFFFF;
        var t = CrcTable;
        foreach (var b in typeBytes) c = t[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = t[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    private static uint Adler32(byte[] data, int off, int len)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < len; i++)
        {
            a = (a + data[off + i]) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
