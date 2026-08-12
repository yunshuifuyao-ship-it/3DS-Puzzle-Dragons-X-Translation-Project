namespace CpkFormat;

public enum CriTypeId : byte
{
    UChar = 0,
    Char = 1,
    UShort = 2,
    Short = 3,
    UInt = 4,
    Int = 5,
    ULLong = 6,
    LLong = 7,
    Float = 8,
    Double = 9,
    String = 10,
    Bytes = 11
}

public enum CriStorageFlag : byte
{
    Constant = 0x10,
    PerRow = 0x30,
    Data = 0x50
}

public static class CriConstants
{
    public static readonly byte[] CPK_MAGIC = { (byte)'C', (byte)'P', (byte)'K', (byte)' ' };
    public static readonly byte[] UTF_MAGIC = { (byte)'@', (byte)'U', (byte)'T', (byte)'F' };
    public static readonly byte[] TOC_MAGIC = { (byte)'T', (byte)'O', (byte)'C', (byte)' ' };
    public static readonly byte[] ETOC_MAGIC = { (byte)'E', (byte)'T', (byte)'O', (byte)'C' };
    public static readonly byte[] CRILAYLA_MAGIC = { (byte)'C', (byte)'R', (byte)'I', (byte)'L', (byte)'A', (byte)'Y', (byte)'L', (byte)'A' };
    public static readonly byte[] ENCRYPTED_UTF_MAGIC = { 0x1F, 0x9E, 0xF3, 0xF5 };
    public const int CHUNK_SIZE = 0x800;
    public static readonly byte[] CRI_FOOTER = { (byte)'(', (byte)'c', (byte)')', (byte)'C', (byte)'R', (byte)'I' };

    public const uint UTF_ENCRYPT_SEED = 0x655F;
    public const uint UTF_ENCRYPT_MULTIPLIER = 0x4115;
}