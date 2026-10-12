using System;
using System.Buffers.Binary;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// An inode record ("Index Nodes"), checked by its checksum when metadata_csum is on: its type and permissions, owner,
/// size, times, flags and the 60 bytes of i_block that hold an extent tree's root, a block map or a fast link's target.
/// </summary>
internal sealed class Ext4Inode
{
    #region Constants

    /// <summary>i_block's bytes.</summary>
    public const int BLOCK_BYTES = 60;

    private const int TYPE_MASK = 0xF000;
    private const int TYPE_DIRECTORY = 0x4000;
    private const int TYPE_FILE = 0x8000;
    private const int TYPE_LINK = 0xA000;
    private const uint FLAG_INDEX = 0x1000;
    private const uint FLAG_HUGE_FILE = 0x40000;
    private const uint FLAG_EXTENTS = 0x80000;
    private const uint FLAG_INLINE_DATA = 0x10000000;
    private const int CHECKSUM_LO = 0x7C;
    private const int CHECKSUM_HI = 0x82;
    private const int OLD_SIZE = 128;

    #endregion

    #region Constructors

    private Ext4Inode(uint number, ReadOnlySpan<byte> bytes, Ext4Superblock superblock)
    {
        Number = number;
        Mode = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        UserId = BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x02..]) | (uint)BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x78..]) << 16;
        GroupId = BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x18..]) | (uint)BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x7A..]) << 16;
        Size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x04..]) | (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x6C..]) << 32;
        Links = BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x1A..]);
        Flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x20..]);
        Block = bytes.Slice(0x28, BLOCK_BYTES).ToArray();
        Generation = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x64..]);
        var blocks = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x1C..]) | (ulong)BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x74..]) << 32;
        Sectors = superblock.HasHugeFiles && (Flags & FLAG_HUGE_FILE) != 0 ? blocks * (ulong)(superblock.BlockSize / 512) : blocks;
        ExtendedAttributeBlock = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x68..]) | (ulong)BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x76..]) << 32;
        var extra = bytes.Length > OLD_SIZE ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x80..]) : 0;
        Accessed = Time(bytes, extra, 0x08, 0x8C);
        Changed = Time(bytes, extra, 0x0C, 0x84);
        Modified = Time(bytes, extra, 0x10, 0x88);
        Created = extra >= 0x94 + 4 - OLD_SIZE ? Time(bytes, extra, 0x90, 0x94) : null;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks an inode record.
    /// </summary>
    /// <param name="number">The inode's number.</param>
    /// <param name="bytes">Its record, InodeSize bytes.</param>
    /// <param name="superblock">The volume's superblock.</param>
    /// <returns>The inode.</returns>
    /// <exception cref="FileSystemException">The record fails its checksum, or its extra size overruns it (Corrupt).</exception>
    public static Ext4Inode Read(uint number, ReadOnlySpan<byte> bytes, Ext4Superblock superblock)
    {
        var extra = bytes.Length > OLD_SIZE ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x80..]) : 0;
        if (OLD_SIZE + extra > bytes.Length || extra % 4 != 0)
            throw new FileSystemException(FileSystemError.Corrupt, $"Inode {number} records {extra} extra bytes in a record of {bytes.Length}.");
        if (superblock.HasMetadataChecksums)
            Verify(number, bytes, extra, superblock);
        return new Ext4Inode(number, bytes, superblock);
    }

    /// <summary>
    /// The seed of the checksums of the inode's blocks: the volume's, the number and the generation.
    /// </summary>
    /// <param name="superblock">The volume's superblock.</param>
    /// <returns>The seed.</returns>
    public uint ChecksumSeed(Ext4Superblock superblock) =>
        Ext4Checksums.Crc32C(Ext4Checksums.Crc32C(superblock.ChecksumSeed, Number), Generation);

    // The checksum over the whole record with its checksum fields zero, seeded by the volume, the number and the
    // generation; the high half only when the record's extra size covers it.
    private static void Verify(uint number, ReadOnlySpan<byte> bytes, int extra, Ext4Superblock superblock)
    {
        var copy = bytes.ToArray();
        var hasHigh = copy.Length > OLD_SIZE && extra >= CHECKSUM_HI + 2 - OLD_SIZE;
        uint stored = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(CHECKSUM_LO));
        copy[CHECKSUM_LO] = copy[CHECKSUM_LO + 1] = 0;
        if (hasHigh)
        {
            stored |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(CHECKSUM_HI)) << 16;
            copy[CHECKSUM_HI] = copy[CHECKSUM_HI + 1] = 0;
        }
        var crc = Ext4Checksums.Crc32C(Ext4Checksums.Crc32C(superblock.ChecksumSeed, number), BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(0x64)));
        crc = Ext4Checksums.Crc32C(crc, copy);
        if ((hasHigh ? crc : crc & 0xFFFF) != stored)
            throw new FileSystemException(FileSystemError.Corrupt, $"Inode {number} fails its checksum.");
    }

    // A time's seconds, with their extra field when the record's extra size covers it.
    private static DateTimeOffset? Time(ReadOnlySpan<byte> bytes, int extra, int seconds, int extraOffset) =>
        Decode(BinaryPrimitives.ReadInt32LittleEndian(bytes[seconds..]),
            extra >= extraOffset + 4 - OLD_SIZE ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[extraOffset..]) : 0);

    // Seconds as a signed 32-bit value with two epoch bits from the extra field, and nanoseconds above them.
    private static DateTimeOffset? Decode(int seconds, uint extra)
    {
        var total = seconds + ((long)(extra & 3) << 32);
        var nanoseconds = extra >> 2;
        if (nanoseconds >= 1_000_000_000 || total < -62135596800L || total > 253402300799L)
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(total).AddTicks(nanoseconds / 100);
    }

    #endregion

    #region Properties

    /// <summary>The inode's number.</summary>
    public uint Number { get; }

    /// <summary>i_mode: the type and the permissions.</summary>
    public int Mode { get; }

    /// <summary>The owner's user id.</summary>
    public uint UserId { get; }

    /// <summary>The owner's group id.</summary>
    public uint GroupId { get; }

    /// <summary>The bytes of the file, the directory or the link's target.</summary>
    public long Size { get; }

    /// <summary>The hard links.</summary>
    public int Links { get; }

    /// <summary>i_flags.</summary>
    public uint Flags { get; }

    /// <summary>i_block.</summary>
    public byte[] Block { get; }

    /// <summary>i_generation, part of every checksum of the inode's metadata.</summary>
    public uint Generation { get; }

    /// <summary>The 512-byte sectors the inode holds, its extended attribute block among them.</summary>
    public ulong Sectors { get; }

    /// <summary>The block of its extended attributes, or zero.</summary>
    public ulong ExtendedAttributeBlock { get; }

    /// <summary>When it was last read.</summary>
    public DateTimeOffset? Accessed { get; }

    /// <summary>When the inode last changed.</summary>
    public DateTimeOffset? Changed { get; }

    /// <summary>When its data were last written.</summary>
    public DateTimeOffset? Modified { get; }

    /// <summary>When it was created, when the record holds it.</summary>
    public DateTimeOffset? Created { get; }

    /// <summary>What the entry is.</summary>
    public FileSystemEntryKind Kind => (Mode & TYPE_MASK) switch
    {
        TYPE_FILE => FileSystemEntryKind.File,
        TYPE_DIRECTORY => FileSystemEntryKind.Directory,
        TYPE_LINK => FileSystemEntryKind.SymbolicLink,
        _ => FileSystemEntryKind.Other
    };

    /// <summary>Whether its data are an extent tree rooted in i_block; otherwise a block map.</summary>
    public bool HasExtents => (Flags & FLAG_EXTENTS) != 0;

    /// <summary>Whether a directory carries an htree index.</summary>
    public bool IsIndexed => (Flags & FLAG_INDEX) != 0;

    /// <summary>Whether its data live in the inode (INLINE_DATA), which the reader refuses with the feature.</summary>
    public bool HasInlineData => (Flags & FLAG_INLINE_DATA) != 0;

    #endregion
}
