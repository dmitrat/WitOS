using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// An inode's extent tree written anew from its runs (D4.3): runs that continue each other merge up to the 32768 blocks
/// an extent holds; up to four extents live in i_block, up to four leaves of extents below an index in i_block beyond
/// that. The leaves take the tree's old blocks first, new ones from the allocator after, and its old blocks left over
/// are freed; each leaf carries its tail's checksum when metadata_csum is on.
/// </summary>
internal static class Ext4ExtentWriter
{
    #region Constants

    private const ushort MAGIC = 0xF30A;
    private const int HEADER = 12;
    private const int ENTRY = 12;
    private const int ROOT_ENTRIES = 4;
    private const uint EXTENT_LIMIT = 32768;

    #endregion

    #region Functions

    /// <summary>
    /// The root of an empty tree in i_block.
    /// </summary>
    /// <param name="block">i_block.</param>
    public static void WriteEmptyRoot(Span<byte> block)
    {
        block.Clear();
        Header(block, 0, ROOT_ENTRIES, 0);
    }

    /// <summary>
    /// Writes an inode's tree for its runs.
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="transaction">The change.</param>
    /// <param name="allocator">The change's allocation.</param>
    /// <param name="inode">The inode's record.</param>
    /// <param name="runs">The runs, in logical order, none overlapping.</param>
    /// <param name="oldTreeBlocks">The blocks the old tree took below its root.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The blocks the new tree takes below its root.</returns>
    /// <exception cref="FileSystemException">The runs need a tree deeper than one index (TooLarge).</exception>
    public static async ValueTask<int> WriteAsync(Ext4Volume volume, Ext4Transaction transaction, Ext4Allocator allocator, Ext4InodeRecord inode,
        IReadOnlyList<Ext4Run> runs, IReadOnlyList<ulong> oldTreeBlocks, CancellationToken cancellationToken)
    {
        var extents = Merge(runs);
        var perLeaf = (volume.Superblock.BlockSize - HEADER) / ENTRY;
        var leaves = extents.Count <= ROOT_ENTRIES ? 0 : (extents.Count + perLeaf - 1) / perLeaf;
        if (leaves > ROOT_ENTRIES)
            throw new FileSystemException(FileSystemError.TooLarge, $"Inode {inode.Number} needs {extents.Count} extents, more than one index holds.");
        var blocks = new List<ulong>();
        for (var i = 0; i < leaves; i++)
        {
            if (i < oldTreeBlocks.Count)
                blocks.Add(oldTreeBlocks[i]);
            else
                blocks.Add((await allocator.AllocateBlocksAsync(extents[0].Physical, 1, cancellationToken).ConfigureAwait(false)).Start);
        }
        for (var i = leaves; i < oldTreeBlocks.Count; i++)
            await allocator.FreeBlocksAsync(oldTreeBlocks[i], 1, cancellationToken).ConfigureAwait(false);
        inode.Flags |= Ext4InodeRecord.FLAG_EXTENTS;
        var root = inode.Block;
        root.Clear();
        if (leaves == 0)
        {
            Header(root, extents.Count, ROOT_ENTRIES, 0);
            for (var i = 0; i < extents.Count; i++)
                Leaf(root[(HEADER + i * ENTRY)..], extents[i]);
            return 0;
        }
        Header(root, leaves, ROOT_ENTRIES, 1);
        for (var leaf = 0; leaf < leaves; leaf++)
        {
            var first = leaf * perLeaf;
            var count = Math.Min(perLeaf, extents.Count - first);
            var bytes = new byte[volume.Superblock.BlockSize];
            Header(bytes, count, perLeaf, 0);
            for (var i = 0; i < count; i++)
                Leaf(bytes.AsSpan(HEADER + i * ENTRY), extents[first + i]);
            if (volume.Superblock.HasMetadataChecksums)
            {
                var tail = HEADER + perLeaf * ENTRY;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(tail),
                    Ext4Checksums.Crc32C(inode.ChecksumSeed(), bytes.AsSpan(0, tail)));
            }
            transaction.Put(blocks[leaf], bytes);
            var index = root[(HEADER + leaf * ENTRY)..];
            BinaryPrimitives.WriteUInt32LittleEndian(index, (uint)extents[first].Logical);
            BinaryPrimitives.WriteUInt32LittleEndian(index[4..], (uint)blocks[leaf]);
            BinaryPrimitives.WriteUInt16LittleEndian(index[8..], (ushort)(blocks[leaf] >> 32));
        }
        return leaves;
    }

    // The runs as extents: those that continue each other merged, none longer than an extent holds.
    private static List<Ext4Run> Merge(IReadOnlyList<Ext4Run> runs)
    {
        var extents = new List<Ext4Run>();
        foreach (var run in runs)
        {
            var remaining = run;
            if (extents.Count > 0 && extents[^1] is var last && last.Initialized && remaining.Initialized &&
                last.Logical + last.Length == remaining.Logical && last.Physical + last.Length == remaining.Physical && last.Length < EXTENT_LIMIT)
            {
                var taken = Math.Min(EXTENT_LIMIT - last.Length, remaining.Length);
                extents[^1] = last with { Length = last.Length + taken };
                remaining = remaining with { Logical = remaining.Logical + taken, Physical = remaining.Physical + taken, Length = remaining.Length - taken };
            }
            while (remaining.Length > 0)
            {
                var length = Math.Min(remaining.Length, remaining.Initialized ? EXTENT_LIMIT : EXTENT_LIMIT - 1);
                extents.Add(remaining with { Length = length });
                remaining = remaining with { Logical = remaining.Logical + length, Physical = remaining.Physical + length, Length = remaining.Length - length };
            }
        }
        return extents;
    }

    private static void Header(Span<byte> node, int entries, int max, int depth)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(node, MAGIC);
        BinaryPrimitives.WriteUInt16LittleEndian(node[2..], (ushort)entries);
        BinaryPrimitives.WriteUInt16LittleEndian(node[4..], (ushort)max);
        BinaryPrimitives.WriteUInt16LittleEndian(node[6..], (ushort)depth);
        BinaryPrimitives.WriteUInt32LittleEndian(node[8..], 0);
    }

    private static void Leaf(Span<byte> entry, Ext4Run extent)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)extent.Logical);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], (ushort)(extent.Initialized ? extent.Length : extent.Length + EXTENT_LIMIT));
        BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], (ushort)(extent.Physical >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)extent.Physical);
    }

    #endregion
}
