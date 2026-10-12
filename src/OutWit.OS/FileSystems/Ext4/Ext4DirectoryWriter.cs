using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// A directory's entries changed (D4.3): an entry inserted where an entry has room past its name, or in a block added
/// at the directory's end; an entry removed by joining it to the one before it; and a new directory's first block. Each
/// block the change touches gets its tail's checksum again. Inserting into an htree would need its hash order, so the
/// directory first becomes linear: its root and index blocks become empty leaves and its index flag goes, which every
/// reader takes; removing keeps the index, which stays true.
/// </summary>
internal static class Ext4DirectoryWriter
{
    #region Constants

    /// <summary>The file type a regular file's entry records.</summary>
    public const byte TYPE_FILE = 1;

    /// <summary>The file type a directory's entry records.</summary>
    public const byte TYPE_DIRECTORY = 2;

    /// <summary>The file type a link's entry records.</summary>
    public const byte TYPE_LINK = 7;

    private const uint FLAG_INDEX = 0x1000;
    private const int TAIL = 12;
    private const byte TAIL_TYPE = 0xDE;

    #endregion

    #region Functions

    /// <summary>
    /// Inserts an entry.
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="transaction">The change.</param>
    /// <param name="allocator">The change's allocation.</param>
    /// <param name="directory">The directory's record.</param>
    /// <param name="name">The entry's name, checked by the caller.</param>
    /// <param name="inode">The inode it names.</param>
    /// <param name="type">The file type it records.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>When the entry is in the directory.</returns>
    public static async ValueTask InsertAsync(Ext4Volume volume, Ext4Transaction transaction, Ext4Allocator allocator, Ext4InodeRecord directory,
        byte[] name, uint inode, byte type, CancellationToken cancellationToken)
    {
        if ((directory.Flags & FLAG_INDEX) != 0)
            await MakeLinearAsync(volume, transaction, directory, cancellationToken).ConfigureAwait(false);
        var size = volume.Superblock.BlockSize;
        var limit = size - Tail(volume);
        var needed = Length(name.Length);
        var map = await Ext4DataMap.ReadAsync(volume, directory.Read(), cancellationToken).ConfigureAwait(false);
        var blocks = (ulong)(directory.Size / size);
        for (var logical = 0UL; logical < blocks; logical++)
        {
            var (physical, _) = map.Find(logical);
            if (physical == 0)
                continue;
            var block = await transaction.GetAsync(volume, physical, cancellationToken).ConfigureAwait(false);
            for (var at = 0; at < limit;)
            {
                var number = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(at));
                var record = RecordLength(block, at, size);
                var used = number == 0 ? 0 : Length(block[at + 6]);
                if (record < 8 || at + record > limit)
                    throw new FileSystemException(FileSystemError.Corrupt, $"Directory inode {directory.Number} holds an entry of {record} bytes at {at}.");
                if (record - used >= needed)
                {
                    if (used > 0)
                        SetRecordLength(block, at, used, size);
                    Entry(volume, block, at + used, record - used, name, inode, type);
                    Seal(volume, directory, block);
                    return;
                }
                at += record;
            }
        }
        // No block has room: a block at the end, holding the entry alone.
        var last = map.Runs.Count > 0 ? map.Runs[^1].Physical + map.Runs[^1].Length : volume.Groups.InodeTable((directory.Number - 1) / volume.Superblock.InodesPerGroup);
        var (start, _) = await allocator.AllocateBlocksAsync(last, 1, cancellationToken).ConfigureAwait(false);
        var bytes = new byte[size];
        Entry(volume, bytes, 0, limit, name, inode, type);
        Seal(volume, directory, bytes);
        transaction.Put(start, bytes);
        var runs = new List<Ext4Run>(map.Runs) { new(blocks, start, 1, true) };
        var tree = await Ext4ExtentWriter.WriteAsync(volume, transaction, allocator, directory, runs, map.TreeBlocks, cancellationToken).ConfigureAwait(false);
        directory.Size += size;
        directory.SectorCount = (Data(runs) + (ulong)tree) * (ulong)(size / 512);
    }

    /// <summary>
    /// Removes an entry.
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="transaction">The change.</param>
    /// <param name="directory">The directory's record.</param>
    /// <param name="name">The entry's name.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The inode the entry named, or null when no entry has the name.</returns>
    public static async ValueTask<uint?> RemoveAsync(Ext4Volume volume, Ext4Transaction transaction, Ext4InodeRecord directory, byte[] name,
        CancellationToken cancellationToken)
    {
        var size = volume.Superblock.BlockSize;
        var limit = size - Tail(volume);
        var map = await Ext4DataMap.ReadAsync(volume, directory.Read(), cancellationToken).ConfigureAwait(false);
        var blocks = (ulong)(directory.Size / size);
        var indexed = (directory.Flags & FLAG_INDEX) != 0;
        for (var logical = indexed ? 1UL : 0UL; logical < blocks; logical++)
        {
            var (physical, _) = map.Find(logical);
            if (physical == 0)
                continue;
            var peek = new byte[size];
            await volume.ReadBlocksAsync(physical, peek, cancellationToken).ConfigureAwait(false);
            if (indexed && IsIndexNode(peek))
                continue;
            var previous = -1;
            for (var at = 0; at < limit;)
            {
                var number = BinaryPrimitives.ReadUInt32LittleEndian(peek.AsSpan(at));
                var record = RecordLength(peek, at, size);
                if (record < 8 || at + record > limit)
                    throw new FileSystemException(FileSystemError.Corrupt, $"Directory inode {directory.Number} holds an entry of {record} bytes at {at}.");
                if (number != 0 && peek[at + 6] == name.Length && peek.AsSpan(at + 8, name.Length).SequenceEqual(name))
                {
                    var block = await transaction.GetAsync(volume, physical, cancellationToken).ConfigureAwait(false);
                    if (previous >= 0)
                        SetRecordLength(block, previous, RecordLength(block, previous, size) + record, size);
                    else
                        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at), 0);
                    Seal(volume, directory, block);
                    return number;
                }
                previous = at;
                at += record;
            }
        }
        return null;
    }

    /// <summary>
    /// A new directory's first block: "." and "..".
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="directory">The new directory's record.</param>
    /// <param name="parent">Its parent's inode.</param>
    /// <returns>The block's bytes.</returns>
    public static byte[] FirstBlock(Ext4Volume volume, Ext4InodeRecord directory, uint parent)
    {
        var size = volume.Superblock.BlockSize;
        var bytes = new byte[size];
        Entry(volume, bytes, 0, 12, "."u8.ToArray(), directory.Number, TYPE_DIRECTORY);
        Entry(volume, bytes, 12, size - Tail(volume) - 12, ".."u8.ToArray(), parent, TYPE_DIRECTORY);
        Seal(volume, directory, bytes);
        return bytes;
    }

    /// <summary>
    /// Points a directory's ".." at another parent.
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="transaction">The change.</param>
    /// <param name="directory">The directory's record.</param>
    /// <param name="parent">The new parent's inode.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>When ".." names the parent.</returns>
    public static async ValueTask SetParentAsync(Ext4Volume volume, Ext4Transaction transaction, Ext4InodeRecord directory, uint parent,
        CancellationToken cancellationToken)
    {
        if ((directory.Flags & FLAG_INDEX) != 0)
            await MakeLinearAsync(volume, transaction, directory, cancellationToken).ConfigureAwait(false);
        var map = await Ext4DataMap.ReadAsync(volume, directory.Read(), cancellationToken).ConfigureAwait(false);
        var block = await transaction.GetAsync(volume, map.Find(0).Physical, cancellationToken).ConfigureAwait(false);
        if (block[12 + 6] != 2 || block[12 + 8] != '.' || block[12 + 9] != '.')
            throw new FileSystemException(FileSystemError.Corrupt, $"Directory inode {directory.Number} has no \"..\" second.");
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), parent);
        Seal(volume, directory, block);
    }

    /// <summary>
    /// The number of data blocks runs map.
    /// </summary>
    /// <param name="runs">The runs.</param>
    /// <returns>The blocks.</returns>
    public static ulong Data(IReadOnlyList<Ext4Run> runs)
    {
        var blocks = 0UL;
        foreach (var run in runs)
            blocks += run.Length;
        return blocks;
    }

    // An htree made linear: the root keeps "." and ".." over the block, each index node becomes one empty entry, and
    // the index flag goes.
    private static async ValueTask MakeLinearAsync(Ext4Volume volume, Ext4Transaction transaction, Ext4InodeRecord directory,
        CancellationToken cancellationToken)
    {
        var size = volume.Superblock.BlockSize;
        var limit = size - Tail(volume);
        var map = await Ext4DataMap.ReadAsync(volume, directory.Read(), cancellationToken).ConfigureAwait(false);
        var blocks = (ulong)(directory.Size / size);
        for (var logical = 0UL; logical < blocks; logical++)
        {
            var (physical, _) = map.Find(logical);
            if (physical == 0)
                continue;
            var peek = new byte[size];
            await volume.ReadBlocksAsync(physical, peek, cancellationToken).ConfigureAwait(false);
            if (logical == 0)
            {
                var self = BinaryPrimitives.ReadUInt32LittleEndian(peek);
                var parent = BinaryPrimitives.ReadUInt32LittleEndian(peek.AsSpan(12));
                var root = new byte[size];
                Entry(volume, root, 0, 12, "."u8.ToArray(), self, TYPE_DIRECTORY);
                Entry(volume, root, 12, limit - 12, ".."u8.ToArray(), parent, TYPE_DIRECTORY);
                Seal(volume, directory, root);
                transaction.Put(physical, root);
            }
            else if (IsIndexNode(peek))
            {
                var empty = new byte[size];
                SetRecordLength(empty, 0, limit, size);
                Seal(volume, directory, empty);
                transaction.Put(physical, empty);
            }
        }
        directory.Flags &= ~FLAG_INDEX;
    }

    private static void Entry(Ext4Volume volume, byte[] block, int at, int record, byte[] name, uint inode, byte type)
    {
        block.AsSpan(at, record).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at), inode);
        SetRecordLength(block, at, record, block.Length);
        block[at + 6] = (byte)name.Length;
        block[at + 7] = volume.Superblock.HasFileType ? type : (byte)0;
        name.CopyTo(block, at + 8);
    }

    // The leaf's tail when metadata_csum is on: an entry of 12 bytes with type 0xDE, and the block's CRC-32C.
    private static void Seal(Ext4Volume volume, Ext4InodeRecord directory, byte[] block)
    {
        if (!volume.Superblock.HasMetadataChecksums)
            return;
        var tail = block.AsSpan(block.Length - TAIL);
        tail.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(tail[4..], TAIL);
        tail[7] = TAIL_TYPE;
        BinaryPrimitives.WriteUInt32LittleEndian(tail[8..], Ext4Checksums.Crc32C(directory.ChecksumSeed(), block.AsSpan(0, block.Length - TAIL)));
    }

    private static bool IsIndexNode(byte[] block) =>
        BinaryPrimitives.ReadUInt32LittleEndian(block) == 0 && RecordLength(block, 0, block.Length) == block.Length && block[6] == 0;

    private static int Tail(Ext4Volume volume) => volume.Superblock.HasMetadataChecksums ? TAIL : 0;

    private static int Length(int nameLength) => (8 + nameLength + 3) & ~3;

    private static int RecordLength(byte[] block, int at, int size)
    {
        var stored = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(at + 4));
        return size >= 65536 && (stored == 65535 || stored == 0) ? 65536 : stored;
    }

    private static void SetRecordLength(byte[] block, int at, int length, int size) =>
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(at + 4), (ushort)(length == 65536 && size >= 65536 ? 65535 : length));

    #endregion
}
