using System.Text;

namespace TimgTool;

/// <summary>
/// TIMG 文件头（0xA0 字节固定头）。
/// 字段顺序与偏移参考游戏 sub_1A87F4 加载器。
/// </summary>
public sealed class TimgHeader
{
    public const int Size = 0xA0;
    public const int ChunkStart = 0xA0; // chunk 固定从 0xA0 开始（Python/IDA 验证）

    public uint Magic;              // 0x00: "TIMG"
    public uint Version;            // 0x04: 通常为 1
    public uint FileSize;           // 0x08: 文件总大小（字节，uint32 LE）
                                    // 游戏 sub_2A50C4 读取此字段：dataSize = FileSize - 0x90
    public uint HeaderSize;         // 0x0C: 字段值非 chunk 起始（实测 0x100），仅保留
    public byte[] Filename = new byte[64]; // 0x10: ASCII 文件名（去扩展名，最多 63 字节 + NUL）
    public uint Hash1;             // 0x50: NW4C 专有哈希，每文件唯一
    public uint PixelFormat;        // 0x54: 仅参考，权威格式由 nw4c_tfm 决定
    public uint Hash2;             // 0x58: NW4C 专有哈希
    public ushort Width;            // 0x90: padded 宽度（2 的幂）
    public ushort Height;           // 0x92: padded 高度（2 的幂）
    public ushort CropWidth;        // 0x94: 实际有效宽
    public ushort CropHeight;       // 0x96: 实际有效高
    public byte[] Reserved = new byte[Size - 0x98]; // 0x98..0xA0

    public string FilenameAsString
    {
        get
        {
            int nul = Array.IndexOf(Filename, (byte)0);
            int len = nul < 0 ? Filename.Length : nul;
            return Encoding.ASCII.GetString(Filename, 0, len);
        }
    }

    public static TimgHeader Parse(byte[] data)
    {
        if (data.Length < Size)
            throw new InvalidDataException($"TIMG header too short: {data.Length} < {Size}");
        var h = new TimgHeader();
        h.Magic = BitConverter.ToUInt32(data, 0x00);
        if (h.Magic != 0x474D4954u) // "TIMG" little-endian
            throw new InvalidDataException($"Not a TIMG file: magic=0x{h.Magic:X8}");
        h.Version = BitConverter.ToUInt32(data, 0x04);
        h.FileSize = BitConverter.ToUInt32(data, 0x08);
        h.HeaderSize = BitConverter.ToUInt32(data, 0x0C);
        Array.Copy(data, 0x10, h.Filename, 0, 64);
        h.Hash1 = BitConverter.ToUInt32(data, 0x50);
        h.PixelFormat = BitConverter.ToUInt32(data, 0x54);
        h.Hash2 = BitConverter.ToUInt32(data, 0x58);
        h.Width = BitConverter.ToUInt16(data, 0x90);
        h.Height = BitConverter.ToUInt16(data, 0x92);
        h.CropWidth = BitConverter.ToUInt16(data, 0x94);
        h.CropHeight = BitConverter.ToUInt16(data, 0x96);
        Array.Copy(data, 0x98, h.Reserved, 0, h.Reserved.Length);
        return h;
    }

    public byte[] ToBytes()
    {
        var b = new byte[Size];
        BitConverter.TryWriteBytes(b.AsSpan(0x00), Magic);
        BitConverter.TryWriteBytes(b.AsSpan(0x04), Version);
        BitConverter.TryWriteBytes(b.AsSpan(0x08), FileSize);
        BitConverter.TryWriteBytes(b.AsSpan(0x0C), HeaderSize);
        Array.Copy(Filename, 0, b, 0x10, 64);
        BitConverter.TryWriteBytes(b.AsSpan(0x50), Hash1);
        BitConverter.TryWriteBytes(b.AsSpan(0x54), PixelFormat);
        BitConverter.TryWriteBytes(b.AsSpan(0x58), Hash2);
        BitConverter.TryWriteBytes(b.AsSpan(0x90), Width);
        BitConverter.TryWriteBytes(b.AsSpan(0x92), Height);
        BitConverter.TryWriteBytes(b.AsSpan(0x94), CropWidth);
        BitConverter.TryWriteBytes(b.AsSpan(0x96), CropHeight);
        Array.Copy(Reserved, 0, b, 0x98, Reserved.Length);
        return b;
    }
}

/// <summary>
/// NW4C chunk：8B signature + 4B size + (size-12)B data。
/// </summary>
public sealed class Nw4cChunk
{
    public string Signature = "";     // 8 字节 ASCII
    public uint Size;                 // 含 signature + size + data
    public byte[] Data = Array.Empty<byte>();

    public static Nw4cChunk Parse(byte[] buf, int offset)
    {
        if (offset + 12 > buf.Length)
            throw new InvalidDataException($"Chunk header out of range at offset {offset}");
        var sig = Encoding.ASCII.GetString(buf, offset, 8);
        var size = BitConverter.ToUInt32(buf, offset + 8);
        if (size < 12 || offset + size > buf.Length)
            throw new InvalidDataException($"Chunk size invalid at offset {offset}: size={size}");
        var dataLen = (int)(size - 12);
        var data = new byte[dataLen];
        if (dataLen > 0)
            Array.Copy(buf, offset + 12, data, 0, dataLen);
        return new Nw4cChunk { Signature = sig, Size = size, Data = data };
    }

    public byte[] ToBytes()
    {
        var b = new byte[12 + Data.Length];
        var sigBytes = Encoding.ASCII.GetBytes(Signature.PadRight(8).Substring(0, 8));
        Array.Copy(sigBytes, 0, b, 0, 8);
        BitConverter.TryWriteBytes(b.AsSpan(8), (uint)(12 + Data.Length));
        if (Data.Length > 0)
            Array.Copy(Data, 0, b, 12, Data.Length);
        return b;
    }
}

/// <summary>
/// TIMG 完整文件：header + 有序 chunk 列表。
/// 标准顺序：tfm → gvr(可选) → txd → gnm → psh → mps(可选) → end
/// </summary>
public sealed class TimgFile
{
    public TimgHeader Header = new();
    public List<Nw4cChunk> Chunks = new();

    public Nw4cChunk? FindChunk(string sig) => Chunks.FirstOrDefault(c => c.Signature == sig);

    /// <summary>
    /// 从 nw4c_tfm chunk 读取格式名（权威）。若缺失返回 null。
    /// </summary>
    public string? GetTfmName()
    {
        var tfm = FindChunk("nw4c_tfm");
        if (tfm is null || tfm.Data.Length == 0) return null;
        int nul = Array.IndexOf(tfm.Data, (byte)0);
        int len = nul < 0 ? tfm.Data.Length : nul;
        return Encoding.ASCII.GetString(tfm.Data, 0, len);
    }

    public static TimgFile Parse(byte[] data)
    {
        var header = TimgHeader.Parse(data);
        var chunks = new List<Nw4cChunk>();
        // chunk 固定从 0xA0 开始（Python timg2png.py / IDA sub_1A87F4 验证）
        // 不使用 header.HeaderSize 字段（实测值 0x100，含义不明）
        int offset = TimgHeader.ChunkStart;
        while (offset < data.Length)
        {
            var c = Nw4cChunk.Parse(data, offset);
            chunks.Add(c);
            offset += (int)c.Size;
            if (c.Signature == "nw4c_end") break;
        }
        return new TimgFile { Header = header, Chunks = chunks };
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        ms.Write(Header.ToBytes(), 0, TimgHeader.Size);
        foreach (var c in Chunks)
        {
            var b = c.ToBytes();
            ms.Write(b, 0, b.Length);
        }
        var result = ms.ToArray();
        // 回填实际文件大小到 offset 0x08（游戏 sub_2A50C4 读取此字段计算数据长度）
        BitConverter.TryWriteBytes(result.AsSpan(0x08), (uint)result.Length);
        return result;
    }
}
