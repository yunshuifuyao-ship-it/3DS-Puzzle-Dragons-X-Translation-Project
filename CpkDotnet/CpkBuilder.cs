using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CpkFormat;

public sealed class CpkBuilder
{
    public int Mode { get; set; } = 3;
    public int Alignment { get; set; } = 0x800;
    public bool Compress { get; set; }
    public string Tvers { get; set; } = "CPKMC2.47.02, DLL3.17.00";
    public int MaxThreads { get; set; } = Environment.ProcessorCount;

    public List<(string Path, byte[] Data, bool IsRaw)> Files { get; } = new();
    public byte[]? GtocData { get; set; }
    public List<long>? FileIds { get; set; }

    public static byte[]? LoadGtocFromCpk(string cpkPath)
    {
        using var archive = CpkArchive.Open(cpkPath);
        return archive.ReadGtoc();
    }

    public void LoadGtocFromFile(string filePath)
    {
        GtocData = File.ReadAllBytes(filePath);
    }

    public static void ApplyPreset(CpkBuilder builder, string presetName)
    {
        switch (presetName)
        {
            case "pad3ds":
                builder.Mode = 3;
                builder.Tvers = "CPKMC2.47.02, DLL3.17.00";
                break;
        }
    }

    public static List<long> LoadIdsFromCpk(string cpkPath)
    {
        using var archive = CpkArchive.Open(cpkPath);
        var ids = new List<long>(archive.Files.Count);
        foreach (var entry in archive.Files)
            ids.Add(entry.Id);
        return ids;
    }

    private sealed class FileBuildInfo
    {
        public string DirName { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
        public long ExtractSize { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    public static CpkBuilder FromArchive(CpkArchive archive)
    {
        var builder = new CpkBuilder
        {
            Mode = archive.Mode,
            Alignment = archive.Alignment,
            Tvers = archive.Tvers ?? "CPKMC2.47.02, DLL3.17.00",
            FileIds = new List<long>(),
            GtocData = archive.ReadGtoc()
        };

        foreach (var entry in archive.Files)
        {
            byte[] raw = archive.ReadFileRaw(entry);
            builder.Files.Add((entry.FullPath, raw, true));
            builder.FileIds.Add(entry.Id);
        }

        return builder;
    }

    public void AddFile(string archivePath, byte[] data, bool isRaw = false)
    {
        Files.Add((archivePath, data, isRaw));
    }

    // 解压目录中常见的非游戏文件扩展名（解压副作用产物）
    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".orig", // 备份文件
        ".png",  // timg 解压副作用（原版 CPK 不含 .png）
    };

    // 原版不压缩的扩展名（已是压缩格式，CRILAYLA 压不进反而极慢）
    // 注：原版 .awb/.acb 文件大小=压缩大小，证明未压缩
    private static readonly HashSet<string> UncompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".awb", // CRI 音频流（已压缩）
        ".acb", // CRI 音频 cue 表（已压缩）
    };

    // 大文件阈值：>= 4MB 不压缩（双保险，避免极端情况卡顿）
    // 原版最大压缩文件是 2.19MB 的 mpb_860_conan.tast，4MB 阈值保留所有原版压缩行为
    private const long LargeFileThreshold = 4 * 1024 * 1024;

    private bool ShouldCompress(string arcPath, long dataLength)
    {
        if (!Compress || dataLength < 256) return false;
        if (dataLength >= LargeFileThreshold) return false;
        string ext = Path.GetExtension(arcPath);
        return !UncompressedExtensions.Contains(ext);
    }

    public void AddDirectory(string dirPath)
    {
        dirPath = Path.GetFullPath(dirPath);

        foreach (string filePath in Directory.EnumerateFiles(
            dirPath, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(filePath);
            if (ExcludedExtensions.Contains(ext))
                continue;

            string relPath = Path.GetRelativePath(dirPath, filePath)
                .Replace('\\', '/');
            byte[] data = File.ReadAllBytes(filePath);
            Files.Add((relPath, data, false));
        }
    }

    public void Build(string outputPath, Action<string, int, int>? progress = null)
    {
        if (Files.Count == 0)
            throw new InvalidOperationException("No files to pack");

        int align = Alignment;
        int numFiles = Files.Count;

        var pending = Files.OrderBy(f => CriSortKey(f.Path)).ToList();

        var fileInfos = new FileBuildInfo[pending.Count];
        int completedCount = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, MaxThreads)
        };

        Parallel.For(0, pending.Count, parallelOptions, i =>
        {
            var (arcPath, data, isRaw) = pending[i];

            int lastSlash = arcPath.LastIndexOf('/');
            string dirName = lastSlash >= 0
                ? arcPath[..lastSlash]
                : "";
            string fileName = lastSlash >= 0
                ? arcPath[(lastSlash + 1)..]
                : arcPath;

            long extractSize;
            byte[] fileData;
            long fileSize;

            if (isRaw)
            {
                fileData = data;
                fileSize = data.Length;
                if (data.Length >= 16 && Crilayla.IsCompressed(data))
                {
                    extractSize = BinaryPrimitives.ReadInt32LittleEndian(
                        data.AsSpan(8, 4)) + 256;
                }
                else
                {
                    extractSize = data.Length;
                }
            }
            else if (ShouldCompress(arcPath, data.Length))
            {
                byte[] compressed = Crilayla.Compress(data);
                if (compressed.Length < data.Length)
                {
                    fileData = compressed;
                    fileSize = compressed.Length;
                    extractSize = data.Length;
                }
                else
                {
                    fileData = data;
                    fileSize = data.Length;
                    extractSize = data.Length;
                }
            }
            else
            {
                fileData = data;
                fileSize = data.Length;
                extractSize = data.Length;
            }

            fileInfos[i] = new FileBuildInfo
            {
                DirName = dirName,
                FileName = fileName,
                FileSize = fileSize,
                ExtractSize = extractSize,
                Data = fileData
            };

            int done = Interlocked.Increment(ref completedCount);
            progress?.Invoke($"Compressing {fileName}", done, numFiles);
        });

        long enabledPacked = fileInfos.Sum(fi => fi.FileSize + fi.ExtractSize);

        var tocTable = new UtfTable { Name = "CpkTocInfo" };
        tocTable.Columns = new List<UtfColumn>
        {
            new("DirName", CriTypeId.String, CriStorageFlag.Data, ""),
            new("FileName", CriTypeId.String, CriStorageFlag.Data, ""),
            new("FileSize", CriTypeId.UInt, CriStorageFlag.Data, 0),
            new("ExtractSize", CriTypeId.UInt, CriStorageFlag.Data, 0),
            new("FileOffset", CriTypeId.ULLong, CriStorageFlag.Data, 0),
            new("ID", CriTypeId.UInt, CriStorageFlag.Data, 0),
            new("UserString", CriTypeId.String, CriStorageFlag.Constant, "<NULL>"),
        };

        for (int i = 0; i < fileInfos.Length; i++)
        {
            var fi = fileInfos[i];
            tocTable.Rows.Add(new Dictionary<string, object?>
            {
                ["DirName"] = fi.DirName,
                ["FileName"] = fi.FileName,
                ["FileSize"] = (uint)fi.FileSize,
                ["ExtractSize"] = (uint)fi.ExtractSize,
                ["FileOffset"] = 0UL,
                ["ID"] = FileIds != null && i < FileIds.Count ? (uint)FileIds[i] : (uint)(i + 1),
            });
        }

        // 生成 GTOC（需要排序后的文件路径和 ID 来构建分组链表）
        if (Mode == 3 && GtocData == null)
        {
            var sortedPaths = pending.Select(f => f.Path).ToList();
            var sortedIds = new List<long>(pending.Count);
            for (int i = 0; i < pending.Count; i++)
                sortedIds.Add(FileIds != null && i < FileIds.Count ? FileIds[i] : (long)(i + 1));
            GtocData = GenerateGtoc(numFiles, sortedPaths, sortedIds);
        }

        byte[] tocUtf = tocTable.Build();
        long tocChunkSize = 16 + tocUtf.Length;
        if (tocChunkSize % align != 0)
            tocChunkSize += align - (tocChunkSize % align);

        long gtocOffset = GtocData != null ? 0x800 + tocChunkSize : 0;
        long contentOffset;
        if (GtocData != null)
        {
            contentOffset = 0x800 + tocChunkSize + GtocData.Length;
            if (contentOffset % align != 0)
                contentOffset += align - (contentOffset % align);
        }
        else
        {
            contentOffset = 0x800 + tocChunkSize;
        }

        long fileDataOffset = contentOffset - 0x800;
        for (int i = 0; i < fileInfos.Length; i++)
        {
            tocTable.Rows[i]["FileOffset"] = (ulong)fileDataOffset;
            long padded = fileInfos[i].FileSize;
            if (padded % align != 0)
                padded += align - (padded % align);
            fileDataOffset += padded;
        }

        tocUtf = tocTable.Build();

        long contentSize = 0;
        foreach (var fi in fileInfos)
        {
            long padded = fi.FileSize;
            if (padded % align != 0)
                padded += align - (padded % align);
            contentSize += padded;
        }

        long gtocSize = GtocData?.Length ?? 0;
        long etocOffset = contentOffset + contentSize;

        var etocTable = new UtfTable { Name = "CpkEtocInfo" };
        etocTable.Columns = new List<UtfColumn>
        {
            new("UpdateDateTime", CriTypeId.ULLong, CriStorageFlag.Data, 0UL),
            new("LocalDir",       CriTypeId.String, CriStorageFlag.Data, ""),
        };

        for (int i = 0; i < fileInfos.Length; i++)
        {
            etocTable.Rows.Add(new Dictionary<string, object?>
            {
                ["UpdateDateTime"] = 0x07E5000000000000UL,
                ["LocalDir"] = fileInfos[i].DirName,
            });
        }
        etocTable.Rows.Add(new Dictionary<string, object?>
        {
            ["UpdateDateTime"] = 0UL,
            ["LocalDir"] = "",
        });

        byte[] etocUtf = etocTable.Build();
        int etocChunkSize = 16 + etocUtf.Length;

        var cpkTable = new UtfTable { Name = "CpkHeader" };
        cpkTable.Columns = new List<UtfColumn>
        {
            new("UpdateDateTime",    CriTypeId.ULLong, CriStorageFlag.Data,     1UL),
            new("FileSize",          CriTypeId.ULLong, CriStorageFlag.Constant,  0UL),
            new("ContentOffset",     CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("ContentSize",       CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("TocOffset",         CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("TocSize",           CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("TocCrc",            CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("EtocOffset",        CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("EtocSize",          CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("ItocOffset",        CriTypeId.ULLong, CriStorageFlag.Constant,  0UL),
            new("ItocSize",          CriTypeId.ULLong, CriStorageFlag.Constant,  0UL),
            new("ItocCrc",           CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("GtocOffset",        CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("GtocSize",          CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("GtocCrc",           CriTypeId.UInt,   CriStorageFlag.Constant, 0U),
            new("EnabledPackedSize", CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("EnabledDataSize",   CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("TotalDataSize",     CriTypeId.ULLong, CriStorageFlag.Constant,  0UL),
            new("Tocs",              CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("Files",             CriTypeId.UInt,   CriStorageFlag.Data,     0U),
            new("Groups",            CriTypeId.UInt,   CriStorageFlag.Data,     0U),
            new("Attrs",             CriTypeId.UInt,   CriStorageFlag.Data,     0U),
            new("TotalFiles",        CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("Directories",       CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("Updates",           CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("Version",           CriTypeId.UShort, CriStorageFlag.Data,     (ushort)7),
            new("Revision",          CriTypeId.UShort, CriStorageFlag.Data,     (ushort)1),
            new("Align",             CriTypeId.UShort, CriStorageFlag.Data,     (ushort)align),
            new("Sorted",            CriTypeId.UShort, CriStorageFlag.Data,     (ushort)1),
            new("EID",               CriTypeId.UShort, CriStorageFlag.Constant,  (ushort)0),
            new("CpkMode",           CriTypeId.UInt,   CriStorageFlag.Data,     (uint)Mode),
            new("Tvers",             CriTypeId.String, CriStorageFlag.Data,      Tvers),
            new("Comment",           CriTypeId.String, CriStorageFlag.Constant,  ""),
            new("Codec",             CriTypeId.UInt,   CriStorageFlag.Data,     0U),
            new("DpkItoc",           CriTypeId.UInt,   CriStorageFlag.Data,     0U),
        };

        cpkTable.Rows.Add(new Dictionary<string, object?>
        {
            ["UpdateDateTime"] = 1UL,
            ["ContentOffset"] = (ulong)contentOffset,
            ["ContentSize"] = (ulong)contentSize,
            ["TocOffset"] = 0x800UL,
            ["TocSize"] = (ulong)(tocUtf.Length + 16),
            ["EtocOffset"] = (ulong)etocOffset,
            ["EtocSize"] = (ulong)etocChunkSize,
            ["GtocOffset"] = (ulong)gtocOffset,
            ["GtocSize"] = (ulong)gtocSize,
            ["GtocCrc"] = 0U,
            ["EnabledPackedSize"] = (ulong)enabledPacked,
            ["EnabledDataSize"] = (ulong)enabledPacked,
            ["Files"] = (uint)fileInfos.Length,
            ["Groups"] = GtocData != null ? 2U : 0U,
            ["Attrs"] = GtocData != null ? 1U : 0U,
            ["Version"] = (ushort)7,
            ["Revision"] = (ushort)1,
            ["Align"] = (ushort)align,
            ["Sorted"] = (ushort)1,
            ["CpkMode"] = (uint)Mode,
            ["Tvers"] = Tvers,
            ["Codec"] = 0U,
            ["DpkItoc"] = 0U,
        });

        byte[] cpkUtf = cpkTable.Build();

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 4096, FileOptions.SequentialScan);

        byte[] cpkChunk = new byte[CriConstants.CHUNK_SIZE];
        CriConstants.CPK_MAGIC.CopyTo(cpkChunk, 0);
        BinaryPrimitives.WriteInt32LittleEndian(cpkChunk.AsSpan(4, 4), 0xFF);
        BinaryPrimitives.WriteInt32LittleEndian(cpkChunk.AsSpan(8, 4), cpkUtf.Length);
        BinaryPrimitives.WriteInt32LittleEndian(cpkChunk.AsSpan(12, 4), 0);
        cpkUtf.CopyTo(cpkChunk.AsSpan(16, cpkUtf.Length));
        CriConstants.CRI_FOOTER.CopyTo(
            cpkChunk.AsSpan(CriConstants.CHUNK_SIZE - CriConstants.CRI_FOOTER.Length,
                CriConstants.CRI_FOOTER.Length));
        fs.Write(cpkChunk);

        byte[] tocChunk = new byte[tocChunkSize];
        CriConstants.TOC_MAGIC.CopyTo(tocChunk, 0);
        BinaryPrimitives.WriteInt32LittleEndian(tocChunk.AsSpan(4, 4), 0xFF);
        BinaryPrimitives.WriteInt32LittleEndian(tocChunk.AsSpan(8, 4), tocUtf.Length);
        BinaryPrimitives.WriteInt32LittleEndian(tocChunk.AsSpan(12, 4), 0);
        tocUtf.CopyTo(tocChunk.AsSpan(16, tocUtf.Length));
        fs.Write(tocChunk);

        if (GtocData != null)
        {
            fs.Write(GtocData);
            long gtocEnd = 0x800 + tocChunkSize + GtocData.Length;
            if (gtocEnd % align != 0)
            {
                int pad = align - (int)(gtocEnd % align);
                fs.Write(new byte[pad]);
            }
        }

        for (int i = 0; i < fileInfos.Length; i++)
        {
            var fi = fileInfos[i];
            progress?.Invoke($"Writing {fi.FileName}", i, numFiles);

            fs.Write(fi.Data);

            int padLen = 0;
            if (fi.Data.Length % align != 0)
                padLen = align - (fi.Data.Length % align);
            if (padLen > 0)
                fs.Write(new byte[padLen]);
        }

        byte[] etocChunk = new byte[etocChunkSize];
        CriConstants.ETOC_MAGIC.CopyTo(etocChunk, 0);
        BinaryPrimitives.WriteInt32LittleEndian(etocChunk.AsSpan(4, 4), 0xFF);
        BinaryPrimitives.WriteInt32LittleEndian(etocChunk.AsSpan(8, 4), etocUtf.Length);
        BinaryPrimitives.WriteInt32LittleEndian(etocChunk.AsSpan(12, 4), 0);
        etocUtf.CopyTo(etocChunk.AsSpan(16, etocUtf.Length));
        fs.Write(etocChunk);

        progress?.Invoke("Done", numFiles, numFiles);
    }

    public byte[] GenerateGtoc(int numFiles, List<string> sortedPaths, List<long> sortedIds)
    {
        // 1. 动态收集 group：第 0 个固定为 "(none)"，其余按顶层文件夹名字母排序
        //    （原版无 GinfData 子表，游戏走线性扫描路径，group name 不被使用，
        //     但仍按顶层文件夹分组以保持结构合理性）
        var groupSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        groupSet.Add("(none)");
        for (int i = 0; i < numFiles; i++)
        {
            int slash = sortedPaths[i].IndexOf('/');
            string topFolder = slash >= 0 ? sortedPaths[i][..slash] : "";
            if (!string.IsNullOrEmpty(topFolder))
                groupSet.Add(topFolder);
        }
        string[] groupNames = groupSet.ToArray();
        // groupNames[0] = "(none)", 其余按字母序

        // 2. 将文件分配到各组（保持排序顺序，i 即为 TOC 行索引）
        var groups = new List<int>[groupNames.Length];
        for (int i = 0; i < groupNames.Length; i++)
            groups[i] = new List<int>();
        var groupIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < groupNames.Length; i++)
            groupIndexMap[groupNames[i]] = i;

        for (int i = 0; i < numFiles; i++)
        {
            int slash = sortedPaths[i].IndexOf('/');
            string topFolder = slash >= 0 ? sortedPaths[i][..slash] : "(none)";
            if (string.IsNullOrEmpty(topFolder)) topFolder = "(none)";
            int gi = groupIndexMap[topFolder];
            groups[gi].Add(i); // i = TOC 行索引
        }

        // 3. 计算 Flink 表中各组 header 行和文件行的索引
        //    布局：组0 header + 组0 files | 组1 header + 组1 files | ...
        var groupHeaderRows = new int[groupNames.Length];
        var groupFileRowLists = new List<int>[groupNames.Length];
        int flinkCursor = 0;
        for (int gi = 0; gi < groupNames.Length; gi++)
        {
            groupHeaderRows[gi] = flinkCursor++;
            groupFileRowLists[gi] = new List<int>();
            for (int fi = 0; fi < groups[gi].Count; fi++)
                groupFileRowLists[gi].Add(flinkCursor++);
        }
        int totalFlinkRows = flinkCursor;

        // 4. 构建 Glink 表（groupNames.Length + 1 行：Row 0 根节点 + 各 group）
        var glinkTable = new UtfTable { Name = "CpkGtocGlink" };
        glinkTable.Columns = new List<UtfColumn>
        {
            new("Gname", CriTypeId.String, CriStorageFlag.Data, ""),
            new("Next", CriTypeId.Int, CriStorageFlag.Data, 0),
            new("Child", CriTypeId.Int, CriStorageFlag.Data, 0),
        };
        // Row 0: 空根节点
        glinkTable.Rows.Add(new Dictionary<string, object?>
        {
            ["Gname"] = "",
            ["Next"] = 0,
            ["Child"] = -1,
        });
        // Rows 1..N: group，Next 形成链表 1→2→...→N→0
        for (int gi = 0; gi < groupNames.Length; gi++)
        {
            int glinkRow = gi + 1;
            int nextGlink = (gi < groupNames.Length - 1) ? (glinkRow + 1) : 0;
            glinkTable.Rows.Add(new Dictionary<string, object?>
            {
                ["Gname"] = groupNames[gi],
                ["Next"] = nextGlink,
                ["Child"] = groupHeaderRows[gi], // 指向 Flink header 行
            });
        }
        byte[] gdata = glinkTable.Build();

        // 5. 构建 Flink 表（结构不变，Child 用 TOC 行索引）
        var flinkTable = new UtfTable { Name = "CpkGtocFlink" };
        flinkTable.Columns = new List<UtfColumn>
        {
            new("Aindex", CriTypeId.UShort, CriStorageFlag.PerRow, (ushort)0),
            new("Next", CriTypeId.Int, CriStorageFlag.Data, 0),
            new("Child", CriTypeId.Int, CriStorageFlag.Data, 0),
            new("SortFlink", CriTypeId.Int, CriStorageFlag.Data, 0),
        };
        for (int gi = 0; gi < groupNames.Length; gi++)
        {
            int headerRow = groupHeaderRows[gi];
            var fileRows = groupFileRowLists[gi];
            int fileCount = fileRows.Count;
            int glinkRow = gi + 1;

            // Header 行：Next=-(Glink行索引)，Child=-(首个文件行索引)，SortFlink=文件数
            int firstFileRow = fileCount > 0 ? fileRows[0] : headerRow;
            flinkTable.Rows.Add(new Dictionary<string, object?>
            {
                ["Next"] = -glinkRow,
                ["Child"] = -firstFileRow,
                ["SortFlink"] = fileCount,
            });

            // 文件行：Next=下个文件行(正) 或 -(header行)回链(最后)，Child=TOC行索引
            // 注：原版 Flink 的 Child 字段是文件在 TOC 表中的行索引（按字母排序后的位置），
            // 游戏通过 |Child| 作为 TOC 行索引读取文件信息，不是文件 ID。
            // SortFlink 简化为与 Next 相同：原版无 GinfData 子表，游戏走线性扫描路径，
            // 不进入 sub_50382C 二分查找，SortFlink 字段不被读取。
            for (int fi = 0; fi < fileRows.Count; fi++)
            {
                int nextRow = (fi < fileRows.Count - 1)
                    ? fileRows[fi + 1]
                    : -headerRow;
                int tocRowIndex = groups[gi][fi]; // TOC 行索引

                flinkTable.Rows.Add(new Dictionary<string, object?>
                {
                    ["Next"] = nextRow,
                    ["Child"] = tocRowIndex,
                    ["SortFlink"] = nextRow,
                });
            }
        }
        byte[] fdata = flinkTable.Build();

        // 6. 构建 AttrData 表（1 行，与原版一致）
        var attrTable = new UtfTable { Name = "CpkGtocAttr" };
        attrTable.Columns = new List<UtfColumn>
        {
            new("Aname", CriTypeId.String, CriStorageFlag.Data, ""),
            new("Align", CriTypeId.UShort, CriStorageFlag.Data, (ushort)0),
            new("Files", CriTypeId.UInt, CriStorageFlag.Data, 0U),
            new("FileSize", CriTypeId.UInt, CriStorageFlag.Data, 0U),
        };
        attrTable.Rows.Add(new Dictionary<string, object?>
        {
            ["Aname"] = "",
            ["Align"] = (ushort)0x800,
            ["Files"] = 0U,
            ["FileSize"] = 0U,
        });
        byte[] attrData = attrTable.Build();

        // 7. 构建 GTOC 包装表
        var gtocTable = new UtfTable { Name = "CpkGtocInfo" };
        gtocTable.Columns = new List<UtfColumn>
        {
            new("Glink", CriTypeId.UInt, CriStorageFlag.Data, 0U),
            new("Flink", CriTypeId.UInt, CriStorageFlag.Data, 0U),
            new("Attr", CriTypeId.UInt, CriStorageFlag.Data, 0U),
            new("Gdata", CriTypeId.Bytes, CriStorageFlag.Data, Array.Empty<byte>()),
            new("Fdata", CriTypeId.Bytes, CriStorageFlag.Data, Array.Empty<byte>()),
            new("AttrData", CriTypeId.Bytes, CriStorageFlag.Data, Array.Empty<byte>()),
        };
        gtocTable.Rows.Add(new Dictionary<string, object?>
        {
            ["Glink"] = (uint)(groupNames.Length + 1), // 含根节点
            ["Flink"] = (uint)totalFlinkRows,
            ["Attr"] = 1U,
            ["Gdata"] = gdata,
            ["Fdata"] = fdata,
            ["AttrData"] = attrData,
        });
        byte[] gtocUtf = gtocTable.Build();

        // 8. 包装为 GTOC chunk（含 16 字节头）
        int chunkSize = 16 + gtocUtf.Length;
        byte[] gtocChunk = new byte[chunkSize];
        gtocChunk[0] = (byte)'G';
        gtocChunk[1] = (byte)'T';
        gtocChunk[2] = (byte)'O';
        gtocChunk[3] = (byte)'C';
        BinaryPrimitives.WriteInt32LittleEndian(gtocChunk.AsSpan(4, 4), 0xFF);
        BinaryPrimitives.WriteInt32LittleEndian(gtocChunk.AsSpan(8, 4), gtocUtf.Length);
        BinaryPrimitives.WriteInt32LittleEndian(gtocChunk.AsSpan(12, 4), 0);
        Array.Copy(gtocUtf, 0, gtocChunk, 16, gtocUtf.Length);

        return gtocChunk;
    }

    private static string CriSortKey(string path)
    {
        return string.Create(path.Length, path, (span, s) =>
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = char.ToLowerInvariant(s[i]);
                span[i] = c == '_' ? '~' : c;
            }
        });
    }
}