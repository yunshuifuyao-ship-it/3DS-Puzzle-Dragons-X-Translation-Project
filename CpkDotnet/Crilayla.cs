using System;
using System.Buffers;
using System.Buffers.Binary;

namespace CpkFormat;

public static class Crilayla
{
    public static bool IsCompressed(ReadOnlySpan<byte> data)
    {
        return data.Length >= 16 && data[..8].SequenceEqual(CriConstants.CRILAYLA_MAGIC);
    }

    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (!IsCompressed(data))
            throw new ArgumentException("Not CRILAYLA data");

        uint uncompSize = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        uint compSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);

        if (16 + compSize + 256 > data.Length)
            throw new ArgumentException("Invalid CRILAYLA data size");

        byte[] output = new byte[256 + uncompSize];

        int compSizeInt = (int)compSize;
        ReadOnlySpan<byte> prefix = data.Slice(16 + compSizeInt, 256);
        prefix.CopyTo(output);

        ReadOnlySpan<byte> compressedData = data.Slice(16, compSizeInt);
        var reader = new BitReader(compressedData);

        int dst = output.Length - 1;
        int dstStart = 256;

        while (dst >= dstStart)
        {
            if (reader.GetBits(1) == 0)
            {
                output[dst--] = (byte)reader.GetBits(8);
            }
            else
            {
                int offset = reader.GetBits(13);
                int length = reader.GetBits(2);

                if (length == 3)
                {
                    length += reader.GetBits(3);
                    if (length == 10)
                    {
                        length += reader.GetBits(5);
                        if (length == 41)
                        {
                            int b;
                            do
                            {
                                b = reader.GetBits(8);
                                length += b;
                            } while (b == 255);
                        }
                    }
                }

                length += 3;
                int srcPtr = dst + offset + 3;

                if (offset + 3 >= length)
                {
                    int copyStart = dst - length + 1;
                    output.AsSpan(srcPtr - length + 1, length).CopyTo(output.AsSpan(copyStart, length));
                    dst -= length;
                }
                else
                {
                    while (length > 0 && dst >= dstStart)
                    {
                        output[dst] = output[srcPtr];
                        dst--;
                        srcPtr--;
                        length--;
                    }
                }
            }
        }

        return output;
    }

    // 哈希链最大深度。窗口大小 0x2000=8192，2048 在游戏资源上接近原版暴力搜索压缩率。
    private const int MaxChainLength = 2048;
    // nice_match：bestLen 达到此长度时提前停止链遍历（zlib 风格），加速大文件
    private const int NiceMatch = 128;
    // 哈希表大小（16 位哈希 → 65536 桶）
    private const int HashSize = 1 << 16;

    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 256)
            throw new ArgumentException("Data too small for CRILAYLA compression (min 256 bytes)");

        int size = data.Length;
        int maxOutput = ((size - 256) * 9 + 7) / 8 + 64;
        int bufSize = maxOutput + size;
        byte[] buf = ArrayPool<byte>.Shared.Rent(bufSize);
        // 哈希链：head[h] = 最近一个哈希为 h 的位置；prev[p] = 上一个相同哈希的位置
        int[] head = ArrayPool<int>.Shared.Rent(HashSize);
        int[] prev = ArrayPool<int>.Shared.Rent(size);

        try
        {
            Array.Fill(head, -1, 0, HashSize);

            // 正向预扫描构建哈希链（p-2 必须 >= 0x100，因为前缀包括 p-2）
            // CRILAYLA 反向匹配：src 和候选 i 共享 (data[p], data[p-1], data[p-2]) 前缀
            for (int p = 0x102; p < size; p++)
            {
                uint h = Hash(data, p);
                prev[p] = head[h];
                head[h] = p;
            }

            int dst = bufSize - 1;
            int src = size - 1;
            uint bitBuf = 0;
            int bitCnt = 0;

            void PushByte(int b)
            {
                buf[dst--] = (byte)(b & 0xFF);
            }

            while (src >= 0x100)
            {
                int bestLen = 0;
                int bestOff = 0;
                int maxLen = src - 0x100;  // 匹配长度上限（不跨越 0x100 边界）
                int windowEnd = src + 3 + 0x2000;
                if (windowEnd > size) windowEnd = size;
                int windowStart = src + 3;

                // 只有 src >= 0x102 才能计算 3 字节前缀哈希
                if (src >= 0x102)
                {
                    uint h = Hash(data, src);
                    int candidate = head[h];
                    int chainLen = 0;

                    while (candidate != -1)
                    {
                        // 候选必须在 [windowStart, windowEnd) 内
                        if (candidate < windowStart)
                            break;  // 链中位置递减，遇到更小的可直接终止
                        if (candidate >= windowEnd)
                        {
                            // 超窗口候选不计入 chainLen，仅跳过
                            candidate = prev[candidate];
                            continue;
                        }

                        // 窗口内候选计数
                        if (chainLen >= MaxChainLength) break;
                        chainLen++;

                        // 对候选做完整匹配扩展（与原算法一致）
                        int k = 0;
                        while (k <= maxLen && data[src - k] == data[candidate - k])
                            k++;

                        if (k > bestLen)
                        {
                            bestOff = candidate - src - 3;
                            bestLen = k;
                            if (k >= maxLen || k >= NiceMatch) break;  // 已达上限或足够好，提前退出
                        }

                        candidate = prev[candidate];
                    }
                }

                if (bestLen < 3)
                {
                    bitBuf = (bitBuf << 9) | data[src];
                    bitCnt += 9;
                    src--;
                }
                else
                {
                    bitBuf = (((bitBuf << 1) | 1) << 13) | (uint)bestOff;
                    bitCnt += 14;
                    src -= bestLen;

                    int n = bestLen;
                    if (n < 6)
                    {
                        bitBuf = (bitBuf << 2) | (uint)(n - 3);
                        bitCnt += 2;
                    }
                    else if (n < 13)
                    {
                        bitBuf = (((bitBuf << 2) | 3) << 3) | (uint)(n - 6);
                        bitCnt += 5;
                    }
                    else if (n < 44)
                    {
                        bitBuf = (((bitBuf << 5) | 0x1F) << 5) | (uint)(n - 13);
                        bitCnt += 10;
                    }
                    else
                    {
                        bitBuf = ((bitBuf << 10) | 0x3FF);
                        bitCnt += 10;
                        n -= 44;

                        while (true)
                        {
                            while (bitCnt >= 8)
                            {
                                PushByte((int)((bitBuf >> (bitCnt - 8)) & 0xFF));
                                bitCnt -= 8;
                                bitBuf &= (1u << bitCnt) - 1;
                            }
                            if (n < 255)
                                break;
                            bitBuf = (bitBuf << 8) | 0xFF;
                            bitCnt += 8;
                            n -= 255;
                        }
                        bitBuf = (bitBuf << 8) | (uint)n;
                        bitCnt += 8;
                    }
                }

                while (bitCnt >= 8)
                {
                    PushByte((int)((bitBuf >> (bitCnt - 8)) & 0xFF));
                    bitCnt -= 8;
                    bitBuf &= (1u << bitCnt) - 1;
                }
            }

            if (bitCnt != 0)
                PushByte((int)(bitBuf << (8 - bitCnt)));

            PushByte(0);
            PushByte(0);

            while (((bufSize - dst) & 3) != 0)
                PushByte(0);

            int compSize = bufSize - dst - 1;
            int compStart = dst + 1;

            uint uncompressedSize = (uint)(size - 256);

            byte[] result = new byte[16 + compSize + 256];
            CriConstants.CRILAYLA_MAGIC.CopyTo(result, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), uncompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), (uint)compSize);
            buf.AsSpan(compStart, compSize).CopyTo(result.AsSpan(16, compSize));
            data[..256].CopyTo(result.AsSpan(16 + compSize, 256));

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            ArrayPool<int>.Shared.Return(head);
            ArrayPool<int>.Shared.Return(prev);
        }
    }

    // 三字节前缀哈希（CRILAYLA 反向匹配：src 和候选共享 (data[p], data[p-1], data[p-2])）
    private static uint Hash(ReadOnlySpan<byte> data, int p)
    {
        uint h = (uint)((data[p] << 16) | (data[p - 1] << 8) | data[p - 2]);
        return (h * 2654435761u) >> 16;  // Knuth 乘法散列，取高 16 位
    }

    // 原 O(n²) 暴力搜索实现，保留作为参考与回退（未在主路径调用）
    public static byte[] CompressNaive(ReadOnlySpan<byte> data)
    {
        if (data.Length < 256)
            throw new ArgumentException("Data too small for CRILAYLA compression (min 256 bytes)");

        int size = data.Length;
        int maxOutput = ((size - 256) * 9 + 7) / 8 + 64;
        int bufSize = maxOutput + size;
        byte[] buf = ArrayPool<byte>.Shared.Rent(bufSize);

        try
        {
            int dst = bufSize - 1;
            int src = size - 1;
            uint bitBuf = 0;
            int bitCnt = 0;

            void PushByte(int b)
            {
                buf[dst--] = (byte)(b & 0xFF);
            }

            while (src >= 0x100)
            {
                int end = src + 3 + 0x2000;
                if (end > size)
                    end = size;

                int bestLen = 0;
                int bestOff = 0;

                for (int i = src + 3; i < end; i++)
                {
                    int k = 0;
                    while (k <= src - 0x100)
                    {
                        if (data[src - k] != data[i - k])
                            break;
                        k++;
                    }
                    if (k > bestLen)
                    {
                        bestOff = i - src - 3;
                        bestLen = k;
                    }
                }

                if (bestLen < 3)
                {
                    bitBuf = (bitBuf << 9) | data[src];
                    bitCnt += 9;
                    src--;
                }
                else
                {
                    bitBuf = (((bitBuf << 1) | 1) << 13) | (uint)bestOff;
                    bitCnt += 14;
                    src -= bestLen;

                    int n = bestLen;
                    if (n < 6)
                    {
                        bitBuf = (bitBuf << 2) | (uint)(n - 3);
                        bitCnt += 2;
                    }
                    else if (n < 13)
                    {
                        bitBuf = (((bitBuf << 2) | 3) << 3) | (uint)(n - 6);
                        bitCnt += 5;
                    }
                    else if (n < 44)
                    {
                        bitBuf = (((bitBuf << 5) | 0x1F) << 5) | (uint)(n - 13);
                        bitCnt += 10;
                    }
                    else
                    {
                        bitBuf = ((bitBuf << 10) | 0x3FF);
                        bitCnt += 10;
                        n -= 44;

                        while (true)
                        {
                            while (bitCnt >= 8)
                            {
                                PushByte((int)((bitBuf >> (bitCnt - 8)) & 0xFF));
                                bitCnt -= 8;
                                bitBuf &= (1u << bitCnt) - 1;
                            }
                            if (n < 255)
                                break;
                            bitBuf = (bitBuf << 8) | 0xFF;
                            bitCnt += 8;
                            n -= 255;
                        }
                        bitBuf = (bitBuf << 8) | (uint)n;
                        bitCnt += 8;
                    }
                }

                while (bitCnt >= 8)
                {
                    PushByte((int)((bitBuf >> (bitCnt - 8)) & 0xFF));
                    bitCnt -= 8;
                    bitBuf &= (1u << bitCnt) - 1;
                }
            }

            if (bitCnt != 0)
                PushByte((int)(bitBuf << (8 - bitCnt)));

            PushByte(0);
            PushByte(0);

            while (((bufSize - dst) & 3) != 0)
                PushByte(0);

            int compSize = bufSize - dst - 1;
            int compStart = dst + 1;

            uint uncompressedSize = (uint)(size - 256);

            byte[] result = new byte[16 + compSize + 256];
            CriConstants.CRILAYLA_MAGIC.CopyTo(result, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), uncompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), (uint)compSize);
            buf.AsSpan(compStart, compSize).CopyTo(result.AsSpan(16, compSize));
            data[..256].CopyTo(result.AsSpan(16 + compSize, 256));

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}

internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private int _bitCount;
    private uint _bitData;

    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = data.Length - 1;
        _bitCount = 0;
        _bitData = 0;
    }

    public int GetBits(int n)
    {
        while (_bitCount < n)
        {
            if (_pos >= 0)
            {
                _bitData = (_bitData << 8) | _data[_pos];
                _pos--;
            }
            else
            {
                _bitData <<= 8;
            }
            _bitCount += 8;
        }

        int data = (int)(_bitData >> (_bitCount - n));
        _bitCount -= n;
        uint mask = (1u << n) - 1;
        return data & (int)mask;
    }
}