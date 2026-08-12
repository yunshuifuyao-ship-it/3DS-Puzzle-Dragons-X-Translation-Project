namespace CpkFormat;

public sealed class CpkFileEntry
{
    public string DirName { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileOffset { get; set; }
    public long FileSize { get; set; }
    public long ExtractSize { get; set; }
    public long Id { get; set; }
    public string UserString { get; set; } = "<NULL>";

    public bool IsCompressed => ExtractSize > FileSize;

    public string FullPath => string.IsNullOrEmpty(DirName)
        ? FileName
        : DirName.Replace('\\', '/') + "/" + FileName;
}