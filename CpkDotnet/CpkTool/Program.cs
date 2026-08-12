using CpkFormat;

if (args.Length == 0)
{
    PrintHelp();
    return 0;
}

var first = args[0].ToLowerInvariant();

switch (first)
{
    case "extract":
        return CmdExtract(args);
    case "pack":
        return CmdPack(args);
    case "list":
        return CmdList(args);
    case "repack":
        return CmdRepack(args);
    case "dumpgtoc":
        return CmdDumpGtoc(args);
    case "testcrilayla":
        return CmdTestCrilayla();
    case "--help":
    case "-h":
    case "help":
        PrintHelp();
        return 0;
    default:
        return HandleDragDrop(args);
}

static void PrintHelp()
{
    Console.WriteLine("CpkTool - CRIWARE CPK Archive Tool (.NET 8)");
    Console.WriteLine();
    Console.WriteLine("Drag & Drop:");
    Console.WriteLine("  Drag .cpk file(s) onto CpkTool.exe  -> Auto-extract");
    Console.WriteLine("  Drag folder(s) onto CpkTool.exe     -> Auto-pack");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  extract <input.cpk> [output_dir]          Extract all files");
    Console.WriteLine("  extract <input.cpk> -f <name> -o <out>    Extract single file");
    Console.WriteLine("  list    <input.cpk>                        List files");
    Console.WriteLine("  pack    <input_dir> <output.cpk> [-c]      Pack directory");
    Console.WriteLine("  repack  <input.cpk> <output.cpk>           Repack preserving structure");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -c          Enable CRILAYLA compression");
    Console.WriteLine("  -f          File name or ID to extract");
    Console.WriteLine("  -o          Output path");
    Console.WriteLine("  -m          CPK mode (0-3, default: 1)");
    Console.WriteLine("  -a          Alignment bytes (default: 2048)");
    Console.WriteLine("  --threads N Number of compression threads (default: CPU cores)");
    Console.WriteLine("  --preset <name>  使用预设参数包 (pad3ds = -m 3 + GTOC + 智龙迷城 Tvers)");
}

static int HandleDragDrop(string[] paths)
{
    var isDragDrop = true;
    var hadError = false;

    foreach (var path in paths)
    {
        try
        {
            if (File.Exists(path))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".cpk")
                {
                    var parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                    var name = Path.GetFileNameWithoutExtension(path);
                    var outDir = Path.Combine(parent, name + "_extracted");
                    Console.WriteLine($"Extracting: {path}");
                    Console.WriteLine($"  -> {outDir}");
                    using var arc = CpkArchive.Open(path);
                    arc.ExtractAll(outDir, (file, current, total) =>
                    {
                        Console.Write($"\r  [{current}/{total}] {file}");
                    });
                    Console.WriteLine();
                    Console.WriteLine($"  Done: {arc.Files.Count} file(s) extracted.");
                }
                else
                {
                    Console.WriteLine($"Skipping (not .cpk): {path}");
                }
            }
            else if (Directory.Exists(path))
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                var name = Path.GetFileName(path);
                var outPath = Path.Combine(parent, name + ".cpk");
                Console.WriteLine($"Packing: {path}");
                Console.WriteLine($"  -> {outPath}");

                // 拖动文件夹打包：应用 pad3ds 预设（Mode=3 + 智龙迷城 Tvers）并启用压缩
                // GTOC 由 GenerateGtoc 独立生成（动态按顶层文件夹分组），不从原版 CPK 复制
                var builder = new CpkBuilder
                {
                    Alignment = 0x800,
                    Compress = true,
                };
                CpkBuilder.ApplyPreset(builder, "pad3ds");

                builder.AddDirectory(path);
                builder.Build(outPath, (msg, current, total) =>
                {
                    if (total > 0)
                        Console.Write($"\r  [{current}/{total}] {msg}");
                });
                Console.WriteLine();
                Console.WriteLine($"  Done: {outPath}");
            }
            else
            {
                Console.WriteLine($"Not found: {path}");
                hadError = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing {path}: {ex.Message}");
            hadError = true;
        }

        Console.WriteLine();
    }

    if (isDragDrop)
    {
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey(intercept: true);
    }

    return hadError ? 1 : 0;
}

static int CmdList(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: CpkTool list <input.cpk>");
        return 1;
    }

    try
    {
        using var arc = CpkArchive.Open(args[1]);
        Console.WriteLine($"Archive: {args[1]}");
        Console.WriteLine($"Mode: {arc.Mode}");
        Console.WriteLine($"Files: {arc.Files.Count}");
        Console.WriteLine($"Alignment: {arc.Alignment} bytes");
        if (arc.Tvers != null)
            Console.WriteLine($"Version: {arc.Tvers}");
        Console.WriteLine();

        Console.WriteLine($"{"ID",-6} {"Path",-50} {"Size",-15} {"Compressed",-15} Ratio");
        Console.WriteLine(new string('-', 96));

        long totalSize = 0;
        long totalCompressed = 0;
        foreach (var entry in arc.Files)
        {
            var path = entry.FullPath;
            if (path.Length > 48)
                path = "..." + path[^45..];

            var ratio = entry.ExtractSize > 0
                ? (100.0 * entry.FileSize / entry.ExtractSize)
                : 100.0;

            var flag = entry.IsCompressed ? " [C]" : "";
            Console.WriteLine($"{entry.Id,-6} {path,-50} {FormatSize(entry.ExtractSize),-15} " +
                              $"{FormatSize(entry.FileSize),-15} {ratio,5:F1}%{flag}");

            totalSize += entry.ExtractSize;
            totalCompressed += entry.FileSize;
        }

        Console.WriteLine(new string('-', 96));
        if (totalSize > 0)
        {
            var ratio = 100.0 * totalCompressed / totalSize;
            Console.WriteLine($"Total: {FormatSize(totalSize)} -> {FormatSize(totalCompressed)} ({ratio:F1}%)");
        }
        else
        {
            Console.WriteLine($"Total: {FormatSize(totalSize)}");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

static int CmdExtract(string[] args)
{
    var input = "";
    var output = "";
    var fileFilter = "";
    var outputFile = "";

    var i = 1;
    while (i < args.Length)
    {
        switch (args[i])
        {
            case "-f":
            case "--file":
                if (++i < args.Length) fileFilter = args[i];
                break;
            case "-o":
            case "--output":
                if (++i < args.Length) outputFile = args[i];
                break;
            default:
                if (input == "") input = args[i];
                else if (output == "") output = args[i];
                break;
        }
        i++;
    }

    if (string.IsNullOrEmpty(input))
    {
        Console.Error.WriteLine("Usage: CpkTool extract <input.cpk> [output_dir] [-f name] [-o out]");
        return 1;
    }

    try
    {
        using var arc = CpkArchive.Open(input);

        if (!string.IsNullOrEmpty(fileFilter))
        {
            var entry = arc.FindFile(fileFilter)
                        ?? (long.TryParse(fileFilter, out var fid) ? arc.FindFileById(fid) : null);

            if (entry == null)
            {
                Console.Error.WriteLine($"File not found: {fileFilter}");
                return 1;
            }

            var data = arc.ReadFile(entry);
            var outPath = string.IsNullOrEmpty(outputFile) ? entry.FileName : outputFile;
            File.WriteAllBytes(outPath, data);
            Console.WriteLine($"Extracted: {entry.FullPath} ({FormatSize(data.Length)}) -> {outPath}");
        }
        else
        {
            var outDir = string.IsNullOrEmpty(output)
                ? Path.GetFileNameWithoutExtension(input) + "_extracted"
                : output;

            arc.ExtractAll(outDir, (file, current, total) =>
            {
                Console.Write($"\r[{current}/{total}] {file}");
            });
            Console.WriteLine();
            Console.WriteLine($"Extracted {arc.Files.Count} file(s) to: {outDir}");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

static int CmdPack(string[] args)
{
    var input = "";
    var output = "";
    var compress = false;
    int? mode = null;
    var align = 0x800;
    string? gtocSource = null;
    string? idsSource = null;
    string? tvers = null;
    string? preset = null;
    int threads = Environment.ProcessorCount;

    var i = 1;
    while (i < args.Length)
    {
        switch (args[i])
        {
            case "-c":
            case "--compress":
                compress = true;
                break;
            case "--preset":
                if (++i < args.Length) preset = args[i];
                break;
            case "-m":
            case "--mode":
                if (++i < args.Length && int.TryParse(args[i], out var m)) mode = m;
                break;
            case "-a":
            case "--align":
                if (++i < args.Length && int.TryParse(args[i], out var a)) align = a;
                break;
            case "-g":
            case "--gtoc":
                if (++i < args.Length) gtocSource = args[i];
                break;
            case "-i":
            case "--ids":
                if (++i < args.Length) idsSource = args[i];
                break;
            case "-t":
            case "--tvers":
                if (++i < args.Length) tvers = args[i];
                break;
            case "--threads":
                if (++i < args.Length && int.TryParse(args[i], out var th)) threads = Math.Max(1, th);
                break;
            default:
                if (input == "") input = args[i];
                else if (output == "") output = args[i];
                break;
        }
        i++;
    }

    if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output))
    {
        Console.Error.WriteLine("Usage: CpkTool pack <input_dir> <output.cpk> [-c] [-m mode] [-a align]");
        return 1;
    }

    try
    {
        var builder = new CpkBuilder
        {
            Alignment = align,
            Compress = compress,
            MaxThreads = threads
        };

        if (!string.IsNullOrEmpty(preset))
            CpkBuilder.ApplyPreset(builder, preset);

        if (mode.HasValue)
            builder.Mode = mode.Value;

        if (!string.IsNullOrEmpty(gtocSource))
        {
            if (gtocSource.EndsWith(".cpk", StringComparison.OrdinalIgnoreCase))
                builder.GtocData = CpkBuilder.LoadGtocFromCpk(gtocSource);
            else
                builder.LoadGtocFromFile(gtocSource);
        }

        if (!string.IsNullOrEmpty(idsSource))
            builder.FileIds = CpkBuilder.LoadIdsFromCpk(idsSource);

        if (!string.IsNullOrEmpty(tvers))
            builder.Tvers = tvers;

        builder.AddDirectory(input);
        builder.Build(output, (msg, current, total) =>
        {
            if (total > 0)
                Console.Write($"\r[{current}/{total}] {msg}");
        });
        Console.WriteLine();
        Console.WriteLine($"Created: {output}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

static int CmdRepack(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: CpkTool repack <input.cpk> <output.cpk>");
        return 1;
    }

    try
    {
        using var arc = CpkArchive.Open(args[1]);
        var builder = CpkBuilder.FromArchive(arc);
        builder.Build(args[2]);
        Console.WriteLine($"Repacked: {args[2]}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

static int CmdDumpGtoc(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: CpkTool dumpgtoc <input.cpk>");
        return 1;
    }

    try
    {
        using var arc = CpkArchive.Open(args[1]);
        Console.WriteLine($"GtocOffset: 0x{arc.GtocOffset:X}, GtocSize: {arc.GtocSize}");
        byte[]? gtocData = arc.ReadGtoc();
        if (gtocData == null)
        {
            Console.Error.WriteLine("No GTOC found");
            return 1;
        }
        Console.WriteLine($"GTOC total bytes: {gtocData.Length}");

        byte[] gtocUtf = gtocData[16..];
        var gtocTable = UtfTable.Parse(gtocUtf);
        Console.WriteLine($"\n=== GTOC Table: {gtocTable.Name} ===");
        Console.WriteLine($"Columns: {gtocTable.Columns.Count}");
        foreach (var col in gtocTable.Columns)
            Console.WriteLine($"  {col.Name} ({col.Type}, {col.Storage})");
        Console.WriteLine($"Rows: {gtocTable.Rows.Count}");
        foreach (var row in gtocTable.Rows)
        {
            foreach (var col in gtocTable.Columns)
            {
                var val = row[col.Name];
                if (val is byte[] bytes)
                    Console.WriteLine($"  {col.Name}: byte[{bytes.Length}]");
                else
                    Console.WriteLine($"  {col.Name}: {val}");
            }
        }

        var row0 = gtocTable.Rows[0];
        void DumpSub(string name)
        {
            if (!row0.TryGetValue(name, out var val) || val is not byte[] bytes) return;
            Console.WriteLine($"\n=== Sub-table: {name} ({bytes.Length} bytes) ===");
            var table = UtfTable.Parse(bytes);
            Console.WriteLine($"Table name: {table.Name}, Columns: {table.Columns.Count}, Rows: {table.Rows.Count}");
            foreach (var col in table.Columns)
                Console.WriteLine($"  {col.Name} ({col.Type}, {col.Storage})");
            for (int i = 0; i < Math.Min(table.Rows.Count, 30); i++)
            {
                Console.Write($"  Row {i}: ");
                foreach (var col in table.Columns)
                {
                    var v = table.Rows[i][col.Name];
                    Console.Write($"{col.Name}={(v is byte[] ? "[" + ((byte[])v).Length + "B]" : v)} ");
                }
                Console.WriteLine();
            }
            if (table.Rows.Count > 30)
                Console.WriteLine($"  ... ({table.Rows.Count - 30} more rows)");

            // Dump raw row bytes for Flink table
            if (name == "Fdata")
            {
                // Parse UTF header to find row data offset
                if (bytes.Length >= 32 && bytes[0..4].SequenceEqual(System.Text.Encoding.ASCII.GetBytes("@UTF")))
                {
                    uint rowsOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
                    ushort rowLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[26..]);
                    ushort numColumns = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[24..]);
                    Console.WriteLine($"  === UTF header: numColumns={numColumns}, rowLength={rowLength}, rowsOffset={rowsOffset} ===");

                    // Dump specific boundary rows (group transitions)
                    int[] boundaries = { 0, 1, 2717, 2718, 2719, 2720, 2721, 2722, 2723, 2739, 2740, 2741, 2848, 2849, 2850, 2876, 2877, 2878, 2879, 2880, 2883, 2884, 2885, 2928, 2929 };
                    Console.WriteLine($"  === Boundary rows (group transitions) ===");
                    foreach (int i in boundaries)
                    {
                        if (i >= table.Rows.Count) continue;
                        var r = table.Rows[i];
                        Console.WriteLine($"  Row {i}: Aindex={r["Aindex"]} Next={r["Next"]} Child={r["Child"]} SortFlink={r["SortFlink"]}");
                    }
                }
            }
        }

        DumpSub("Gdata");
        DumpSub("Fdata");
        DumpSub("AttrData");
        DumpSub("GinfData");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Console.Error.WriteLine(ex.StackTrace);
        return 1;
    }
}

static string FormatSize(long size)
{
    return size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024.0:F2} KB",
        < 1024L * 1024 * 1024 => $"{size / (1024.0 * 1024):F2} MB",
        _ => $"{size / (1024.0 * 1024 * 1024):F2} GB"
    };
}

static int CmdTestCrilayla()
{
    Console.WriteLine("=== CRILAYLA Round-Trip Test ===\n");

    var allPassed = true;

    var tests = new List<(string name, Func<byte[]> generator)>
    {
        ("zeros 512", () => new byte[512]),
        ("zeros 4096", () => new byte[4096]),
        ("incrementing 512", () => Enumerable.Range(0, 512).Select(i => (byte)(i & 0xFF)).ToArray()),
        ("incrementing 4096", () => Enumerable.Range(0, 4096).Select(i => (byte)(i & 0xFF)).ToArray()),
        ("repeating-A 1024", () => Enumerable.Repeat((byte)0x41, 1024).ToArray()),
        ("repeating-A 8192", () => Enumerable.Repeat((byte)0x41, 8192).ToArray()),
        ("pattern-AB 2048", () => Enumerable.Range(0, 2048).Select(i => (byte)((i & 1) == 0 ? 0xAB : 0xCD)).ToArray()),
        ("text-like 2048", () =>
        {
            var text = "Hello World! This is a test of CRILAYLA compression. ";
            var data = new byte[2048];
            for (var i = 0; i < 2048; i++)
                data[i] = (byte)text[i % text.Length];
            return data;
        }),
    };

    var seed = 42;
    var rng = new Random(seed);
    var sizes = new[] { 256, 300, 512, 1000, 1024, 2000, 2048, 4096, 5000, 8192, 10000, 16384 };
    foreach (var size in sizes)
        tests.Add(($"random {size}", () =>
        {
            var d = new byte[size];
            rng.NextBytes(d);
            return d;
        }));

    foreach (var (name, generator) in tests)
    {
        var original = generator();

        byte[] compressed;
        try
        {
            compressed = Crilayla.Compress(original);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL [{name}]: Compress threw: {ex.Message}");
            allPassed = false;
            continue;
        }

        if (!Crilayla.IsCompressed(compressed))
            {
                Console.WriteLine($"FAIL [{name}]: IsCompressed returned false");
                allPassed = false;
                continue;
            }

            var compSizeRead = BitConverter.ToUInt32(compressed.AsSpan(12, 4));
            var uncompSizeRead = BitConverter.ToUInt32(compressed.AsSpan(8, 4));
            Console.WriteLine($"     [{name,-22}] uncompSize={uncompSizeRead} compSize={compSizeRead} total={compressed.Length}");

            if (name.Contains("4096"))
            {
                var cd = compressed.AsSpan(16, (int)compSizeRead);
                var first32 = Convert.ToHexString(cd[..Math.Min(32, cd.Length)]);
                var last32 = Convert.ToHexString(cd[^Math.Min(32, cd.Length)..]);
                Console.WriteLine($"     Compressed first 32: {first32}");
                Console.WriteLine($"     Compressed last 32:  {last32}");
            }

        byte[] decompressed;
        try
        {
            decompressed = Crilayla.Decompress(compressed);
        }
        catch (Exception ex)
            {
                Console.WriteLine($"FAIL [{name}]: Decompress threw: {ex.GetType().Name}: {ex.Message}");
                if (ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
                    Console.WriteLine($"  {ex.StackTrace}");
                allPassed = false;
                continue;
            }

        if (decompressed.Length != original.Length)
        {
            Console.WriteLine($"FAIL [{name}]: Length mismatch: {decompressed.Length} vs {original.Length}");
            allPassed = false;
            continue;
        }

        var match = true;
        var firstMismatch = -1;
        for (var i = 0; i < original.Length; i++)
        {
            if (original[i] != decompressed[i])
            {
                match = false;
                firstMismatch = i;
                break;
            }
        }

        if (match)
        {
            var ratio = 100.0 * compressed.Length / original.Length;
            Console.WriteLine($"OK   [{name,-22}] comp={compressed.Length,5} ({ratio,5:F1}%)");
        }
        else
        {
            Console.WriteLine($"FAIL [{name}]: First mismatch at byte {firstMismatch}: orig=0x{original[firstMismatch]:X2} decomp=0x{decompressed[firstMismatch]:X2}");
            allPassed = false;
        }
    }

    if (allPassed)
    {
        Console.WriteLine($"\nAll {tests.Count} tests PASSED!");
    }
    else
    {
        Console.WriteLine("\nSome tests FAILED!");
    }

    Console.WriteLine("\n--- Cross-Compatibility Check ---");
    var crossDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var pyCompFile = Path.Combine(crossDir, "_cross_test_py_compressed.bin");
    var origFile = Path.Combine(crossDir, "_cross_test_original.bin");

    if (File.Exists(pyCompFile) && File.Exists(origFile))
    {
        var pyCompressed = File.ReadAllBytes(pyCompFile);
        var origData = File.ReadAllBytes(origFile);

        try
        {
            var decoded = Crilayla.Decompress(pyCompressed);
            if (decoded.SequenceEqual(origData))
                Console.WriteLine("Python-compressed -> C#-decompress: OK");
            else
            {
                Console.WriteLine("Python-compressed -> C#-decompress: FAIL (data mismatch)");
                allPassed = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Python-compressed -> C#-decompress: FAIL ({ex.Message})");
            allPassed = false;
        }

        var csCompressed = Crilayla.Compress(origData);
        File.WriteAllBytes(Path.Combine(crossDir, "_cross_test_cs_compressed.bin"), csCompressed);
        Console.WriteLine("C#-compressed data saved for Python cross-check");
    }
    else
    {
        Console.WriteLine("Cross-check files not found (run Python cross-gen first)");
    }

    return allPassed ? 0 : 1;
}