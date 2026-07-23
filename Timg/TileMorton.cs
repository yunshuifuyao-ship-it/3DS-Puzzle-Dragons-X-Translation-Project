namespace TimgTool;

/// <summary>
/// 3DS 8×8 Morton Z-order 瓦片重排。
/// 算法严格遵循 Kuriimu2 CtrSwizzle.cs：bitFieldCoords = [(1,0),(0,1),(2,0),(0,2),(4,0),(0,4)]。
/// IDA sub_2A50C4 验证此 8×8 Morton 布局。
/// </summary>
public static class TileMorton
{
    /// <summary>
    /// 8×8 瓦片内的 Morton Z-order 查找表：tileIndex (0..63) → (x, y)。
    /// 通过将 6 个 bit 分配给 (x, y) 得到：
    ///   bit0→x0, bit1→y0, bit2→x1, bit3→y1, bit4→x2, bit5→y2
    /// </summary>
    public static readonly (int x, int y)[] TileCoords = new (int, int)[64];

    static TileMorton()
    {
        for (int i = 0; i < 64; i++)
        {
            // Morton Z-order: bit0→x0, bit1→y0, bit2→x1, bit3→y1, bit4→x2, bit5→y2
            int x = (i & 1) | ((i >> 2) & 1) << 1 | ((i >> 4) & 1) << 2;
            int y = ((i >> 1) & 1) | ((i >> 3) & 1) << 1 | ((i >> 5) & 1) << 2;
            TileCoords[i] = (x, y);
        }
    }

    /// <summary>
    /// 解码：将瓦片排列的像素数据转换为线性 RGBA8888（按 padded 宽高 stride）。
    /// bytesPerPixel 通常为 4（RGBA8888）；ETC1 块压缩格式不走此路径。
    /// </summary>
    public static byte[] DecodeToLinear(byte[] tiledData, int paddedWidth, int paddedHeight, int bytesPerPixel)
    {
        var linear = new byte[paddedWidth * paddedHeight * bytesPerPixel];
        int tilesX = paddedWidth / 8;
        int tilesY = paddedHeight / 8;
        int tileBytes = 8 * 8 * bytesPerPixel;
        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                int tileBase = (ty * tilesX + tx) * tileBytes;
                for (int i = 0; i < 64; i++)
                {
                    var (lx, ly) = TileCoords[i];
                    int src = tileBase + i * bytesPerPixel;
                    int dst = ((ty * 8 + ly) * paddedWidth + (tx * 8 + lx)) * bytesPerPixel;
                    if (src + bytesPerPixel > tiledData.Length) continue;
                    Buffer.BlockCopy(tiledData, src, linear, dst, bytesPerPixel);
                }
            }
        }
        return linear;
    }

    /// <summary>
    /// 编码：将线性 RGBA8888 像素（按 padded 宽高 stride）打包为瓦片排列。
    /// </summary>
    public static byte[] EncodeFromLinear(byte[] linearData, int paddedWidth, int paddedHeight, int bytesPerPixel)
    {
        var tiled = new byte[paddedWidth * paddedHeight * bytesPerPixel];
        int tilesX = paddedWidth / 8;
        int tilesY = paddedHeight / 8;
        int tileBytes = 8 * 8 * bytesPerPixel;
        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                int tileBase = (ty * tilesX + tx) * tileBytes;
                for (int i = 0; i < 64; i++)
                {
                    var (lx, ly) = TileCoords[i];
                    int dst = tileBase + i * bytesPerPixel;
                    int src = ((ty * 8 + ly) * paddedWidth + (tx * 8 + lx)) * bytesPerPixel;
                    if (src + bytesPerPixel > linearData.Length) continue;
                    Buffer.BlockCopy(linearData, src, tiled, dst, bytesPerPixel);
                }
            }
        }
        return tiled;
    }
}
