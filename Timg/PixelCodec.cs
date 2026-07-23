using System.Buffers.Binary;

namespace TimgTool;

/// <summary>
/// 像素编解码器：处理 9 种 Tfm 格式的 TIMG ↔ RGBA8888 线性像素转换。
/// 非压缩格式走 IPixelDescriptor；ETC1/ETC1A4 走 Etc1Codec。
/// </summary>
public static class PixelCodec
{
    /// <summary>
    /// 获取每种的 descriptor（ETC1 返回 null）。
    /// </summary>
    public static IPixelDescriptor? GetDescriptor(PixelFormatInfo fmt)
    {
        return fmt.Kind switch
        {
            PixelFormatKind.Rgba8  => new RgbaDescriptor(8, 8, 8, 8, "RGBA"),
            PixelFormatKind.Rgba4  => new RgbaDescriptor(4, 4, 4, 4, "RGBA"),
            PixelFormatKind.Rgb565 => new RgbaDescriptor(5, 6, 5, 0, "RGB"),
            PixelFormatKind.Rgb8   => new RgbaDescriptor(8, 8, 8, 0, "RGB"),
            PixelFormatKind.La8    => new LaDescriptor(8, 8, "LA"),
            PixelFormatKind.La4    => new LaDescriptor(4, 4, "LA"),
            PixelFormatKind.L4     => new LaDescriptor(4, 0, "L"),
            _ => null,
        };
    }

    /// <summary>
    /// 解码 TIMG txd 数据为线性 RGBA8888 像素（paddedWidth × paddedHeight）。
    /// ETC1/ETC1A4 块压缩格式不走 TileMorton（块本身已 8×8 排列）。
    /// </summary>
    public static byte[] Decode(PixelFormatInfo fmt, byte[] txdData, int paddedWidth, int paddedHeight)
    {
        if (fmt.IsBlockCompressed)
        {
            return DecodeEtc1(fmt, txdData, paddedWidth, paddedHeight);
        }

        var desc = GetDescriptor(fmt) ?? throw new InvalidOperationException($"No descriptor for {fmt.TfmName}");
        int bytesPerPixel = (desc.BitDepth + 7) / 8;
        int pixelCount = paddedWidth * paddedHeight;

        // 1. 从 txdData 读取每个像素的 long 值（按 BitDepth 决定读取方式）
        long[] values = ReadPixelValues(txdData, desc.BitDepth, fmt.Kind, pixelCount);

        // 2. descriptor 解码为 RGBA8888
        var tiledRgba = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            var c = desc.GetColor(values[i]);
            tiledRgba[i * 4 + 0] = c.R;
            tiledRgba[i * 4 + 1] = c.G;
            tiledRgba[i * 4 + 2] = c.B;
            tiledRgba[i * 4 + 3] = c.A;
        }

        // 3. 8×8 Morton 瓦片重排
        return TileMorton.DecodeToLinear(tiledRgba, paddedWidth, paddedHeight, 4);
    }

    /// <summary>
    /// 编码线性 RGBA8888 像素（paddedWidth × paddedHeight）为 TIMG txd 数据。
    /// </summary>
    public static byte[] Encode(PixelFormatInfo fmt, byte[] linearRgba, int paddedWidth, int paddedHeight)
    {
        if (fmt.IsBlockCompressed)
        {
            return EncodeEtc1(fmt, linearRgba, paddedWidth, paddedHeight);
        }

        var desc = GetDescriptor(fmt) ?? throw new InvalidOperationException($"No descriptor for {fmt.TfmName}");
        int pixelCount = paddedWidth * paddedHeight;

        // 1. 线性 → 瓦片排列
        var tiledRgba = TileMorton.EncodeFromLinear(linearRgba, paddedWidth, paddedHeight, 4);

        // 2. 每像素 descriptor 编码为 long 值
        long[] values = new long[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            var c = new RgbaColor(
                tiledRgba[i * 4 + 0],
                tiledRgba[i * 4 + 1],
                tiledRgba[i * 4 + 2],
                tiledRgba[i * 4 + 3]);
            values[i] = desc.GetValue(c);
        }

        // 3. 写入 txd
        return WritePixelValues(values, desc.BitDepth, fmt.Kind);
    }

    /// <summary>
    /// 按 BitDepth 与 BitOrder 读取像素 long 值。
    /// BitDepth∈{1,2,4}：按 BitOrder 读 nibble（L4 用 LSB-first）
    /// BitDepth≥8：按 ByteOrder=LE 读取（3DS 一律 LE）
    /// </summary>
    private static long[] ReadPixelValues(byte[] data, int bitDepth, PixelFormatKind kind, int pixelCount)
    {
        var values = new long[pixelCount];
        if (bitDepth is 1 or 2 or 4)
        {
            int mask = (1 << bitDepth) - 1;
            int pixelsPerByte = 8 / bitDepth;
            bool lsbFirst = kind == PixelFormatKind.L4; // L4 用 LSB-first
            for (int i = 0; i < pixelCount; i++)
            {
                int byteIdx = i / pixelsPerByte;
                int slot = i % pixelsPerByte;
                int shift = lsbFirst ? bitDepth * slot : bitDepth * (pixelsPerByte - 1 - slot);
                values[i] = (data[byteIdx] >> shift) & mask;
            }
        }
        else
        {
            int bytesPerPixel = bitDepth / 8;
            for (int i = 0; i < pixelCount; i++)
            {
                long v = 0;
                for (int b = 0; b < bytesPerPixel; b++)
                    v |= (long)data[i * bytesPerPixel + b] << (8 * b); // LE
                values[i] = v;
            }
        }
        return values;
    }

    private static byte[] WritePixelValues(long[] values, int bitDepth, PixelFormatKind kind)
    {
        if (bitDepth is 1 or 2 or 4)
        {
            int pixelsPerByte = 8 / bitDepth;
            int byteCount = (values.Length + pixelsPerByte - 1) / pixelsPerByte;
            var data = new byte[byteCount];
            bool lsbFirst = kind == PixelFormatKind.L4;
            int mask = (1 << bitDepth) - 1;
            for (int i = 0; i < values.Length; i++)
            {
                int byteIdx = i / pixelsPerByte;
                int slot = i % pixelsPerByte;
                int shift = lsbFirst ? bitDepth * slot : bitDepth * (pixelsPerByte - 1 - slot);
                data[byteIdx] |= (byte)(((int)values[i] & mask) << shift);
            }
            return data;
        }
        else
        {
            int bytesPerPixel = bitDepth / 8;
            var data = new byte[values.Length * bytesPerPixel];
            for (int i = 0; i < values.Length; i++)
            {
                long v = values[i];
                for (int b = 0; b < bytesPerPixel; b++)
                    data[i * bytesPerPixel + b] = (byte)((v >> (8 * b)) & 0xFF); // LE
            }
            return data;
        }
    }

    // =================== ETC1/ETC1A4 解码 ===================

    private static byte[] DecodeEtc1(PixelFormatInfo fmt, byte[] txdData, int paddedWidth, int paddedHeight)
    {
        // 8×8 瓦片 + 4 子块布局（TL→TR→BL→BR）+ LE 字节序 + NormalOrder。
        // 每瓦片 4 个 4×4 子块，子块内 ETC1/ETC1A4 块按 LE 读取。
        bool useAlpha = fmt.Kind == PixelFormatKind.Etc1_a4;
        int blockBytes = useAlpha ? 16 : 8;
        int tilesX = paddedWidth / 8;
        int tilesY = paddedHeight / 8;
        // 4 子块偏移 (sy, sx): TL, TR, BL, BR
        int[][] subBlockOffsets = [[0, 0], [0, 4], [4, 0], [4, 4]];

        var linear = new byte[paddedWidth * paddedHeight * 4];
        int dataLen = txdData.Length;
        int tileBytes = 4 * blockBytes;

        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                int tileBase = (ty * tilesX + tx) * tileBytes;
                for (int sb = 0; sb < 4; sb++)
                {
                    int off = tileBase + sb * blockBytes;
                    if (off + blockBytes > dataLen) continue;

                    int sy = subBlockOffsets[sb][0];
                    int sx = subBlockOffsets[sb][1];

                    int[]? alpha = null;
                    int aOff = off;
                    if (useAlpha)
                    {
                        ulong alphaData = BinaryPrimitives.ReadUInt64LittleEndian(txdData.AsSpan(aOff));
                        alpha = Etc1Codec.DecodeAlpha(alphaData);
                        aOff += 8;
                    }
                    ulong colorData = BinaryPrimitives.ReadUInt64LittleEndian(txdData.AsSpan(aOff));
                    var block = Etc1Block.FromUInt64(colorData);
                    var pixels = Etc1Codec.DecodeBlock(block, alpha, useZOrder: false);

                    // pixels[i] 对应块内 (x=i%4, y=i/4)（NormalOrder 输出行优先）
                    for (int i = 0; i < 16; i++)
                    {
                        int bx = i % 4;
                        int by = i / 4;
                        int gx = tx * 8 + sx + bx;
                        int gy = ty * 8 + sy + by;
                        int dstIdx = (gy * paddedWidth + gx) * 4;
                        linear[dstIdx + 0] = pixels[i].R;
                        linear[dstIdx + 1] = pixels[i].G;
                        linear[dstIdx + 2] = pixels[i].B;
                        linear[dstIdx + 3] = pixels[i].A;
                    }
                }
            }
        }
        return linear;
    }

    private static byte[] EncodeEtc1(PixelFormatInfo fmt, byte[] linearRgba, int paddedWidth, int paddedHeight)
    {
        // DecodeEtc1 的精确逆运算：8×8 瓦片 + 4 子块布局 + LE 字节序 + NormalOrder。
        bool useAlpha = fmt.Kind == PixelFormatKind.Etc1_a4;
        int blockBytes = useAlpha ? 16 : 8;
        int tilesX = paddedWidth / 8;
        int tilesY = paddedHeight / 8;
        int[][] subBlockOffsets = [[0, 0], [0, 4], [4, 0], [4, 4]];
        var order = Etc1Codec.NormalOrder;

        int tileBytes = 4 * blockBytes;
        var txdData = new byte[tilesX * tilesY * tileBytes];

        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                int tileBase = (ty * tilesX + tx) * tileBytes;
                for (int sb = 0; sb < 4; sb++)
                {
                    int off = tileBase + sb * blockBytes;
                    int sy = subBlockOffsets[sb][0];
                    int sx = subBlockOffsets[sb][1];

                    // 从线性图像提取 4×4 子块像素
                    var pixels = new RgbaColor[16];
                    for (int i = 0; i < 16; i++)
                    {
                        int bx = i % 4;
                        int by = i / 4;
                        int gx = tx * 8 + sx + bx;
                        int gy = ty * 8 + sy + by;
                        int srcIdx = (gy * paddedWidth + gx) * 4;
                        pixels[i] = new RgbaColor(
                            linearRgba[srcIdx + 0],
                            linearRgba[srcIdx + 1],
                            linearRgba[srcIdx + 2],
                            linearRgba[srcIdx + 3]);
                    }

                    if (useAlpha)
                    {
                        int[] alphas = new int[16];
                        for (int i = 0; i < 16; i++)
                            alphas[order[i]] = pixels[i].A;
                        ulong alphaData = Etc1Codec.EncodeAlpha(alphas);
                        BinaryPrimitives.WriteUInt64LittleEndian(txdData.AsSpan(off), alphaData);
                        off += 8;
                    }

                    var block = Etc1Codec.EncodeBlock(pixels, useZOrder: false);
                    BinaryPrimitives.WriteUInt64LittleEndian(txdData.AsSpan(off), block.ToUInt64());
                }
            }
        }
        return txdData;
    }
}
