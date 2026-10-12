using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// The allocation of a writable mount (D4.3): blocks and inodes taken from and given back to the groups' bitmaps, each
/// bitmap checked by its descriptor's checksum when it is first read, and the free counts of the groups and the
/// superblock kept with them. At the commit every bitmap and descriptor the change touched is sealed into the
/// transaction with the superblock. A group whose block bitmap was never written (BLOCK_UNINIT) is passed over; a group
/// whose inode bitmap was never written (INODE_UNINIT) gets an empty one when an inode is taken from it.
/// </summary>
internal sealed class Ext4Allocator
{
    #region Fields

    private readonly Ext4Volume m_volume;
    private readonly Ext4Transaction m_transaction;
    private readonly HashSet<uint> m_groups = [];
    private readonly HashSet<uint> m_blockBitmaps = [];
    private readonly HashSet<uint> m_inodeBitmaps = [];

    #endregion

    #region Constructors

    public Ext4Allocator(Ext4Volume volume, Ext4Transaction transaction)
    {
        m_volume = volume;
        m_transaction = transaction;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Takes a run of free blocks, contiguous, from the goal onwards.
    /// </summary>
    /// <param name="goal">The block to start from, as close to which the run is taken.</param>
    /// <param name="count">The blocks wanted.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The run's first block and its length, from one to the count.</returns>
    /// <exception cref="FileSystemException">No free block is left (NoSpace).</exception>
    public async ValueTask<(ulong Start, uint Length)> AllocateBlocksAsync(ulong goal, uint count, CancellationToken cancellationToken)
    {
        var superblock = m_volume.Superblock;
        if (goal < superblock.FirstDataBlock || goal >= superblock.BlocksCount)
            goal = superblock.FirstDataBlock;
        var groups = superblock.GroupCount;
        var start = Group(goal);
        for (var step = 0U; step <= groups; step++)
        {
            var group = (start + step) % groups;
            if ((m_volume.Groups.Flags(group) & Ext4GroupTable.BLOCK_UNINIT) != 0 || m_volume.Groups.FreeBlocks(group) == 0)
                continue;
            var bitmap = await BlockBitmapAsync(group, cancellationToken).ConfigureAwait(false);
            var first = First(group);
            var blocks = (uint)Math.Min(superblock.BlocksPerGroup, superblock.BlocksCount - first);
            var from = step == 0 ? (uint)(goal - first) : 0;
            for (var pass = 0; pass < 2; pass++)
            {
                for (var bit = pass == 0 ? from : 0; bit < (pass == 0 ? blocks : Math.Min(from, blocks)); bit++)
                {
                    if (IsSet(bitmap, bit))
                        continue;
                    var length = 0U;
                    while (length < count && bit + length < blocks && !IsSet(bitmap, bit + length))
                    {
                        Set(bitmap, bit + length, true);
                        length++;
                    }
                    Count(group, -(int)length, 0, 0);
                    superblock.FreeBlocks -= length;
                    return (first + bit, length);
                }
            }
        }
        throw new FileSystemException(FileSystemError.NoSpace, "The volume has no free block left.");
    }

    /// <summary>
    /// Gives back a run of blocks.
    /// </summary>
    /// <param name="start">The first block.</param>
    /// <param name="length">The blocks.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>When the blocks are free.</returns>
    /// <exception cref="FileSystemException">A block of the run is free already (Corrupt).</exception>
    public async ValueTask FreeBlocksAsync(ulong start, ulong length, CancellationToken cancellationToken)
    {
        for (var block = start; block < start + length;)
        {
            var group = Group(block);
            var first = First(group);
            var bitmap = await BlockBitmapAsync(group, cancellationToken).ConfigureAwait(false);
            var freed = 0;
            for (; block < start + length && block < first + m_volume.Superblock.BlocksPerGroup; block++, freed++)
            {
                if (!IsSet(bitmap, (uint)(block - first)))
                    throw new FileSystemException(FileSystemError.Corrupt, $"Block {block} is freed but was free.");
                Set(bitmap, (uint)(block - first), false);
                m_transaction.Forget(block);
            }
            Count(group, freed, 0, 0);
            m_volume.Superblock.FreeBlocks += (ulong)freed;
        }
    }

    /// <summary>
    /// Takes a free inode, from a preferred group onwards.
    /// </summary>
    /// <param name="preferred">The group to start from: the parent directory's.</param>
    /// <param name="directory">Whether the inode is a directory's, which the group counts.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The inode's number.</returns>
    /// <exception cref="FileSystemException">No free inode is left (NoSpace).</exception>
    public async ValueTask<uint> AllocateInodeAsync(uint preferred, bool directory, CancellationToken cancellationToken)
    {
        var superblock = m_volume.Superblock;
        var groups = superblock.GroupCount;
        for (var step = 0U; step < groups; step++)
        {
            var group = (preferred + step) % groups;
            if (m_volume.Groups.FreeInodes(group) == 0)
                continue;
            var bitmap = await InodeBitmapAsync(group, cancellationToken).ConfigureAwait(false);
            var low = group == 0 ? superblock.FirstInode - 1 : 0;
            for (var bit = low; bit < superblock.InodesPerGroup; bit++)
            {
                var number = group * superblock.InodesPerGroup + bit + 1;
                if (IsSet(bitmap, bit) || number > superblock.InodesCount)
                    continue;
                Set(bitmap, bit, true);
                Count(group, 0, -1, directory ? 1 : 0);
                var unused = m_volume.Groups.UnusedInodes(group);
                if (bit >= superblock.InodesPerGroup - unused)
                    m_volume.Groups.SetUnusedInodes(group, superblock.InodesPerGroup - bit - 1);
                superblock.FreeInodes--;
                return number;
            }
        }
        throw new FileSystemException(FileSystemError.NoSpace, "The volume has no free inode left.");
    }

    /// <summary>
    /// Gives back an inode.
    /// </summary>
    /// <param name="number">The inode's number.</param>
    /// <param name="directory">Whether it was a directory's.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>When the inode is free.</returns>
    /// <exception cref="FileSystemException">The inode is free already (Corrupt).</exception>
    public async ValueTask FreeInodeAsync(uint number, bool directory, CancellationToken cancellationToken)
    {
        var superblock = m_volume.Superblock;
        var group = (number - 1) / superblock.InodesPerGroup;
        var bit = (number - 1) % superblock.InodesPerGroup;
        var bitmap = await InodeBitmapAsync(group, cancellationToken).ConfigureAwait(false);
        if (!IsSet(bitmap, bit))
            throw new FileSystemException(FileSystemError.Corrupt, $"Inode {number} is freed but was free.");
        Set(bitmap, bit, false);
        Count(group, 0, 1, directory ? -1 : 0);
        superblock.FreeInodes++;
    }

    /// <summary>
    /// The group of a block.
    /// </summary>
    /// <param name="block">The block.</param>
    /// <returns>Its group.</returns>
    public uint Group(ulong block) => (uint)((block - m_volume.Superblock.FirstDataBlock) / m_volume.Superblock.BlocksPerGroup);

    /// <summary>
    /// Seals into the transaction every bitmap and descriptor the change touched, with the superblock.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>When the transaction holds them.</returns>
    public async ValueTask SealAsync(CancellationToken cancellationToken)
    {
        var superblock = m_volume.Superblock;
        foreach (var group in m_blockBitmaps)
        {
            var bitmap = await m_transaction.GetAsync(m_volume, m_volume.Groups.BlockBitmap(group), cancellationToken).ConfigureAwait(false);
            m_volume.Groups.SetBlockBitmapChecksum(group, Ext4Checksums.Crc32C(superblock.ChecksumSeed, bitmap.AsSpan(0, (int)(superblock.BlocksPerGroup / 8))));
        }
        foreach (var group in m_inodeBitmaps)
        {
            var bitmap = await m_transaction.GetAsync(m_volume, m_volume.Groups.InodeBitmap(group), cancellationToken).ConfigureAwait(false);
            m_volume.Groups.SetInodeBitmapChecksum(group, Ext4Checksums.Crc32C(superblock.ChecksumSeed, bitmap.AsSpan(0, (int)(superblock.InodesPerGroup / 8))));
        }
        foreach (var group in m_groups)
        {
            var (block, bytes) = m_volume.Groups.Seal(group);
            m_transaction.Put(block, bytes);
        }
        var offset = Ext4Superblock.OFFSET % superblock.BlockSize;
        var superblockBlock = (ulong)(Ext4Superblock.OFFSET / superblock.BlockSize);
        var holder = await m_transaction.GetAsync(m_volume, superblockBlock, cancellationToken).ConfigureAwait(false);
        superblock.Seal().CopyTo(holder, offset);
    }

    private ulong First(uint group) => m_volume.Superblock.FirstDataBlock + (ulong)group * m_volume.Superblock.BlocksPerGroup;

    private void Count(uint group, int blocks, int inodes, int directories)
    {
        var table = m_volume.Groups;
        table.SetFreeBlocks(group, (uint)(table.FreeBlocks(group) + blocks));
        table.SetFreeInodes(group, (uint)(table.FreeInodes(group) + inodes));
        table.SetUsedDirectories(group, (uint)(table.UsedDirectories(group) + directories));
        m_groups.Add(group);
    }

    // A group's block bitmap, the transaction's copy, checked by its checksum when it is first read.
    private async ValueTask<byte[]> BlockBitmapAsync(uint group, CancellationToken cancellationToken)
    {
        var block = m_volume.Groups.BlockBitmap(group);
        var loaded = m_transaction.TryGet(block, out _);
        var bitmap = await m_transaction.GetAsync(m_volume, block, cancellationToken).ConfigureAwait(false);
        if (!loaded && m_volume.Superblock.HasMetadataChecksums &&
            !Matches(Ext4Checksums.Crc32C(m_volume.Superblock.ChecksumSeed, bitmap.AsSpan(0, (int)(m_volume.Superblock.BlocksPerGroup / 8))),
                m_volume.Groups.BlockBitmapChecksum(group)))
            throw new FileSystemException(FileSystemError.Corrupt, $"Group {group}'s block bitmap fails its checksum.");
        m_blockBitmaps.Add(group);
        return bitmap;
    }

    // A group's inode bitmap: an empty one, its bits past the group's inodes set, for a group that never wrote one.
    private async ValueTask<byte[]> InodeBitmapAsync(uint group, CancellationToken cancellationToken)
    {
        var superblock = m_volume.Superblock;
        var block = m_volume.Groups.InodeBitmap(group);
        byte[] bitmap;
        if ((m_volume.Groups.Flags(group) & Ext4GroupTable.INODE_UNINIT) != 0)
        {
            bitmap = new byte[superblock.BlockSize];
            for (var bit = superblock.InodesPerGroup; bit < superblock.BlockSize * 8; bit++)
                Set(bitmap, bit, true);
            m_transaction.Put(block, bitmap);
            m_volume.Groups.SetFlags(group, (ushort)(m_volume.Groups.Flags(group) & ~Ext4GroupTable.INODE_UNINIT));
            m_volume.Groups.SetUnusedInodes(group, superblock.InodesPerGroup);
            m_groups.Add(group);
        }
        else
        {
            var loaded = m_transaction.TryGet(block, out _);
            bitmap = await m_transaction.GetAsync(m_volume, block, cancellationToken).ConfigureAwait(false);
            if (!loaded && superblock.HasMetadataChecksums &&
                !Matches(Ext4Checksums.Crc32C(superblock.ChecksumSeed, bitmap.AsSpan(0, (int)(superblock.InodesPerGroup / 8))),
                    m_volume.Groups.InodeBitmapChecksum(group)))
                throw new FileSystemException(FileSystemError.Corrupt, $"Group {group}'s inode bitmap fails its checksum.");
        }
        m_inodeBitmaps.Add(group);
        return bitmap;
    }

    // A bitmap checksum against the descriptor's: its low half, or all of it where the descriptor holds the high half.
    private bool Matches(uint checksum, uint stored) => m_volume.Superblock.DescriptorSize >= 64 ? checksum == stored : (checksum & 0xFFFF) == stored;

    private static bool IsSet(byte[] bitmap, uint bit) => (bitmap[bit >> 3] & (1 << (int)(bit & 7))) != 0;

    private static void Set(byte[] bitmap, uint bit, bool value)
    {
        if (value)
            bitmap[bit >> 3] |= (byte)(1 << (int)(bit & 7));
        else
            bitmap[bit >> 3] &= (byte)~(1 << (int)(bit & 7));
    }

    #endregion
}
