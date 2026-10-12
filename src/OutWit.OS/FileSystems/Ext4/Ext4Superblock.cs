using System;
using System.Buffers.Binary;
using System.Text;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// An ext2, ext3 or ext4 superblock (the kernel's Documentation/filesystems/ext4, "Super Block"), 1024 bytes at byte
/// 1024 of the volume, checked whole before anything else of the volume is read: its magic, its checksum when
/// metadata_csum is on, its geometry, and the incompatible features the reader serves.
/// </summary>
internal sealed class Ext4Superblock
{
    #region Constants

    /// <summary>The superblock's offset in the volume.</summary>
    public const int OFFSET = 1024;

    /// <summary>The superblock's bytes.</summary>
    public const int SIZE = 1024;

    /// <summary>The root directory's inode.</summary>
    public const uint ROOT_INODE = 2;

    // Compatible features (s_feature_compat).
    private const uint COMPAT_HAS_JOURNAL = 0x4;

    // Incompatible features (s_feature_incompat).
    private const uint INCOMPAT_FILETYPE = 0x2;
    private const uint INCOMPAT_RECOVER = 0x4;
    private const uint INCOMPAT_EXTENTS = 0x40;
    private const uint INCOMPAT_64BIT = 0x80;
    private const uint INCOMPAT_FLEX_BG = 0x200;
    private const uint INCOMPAT_EA_INODE = 0x400;
    private const uint INCOMPAT_CSUM_SEED = 0x2000;
    private const uint INCOMPAT_LARGEDIR = 0x4000;
    private const uint INCOMPAT_SERVED = INCOMPAT_FILETYPE | INCOMPAT_EXTENTS | INCOMPAT_64BIT | INCOMPAT_FLEX_BG | INCOMPAT_EA_INODE |
        INCOMPAT_CSUM_SEED | INCOMPAT_LARGEDIR;

    // Read-only compatible features (s_feature_ro_compat).
    private const uint RO_COMPAT_HUGE_FILE = 0x8;
    private const uint RO_COMPAT_GDT_CSUM = 0x10;
    private const uint RO_COMPAT_METADATA_CSUM = 0x400;

    private const ushort MAGIC = 0xEF53;
    private const int CHECKSUM = 0x3FC;

    #endregion

    #region Constructors

    private Ext4Superblock(ReadOnlySpan<byte> bytes)
    {
        InodesCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        FirstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x14..]);
        var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x18..]);
        BlockSize = logBlockSize <= 6 ? 1024 << (int)logBlockSize : 0;
        BlocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x20..]);
        InodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x28..]);
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x4C..]);
        InodeSize = revision == 0 ? 128 : BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x58..]);
        Compatible = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x5C..]);
        Incompatible = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x60..]);
        ReadOnlyCompatible = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x64..]);
        Uuid = bytes.Slice(0x68, 16).ToArray();
        Label = Encoding.UTF8.GetString(bytes.Slice(0x78, 16)).TrimEnd('\0');
        DescriptorSize = Has64Bit ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[0xFE..]) : 32;
        BlocksCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x04..]) |
            (Has64Bit ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x150..]) << 32 : 0);
        ChecksumSeed = (Incompatible & INCOMPAT_CSUM_SEED) != 0
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x270..])
            : Ext4Checksums.Crc32C(~0U, Uuid);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the superblock of a volume of the given bytes.
    /// </summary>
    /// <param name="bytes">The superblock's 1024 bytes.</param>
    /// <param name="volumeBytes">The bytes of the resource the volume lies on.</param>
    /// <returns>The superblock.</returns>
    /// <exception cref="FileSystemException">No ext volume is there, it uses what the reader does not serve
    /// (Unsupported), or its superblock is damaged (Corrupt).</exception>
    public static Ext4Superblock Read(ReadOnlySpan<byte> bytes, long volumeBytes)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x38..]) != MAGIC)
            throw new FileSystemException(FileSystemError.Unsupported, "The volume has no ext2, ext3 or ext4 superblock.");
        var superblock = new Ext4Superblock(bytes);
        if (superblock.HasMetadataChecksums &&
            Ext4Checksums.Crc32C(~0U, bytes[..CHECKSUM]) != BinaryPrimitives.ReadUInt32LittleEndian(bytes[CHECKSUM..]))
            throw new FileSystemException(FileSystemError.Corrupt, "The superblock fails its checksum.");
        var unserved = superblock.Incompatible & ~INCOMPAT_SERVED;
        if ((unserved & INCOMPAT_RECOVER) != 0)
            throw new FileSystemException(FileSystemError.Unsupported, "The volume's journal needs recovery, which the reader does not make yet.");
        if (unserved != 0)
            throw new FileSystemException(FileSystemError.Unsupported, $"The volume uses incompatible features {unserved:x} the reader does not serve.");
        superblock.Check(volumeBytes);
        return superblock;
    }

    /// <summary>
    /// The group descriptor table's first block.
    /// </summary>
    /// <returns>Its block.</returns>
    public ulong DescriptorTableBlock() => FirstDataBlock + 1UL;

    private void Check(long volumeBytes)
    {
        var groups = GroupCount;
        if (BlockSize == 0 || BlocksPerGroup == 0 || BlocksPerGroup > 8UL * (ulong)BlockSize || InodesPerGroup == 0 ||
            InodesPerGroup > 8UL * (ulong)BlockSize || InodeSize < 128 || InodeSize > BlockSize || (InodeSize & (InodeSize - 1)) != 0 ||
            DescriptorSize < 32 || DescriptorSize > BlockSize || (DescriptorSize & (DescriptorSize - 1)) != 0 ||
            (Has64Bit && DescriptorSize < 64) || FirstDataBlock > (BlockSize == 1024 ? 1U : 0U) || BlocksCount <= FirstDataBlock ||
            BlocksCount > (ulong)volumeBytes / (ulong)BlockSize || groups == 0 || (ulong)InodesPerGroup * groups < InodesCount ||
            InodesCount < ROOT_INODE)
            throw new FileSystemException(FileSystemError.Corrupt, $"The superblock describes {BlocksCount} blocks of {BlockSize} bytes in groups " +
                $"of {BlocksPerGroup}, {InodesCount} inodes of {InodeSize} bytes, {InodesPerGroup} a group, descriptors of {DescriptorSize}, " +
                $"on {volumeBytes} bytes.");
    }

    #endregion

    #region Properties

    /// <summary>The inodes of the volume.</summary>
    public uint InodesCount { get; }

    /// <summary>The blocks of the volume.</summary>
    public ulong BlocksCount { get; }

    /// <summary>The first block a group covers: 1 for blocks of 1024 bytes, otherwise 0.</summary>
    public uint FirstDataBlock { get; }

    /// <summary>The bytes of a block.</summary>
    public int BlockSize { get; }

    /// <summary>The blocks of a group.</summary>
    public uint BlocksPerGroup { get; }

    /// <summary>The inodes of a group.</summary>
    public uint InodesPerGroup { get; }

    /// <summary>The bytes of an inode record.</summary>
    public int InodeSize { get; }

    /// <summary>The bytes of a group descriptor.</summary>
    public int DescriptorSize { get; }

    /// <summary>s_feature_compat.</summary>
    public uint Compatible { get; }

    /// <summary>s_feature_incompat.</summary>
    public uint Incompatible { get; }

    /// <summary>s_feature_ro_compat.</summary>
    public uint ReadOnlyCompatible { get; }

    /// <summary>The volume's UUID.</summary>
    public byte[] Uuid { get; }

    /// <summary>The volume's label.</summary>
    public string Label { get; }

    /// <summary>The seed of every metadata checksum.</summary>
    public uint ChecksumSeed { get; }

    /// <summary>The groups of the volume.</summary>
    public uint GroupCount => BlocksPerGroup == 0 ? 0 : (uint)((BlocksCount - FirstDataBlock + BlocksPerGroup - 1) / BlocksPerGroup);

    /// <summary>Whether block numbers and group descriptors are 64-bit.</summary>
    public bool Has64Bit => (Incompatible & INCOMPAT_64BIT) != 0;

    /// <summary>Whether directory entries record their file type.</summary>
    public bool HasFileType => (Incompatible & INCOMPAT_FILETYPE) != 0;

    /// <summary>Whether metadata carries CRC-32C checksums.</summary>
    public bool HasMetadataChecksums => (ReadOnlyCompatible & RO_COMPAT_METADATA_CSUM) != 0;

    /// <summary>Whether group descriptors carry the older CRC-16 checksum.</summary>
    public bool HasGroupChecksums => (ReadOnlyCompatible & RO_COMPAT_GDT_CSUM) != 0;

    /// <summary>Whether an inode with HUGE_FILE counts its blocks in filesystem blocks.</summary>
    public bool HasHugeFiles => (ReadOnlyCompatible & RO_COMPAT_HUGE_FILE) != 0;

    /// <summary>The format's name: ext4 with extents or flexible groups, ext3 with a journal, otherwise ext2.</summary>
    public string Format => (Incompatible & (INCOMPAT_EXTENTS | INCOMPAT_FLEX_BG | INCOMPAT_64BIT)) != 0 ? "ext4"
        : (Compatible & COMPAT_HAS_JOURNAL) != 0 ? "ext3" : "ext2";

    #endregion
}
