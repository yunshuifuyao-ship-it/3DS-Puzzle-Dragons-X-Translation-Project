using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;

namespace CpkFormat;

public sealed class CpkArchive : IDisposable
{
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private long _fileSize;
    private bool _disposed;

    private long _itocOffset;
    private long _itocSize;
    private bool _hasToc;

    public List<CpkFileEntry> Files { get; } = new();
    public int Mode { get; private set; }
    public int Alignment { get; private set; }
    public string? Tvers { get; private set; }
    public long ContentOffset { get; private set; }
    public long ContentSize { get; private set; }
    public long TocOffset { get; private set; }
    public long TocSize { get; private set; }
    public long GtocOffset { get; private set; }
    public long GtocSize { get; private set; }

    private CpkArchive() { }

    public static CpkArchive Open(string path)
    {
        var archive = new CpkArchive();
        var fileInfo = new FileInfo(path);
        archive._fileSize = fileInfo.Length;
        archive._mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        archive._accessor = archive._mmf.CreateViewAccessor(
            0, archive._fileSize, MemoryMappedFileAccess.Read);
        archive.Parse();
        return archive;
    }

    private void Parse()
    {
        byte[] headerRaw = new byte[CriConstants.CHUNK_SIZE];
        _accessor!.ReadArray(0, headerRaw, 0, CriConstants.CHUNK_SIZE);

        ReadOnlySpan<byte> magic = headerRaw.AsSpan(0, 4);
        if (!magic.SequenceEqual(CriConstants.CPK_MAGIC))
            throw new InvalidDataException("Invalid CPK magic");

        byte[] headerUtfData = new byte[CriConstants.CHUNK_SIZE - 0x10];
        Array.Copy(headerRaw, 0x10, headerUtfData, 0, headerUtfData.Length);

        var cpkHeader = UtfTable.Parse(headerUtfData);
        var row0 = cpkHeader.Rows.Count > 0
            ? cpkHeader.Rows[0]
            : new Dictionary<string, object?>();

        ContentOffset = GetLong(row0, "ContentOffset");
        ContentSize = GetLong(row0, "ContentSize");
        TocOffset = GetLong(row0, "TocOffset");
        TocSize = GetLong(row0, "TocSize");
        _itocOffset = GetLong(row0, "ItocOffset");
        _itocSize = GetLong(row0, "ItocSize");
        GtocOffset = GetLong(row0, "GtocOffset");
        GtocSize = GetLong(row0, "GtocSize");
        Alignment = (int)GetLong(row0, "Align", 0x800);
        Tvers = GetString(row0, "Tvers");
        Mode = (int)GetLong(row0, "CpkMode", 1);

        if (TocOffset > 0 && TocSize > 0)
        {
            _hasToc = true;
            ParseToc();
        }
        else if (_itocOffset > 0 && _itocSize > 0)
        {
            _hasToc = false;
            ParseItoc();
        }
    }

    private void ParseToc()
    {
        byte[] tocData = ReadRange(TocOffset, (int)TocSize);

        if (tocData.Length < 16)
            throw new InvalidDataException("TOC data too small");

        ReadOnlySpan<byte> tocMagic = tocData.AsSpan(0, 4);
        if (!tocMagic.SequenceEqual(CriConstants.TOC_MAGIC))
            throw new InvalidDataException("Invalid TOC chunk");

        byte[] tocUtfData = new byte[tocData.Length - 16];
        Array.Copy(tocData, 16, tocUtfData, 0, tocUtfData.Length);

        var tocTable = UtfTable.Parse(tocUtfData);

        foreach (var row in tocTable.Rows)
        {
            Files.Add(new CpkFileEntry
            {
                DirName = GetString(row, "DirName") ?? "",
                FileName = GetString(row, "FileName") ?? "",
                FileOffset = GetLong(row, "FileOffset"),
                FileSize = GetLong(row, "FileSize"),
                ExtractSize = GetLong(row, "ExtractSize"),
                Id = GetLong(row, "ID"),
                UserString = GetString(row, "UserString") ?? "<NULL>"
            });
        }
    }

    private void ParseItoc()
    {
        byte[] itocData = ReadRange(_itocOffset, (int)_itocSize);

        if (itocData.Length < 16)
            throw new InvalidDataException("ITOC data too small");

        ReadOnlySpan<byte> itocMagic = itocData.AsSpan(0, 4);
        if (!itocMagic.SequenceEqual(CriConstants.ETOC_MAGIC))
            throw new InvalidDataException("Invalid ITOC chunk");

        byte[] itocUtfData = new byte[itocData.Length - 16];
        Array.Copy(itocData, 16, itocUtfData, 0, itocUtfData.Length);

        var itocTable = UtfTable.Parse(itocUtfData);
        var row0 = itocTable.Rows.Count > 0
            ? itocTable.Rows[0]
            : new Dictionary<string, object?>();

        var entriesL = new List<CpkFileEntry>();
        var entriesH = new List<CpkFileEntry>();

        if (row0.TryGetValue("DataL", out var dataLObj) && dataLObj is byte[] dataL)
        {
            var tableL = UtfTable.Parse(dataL);
            foreach (var row in tableL.Rows)
            {
                entriesL.Add(new CpkFileEntry
                {
                    Id = GetLong(row, "ID"),
                    FileSize = GetLong(row, "FileSize"),
                    ExtractSize = GetLong(row, "ExtractSize"),
                    FileName = GetLong(row, "ID").ToString()
                });
            }
        }

        if (row0.TryGetValue("DataH", out var dataHObj) && dataHObj is byte[] dataH)
        {
            var tableH = UtfTable.Parse(dataH);
            foreach (var row in tableH.Rows)
            {
                entriesH.Add(new CpkFileEntry
                {
                    Id = GetLong(row, "ID"),
                    FileSize = GetLong(row, "FileSize"),
                    ExtractSize = GetLong(row, "ExtractSize"),
                    FileName = GetLong(row, "ID").ToString()
                });
            }
        }

        var allEntries = entriesL.Concat(entriesH).OrderBy(e => e.Id).ToList();

        long currentOffset = 0;
        foreach (var entry in allEntries)
        {
            entry.FileOffset = currentOffset;
            currentOffset += entry.FileSize;
            long rem = currentOffset % Alignment;
            if (rem != 0)
                currentOffset += Alignment - rem;
        }

        Files.AddRange(allEntries);
    }

    private byte[] ReadRange(long offset, int size)
    {
        byte[] buffer = new byte[size];
        _accessor!.ReadArray(offset, buffer, 0, size);
        return buffer;
    }

    public byte[] ReadFileRaw(CpkFileEntry entry)
    {
        long off = _hasToc ? 0x800 + entry.FileOffset : ContentOffset + entry.FileOffset;
        return ReadRange(off, (int)entry.FileSize);
    }

    public byte[] ReadFile(CpkFileEntry entry)
    {
        byte[] data = ReadFileRaw(entry);
        if (entry.IsCompressed && Crilayla.IsCompressed(data))
            return Crilayla.Decompress(data);
        return data;
    }

    public byte[]? ReadGtoc()
    {
        if (GtocOffset > 0 && GtocSize > 0)
            return ReadRange(GtocOffset, (int)GtocSize);
        return null;
    }

    public void ExtractAll(string outputDir, Action<string, int, int>? progress = null)
    {
        Directory.CreateDirectory(outputDir);

        int total = Files.Count;
        for (int i = 0; i < total; i++)
        {
            var entry = Files[i];
            progress?.Invoke(entry.FullPath, i, total);

            string outPath = outputDir;
            if (!string.IsNullOrEmpty(entry.DirName))
            {
                outPath = Path.Combine(outPath,
                    entry.DirName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(outPath);
            }
            outPath = Path.Combine(outPath, entry.FileName);

            byte[] data = ReadFile(entry);
            File.WriteAllBytes(outPath, data);
        }

        progress?.Invoke("Done", total, total);
    }

    public CpkFileEntry? FindFile(string name)
    {
        foreach (var entry in Files)
        {
            if (entry.FullPath == name || entry.FileName == name)
                return entry;
        }
        return null;
    }

    public CpkFileEntry? FindFileById(long id)
    {
        foreach (var entry in Files)
        {
            if (entry.Id == id)
                return entry;
        }
        return null;
    }

    private static long GetLong(Dictionary<string, object?> row, string key,
        long defaultValue = 0)
    {
        if (row.TryGetValue(key, out var val))
        {
            if (val is long l) return l;
            if (val is ulong ul) return (long)ul;
            if (val is int i) return i;
            if (val is uint ui) return ui;
            if (val is short s) return s;
            if (val is ushort us) return us;
        }
        return defaultValue;
    }

    private static string? GetString(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var val) && val is string s)
            return s;
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _accessor?.Dispose();
        _mmf?.Dispose();
    }
}