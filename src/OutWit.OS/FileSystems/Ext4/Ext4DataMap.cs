using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// Where an inode's data lie: its extent tree ("Extent Tree"), each block checked by its tail's checksum with
/// metadata_csum, or the block map of ext2 and ext3 (direct, indirect, double and triple indirect blocks), as runs of
/// logical blocks on physical blocks; a logical block no run covers is a hole and reads as zeros, as an uninitialized
/// extent does.
/// </summary>
internal sealed class Ext4DataMap
{
    #region Constants

    private const ushort EXTENT_MAGIC = 0xF30A;
    private const int HEADER = 12;
    private const int ENTRY = 12;
    private const int DEPTH_LIMIT = 5;
    private const ushort UNINITIALIZED = 32768;

    #endregion

    #region Fields

    private readonly Run[] m_runs;

    #endregion

    #region Constructors

    private Ext4DataMap(Run[] runs)
    {
        m_runs = runs;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads the map of an inode's data.
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="inode">The inode.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The map.</returns>
    /// <exception cref="FileSystemException">The tree or the map is damaged, or names blocks beyond the volume (Corrupt).</exception>
    public static async ValueTask<Ext4DataMap> ReadAsync(Ext4Volume volume, Ext4Inode inode, CancellationToken cancellationToken)
    {
        var runs = new List<Run>();
        var blocks = (ulong)((inode.Size + volume.Superblock.BlockSize - 1) / volume.Superblock.BlockSize);
        if (inode.HasExtents)
            await ReadExtentsAsync(volume, inode, inode.Block, -1, runs, cancellationToken).ConfigureAwait(false);
        else
            await ReadBlockMapAsync(volume, inode, blocks, runs, cancellationToken).ConfigureAwait(false);
        var bound = volume.Superblock.BlocksCount;
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.Length == 0 || run.Physical == 0 || run.Physical > bound || run.Length > bound - run.Physical ||
                (i > 0 && run.Logical < runs[i - 1].Logical + runs[i - 1].Length))
                throw new FileSystemException(FileSystemError.Corrupt, $"Inode {inode.Number} maps logical block {run.Logical} on {run.Length} " +
                    $"blocks at {run.Physical}, outside the volume or out of order.");
        }
        return new Ext4DataMap([.. runs]);
    }

    /// <summary>
    /// Where a logical block lies.
    /// </summary>
    /// <param name="logical">The logical block.</param>
    /// <returns>Its physical block (zero for a hole or an uninitialized extent) and the blocks from it that continue
    /// the same way, contiguous or a hole.</returns>
    public (ulong Physical, ulong Blocks) Find(ulong logical)
    {
        int low = 0, high = m_runs.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var run = m_runs[middle];
            if (logical < run.Logical)
                high = middle - 1;
            else if (logical >= run.Logical + run.Length)
                low = middle + 1;
            else
                return (run.Initialized ? run.Physical + (logical - run.Logical) : 0, run.Logical + run.Length - logical);
        }
        // A hole up to the next run.
        return (0, low < m_runs.Length ? m_runs[low].Logical - logical : ulong.MaxValue - logical);
    }

    // A node of the extent tree: the root in i_block (expected depth -1, from its header) or a block below an index.
    private static async ValueTask ReadExtentsAsync(Ext4Volume volume, Ext4Inode inode, byte[] node, int expectedDepth, List<Run> runs,
        CancellationToken cancellationToken)
    {
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(node);
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(2));
        var max = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(4));
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(6));
        if (magic != EXTENT_MAGIC || entries > max || HEADER + max * ENTRY > node.Length || depth > DEPTH_LIMIT ||
            (expectedDepth >= 0 && depth != expectedDepth))
            throw new FileSystemException(FileSystemError.Corrupt, $"Inode {inode.Number} has an extent node of magic {magic:x4}, {entries} " +
                $"of {max} entries at depth {depth}.");
        for (var i = 0; i < entries; i++)
        {
            var entry = node.AsSpan(HEADER + i * ENTRY, ENTRY);
            var logical = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (depth == 0)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
                var physical = (ulong)BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
                var initialized = length <= UNINITIALIZED;
                runs.Add(new Run(logical, physical, initialized ? length : (uint)(length - UNINITIALIZED), initialized));
                continue;
            }
            var child = (ulong)BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var block = new byte[volume.Superblock.BlockSize];
            await volume.ReadBlocksAsync(child, block, cancellationToken).ConfigureAwait(false);
            VerifyExtentBlock(volume, inode, block, child);
            await ReadExtentsAsync(volume, inode, block, depth - 1, runs, cancellationToken).ConfigureAwait(false);
        }
    }

    // An extent block's tail: CRC-32C of the header and every slot the header's maximum names, seeded by the inode.
    private static void VerifyExtentBlock(Ext4Volume volume, Ext4Inode inode, byte[] block, ulong number)
    {
        if (!volume.Superblock.HasMetadataChecksums)
            return;
        var tail = HEADER + BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(4)) * ENTRY;
        if (tail + 4 > block.Length ||
            Ext4Checksums.Crc32C(inode.ChecksumSeed(volume.Superblock), block.AsSpan(0, tail)) != BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(tail)))
            throw new FileSystemException(FileSystemError.Corrupt, $"Inode {inode.Number}'s extent block {number} fails its checksum.");
    }

    // The block map: twelve direct blocks, then an indirect, a double and a triple indirect block of 32-bit numbers.
    private static async ValueTask ReadBlockMapAsync(Ext4Volume volume, Ext4Inode inode, ulong blocks, List<Run> runs,
        CancellationToken cancellationToken)
    {
        var perBlock = (ulong)(volume.Superblock.BlockSize / 4);
        ulong logical = 0;
        for (var i = 0; i < 12 && logical < blocks; i++, logical++)
            Add(runs, logical, BinaryPrimitives.ReadUInt32LittleEndian(inode.Block.AsSpan(i * 4)));
        var span = perBlock;
        for (var level = 1; level <= 3 && logical < blocks; level++, span *= perBlock)
        {
            var pointer = BinaryPrimitives.ReadUInt32LittleEndian(inode.Block.AsSpan((11 + level) * 4));
            if (pointer != 0)
                await ReadIndirectAsync(volume, pointer, level, logical, blocks, runs, cancellationToken).ConfigureAwait(false);
            logical += span;
        }
    }

    private static async ValueTask ReadIndirectAsync(Ext4Volume volume, uint pointer, int level, ulong logical, ulong blocks, List<Run> runs,
        CancellationToken cancellationToken)
    {
        var block = new byte[volume.Superblock.BlockSize];
        await volume.ReadBlocksAsync(pointer, block, cancellationToken).ConfigureAwait(false);
        var perBlock = (ulong)(block.Length / 4);
        var span = 1UL;
        for (var i = 1; i < level; i++)
            span *= perBlock;
        for (var i = 0UL; i < perBlock && logical < blocks; i++, logical += span)
        {
            var next = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan((int)i * 4));
            if (level == 1)
                Add(runs, logical, next);
            else if (next != 0)
                await ReadIndirectAsync(volume, next, level - 1, logical, blocks, runs, cancellationToken).ConfigureAwait(false);
        }
    }

    // A mapped block, merged into the last run when it continues it.
    private static void Add(List<Run> runs, ulong logical, uint physical)
    {
        if (physical == 0)
            return;
        if (runs.Count > 0 && runs[^1] is var last && last.Logical + last.Length == logical && last.Physical + last.Length == physical &&
            last.Length < uint.MaxValue)
            runs[^1] = last with { Length = last.Length + 1 };
        else
            runs.Add(new Run(logical, physical, 1, true));
    }

    #endregion

    #region Nested Types

    private readonly record struct Run(ulong Logical, ulong Physical, uint Length, bool Initialized);

    #endregion
}
