using System.Buffers.Binary;
using TimgTool;

if (args.Length == 0)
{
    PrintHelp();
    return 0;
}

var first = args[0];

// 拖放模式：首参数不是已知子命令（且不是 --help/-h）
if (first != "timg2png" && first != "png2timg" && first != "info" && first != "diag" &&
    first != "--help" && first != "-h" && first != "help")
{
    return HandleDragDrop(args);
}

switch (first)
{
    case "timg2png":
        return CmdTimg2Png(args);
    case "png2timg":
        return CmdPng2Timg(args);
    case "info":
        return CmdInfo(args);
    case "diag":
        return CmdDiag(args);
    case "--help":
    case "-h":
    case "help":
        PrintHelp();
        return 0;
    default:
        Console.Error.WriteLine($"Unknown command: {first}");
        PrintHelp();
        return 1;
}

static void PrintHelp()
{
    Console.WriteLine("TimgTool - 3DS TIMG Image Tool (.NET 8)");
    Console.WriteLine();
    Console.WriteLine("Drag & Drop:");
    Console.WriteLine("  Drag .timg file(s)/folder(s) onto TimgTool.exe  -> Auto-export to _<TFM>.png");
    Console.WriteLine("  Drag .png file(s)/folder(s) onto TimgTool.exe   -> Auto-import to .timg");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  timg2png <input.timg|dir> [-o output.png|dir] [-r]    Export TIMG to PNG");
    Console.WriteLine("  png2timg <input.png|dir>  [-o output.timg|dir] [-r] [--format <name>]    Import PNG to TIMG");
    Console.WriteLine("  info     <input.timg>                                Show TIMG metadata");
    Console.WriteLine("  --help | -h | help                                   Show this help");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -r               Recurse directories");
    Console.WriteLine("  -o <path>        Output path (file or directory)");
    Console.WriteLine("  --format <name>  Force pixel format (Etc1_a4/Rgba8/Rgba4/Etc1/Rgb565/La4/Rgb8/La8/L4)");
    Console.WriteLine();
    Console.WriteLine("PNG filename convention:");
    Console.WriteLine("  Export: foo.timg (Tfm=Etc1_a4) -> foo_Etc1_a4.png");
    Console.WriteLine("  Import: foo_Etc1_a4.png -> foo.timg (Tfm=Etc1_a4, restored from suffix)");
    Console.WriteLine("  Priority: --format > filename suffix > auto-detect");
}

static int CmdTimg2Png(string[] args)
{
    string input = "";
    string? output = null;
    bool recurse = false;
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "-r") recurse = true;
        else if (args[i] == "-o" && i + 1 < args.Length) { output = args[++i]; }
        else if (!args[i].StartsWith("-")) input = args[i];
    }
    if (string.IsNullOrEmpty(input))
    {
        Console.Error.WriteLine("Error: input path required");
        return 1;
    }

    if (Directory.Exists(input))
    {
        return ProcessBatch(input, output, recurse, isTimgToPng: true);
    }
    else if (File.Exists(input))
    {
        try
        {
            // 若 -o 指定目录，则自动构造输出文件名
            string? outFile = output;
            if (!string.IsNullOrEmpty(output) && Directory.Exists(output))
            {
                var tfm = GetTfmFormatName(input);
                var baseName = Path.GetFileNameWithoutExtension(input);
                outFile = Path.Combine(output, tfm != null ? $"{baseName}_{tfm}.png" : $"{baseName}.png");
            }
            var outPath = TimgToPng.Convert(input, outFile);
            var fmt = GetTfmFormat(input);
            Console.WriteLine($"{input} -> {outPath} [{fmt}]");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERR] {input}: {ex}");
            return 1;
        }
    }
    else
    {
        Console.Error.WriteLine($"Error: input not found: {input}");
        return 1;
    }
}

static int CmdPng2Timg(string[] args)
{
    string input = "";
    string? output = null;
    string? format = null;
    bool recurse = false;
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "-r") recurse = true;
        else if (args[i] == "-o" && i + 1 < args.Length) { output = args[++i]; }
        else if (args[i] == "--format" && i + 1 < args.Length) { format = args[++i]; }
        else if (!args[i].StartsWith("-")) input = args[i];
    }
    if (string.IsNullOrEmpty(input))
    {
        Console.Error.WriteLine("Error: input path required");
        return 1;
    }

    if (Directory.Exists(input))
    {
        return ProcessBatch(input, output, recurse, isTimgToPng: false, forcedFormat: format);
    }
    else if (File.Exists(input))
    {
        try
        {
            // 若 -o 指定目录，则自动构造输出文件名
            string? outFile = output;
            if (!string.IsNullOrEmpty(output) && Directory.Exists(output))
            {
                var baseName = Path.GetFileNameWithoutExtension(input);
                baseName = PngToTimg.StripTfmSuffix(baseName);
                outFile = Path.Combine(output, $"{baseName}.timg");
            }
            var opts = new PngToTimg.Options { Format = format, OutputTimgPath = outFile };
            var outPath = PngToTimg.Convert(input, opts);
            Console.WriteLine($"{input} -> {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERR] {input}: {ex}");
            return 1;
        }
    }
    else
    {
        Console.Error.WriteLine($"Error: input not found: {input}");
        return 1;
    }
}

static int CmdInfo(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Error: input path required");
        return 1;
    }
    var path = args[1];
    try
    {
        var data = File.ReadAllBytes(path);
        var timg = TimgFile.Parse(data);
        var h = timg.Header;
        Console.WriteLine($"File: {path}");
        Console.WriteLine($"Magic: TIMG (0x{h.Magic:X8})");
        Console.WriteLine($"Version: {h.Version}");
        var actualSize = new FileInfo(path).Length;
        Console.WriteLine($"FileSize (header): {h.FileSize} (0x{h.FileSize:X8})  Actual: {actualSize}  Match: {h.FileSize == (uint)actualSize}");
        Console.WriteLine($"HeaderSize: 0x{h.HeaderSize:X}");
        Console.WriteLine($"Filename: {h.FilenameAsString}");
        Console.WriteLine($"PixelFormat (header): {h.PixelFormat}");
        Console.WriteLine($"Width x Height: {h.Width} x {h.Height}");
        Console.WriteLine($"Crop  x CropH : {h.CropWidth} x {h.CropHeight}");
        var tfm = timg.GetTfmName();
        Console.WriteLine($"Tfm (chunk): {tfm}");
        if (tfm != null)
        {
            var fmt = PixelFormats.ByTfmName(tfm);
            if (fmt != null)
                Console.WriteLine($"  -> PICA200 ID={fmt.Pica200Id}, BPP={fmt.Bpp}");
        }
        Console.WriteLine($"Chunks ({timg.Chunks.Count}):");
        foreach (var c in timg.Chunks)
            Console.WriteLine($"  {c.Signature}  size={c.Size}  dataLen={c.Data.Length}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[ERR] {path}: {ex.Message}");
        return 1;
    }
}

static int ProcessBatch(string inputDir, string? outputDir, bool recurse, bool isTimgToPng, string? forcedFormat = null)
{
    var searchOpt = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    var ext = isTimgToPng ? "*.timg" : "*.png";
    var files = Directory.EnumerateFiles(inputDir, ext, searchOpt).ToList();
    int ok = 0, err = 0;
    foreach (var f in files)
    {
        try
        {
            string? outFile = null;
            if (!string.IsNullOrEmpty(outputDir))
            {
                // recurse 模式下镜像相对子目录，避免同名文件冲突
                string relSubDir = "";
                if (recurse)
                {
                    var fullF = Path.GetFullPath(f);
                    var fullInput = Path.GetFullPath(inputDir);
                    var rel = Path.GetRelativePath(fullInput, fullF);
                    var relDir = Path.GetDirectoryName(rel);
                    if (!string.IsNullOrEmpty(relDir) && relDir != ".")
                        relSubDir = relDir;
                }
                var outDir = string.IsNullOrEmpty(relSubDir) ? outputDir : Path.Combine(outputDir, relSubDir);
                Directory.CreateDirectory(outDir);
                if (isTimgToPng)
                {
                    // 需要先读 TFM 才能构造 <base>_<TFM>.png 文件名
                    var data = File.ReadAllBytes(f);
                    var timg = TimgFile.Parse(data);
                    var tfm = timg.GetTfmName() ?? "unknown";
                    var baseName = Path.GetFileNameWithoutExtension(f);
                    outFile = Path.Combine(outDir, $"{baseName}_{tfm}.png");
                }
                else
                {
                    // png2timg：去除 _<TFM> 后缀后作为输出文件名
                    var baseName = Path.GetFileNameWithoutExtension(f);
                    baseName = PngToTimg.StripTfmSuffix(baseName);
                    outFile = Path.Combine(outDir, baseName + ".timg");
                }
            }

            if (isTimgToPng)
            {
                var outPath = TimgToPng.Convert(f, outFile);
                ok++;
            }
            else
            {
                var opts = new PngToTimg.Options { Format = forcedFormat, OutputTimgPath = outFile };
                var outPath = PngToTimg.Convert(f, opts);
                ok++;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERR] {f}: {ex.Message}");
            err++;
        }
    }
    Console.WriteLine($"Done: {ok} OK, {err} ERR");
    return err > 0 ? 1 : 0;
}

static string GetTfmFormat(string timgPath)
{
    try
    {
        var data = File.ReadAllBytes(timgPath);
        var timg = TimgFile.Parse(data);
        var tfm = timg.GetTfmName();
        if (tfm == null) return "unknown";
        var h = timg.Header;
        return $"{tfm} {h.Width}x{h.Height} crop={h.CropWidth}x{h.CropHeight}";
    }
    catch { return "?"; }
}

static string? GetTfmFormatName(string timgPath)
{
    try
    {
        var data = File.ReadAllBytes(timgPath);
        var timg = TimgFile.Parse(data);
        return timg.GetTfmName();
    }
    catch { return null; }
}

static int CmdDiag(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: diag <input.timg>");
        return 1;
    }
    var path = args[1];
    var data = File.ReadAllBytes(path);
    var timg = TimgFile.Parse(data);
    var tfmName = timg.GetTfmName() ?? "?";
    var fmt = PixelFormats.ByTfmName(tfmName);
    if (fmt == null) { Console.Error.WriteLine($"Unknown format: {tfmName}"); return 1; }

    var txd = timg.FindChunk("nw4c_txd");
    if (txd == null) { Console.Error.WriteLine("No nw4c_txd chunk"); return 1; }

    int pw = timg.Header.Width, ph = timg.Header.Height;
    Console.WriteLine($"File: {path}");
    Console.WriteLine($"Format: {tfmName}  Kind={fmt.Kind}  IsBlockCompressed={fmt.IsBlockCompressed}");
    Console.WriteLine($"Padded: {pw}x{ph}  Crop: {timg.Header.CropWidth}x{timg.Header.CropHeight}");
    Console.WriteLine($"txd data length: {txd.Data.Length}");

    // --- Full decode + stats (works for all formats) ---
    Console.WriteLine("\n=== Full PixelCodec.Decode ===");
    var linearRgba = PixelCodec.Decode(fmt, txd.Data, pw, ph);
    Console.WriteLine($"Output length: {linearRgba.Length} (expected {pw * ph * 4})");

    int maxR = 0, maxG = 0, maxB = 0, maxA = 0;
    int maxR_opaque = 0, maxG_opaque = 0, maxB_opaque = 0;
    int opaqueCount = 0;
    for (int i = 0; i < linearRgba.Length; i += 4)
    {
        byte r = linearRgba[i], g = linearRgba[i + 1], b = linearRgba[i + 2], a = linearRgba[i + 3];
        if (r > maxR) maxR = r;
        if (g > maxG) maxG = g;
        if (b > maxB) maxB = b;
        if (a > maxA) maxA = a;
        if (a > 0)
        {
            opaqueCount++;
            if (r > maxR_opaque) maxR_opaque = r;
            if (g > maxG_opaque) maxG_opaque = g;
            if (b > maxB_opaque) maxB_opaque = b;
        }
    }
    Console.WriteLine($"ALL:  maxR={maxR} maxG={maxG} maxB={maxB} maxA={maxA}");
    Console.WriteLine($"OPAQUE (A>0): count={opaqueCount} maxR={maxR_opaque} maxG={maxG_opaque} maxB={maxB_opaque}");

    // First 8x8 tile in linear output
    Console.WriteLine("\n=== First 8x8 tile in linear output (image coords 0,0 to 7,7) ===");
    for (int y = 0; y < Math.Min(8, ph); y++)
    {
        for (int x = 0; x < Math.Min(8, pw); x++)
        {
            int idx = (y * pw + x) * 4;
            Console.Write($"({linearRgba[idx],3},{linearRgba[idx + 1],3},{linearRgba[idx + 2],3},{linearRgba[idx + 3],3}) ");
        }
        Console.WriteLine();
    }

    // First 16 bytes of txd data (for manual inspection)
    Console.WriteLine("\n=== First 16 bytes of txd data ===");
    for (int i = 0; i < Math.Min(16, txd.Data.Length); i++)
        Console.Write($"{txd.Data[i]:X2} ");
    Console.WriteLine();

    if (!fmt.IsBlockCompressed)
    {
        Console.WriteLine("\n(Non-block-compressed format, skipping ETC1 block diagnostics.)");
        return 0;
    }

    // --- ETC1 block diagnostics ---
    bool useAlpha = fmt.Kind == PixelFormatKind.Etc1_a4;
    int blockBytes = useAlpha ? 16 : 8;
    int blocksX = pw / 4, blocksY = ph / 4;
    int blockCount = blocksX * blocksY;
    Console.WriteLine($"\nBlock: {blockBytes}B, {blocksX}x{blocksY}={blockCount} blocks, expected data={blockCount * blockBytes}B");

    // --- Block 0 raw bytes ---
    Console.WriteLine("\n=== Block 0 raw bytes ===");
    for (int i = 0; i < Math.Min(blockBytes, 16); i++)
        Console.Write($"{txd.Data[i]:X2} ");
    Console.WriteLine();

    // --- Block 0 manual decode ---
    Console.WriteLine("\n=== Block 0 manual decode ===");
    int[]? alpha0 = null;
    int aOff = 0;
    if (useAlpha)
    {
        ulong alphaData = BinaryPrimitives.ReadUInt64LittleEndian(txd.Data.AsSpan(0));
        alpha0 = Etc1Codec.DecodeAlpha(alphaData);
        aOff = 8;
    }
    ulong colorData0 = BinaryPrimitives.ReadUInt64LittleEndian(txd.Data.AsSpan(aOff));
    var block0 = Etc1Block.FromUInt64(colorData0);
    Console.WriteLine($"  Lsb=0x{block0.Lsb:X4} Msb=0x{block0.Msb:X4} Flags=0x{block0.Flags:X2} B=0x{block0.B:X2} G=0x{block0.G:X2} R=0x{block0.R:X2}");
    Console.WriteLine($"  DiffBit={block0.DiffBit} FlipBit={block0.FlipBit} Table0={block0.Table0} Table1={block0.Table1} ColorDepth={block0.ColorDepth}");
    var c0 = block0.Color0;
    var c1 = block0.Color1;
    Console.WriteLine($"  Color0(raw)=({c0.R},{c0.G},{c0.B}) Color1(raw)=({c1.R},{c1.G},{c1.B})");
    Console.WriteLine($"  Color0(scaled)=({Etc1Codec.Scale(c0.R, block0.ColorDepth)},{Etc1Codec.Scale(c0.G, block0.ColorDepth)},{Etc1Codec.Scale(c0.B, block0.ColorDepth)})");
    Console.WriteLine($"  Color1(scaled)=({Etc1Codec.Scale(c1.R, block0.ColorDepth)},{Etc1Codec.Scale(c1.G, block0.ColorDepth)},{Etc1Codec.Scale(c1.B, block0.ColorDepth)})");

    var pixels0 = Etc1Codec.DecodeBlock(block0, alpha0, false);
    for (int i = 0; i < 16; i++)
        Console.WriteLine($"  pixel[{i,2}] = R={pixels0[i].R,3} G={pixels0[i].G,3} B={pixels0[i].B,3} A={pixels0[i].A,3}");

    // Tiled array inspection (before TileMorton)
    Console.WriteLine("\n=== Tiled array (first 64 pixels = first 8x8 tile) ===");
    bool useZOrder = false;
    var tiled = new byte[pw * ph * 4];
    int idx2 = 0;
    for (int b = 0; b < blockCount; b++)
    {
        int off = b * blockBytes;
        if (off + blockBytes > txd.Data.Length) break;
        int[]? alpha = null;
        int ao = off;
        if (useAlpha)
        {
            ulong ad = BinaryPrimitives.ReadUInt64LittleEndian(txd.Data.AsSpan(ao));
            alpha = Etc1Codec.DecodeAlpha(ad);
            ao += 8;
        }
        ulong cd = BinaryPrimitives.ReadUInt64LittleEndian(txd.Data.AsSpan(ao));
        var blk = Etc1Block.FromUInt64(cd);
        var px = Etc1Codec.DecodeBlock(blk, alpha, useZOrder);
        for (int i = 0; i < 16; i++)
        {
            tiled[idx2 * 4 + 0] = px[i].R;
            tiled[idx2 * 4 + 1] = px[i].G;
            tiled[idx2 * 4 + 2] = px[i].B;
            tiled[idx2 * 4 + 3] = px[i].A;
            idx2++;
        }
    }
    for (int y = 0; y < 8; y++)
    {
        for (int x = 0; x < 8; x++)
        {
            int i = y * 8 + x;
            Console.Write($"({tiled[i * 4],3},{tiled[i * 4 + 1],3},{tiled[i * 4 + 2],3},{tiled[i * 4 + 3],3}) ");
        }
        Console.WriteLine();
    }

    int maxR_tiled = 0;
    for (int i = 0; i < tiled.Length; i += 4)
        if (tiled[i] > maxR_tiled) maxR_tiled = tiled[i];
    Console.WriteLine($"Tiled maxR={maxR_tiled}");

    return 0;
}

static int HandleDragDrop(string[] paths)
{
    int ok = 0, err = 0;
    foreach (var path in paths)
    {
        try
        {
            if (File.Exists(path))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".timg")
                {
                    var outPath = TimgToPng.Convert(path);
                    var fmt = GetTfmFormat(path);
                    Console.WriteLine($"{path} -> {outPath} [{fmt}] [OK]");
                    ok++;
                }
                else if (ext == ".png")
                {
                    var outPath = PngToTimg.Convert(path, new PngToTimg.Options());
                    Console.WriteLine($"{path} -> {outPath} [OK]");
                    ok++;
                }
                else
                {
                    Console.WriteLine($"Skipping (not .timg/.png): {path}");
                }
            }
            else if (Directory.Exists(path))
            {
                var timgs = Directory.EnumerateFiles(path, "*.timg", SearchOption.AllDirectories).ToList();
                var pngs = Directory.EnumerateFiles(path, "*.png", SearchOption.AllDirectories).ToList();
                if (timgs.Count == 0 && pngs.Count == 0)
                {
                    Console.WriteLine($"Skipping (no .timg/.png): {path}");
                    continue;
                }
                foreach (var f in timgs)
                {
                    try
                    {
                        var outPath = TimgToPng.Convert(f);
                        var fmt = GetTfmFormat(f);
                        Console.WriteLine($"{f} -> {outPath} [{fmt}] [OK]");
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[ERR] {f}: {ex.Message}");
                        err++;
                    }
                }
                foreach (var f in pngs)
                {
                    try
                    {
                        var outPath = PngToTimg.Convert(f, new PngToTimg.Options());
                        Console.WriteLine($"{f} -> {outPath} [OK]");
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[ERR] {f}: {ex.Message}");
                        err++;
                    }
                }
            }
            else
            {
                Console.WriteLine($"Skipping (not found): {path}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERR] {path}: {ex.Message}");
            err++;
        }
    }
    Console.WriteLine($"Done: {ok} OK, {err} ERR");
    Console.WriteLine("Press any key to exit...");
    try { if (!Console.IsInputRedirected) Console.ReadKey(); } catch { }
    return err > 0 ? 1 : 0;
}
