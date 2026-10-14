using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// The changes of a writable mount (D4.3), each one transaction: its metadata gathered in the transaction, its data
/// written to their blocks first, then the journal's commit (or the blocks in place on a volume without a journal).
/// A change that fails leaves the volume as it was: nothing reached its blocks before the commit, and the descriptors
/// and the superblock the mount holds are restored.
/// </summary>
internal sealed class Ext4Writer
{
    #region Constants

    /// <summary>A new regular file's mode: 0100644.</summary>
    public const int FILE_MODE = 0x81A4;

    /// <summary>A new directory's mode: 040755.</summary>
    public const int DIRECTORY_MODE = 0x41ED;

    #endregion

    #region Fields

    private readonly Ext4Volume m_volume;
    private readonly Ext4Journal? m_journal;
    private readonly TimeProvider m_clock;

    #endregion

    #region Constructors

    public Ext4Writer(Ext4Volume volume, Ext4Journal? journal, TimeProvider clock)
    {
        m_volume = volume;
        m_journal = journal;
        m_clock = clock;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Runs one change as a transaction and commits it.
    /// </summary>
    /// <typeparam name="T">The change's result.</typeparam>
    /// <param name="change">The change, given the transaction, its allocation and the time.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The change's result.</returns>
    public async ValueTask<T> ChangeAsync<T>(Func<Ext4Transaction, Ext4Allocator, DateTimeOffset, ValueTask<T>> change, CancellationToken cancellationToken)
    {
        var groups = m_volume.Groups.Snapshot();
        var superblock = m_volume.Superblock.Snapshot();
        var transaction = new Ext4Transaction();
        m_volume.Overlay = transaction;
        try
        {
            var allocator = new Ext4Allocator(m_volume, transaction);
            var result = await change(transaction, allocator, m_clock.GetUtcNow()).ConfigureAwait(false);
            await allocator.SealAsync(cancellationToken).ConfigureAwait(false);
            if (m_journal is not null)
            {
                await m_journal.CommitAsync(transaction.Blocks, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await m_volume.Disk.FlushAsync(cancellationToken).ConfigureAwait(false);
                foreach (var (block, bytes) in transaction.Blocks)
                    await m_volume.Disk.WriteAsync((long)block * m_volume.Superblock.BlockSize, bytes, cancellationToken).ConfigureAwait(false);
                await m_volume.Disk.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }
        catch
        {
            m_volume.Groups.Restore(groups);
            m_volume.Superblock.Restore(superblock);
            throw;
        }
        finally
        {
            m_volume.Overlay = null;
        }
    }

    /// <summary>
    /// Creates an inode and its entry: a file, or a directory with its first block.
    /// </summary>
    /// <param name="transaction">The change.</param>
    /// <param name="allocator">Its allocation.</param>
    /// <param name="now">The time.</param>
    /// <param name="parent">The parent directory's inode.</param>
    /// <param name="name">The entry's name.</param>
    /// <param name="directory">Whether to make a directory.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The new inode's number.</returns>
    public async ValueTask<uint> CreateAsync(Ext4Transaction transaction, Ext4Allocator allocator, DateTimeOffset now, uint parent, byte[] name,
        bool directory, CancellationToken cancellationToken)
    {
        var parentRecord = await Ext4InodeRecord.OpenAsync(m_volume, transaction, parent, cancellationToken).ConfigureAwait(false);
        var group = (parent - 1) / m_volume.Superblock.InodesPerGroup;
        var number = await allocator.AllocateInodeAsync(group, directory, cancellationToken).ConfigureAwait(false);
        var record = await Ext4InodeRecord.CreateAsync(m_volume, transaction, number, directory ? DIRECTORY_MODE : FILE_MODE, directory ? 2 : 1, now,
            cancellationToken).ConfigureAwait(false);
        if (directory)
        {
            var (block, _) = await allocator.AllocateBlocksAsync(m_volume.Groups.InodeTable(group), 1, cancellationToken).ConfigureAwait(false);
            transaction.Put(block, Ext4DirectoryWriter.FirstBlock(m_volume, record, parent));
            await Ext4ExtentWriter.WriteAsync(m_volume, transaction, allocator, record, [new Ext4Run(0, block, 1, true)], [], cancellationToken)
                .ConfigureAwait(false);
            record.Size = m_volume.Superblock.BlockSize;
            record.SectorCount = (ulong)(m_volume.Superblock.BlockSize / 512);
            parentRecord.Links++;
        }
        record.Seal();
        await Ext4DirectoryWriter.InsertAsync(m_volume, transaction, allocator, parentRecord, name, number,
            directory ? Ext4DirectoryWriter.TYPE_DIRECTORY : Ext4DirectoryWriter.TYPE_FILE, cancellationToken).ConfigureAwait(false);
        parentRecord.Touch(now, changed: true, modified: true);
        parentRecord.Seal();
        return number;
    }

    /// <summary>
    /// Writes bytes of a file: the blocks it lacks taken near its others, every block's data written before the commit,
    /// its size grown, its tree written anew.
    /// </summary>
    /// <param name="transaction">The change.</param>
    /// <param name="allocator">Its allocation.</param>
    /// <param name="now">The time.</param>
    /// <param name="inode">The file's inode.</param>
    /// <param name="offset">The first byte.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The file's new size.</returns>
    public async ValueTask<long> WriteAsync(Ext4Transaction transaction, Ext4Allocator allocator, DateTimeOffset now, uint inode, long offset,
        ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var record = await Ext4InodeRecord.OpenAsync(m_volume, transaction, inode, cancellationToken).ConfigureAwait(false);
        var map = await Ext4DataMap.ReadAsync(m_volume, RequireExtents(record.Read()), cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 0)
        {
            var size = m_volume.Superblock.BlockSize;
            var first = (ulong)(offset / size);
            var last = (ulong)((offset + bytes.Length - 1) / size);
            var (runs, fresh) = await MapRangeAsync(allocator, record, map.Runs, first, last, cancellationToken).ConfigureAwait(false);
            await WriteDataAsync(runs, fresh, first, last, offset, bytes, cancellationToken).ConfigureAwait(false);
            await SetTreeAsync(transaction, allocator, record, runs, map.TreeBlocks, cancellationToken).ConfigureAwait(false);
        }
        if (bytes.Length > 0)
            record.Size = Math.Max(record.Size, offset + bytes.Length);
        record.Touch(now, changed: true, modified: true);
        record.Seal();
        return record.Size;
    }

    /// <summary>
    /// Cuts or grows a file: blocks past a cut freed and the cut block's rest zeroed, a growth a hole.
    /// </summary>
    /// <param name="transaction">The change.</param>
    /// <param name="allocator">Its allocation.</param>
    /// <param name="now">The time.</param>
    /// <param name="inode">The file's inode.</param>
    /// <param name="length">The new length.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The new length.</returns>
    public async ValueTask<long> SetLengthAsync(Ext4Transaction transaction, Ext4Allocator allocator, DateTimeOffset now, uint inode, long length,
        CancellationToken cancellationToken)
    {
        var record = await Ext4InodeRecord.OpenAsync(m_volume, transaction, inode, cancellationToken).ConfigureAwait(false);
        var map = await Ext4DataMap.ReadAsync(m_volume, RequireExtents(record.Read()), cancellationToken).ConfigureAwait(false);
        var size = m_volume.Superblock.BlockSize;
        if (length < record.Size)
        {
            var keep = (ulong)((length + size - 1) / size);
            var runs = new List<Ext4Run>();
            foreach (var run in map.Runs)
            {
                if (run.Logical >= keep)
                {
                    await allocator.FreeBlocksAsync(run.Physical, run.Length, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (run.Logical + run.Length > keep)
                {
                    var kept = (uint)(keep - run.Logical);
                    await allocator.FreeBlocksAsync(run.Physical + kept, run.Length - kept, cancellationToken).ConfigureAwait(false);
                    runs.Add(run with { Length = kept });
                }
                else
                {
                    runs.Add(run);
                }
            }
            var within = (int)(length % size);
            var (physical, _) = Find(runs, keep - 1);
            if (within != 0 && physical != 0 && Initialized(runs, keep - 1))
            {
                var block = new byte[size];
                await m_volume.Disk.ReadAsync((long)physical * size, block, cancellationToken).ConfigureAwait(false);
                block.AsSpan(within).Clear();
                await m_volume.Disk.WriteAsync((long)physical * size, block, cancellationToken).ConfigureAwait(false);
            }
            await SetTreeAsync(transaction, allocator, record, runs, map.TreeBlocks, cancellationToken).ConfigureAwait(false);
        }
        record.Size = length;
        record.Touch(now, changed: true, modified: true);
        record.Seal();
        return length;
    }

    /// <summary>
    /// Sets an inode's permission bits (N4.1), keeping its type; the change time moves.
    /// </summary>
    /// <param name="transaction">The change.</param>
    /// <param name="now">The time.</param>
    /// <param name="inode">The inode.</param>
    /// <param name="mode">The permission bits (07777).</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The inode's mode afterwards, type included.</returns>
    public async ValueTask<int> SetModeAsync(Ext4Transaction transaction, DateTimeOffset now, uint inode, int mode, CancellationToken cancellationToken)
    {
        var record = await Ext4InodeRecord.OpenAsync(m_volume, transaction, inode, cancellationToken).ConfigureAwait(false);
        record.Mode = (record.Mode & 0xF000) | (mode & 0xFFF);
        record.Touch(now, changed: true, modified: false);
        record.Seal();
        return record.Mode;
    }

    /// <summary>
    /// Removes an entry and, with its last link, the inode and its blocks.
    /// </summary>
    /// <param name="transaction">The change.</param>
    /// <param name="allocator">Its allocation.</param>
    /// <param name="now">The time.</param>
    /// <param name="parent">The parent directory's inode.</param>
    /// <param name="name">The entry's name.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>When the entry is gone.</returns>
    public async ValueTask<bool> DeleteAsync(Ext4Transaction transaction, Ext4Allocator allocator, DateTimeOffset now, uint parent, byte[] name,
        CancellationToken cancellationToken)
    {
        var parentRecord = await Ext4InodeRecord.OpenAsync(m_volume, transaction, parent, cancellationToken).ConfigureAwait(false);
        var removed = await Ext4DirectoryWriter.RemoveAsync(m_volume, transaction, parentRecord, name, cancellationToken).ConfigureAwait(false) ??
            throw new FileSystemException(FileSystemError.NotFound, "The entry is gone.");
        var record = await Ext4InodeRecord.OpenAsync(m_volume, transaction, removed, cancellationToken).ConfigureAwait(false);
        var inode = record.Read();
        var directory = inode.Kind == FileSystemEntryKind.Directory;
        if (directory)
            parentRecord.Links--;
        record.Links -= directory ? 2 : 1;
        if (record.Links <= 0)
        {
            if (inode.HasExtents || (inode.Kind != FileSystemEntryKind.SymbolicLink && inode.Kind != FileSystemEntryKind.Other))
            {
                var map = await Ext4DataMap.ReadAsync(m_volume, RequireExtents(inode), cancellationToken).ConfigureAwait(false);
                foreach (var run in map.Runs)
                    await allocator.FreeBlocksAsync(run.Physical, run.Length, cancellationToken).ConfigureAwait(false);
                foreach (var block in map.TreeBlocks)
                    await allocator.FreeBlocksAsync(block, 1, cancellationToken).ConfigureAwait(false);
            }
            record.Delete(now);
            await allocator.FreeInodeAsync(removed, directory, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            record.Touch(now, changed: true, modified: false);
        }
        record.Seal();
        parentRecord.Touch(now, changed: true, modified: true);
        parentRecord.Seal();
        return true;
    }

    /// <summary>
    /// Moves an entry to another name, in its directory or another, in one transaction: removed from its old place and
    /// inserted at the new; a directory's ".." and its parents' links follow it.
    /// </summary>
    /// <param name="transaction">The change.</param>
    /// <param name="allocator">Its allocation.</param>
    /// <param name="now">The time.</param>
    /// <param name="fromParent">The old parent's inode.</param>
    /// <param name="fromName">The old name.</param>
    /// <param name="toParent">The new parent's inode.</param>
    /// <param name="toName">The new name.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The moved inode's number.</returns>
    public async ValueTask<uint> RenameAsync(Ext4Transaction transaction, Ext4Allocator allocator, DateTimeOffset now, uint fromParent, byte[] fromName,
        uint toParent, byte[] toName, CancellationToken cancellationToken)
    {
        var source = await Ext4InodeRecord.OpenAsync(m_volume, transaction, fromParent, cancellationToken).ConfigureAwait(false);
        var destination = fromParent == toParent
            ? source
            : await Ext4InodeRecord.OpenAsync(m_volume, transaction, toParent, cancellationToken).ConfigureAwait(false);
        var moved = await Ext4DirectoryWriter.RemoveAsync(m_volume, transaction, source, fromName, cancellationToken).ConfigureAwait(false) ??
            throw new FileSystemException(FileSystemError.NotFound, "The entry is gone.");
        var record = await Ext4InodeRecord.OpenAsync(m_volume, transaction, moved, cancellationToken).ConfigureAwait(false);
        var kind = record.Read().Kind;
        var type = kind switch
        {
            FileSystemEntryKind.Directory => Ext4DirectoryWriter.TYPE_DIRECTORY,
            FileSystemEntryKind.SymbolicLink => Ext4DirectoryWriter.TYPE_LINK,
            _ => Ext4DirectoryWriter.TYPE_FILE
        };
        await Ext4DirectoryWriter.InsertAsync(m_volume, transaction, allocator, destination, toName, moved, type, cancellationToken).ConfigureAwait(false);
        if (kind == FileSystemEntryKind.Directory && fromParent != toParent)
        {
            await Ext4DirectoryWriter.SetParentAsync(m_volume, transaction, record, toParent, cancellationToken).ConfigureAwait(false);
            source.Links--;
            destination.Links++;
        }
        record.Touch(now, changed: true, modified: false);
        record.Seal();
        source.Touch(now, changed: true, modified: true);
        source.Seal();
        destination.Touch(now, changed: true, modified: true);
        destination.Seal();
        return moved;
    }

    // The runs with every block from first to last mapped: holes take new blocks near the blocks before them, and the
    // part of an uninitialized extent the range touches becomes initialized; those blocks are fresh, their old bytes
    // not to be read.
    private async ValueTask<(List<Ext4Run> Runs, HashSet<ulong> Fresh)> MapRangeAsync(Ext4Allocator allocator, Ext4InodeRecord record,
        IReadOnlyList<Ext4Run> current, ulong first, ulong last, CancellationToken cancellationToken)
    {
        var runs = new List<Ext4Run>();
        var fresh = new HashSet<ulong>();
        foreach (var run in current)
        {
            if (run.Initialized || run.Logical + run.Length <= first || run.Logical > last)
            {
                runs.Add(run);
                continue;
            }
            var from = Math.Max(first, run.Logical);
            var to = Math.Min(last, run.Logical + run.Length - 1);
            if (from > run.Logical)
                runs.Add(run with { Length = (uint)(from - run.Logical) });
            runs.Add(new Ext4Run(from, run.Physical + (from - run.Logical), (uint)(to - from + 1), true));
            if (to < run.Logical + run.Length - 1)
                runs.Add(run with { Logical = to + 1, Physical = run.Physical + (to + 1 - run.Logical), Length = (uint)(run.Logical + run.Length - 1 - to) });
            for (var block = from; block <= to; block++)
                fresh.Add(block);
        }
        for (var block = first; block <= last;)
        {
            if (Find(runs, block).Physical != 0)
            {
                block++;
                continue;
            }
            var end = block;
            while (end < last && Find(runs, end + 1).Physical == 0)
                end++;
            var (before, _) = block > 0 ? Find(runs, block - 1) : (0UL, 0UL);
            var goal = before != 0 ? before + 1
                : runs.Count > 0 ? runs[^1].Physical + runs[^1].Length
                : m_volume.Groups.InodeTable((record.Number - 1) / m_volume.Superblock.InodesPerGroup);
            var (start, length) = await allocator.AllocateBlocksAsync(goal, (uint)Math.Min(end - block + 1, uint.MaxValue), cancellationToken)
                .ConfigureAwait(false);
            runs.Add(new Ext4Run(block, start, length, true));
            runs.Sort((a, b) => a.Logical.CompareTo(b.Logical));
            for (var i = 0UL; i < length; i++)
                fresh.Add(block + i);
            block += length;
        }
        return (runs, fresh);
    }

    // The range's data: a fresh block is zeros with the bytes in it, a block the write covers whole is the bytes, and
    // any other is read, changed and written back; physically contiguous blocks go in one write.
    private async ValueTask WriteDataAsync(List<Ext4Run> runs, HashSet<ulong> fresh, ulong first, ulong last, long offset, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var size = m_volume.Superblock.BlockSize;
        for (var block = first; block <= last;)
        {
            var (physical, contiguous) = Find(runs, block);
            var count = Math.Min(contiguous, last - block + 1);
            var buffer = new byte[count * (ulong)size];
            for (var i = 0UL; i < count; i++)
            {
                var logical = block + i;
                var blockStart = (long)logical * size;
                var from = Math.Max(offset, blockStart);
                var to = Math.Min(offset + bytes.Length, blockStart + size);
                var target = buffer.AsMemory((int)(i * (ulong)size), size);
                if (!fresh.Contains(logical) && (from > blockStart || to < blockStart + size))
                    await m_volume.Disk.ReadAsync((long)(physical + i) * size, target, cancellationToken).ConfigureAwait(false);
                bytes.Slice((int)(from - offset), (int)(to - from)).CopyTo(target[(int)(from - blockStart)..]);
            }
            await m_volume.Disk.WriteAsync((long)physical * size, buffer, cancellationToken).ConfigureAwait(false);
            block += count;
        }
    }

    private async ValueTask SetTreeAsync(Ext4Transaction transaction, Ext4Allocator allocator, Ext4InodeRecord record, List<Ext4Run> runs,
        IReadOnlyList<ulong> oldTree, CancellationToken cancellationToken)
    {
        var tree = await Ext4ExtentWriter.WriteAsync(m_volume, transaction, allocator, record, runs, oldTree, cancellationToken).ConfigureAwait(false);
        record.SectorCount = (Ext4DirectoryWriter.Data(runs) + (ulong)tree) * (ulong)(m_volume.Superblock.BlockSize / 512);
    }

    // An inode a change maps: by extents, since the provider writes no block map of ext2 and ext3.
    private static Ext4Inode RequireExtents(Ext4Inode inode) => inode.HasExtents || inode.Size == 0
        ? inode
        : throw new FileSystemException(FileSystemError.Unsupported, $"Inode {inode.Number} maps its data by blocks, which the provider does not write.");

    // A logical block's physical block in runs (zero for a hole) and the blocks from it that continue contiguously.
    private static (ulong Physical, ulong Contiguous) Find(List<Ext4Run> runs, ulong logical)
    {
        foreach (var run in runs)
        {
            if (logical >= run.Logical && logical < run.Logical + run.Length)
                return (run.Physical + (logical - run.Logical), run.Logical + run.Length - logical);
        }
        return (0, 0);
    }

    private static bool Initialized(List<Ext4Run> runs, ulong logical)
    {
        foreach (var run in runs)
        {
            if (logical >= run.Logical && logical < run.Logical + run.Length)
                return run.Initialized;
        }
        return false;
    }

    #endregion
}
