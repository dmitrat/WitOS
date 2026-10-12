using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// The group descriptor table ("Block Group Descriptors"), read whole at the mount after the superblock: each group's
/// inode table, checked by its descriptor's checksum — CRC-32C with metadata_csum, CRC-16 with GDT_CSUM — and kept to
/// within the volume. A volume with META_BG is refused by the superblock, so the table is contiguous.
/// </summary>
internal sealed class Ext4GroupTable
{
    #region Constants

    private const int INODE_TABLE_LO = 0x08;
    private const int INODE_TABLE_HI = 0x28;
    private const int CHECKSUM = 0x1E;

    #endregion

    #region Fields

    private readonly ulong[] m_inodeTables;

    #endregion

    #region Constructors

    private Ext4GroupTable(ulong[] inodeTables)
    {
        m_inodeTables = inodeTables;
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
    /// <exception cref="FileSystemException">A descriptor fails its checksum or names an inode table beyond the volume (Corrupt).</exception>
    public static async ValueTask<Ext4GroupTable> ReadAsync(Ext4Disk disk, Ext4Superblock superblock, CancellationToken cancellationToken)
    {
        var size = superblock.DescriptorSize;
        var groups = (int)superblock.GroupCount;
        var bytes = new byte[(long)groups * size];
        await disk.ReadAsync((long)superblock.DescriptorTableBlock() * superblock.BlockSize, bytes, cancellationToken).ConfigureAwait(false);
        var tables = new ulong[groups];
        var tableBlocks = ((ulong)superblock.InodesPerGroup * (ulong)superblock.InodeSize + (ulong)superblock.BlockSize - 1) /
            (ulong)superblock.BlockSize;
        for (var group = 0; group < groups; group++)
        {
            var descriptor = bytes.AsSpan(group * size, size);
            Verify(superblock, (uint)group, descriptor);
            tables[group] = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[INODE_TABLE_LO..]) |
                (size >= 64 ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[INODE_TABLE_HI..]) << 32 : 0);
            if (tables[group] == 0 || tables[group] > superblock.BlocksCount - tableBlocks)
                throw new FileSystemException(FileSystemError.Corrupt, $"Group {group} places its inode table at block {tables[group]}.");
        }
        return new Ext4GroupTable(tables);
    }

    /// <summary>
    /// The inode table of a group.
    /// </summary>
    /// <param name="group">The group.</param>
    /// <returns>Its first block.</returns>
    public ulong InodeTable(uint group) => m_inodeTables[group];

    private static void Verify(Ext4Superblock superblock, uint group, ReadOnlySpan<byte> descriptor)
    {
        var stored = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[CHECKSUM..]);
        if (superblock.HasMetadataChecksums)
        {
            var crc = Ext4Checksums.Crc32C(superblock.ChecksumSeed, group);
            crc = Ext4Checksums.Crc32C(crc, descriptor[..CHECKSUM]);
            crc = Ext4Checksums.Crc32C(crc, [0, 0]);
            crc = Ext4Checksums.Crc32C(crc, descriptor[(CHECKSUM + 2)..]);
            if ((ushort)crc != stored)
                throw new FileSystemException(FileSystemError.Corrupt, $"Group descriptor {group} fails its checksum.");
        }
        else if (superblock.HasGroupChecksums)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(number, group);
            var crc = Ext4Checksums.Crc16(0xFFFF, superblock.Uuid);
            crc = Ext4Checksums.Crc16(crc, number);
            crc = Ext4Checksums.Crc16(crc, descriptor[..CHECKSUM]);
            crc = Ext4Checksums.Crc16(crc, descriptor[(CHECKSUM + 2)..]);
            if (crc != stored)
                throw new FileSystemException(FileSystemError.Corrupt, $"Group descriptor {group} fails its CRC-16.");
        }
    }

    #endregion
}
