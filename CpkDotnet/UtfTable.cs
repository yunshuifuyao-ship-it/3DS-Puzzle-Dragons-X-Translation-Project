using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace CpkFormat;

public sealed class UtfColumn
{
    public string Name { get; set; }
    public CriTypeId Type { get; set; }
    public CriStorageFlag Storage { get; set; }
    public object? ConstantValue { get; set; }

    public UtfColumn(string name, CriTypeId type, CriStorageFlag storage, object? constantValue = null)
    {
        Name = name;
        Type = type;
        Storage = storage;
        ConstantValue = constantValue ?? DefaultValueFor(type);
    }

    internal static object? DefaultValueFor(CriTypeId type)
    {
        return type switch
        {
            CriTypeId.UChar => (byte)0,
            CriTypeId.Char => (sbyte)0,
            CriTypeId.UShort => (ushort)0,
            CriTypeId.Short => (short)0,
            CriTypeId.UInt => (uint)0,
            CriTypeId.Int => 0,
            CriTypeId.ULLong => (ulong)0,
            CriTypeId.LLong => (long)0,
            CriTypeId.Float => 0.0f,
            CriTypeId.Double => 0.0,
            CriTypeId.String => "<NULL>",
            CriTypeId.Bytes => Array.Empty<byte>(),
            _ => (byte)0
        };
    }

    internal static int TypeSize(CriTypeId type)
    {
        return type switch
        {
            CriTypeId.UChar => 1,
            CriTypeId.Char => 1,
            CriTypeId.UShort => 2,
            CriTypeId.Short => 2,
            CriTypeId.UInt => 4,
            CriTypeId.Int => 4,
            CriTypeId.Float => 4,
            CriTypeId.String => 4,
            CriTypeId.ULLong => 8,
            CriTypeId.LLong => 8,
            CriTypeId.Double => 8,
            CriTypeId.Bytes => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }
}

public sealed class UtfTable
{
    public string Name { get; set; }
    public List<UtfColumn> Columns { get; set; }
    public List<Dictionary<string, object?>> Rows { get; set; }

    public UtfTable(string name = "")
    {
        Name = name;
        Columns = new List<UtfColumn>();
        Rows = new List<Dictionary<string, object?>>();
    }

    public static UtfTable Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32)
            throw new ArgumentException("Data too small for UTF header");

        ReadOnlySpan<byte> tableData = data;
        byte[]? decrypted = null;

        if (data.Length >= 4 && data[..4].SequenceEqual(CriConstants.ENCRYPTED_UTF_MAGIC))
        {
            decrypted = UtfDecrypt(data);
            tableData = decrypted;
        }

        if (!tableData[..4].SequenceEqual(CriConstants.UTF_MAGIC))
            throw new ArgumentException($"Invalid UTF magic: {Convert.ToHexString(tableData[..4])}");

        uint tableSize = BinaryPrimitives.ReadUInt32BigEndian(tableData[4..]);
        uint rowsOffset = BinaryPrimitives.ReadUInt32BigEndian(tableData[8..]);
        uint stringOffset = BinaryPrimitives.ReadUInt32BigEndian(tableData[12..]);
        uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(tableData[16..]);
        uint tableNameOffset = BinaryPrimitives.ReadUInt32BigEndian(tableData[20..]);
        ushort numColumns = BinaryPrimitives.ReadUInt16BigEndian(tableData[24..]);
        ushort rowLength = BinaryPrimitives.ReadUInt16BigEndian(tableData[26..]);
        uint numRows = BinaryPrimitives.ReadUInt32BigEndian(tableData[28..]);

        const int baseOffset = 8;
        ReadOnlySpan<byte> columnData = tableData[32..];
        ReadOnlySpan<byte> rowData = tableData.Slice(baseOffset + (int)rowsOffset);
        ReadOnlySpan<byte> stringTable = tableData.Slice(baseOffset + (int)stringOffset);
        ReadOnlySpan<byte> binaryData = tableData.Slice(baseOffset + (int)dataOffset);

        int stringTableSize = (int)(dataOffset - stringOffset);
        int dataTableSize = (int)(tableSize - dataOffset);
        stringTable = stringTable[..stringTableSize];
        binaryData = binaryData[..dataTableSize];

        var table = new UtfTable();

        if (tableNameOffset < stringTable.Length)
        {
            int end = IndexOfNull(stringTable, (int)tableNameOffset);
            if (end < 0) end = stringTable.Length;
            table.Name = Encoding.UTF8.GetString(stringTable[(int)tableNameOffset..end]);
        }

        int colPtr = 0;
        for (int i = 0; i < numColumns; i++)
        {
            byte flag = columnData[colPtr++];
            var storage = (CriStorageFlag)(flag & 0xF0);
            var type = (CriTypeId)(flag & 0x0F);

            uint nameOff = BinaryPrimitives.ReadUInt32BigEndian(columnData[colPtr..]);
            colPtr += 4;

            string colName = "";
            if (nameOff < stringTable.Length)
            {
                int end = IndexOfNull(stringTable, (int)nameOff);
                if (end < 0) end = stringTable.Length;
                colName = Encoding.UTF8.GetString(stringTable[(int)nameOff..end]);
            }

            object? constVal = null;
            if (storage == CriStorageFlag.PerRow)
            {
                int sz = UtfColumn.TypeSize(type);
                constVal = ReadValue(columnData[colPtr..], type, stringTable, binaryData);
                colPtr += sz;
            }
            else if (storage == CriStorageFlag.Constant)
            {
                constVal = UtfColumn.DefaultValueFor(type);
            }

            table.Columns.Add(new UtfColumn(colName, type, storage, constVal));
        }

        for (int ri = 0; ri < numRows; ri++)
        {
            int rp = ri * rowLength;
            var row = new Dictionary<string, object?>();
            foreach (var col in table.Columns)
            {
                // PerRow 列不从行数据读取（值已在列定义的 ConstantValue 中）
                if (col.Storage == CriStorageFlag.Data)
                {
                    row[col.Name] = ReadValue(rowData[rp..], col.Type, stringTable, binaryData);
                    rp += UtfColumn.TypeSize(col.Type);
                }
                else
                {
                    row[col.Name] = col.ConstantValue;
                }
            }
            table.Rows.Add(row);
        }

        return table;
    }

    public byte[] Build(bool encrypt = false)
    {
        var stringTable = new List<byte>();
        var dataTable = new List<byte>();
        var stringOffsets = new Dictionary<string, int>();

        int AddString(string s)
        {
            if (stringOffsets.TryGetValue(s, out int existing))
                return existing;
            int off = stringTable.Count;
            stringOffsets[s] = off;
            stringTable.AddRange(Encoding.UTF8.GetBytes(s));
            stringTable.Add(0);
            return off;
        }

        AddString("<NULL>");
        int tableNameOffset = AddString(Name);
        foreach (var col in Columns)
            AddString(col.Name);

        foreach (var col in Columns)
        {
            if (col.Type == CriTypeId.String && (col.Storage == CriStorageFlag.PerRow || col.Storage == CriStorageFlag.Data))
            {
                if (col.ConstantValue is string s && s.Length > 0)
                    AddString(s);
            }
        }

        if (Name == "CpkTocInfo" || Name == "CpkEtocInfo" ||
            Name == "CpkGtocGlink" || Name == "CpkGtocAttr" ||
            Name == "CpkGtocInfo" || Name == "CpkGtocFlink")
            AddString("");

        foreach (var row in Rows)
        {
            foreach (var col in Columns)
            {
                if (col.Type == CriTypeId.String && col.Storage == CriStorageFlag.Data)
                {
                    if (row.TryGetValue(col.Name, out var v) && v is string s && s.Length > 0)
                        AddString(s);
                }
            }
        }

        var columnBuf = new List<byte>();
        int rowLength = 0;
        foreach (var col in Columns)
        {
            columnBuf.Add((byte)((byte)col.Storage | (byte)col.Type));
            columnBuf.AddRange(BigEndianBytes((uint)stringOffsets[col.Name]));

            if (col.Storage == CriStorageFlag.PerRow)
            {
                // PerRow 值仅写入列定义，不占用行数据空间（rowLength 不含 PerRow 列）
                WriteValue(columnBuf, col.ConstantValue ?? UtfColumn.DefaultValueFor(col.Type), col.Type, stringTable, stringOffsets, dataTable);
            }
            else if (col.Storage == CriStorageFlag.Data)
            {
                rowLength += UtfColumn.TypeSize(col.Type);
            }
        }

        var rowBuf = new List<byte>();
        foreach (var row in Rows)
        {
            foreach (var col in Columns)
            {
                // PerRow 列不写入行数据（值已在列定义中）
                if (col.Storage == CriStorageFlag.Data)
                {
                    object? v = row.TryGetValue(col.Name, out var val) ? val : col.ConstantValue;
                    WriteValue(rowBuf, v, col.Type, stringTable, stringOffsets, dataTable);
                }
            }
        }

        if (Name == "CpkHeader")
            stringTable.AddRange(new byte[7]);
        else if (Name == "CpkTocInfo")
            stringTable.AddRange(new byte[4]);
        else if (Name == "CpkEtocInfo")
            stringTable.AddRange(new byte[6]);

        int rowsOff = columnBuf.Count + 24;
        int stringOff = rowsOff + rowBuf.Count;
        int dataOff = stringOff + stringTable.Count;
        int tableSize = dataOff + dataTable.Count;

        var result = new List<byte>();
        result.AddRange(CriConstants.UTF_MAGIC);
        result.AddRange(BigEndianBytes((uint)tableSize));
        result.AddRange(BigEndianBytes((uint)rowsOff));
        result.AddRange(BigEndianBytes((uint)stringOff));
        result.AddRange(BigEndianBytes((uint)dataOff));
        result.AddRange(BigEndianBytes((uint)tableNameOffset));
        result.AddRange(BigEndianBytes((ushort)Columns.Count));
        result.AddRange(BigEndianBytes((ushort)rowLength));
        result.AddRange(BigEndianBytes((uint)Rows.Count));

        result.AddRange(columnBuf);
        result.AddRange(rowBuf);
        result.AddRange(stringTable);
        result.AddRange(dataTable);

        byte[] output = result.ToArray();

        if (encrypt)
            output = UtfDecrypt(output);

        return output;
    }

    private static object? ReadValue(ReadOnlySpan<byte> data, CriTypeId type,
        ReadOnlySpan<byte> stringTable, ReadOnlySpan<byte> binaryData)
    {
        switch (type)
        {
            case CriTypeId.UChar:
                return data[0];
            case CriTypeId.Char:
                return (sbyte)data[0];
            case CriTypeId.UShort:
                return BinaryPrimitives.ReadUInt16BigEndian(data);
            case CriTypeId.Short:
                return BinaryPrimitives.ReadInt16BigEndian(data);
            case CriTypeId.UInt:
                return BinaryPrimitives.ReadUInt32BigEndian(data);
            case CriTypeId.Int:
                return BinaryPrimitives.ReadInt32BigEndian(data);
            case CriTypeId.ULLong:
                return BinaryPrimitives.ReadUInt64BigEndian(data);
            case CriTypeId.LLong:
                return BinaryPrimitives.ReadInt64BigEndian(data);
            case CriTypeId.Float:
                return BitConverter.IsLittleEndian
                    ? BitConverter.Int32BitsToSingle((int)BinaryPrimitives.ReverseEndianness(BinaryPrimitives.ReadUInt32BigEndian(data)))
                    : BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data));
            case CriTypeId.Double:
                return BitConverter.IsLittleEndian
                    ? BitConverter.Int64BitsToDouble((long)BinaryPrimitives.ReverseEndianness(BinaryPrimitives.ReadUInt64BigEndian(data)))
                    : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data));
            case CriTypeId.String:
                {
                    int off = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    if (off < stringTable.Length)
                    {
                        int end = IndexOfNull(stringTable, off);
                        if (end < 0) end = stringTable.Length;
                        return Encoding.UTF8.GetString(stringTable[off..end]);
                    }
                    return "";
                }
            case CriTypeId.Bytes:
                {
                    int off = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    if (off + size <= binaryData.Length)
                        return binaryData.Slice(off, size).ToArray();
                    return Array.Empty<byte>();
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static void WriteValue(List<byte> buf, object? value, CriTypeId type,
        List<byte> stringTable, Dictionary<string, int> stringOffsets, List<byte> dataTable)
    {
        switch (type)
        {
            case CriTypeId.UChar:
                buf.Add((byte)(value is byte b ? b : 0));
                break;
            case CriTypeId.Char:
                buf.Add((byte)(value is sbyte sb ? sb : (sbyte)0));
                break;
            case CriTypeId.UShort:
                buf.AddRange(BigEndianBytes(value is ushort us ? us : (ushort)0));
                break;
            case CriTypeId.Short:
                buf.AddRange(BigEndianBytes((ushort)(value is short s ? s : (short)0)));
                break;
            case CriTypeId.UInt:
                buf.AddRange(BigEndianBytes(value is uint ui ? ui : 0U));
                break;
            case CriTypeId.Int:
                buf.AddRange(BigEndianBytes((uint)(value is int i ? i : 0)));
                break;
            case CriTypeId.ULLong:
                buf.AddRange(BigEndianBytes(value is ulong ul ? ul : 0UL));
                break;
            case CriTypeId.LLong:
                buf.AddRange(BigEndianBytes((ulong)(value is long l ? l : 0L)));
                break;
            case CriTypeId.Float:
                {
                    float f = value is float ff ? ff : 0.0f;
                    uint bits = BitConverter.IsLittleEndian
                        ? BinaryPrimitives.ReverseEndianness((uint)BitConverter.SingleToInt32Bits(f))
                        : (uint)BitConverter.SingleToInt32Bits(f);
                    buf.AddRange(BigEndianBytes(bits));
                    break;
                }
            case CriTypeId.Double:
                {
                    double d = value is double dd ? dd : 0.0;
                    ulong bits = BitConverter.IsLittleEndian
                        ? BinaryPrimitives.ReverseEndianness((ulong)BitConverter.DoubleToInt64Bits(d))
                        : (ulong)BitConverter.DoubleToInt64Bits(d);
                    buf.AddRange(BigEndianBytes(bits));
                    break;
                }
            case CriTypeId.String:
                {
                    string strVal = value is string str ? str : "";
                    int off = 0;
                    if (stringOffsets.TryGetValue(strVal, out var found))
                        off = found;
                    else if (strVal.Length == 0 && stringOffsets.TryGetValue("", out var emptyOff))
                        off = emptyOff;
                    buf.AddRange(BigEndianBytes((uint)off));
                    break;
                }
            case CriTypeId.Bytes:
                {
                    byte[] byteArr = value is byte[] bytes ? bytes : Array.Empty<byte>();
                    int off = dataTable.Count;
                    dataTable.AddRange(byteArr);
                    buf.AddRange(BigEndianBytes((uint)off));
                    buf.AddRange(BigEndianBytes((uint)byteArr.Length));
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static byte[] UtfDecrypt(ReadOnlySpan<byte> data)
    {
        uint m = CriConstants.UTF_ENCRYPT_SEED;
        byte[] result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ (m & 0xFF));
            m = (m * CriConstants.UTF_ENCRYPT_MULTIPLIER) & 0xFFFFFFFF;
        }
        return result;
    }

    private static int IndexOfNull(ReadOnlySpan<byte> span, int start)
    {
        for (int i = start; i < span.Length; i++)
        {
            if (span[i] == 0)
                return i;
        }
        return -1;
    }

    private static byte[] BigEndianBytes(ushort v)
    {
        var buf = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, v);
        return buf;
    }

    private static byte[] BigEndianBytes(uint v)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, v);
        return buf;
    }

    private static byte[] BigEndianBytes(ulong v)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, v);
        return buf;
    }
}