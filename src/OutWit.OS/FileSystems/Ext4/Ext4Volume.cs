using System;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.Storage;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// A mounted ext volume's checked structures and its reads: blocks within the volume and inodes by number. On a
/// read-only mount everything it holds is read at the mount and never changes, so reads may run at once; on a writable
/// mount (D4.3) the change in progress is an overlay its reads see, one change at a time.
/// </summary>
internal sealed class Ext4Volume
{
    #region Constructors

    private Ext4Volume(Ext4Disk disk, Ext4Superblock superblock, Ext4GroupTable groups)
    {
        Disk = disk;
        Superblock = superblock;
        Groups = groups;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the superblock and the group descriptors of the volume at the start of a resource.
    /// </summary>
    /// <param name="storage">The resource.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The volume.</returns>
    /// <exception cref="FileSystemException">No ext volume the reader serves is there (Unsupported), or it is damaged (Corrupt).</exception>
    public static async ValueTask<Ext4Volume> OpenAsync(IBlockStorage storage, CancellationToken cancellationToken)
    {
        var disk = new Ext4Disk(storage);
        if (disk.Length < Ext4Superblock.OFFSET + Ext4Superblock.SIZE)
            throw new FileSystemException(FileSystemError.Unsupported, $"A resource of {disk.Length} bytes holds no ext superblock.");
        var bytes = new byte[Ext4Superblock.SIZE];
        await disk.ReadAsync(Ext4Superblock.OFFSET, bytes, cancellationToken).ConfigureAwait(false);
        var superblock = Ext4Superblock.Read(bytes, disk.Length);
        var groups = await Ext4GroupTable.ReadAsync(disk, superblock, cancellationToken).ConfigureAwait(false);
        return new Ext4Volume(disk, superblock, groups);
    }

    /// <summary>
    /// Reads whole blocks of the volume.
    /// </summary>
    /// <param name="block">The first block.</param>
    /// <param name="buffer">What to fill, whole blocks.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>When the blocks are in the buffer.</returns>
    /// <exception cref="FileSystemException">The blocks lie beyond the volume (Corrupt).</exception>
    public async ValueTask ReadBlocksAsync(ulong block, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var size = Superblock.BlockSize;
        var count = (ulong)(buffer.Length / size);
        if (block >= Superblock.BlocksCount || count > Superblock.BlocksCount - block)
            throw new FileSystemException(FileSystemError.Corrupt, $"The volume names blocks {block} to {block + count} of its {Superblock.BlocksCount}.");
        await Disk.ReadAsync((long)block * size, buffer, cancellationToken).ConfigureAwait(false);
        if (Overlay is not { Count: > 0 } overlay)
            return;
        for (var i = 0UL; i < count; i++)
        {
            if (overlay.TryGet(block + i, out var changed))
                changed.CopyTo(buffer.Slice((int)i * size, size));
        }
    }

    /// <summary>
    /// Where an inode's record lies.
    /// </summary>
    /// <param name="number">The inode's number.</param>
    /// <returns>Its inode table block and its offset there.</returns>
    /// <exception cref="FileSystemException">The number lies outside the volume's inodes (Corrupt).</exception>
    public (ulong Block, int Offset) InodeLocation(uint number)
    {
        if (number == 0 || number > Superblock.InodesCount)
            throw new FileSystemException(FileSystemError.Corrupt, $"An entry names inode {number} of the volume's {Superblock.InodesCount}.");
        var index = number - 1;
        var offset = (long)(index % Superblock.InodesPerGroup) * Superblock.InodeSize;
        return (Groups.InodeTable(index / Superblock.InodesPerGroup) + (ulong)(offset / Superblock.BlockSize), (int)(offset % Superblock.BlockSize));
    }

    /// <summary>
    /// Reads and checks an inode.
    /// </summary>
    /// <param name="number">Its number.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The inode.</returns>
    /// <exception cref="FileSystemException">The number lies outside the volume's inodes, or the record is damaged (Corrupt).</exception>
    public async ValueTask<Ext4Inode> ReadInodeAsync(uint number, CancellationToken cancellationToken)
    {
        var (block, offset) = InodeLocation(number);
        var bytes = new byte[Superblock.BlockSize];
        await ReadBlocksAsync(block, bytes, cancellationToken).ConfigureAwait(false);
        return Ext4Inode.Read(number, bytes.AsSpan(offset, Superblock.InodeSize), Superblock);
    }

    /// <summary>
    /// Writes the superblock in place, outside the journal, and flushes: a writable mount's needs_recovery flag at the
    /// mount and the unmount.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>When the superblock is durable.</returns>
    public async ValueTask WriteSuperblockAsync(CancellationToken cancellationToken)
    {
        await Disk.FlushAsync(cancellationToken).ConfigureAwait(false);
        await Disk.WriteAsync(Ext4Superblock.OFFSET, Superblock.Seal(), cancellationToken).ConfigureAwait(false);
        await Disk.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Properties

    /// <summary>The resource, read by byte ranges.</summary>
    public Ext4Disk Disk { get; }

    /// <summary>The checked superblock.</summary>
    public Ext4Superblock Superblock { get; }

    /// <summary>The checked group descriptors.</summary>
    public Ext4GroupTable Groups { get; }

    /// <summary>The change in progress on a writable mount, whose blocks reads see; null otherwise.</summary>
    public Ext4Transaction? Overlay { get; set; }

    #endregion
}
