using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// The group descriptor table ("Block Group Descriptors"), read whole at the mount after the superblock, its blocks
/// kept: each group's bitmaps, inode table, free counts and flags, checked by the descriptor's checksum — CRC-32C with
/// metadata_csum, CRC-16 with GDT_CSUM — and kept within the volume. A writable mount changes descriptors in place
/// and seals them again (D4.3). A volume with META_BG is refused by the superblock, so the table is contiguous.
/// </summary>
internal sealed class Ext4GroupTable
{
    #region Constants

    /// <summary>The group's block bitmap has not been written yet (BLOCK_UNINIT).</summary>
    public const ushort BLOCK_UNINIT = 0x2;

    /// <summary>The group's inode bitmap has not been written yet (INODE_UNINIT).</summary>
    public const ushort INODE_UNINIT = 0x1;

    private const int BLOCK_BITMAP = 0x00;
    private const int INODE_BITMAP = 0x04;
    private const int INODE_TABLE = 0x08;
    private const int FREE_BLOCKS = 0x0C;
    private const int FREE_INODES = 0x0E;
    private const int USED_DIRECTORIES = 0x10;
    private const int FLAGS = 0x12;
    private const int BLOCK_BITMAP_CHECKSUM = 0x18;
    private const int INODE_BITMAP_CHECKSUM = 0x1A;
    private const int UNUSED_INODES = 0x1C;
    private const int CHECKSUM = 0x1E;
    private const int HIGH = 0x20; // each 32-bit field's high half at its offset + HIGH, each 16-bit's at the offsets below
    private const int FREE_BLOCKS_HI = 0x2C;
    private const int FREE_INODES_HI = 0x2E;
    private const int USED_DIRECTORIES_HI = 0x30;
    private const int UNUSED_INODES_HI = 0x32;
    private const int BLOCK_BITMAP_CHECKSUM_HI = 0x38;
    private const int INODE_BITMAP_CHECKSUM_HI = 0x3A;

    #endregion

    #region Fields

    private readonly Ext4Superblock m_superblock;
    private readonly byte[] m_blocks;

    #endregion

    #region Constructors

    private Ext4GroupTable(Ext4Superblock superblock, byte[] blocks)
    {
        m_superblock = superblock;
        m_blocks = blocks;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the table.
    /// </summary>
    /// <param name="disk">The volume's resource.</param>
    /// <param name="superblock">The checked superblock.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The table.</returns>
    /// <exception cref="FileSystemException">A descriptor fails its checksum or names a structure beyond the volume (Corrupt).</exception>
    public static async ValueTask<Ext4GroupTable> ReadAsync(Ext4Disk disk, Ext4Superblock superblock, CancellationToken cancellationToken)
    {
        var bytes = (long)superblock.GroupCount * superblock.DescriptorSize;
        var blocks = new byte[(bytes + superblock.BlockSize - 1) / superblock.BlockSize * superblock.BlockSize];
        await disk.ReadAsync((long)superblock.DescriptorTableBlock() * superblock.BlockSize, blocks, cancellationToken).ConfigureAwait(false);
        var table = new Ext4GroupTable(superblock, blocks);
        var tableBlocks = ((ulong)superblock.InodesPerGroup * (ulong)superblock.InodeSize + (ulong)superblock.BlockSize - 1) /
            (ulong)superblock.BlockSize;
        for (var group = 0U; group < superblock.GroupCount; group++)
        {
            if (table.Checksum(group) != BinaryPrimitives.ReadUInt16LittleEndian(table.Descriptor(group)[CHECKSUM..]))
                throw new FileSystemException(FileSystemError.Corrupt, $"Group descriptor {group} fails its checksum.");
            var inodes = table.InodeTable(group);
            if (inodes == 0 || inodes > superblock.BlocksCount - tableBlocks || table.BlockBitmap(group) >= superblock.BlocksCount ||
                table.InodeBitmap(group) >= superblock.BlocksCount)
                throw new FileSystemException(FileSystemError.Corrupt, $"Group {group} places its bitmaps or its inode table beyond the volume.");
        }
        return table;
    }

    /// <summary>The block bitmap of a group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its block.</returns>
    public ulong BlockBitmap(uint group) => Read32(group, BLOCK_BITMAP);

    /// <summary>The inode bitmap of a group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its block.</returns>
    public ulong InodeBitmap(uint group) => Read32(group, INODE_BITMAP);

    /// <summary>The inode table of a group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its first block.</returns>
    public ulong InodeTable(uint group) => Read32(group, INODE_TABLE);

    /// <summary>The flags of a group: BLOCK_UNINIT, INODE_UNINIT, INODE_ZEROED.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its flags.</returns>
    public ushort Flags(uint group) => BinaryPrimitives.ReadUInt16LittleEndian(Descriptor(group)[FLAGS..]);

    /// <summary>Sets the flags of a group.</summary>
    /// <param name="group">The group.</param>
    /// <param name="flags">Its flags.</param>
    public void SetFlags(uint group, ushort flags) => BinaryPrimitives.WriteUInt16LittleEndian(Descriptor(group)[FLAGS..], flags);

    /// <summary>The free blocks of a group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The count.</returns>
    public uint FreeBlocks(uint group) => Read16(group, FREE_BLOCKS, FREE_BLOCKS_HI);

    /// <summary>Sets the free blocks of a group.</summary>
    /// <param name="group">The group.</param>
    /// <param name="value">The count.</param>
    public void SetFreeBlocks(uint group, uint value) => Write16(group, FREE_BLOCKS, FREE_BLOCKS_HI, value);

    /// <summary>The free inodes of a group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The count.</returns>
    public uint FreeInodes(uint group) => Read16(group, FREE_INODES, FREE_INODES_HI);

    /// <summary>Sets the free inodes of a group.</summary>
    /// <param name="group">The group.</param>
    /// <param name="value">The count.</param>
    public void SetFreeInodes(uint group, uint value) => Write16(group, FREE_INODES, FREE_INODES_HI, value);

    /// <summary>The directories of a group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The count.</returns>
    public uint UsedDirectories(uint group) => Read16(group, USED_DIRECTORIES, USED_DIRECTORIES_HI);

    /// <summary>Sets the directories of a group.</summary>
    /// <param name="group">The group.</param>
    /// <param name="value">The count.</param>
    public void SetUsedDirectories(uint group, uint value) => Write16(group, USED_DIRECTORIES, USED_DIRECTORIES_HI, value);

    /// <summary>The inodes at the end of a group's table never used (itable_unused).</summary>
    /// <param name="group">The group.</param>
    /// <returns>The count.</returns>
    public uint UnusedInodes(uint group) => Read16(group, UNUSED_INODES, UNUSED_INODES_HI);

    /// <summary>Sets the inodes at the end of a group's table never used.</summary>
    /// <param name="group">The group.</param>
    /// <param name="value">The count.</param>
    public void SetUnusedInodes(uint group, uint value) => Write16(group, UNUSED_INODES, UNUSED_INODES_HI, value);

    /// <summary>The checksum a group's block bitmap carries.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The checksum, its high half where the descriptor holds one.</returns>
    public uint BlockBitmapChecksum(uint group) => Read16(group, BLOCK_BITMAP_CHECKSUM, BLOCK_BITMAP_CHECKSUM_HI);

    /// <summary>Sets the checksum of a group's block bitmap.</summary>
    /// <param name="group">The group.</param>
    /// <param name="value">The checksum.</param>
    public void SetBlockBitmapChecksum(uint group, uint value) => Write16(group, BLOCK_BITMAP_CHECKSUM, BLOCK_BITMAP_CHECKSUM_HI, value);

    /// <summary>The checksum a group's inode bitmap carries.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The checksum, its high half where the descriptor holds one.</returns>
    public uint InodeBitmapChecksum(uint group) => Read16(group, INODE_BITMAP_CHECKSUM, INODE_BITMAP_CHECKSUM_HI);

    /// <summary>Sets the checksum of a group's inode bitmap.</summary>
    /// <param name="group">The group.</param>
    /// <param name="value">The checksum.</param>
    public void SetInodeBitmapChecksum(uint group, uint value) => Write16(group, INODE_BITMAP_CHECKSUM, INODE_BITMAP_CHECKSUM_HI, value);

    /// <summary>
    /// Seals a group's descriptor with its checksum and gives the table block that holds it.
    /// </summary>
    /// <param name="group">The group.</param>
    /// <returns>The block's number and a copy of its bytes.</returns>
    public (ulong Block, byte[] Bytes) Seal(uint group)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Descriptor(group)[CHECKSUM..], Checksum(group));
        var size = m_superblock.BlockSize;
        var index = (int)((long)group * m_superblock.DescriptorSize / size);
        return (m_superblock.DescriptorTableBlock() + (ulong)index, m_blocks.AsSpan(index * size, size).ToArray());
    }

    /// <summary>
    /// A copy of the table's blocks, to restore after a change that failed.
    /// </summary>
    /// <returns>The copy.</returns>
    public byte[] Snapshot() => (byte[])m_blocks.Clone();

    /// <summary>
    /// Restores the blocks a snapshot holds.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    public void Restore(byte[] snapshot) => snapshot.CopyTo(m_blocks, 0);

    private Span<byte> Descriptor(uint group) => m_blocks.AsSpan((int)(group * m_superblock.DescriptorSize), m_superblock.DescriptorSize);

    private ulong Read32(uint group, int offset)
    {
        var descriptor = Descriptor(group);
        return BinaryPrimitives.ReadUInt32LittleEndian(descriptor[offset..]) |
            (descriptor.Length >= 64 ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[(offset + HIGH)..]) << 32 : 0);
    }

    private uint Read16(uint group, int low, int high)
    {
        var descriptor = Descriptor(group);
        return BinaryPrimitives.ReadUInt16LittleEndian(descriptor[low..]) |
            (descriptor.Length >= 64 ? (uint)BinaryPrimitives.ReadUInt16LittleEndian(descriptor[high..]) << 16 : 0);
    }

    private void Write16(uint group, int low, int high, uint value)
    {
        var descriptor = Descriptor(group);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[low..], (ushort)value);
        if (descriptor.Length >= 64)
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor[high..], (ushort)(value >> 16));
    }

    // The descriptor's checksum: CRC-32C's low half with metadata_csum, CRC-16 with GDT_CSUM, the stored value otherwise.
    private ushort Checksum(uint group)
    {
        var descriptor = Descriptor(group);
        if (m_superblock.HasMetadataChecksums)
        {
            var crc = Ext4Checksums.Crc32C(m_superblock.ChecksumSeed, group);
            crc = Ext4Checksums.Crc32C(crc, descriptor[..CHECKSUM]);
            crc = Ext4Checksums.Crc32C(crc, [0, 0]);
            crc = Ext4Checksums.Crc32C(crc, descriptor[(CHECKSUM + 2)..]);
            return (ushort)crc;
        }
        if (m_superblock.HasGroupChecksums)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(number, group);
            var crc = Ext4Checksums.Crc16(0xFFFF, m_superblock.Uuid);
            crc = Ext4Checksums.Crc16(crc, number);
            crc = Ext4Checksums.Crc16(crc, descriptor[..CHECKSUM]);
            return Ext4Checksums.Crc16(crc, descriptor[(CHECKSUM + 2)..]);
        }
        return BinaryPrimitives.ReadUInt16LittleEndian(descriptor[CHECKSUM..]);
    }

    #endregion
}
