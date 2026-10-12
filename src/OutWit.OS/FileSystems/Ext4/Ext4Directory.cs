using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// A directory's entries ("Directory Entries"), read linearly from every block: an htree's index blocks hold no entries
/// a linear reader sees (its root holds "." and "..", its nodes one empty entry over the whole block), so the leaves
/// give every name. With metadata_csum each leaf ends with a tail whose CRC-32C covers the block, seeded by the
/// directory's inode. Every entry's length is checked against its block before its name is read.
/// </summary>
internal static class Ext4Directory
{
    #region Constants

    private const int TAIL = 12;
    private const byte TAIL_TYPE = 0xDE;

    #endregion

    #region Functions

    /// <summary>
    /// Reads a directory's entries, without "." and "..".
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="directory">The directory's inode.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The entries, in the blocks' order.</returns>
    /// <exception cref="FileSystemException">A block fails its checksum or holds an entry that overruns it (Corrupt).</exception>
    public static async ValueTask<List<Entry>> ReadAsync(Ext4Volume volume, Ext4Inode directory, CancellationToken cancellationToken)
    {
        var size = volume.Superblock.BlockSize;
        if (directory.Size % size != 0)
            throw new FileSystemException(FileSystemError.Corrupt, $"Directory inode {directory.Number} holds {directory.Size} bytes, no whole blocks.");
        var map = await Ext4DataMap.ReadAsync(volume, directory, cancellationToken).ConfigureAwait(false);
        var blocks = (ulong)(directory.Size / size);
        var entries = new List<Entry>();
        for (var logical = 0UL; logical < blocks;)
        {
            var (physical, run) = map.Find(logical);
            var count = (int)Math.Min(Math.Min(run, blocks - logical), 64);
            if (physical != 0)
            {
                var bytes = new byte[count * size];
                await volume.ReadBlocksAsync(physical, bytes, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < count; i++)
                    Parse(volume.Superblock, directory, logical + (ulong)i, bytes.AsSpan(i * size, size), entries);
            }
            logical += (ulong)count; // a hole holds no entries
        }
        return entries;
    }

    private static void Parse(Ext4Superblock superblock, Ext4Inode directory, ulong logical, ReadOnlySpan<byte> block, List<Entry> entries)
    {
        if (directory.IsIndexed && (logical == 0 || IsIndexNode(block)))
        {
            if (logical != 0)
                return; // an index node: no entries
            ParseEntries(superblock, directory, logical, block, entries); // the root: "." and ".." alone
            return;
        }
        var limit = block.Length;
        if (superblock.HasMetadataChecksums)
        {
            var tail = block[^TAIL..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(tail[4..]) != TAIL ||
                tail[6] != 0 || tail[7] != TAIL_TYPE)
                throw new FileSystemException(FileSystemError.Corrupt, $"Block {logical} of directory inode {directory.Number} has no checksum tail.");
            if (Ext4Checksums.Crc32C(directory.ChecksumSeed(superblock), block[..^TAIL]) != BinaryPrimitives.ReadUInt32LittleEndian(tail[8..]))
                throw new FileSystemException(FileSystemError.Corrupt, $"Block {logical} of directory inode {directory.Number} fails its checksum.");
            limit -= TAIL;
        }
        ParseEntries(superblock, directory, logical, block[..limit], entries);
    }

    private static void ParseEntries(Ext4Superblock superblock, Ext4Inode directory, ulong logical, ReadOnlySpan<byte> block, List<Entry> entries)
    {
        for (var at = 0; at < block.Length;)
        {
            if (block.Length - at < 8)
                throw new FileSystemException(FileSystemError.Corrupt, $"Block {logical} of directory inode {directory.Number} ends inside an entry.");
            var inode = BinaryPrimitives.ReadUInt32LittleEndian(block[at..]);
            var length = RecordLength(BinaryPrimitives.ReadUInt16LittleEndian(block[(at + 4)..]), superblock.BlockSize);
            var nameLength = superblock.HasFileType ? block[at + 6] : BinaryPrimitives.ReadUInt16LittleEndian(block[(at + 6)..]);
            if (length < 8 + nameLength || length % 4 != 0 || length > block.Length - at)
                throw new FileSystemException(FileSystemError.Corrupt, $"Block {logical} of directory inode {directory.Number} holds an entry of " +
                    $"{length} bytes with a name of {nameLength} at {at}.");
            if (inode != 0 && nameLength > 0)
            {
                var name = block.Slice(at + 8, nameLength);
                if (!(nameLength == 1 && name[0] == '.') && !(nameLength == 2 && name[0] == '.' && name[1] == '.'))
                    entries.Add(new Entry(Encoding.UTF8.GetString(name), name.ToArray(), inode));
            }
            at += length;
        }
    }

    // An htree node: one empty entry over the whole block, its index after it.
    private static bool IsIndexNode(ReadOnlySpan<byte> block) =>
        BinaryPrimitives.ReadUInt32LittleEndian(block) == 0 &&
        RecordLength(BinaryPrimitives.ReadUInt16LittleEndian(block[4..]), block.Length) == block.Length && block[6] == 0;

    // A record length as ext4 stores it: 65535 or 0 for a whole block of 65536 bytes.
    private static int RecordLength(ushort stored, int blockSize) =>
        blockSize >= 65536 && (stored == 65535 || stored == 0) ? 65536 : stored;

    #endregion

    #region Nested Types

    /// <summary>
    /// A directory entry.
    /// </summary>
    /// <param name="Name">The name, decoded as UTF-8.</param>
    /// <param name="NameBytes">The name as recorded.</param>
    /// <param name="Inode">The inode it names.</param>
    public readonly record struct Entry(string Name, byte[] NameBytes, uint Inode);

    #endregion
}
