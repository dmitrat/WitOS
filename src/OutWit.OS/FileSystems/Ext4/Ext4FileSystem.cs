using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.Storage;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// ext4, ext3 and ext2 through a managed provider (storage document §17–19, plan steps D4.2 and D4.3): the on-disk
/// format, not Linux's VFS. Every structure is checked when it is read, and nothing the volume holds is trusted before
/// (§72); a lookup never follows a symbolic link.
/// <list type="bullet">
/// <item>A read-only resource, or a volume without extents or with a read-only compatible feature the provider does
/// not write, mounts read-only, and its reads run at once; a journal that needs recovery is refused there.</item>
/// <item>A writable ext4 resource mounts writable: the journal is replayed first, the superblock says needs_recovery
/// while the volume is mounted, and every change is one transaction through the journal (§45–49), one at a time.</item>
/// </list>
/// </summary>
public sealed class Ext4FileSystem : IFileSystem
{
    #region Constants

    private const ushort MAGIC = 0xEF53;

    #endregion

    #region Fields

    private readonly Ext4Volume m_volume;
    private readonly Ext4Namespace m_names;
    private readonly Ext4Writer? m_writer;
    private readonly SemaphoreSlim m_lock = new(1, 1);
    private int m_disposed;

    #endregion

    #region Constructors

    private Ext4FileSystem(Ext4Volume volume, Ext4Writer? writer)
    {
        m_volume = volume;
        m_names = new Ext4Namespace(volume);
        m_writer = writer;
        var superblock = volume.Superblock;
        Volume = new FileSystemVolume(superblock.Format, superblock.Label.Length == 0 ? null : superblock.Label,
            (long)superblock.BlocksCount * superblock.BlockSize, superblock.BlockSize, writer is null);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Whether a resource holds an ext superblock at its start (storage document §58: detection is not authority; the
    /// mount checks the rest).
    /// </summary>
    /// <param name="storage">The resource.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>Whether the superblock's magic is there.</returns>
    public static async ValueTask<bool> DetectAsync(IBlockStorage storage, CancellationToken cancellationToken = default)
    {
        var disk = new Ext4Disk(storage);
        if (disk.Length < Ext4Superblock.OFFSET + Ext4Superblock.SIZE)
            return false;
        var bytes = new byte[Ext4Superblock.SIZE];
        await disk.ReadAsync(Ext4Superblock.OFFSET, bytes, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x38)) == MAGIC;
    }

    /// <summary>
    /// Mounts the volume at the start of a resource: writable when the resource and the volume allow it.
    /// </summary>
    /// <param name="storage">The resource; it stays its owner's.</param>
    /// <param name="cancellationToken">Cancels the mount.</param>
    /// <returns>The filesystem.</returns>
    /// <exception cref="FileSystemException">No ext volume the provider serves is there, or its journal needs a recovery
    /// a read-only mount cannot make (Unsupported), or it is damaged (Corrupt).</exception>
    public static ValueTask<Ext4FileSystem> MountAsync(IBlockStorage storage, CancellationToken cancellationToken = default) =>
        MountAsync(storage, TimeProvider.System, cancellationToken);

    /// <summary>
    /// Mounts the volume at the start of a resource with a clock for the times it records.
    /// </summary>
    /// <param name="storage">The resource; it stays its owner's.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="cancellationToken">Cancels the mount.</param>
    /// <returns>The filesystem.</returns>
    public static async ValueTask<Ext4FileSystem> MountAsync(IBlockStorage storage, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var volume = await Ext4Volume.OpenAsync(storage, cancellationToken).ConfigureAwait(false);
        var superblock = volume.Superblock;
        var writable = !storage.Info.IsReadOnly && superblock.HasExtents && superblock.CanWrite;
        if (superblock.NeedsRecovery && !(writable && superblock.HasJournal))
            throw new FileSystemException(FileSystemError.Unsupported, "The volume's journal needs recovery, which a read-only mount does not make.");
        Ext4Writer? writer = null;
        if (writable)
        {
            if (superblock.HasJournal)
            {
                var journal = await Ext4Journal.OpenAsync(volume, cancellationToken).ConfigureAwait(false);
                if (!journal.IsEmpty)
                {
                    await journal.ReplayAsync(cancellationToken).ConfigureAwait(false);
                    volume = await Ext4Volume.OpenAsync(storage, cancellationToken).ConfigureAwait(false); // the metadata the log held
                    journal = await Ext4Journal.OpenAsync(volume, cancellationToken).ConfigureAwait(false);
                }
                volume.Superblock.NeedsRecovery = true;
                writer = new Ext4Writer(volume, journal, clock);
            }
            else
            {
                writer = new Ext4Writer(volume, null, clock);
            }
            await volume.WriteSuperblockAsync(cancellationToken).ConfigureAwait(false);
        }
        var root = await volume.ReadInodeAsync(Ext4Superblock.ROOT_INODE, cancellationToken).ConfigureAwait(false);
        if (root.Kind != FileSystemEntryKind.Directory)
            throw new FileSystemException(FileSystemError.Corrupt, "The volume's root inode is no directory.");
        return new Ext4FileSystem(volume, writer);
    }

    /// <inheritdoc />
    public ValueTask<FileSystemEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) =>
        ReadAsync<FileSystemEntry?>(async () =>
            await m_names.ResolveAsync(path, cancellationToken).ConfigureAwait(false) is { } found ? Ext4Namespace.Entry(found.Path, found.Inode) : null,
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FileSystemEntry>> EnumerateAsync(string path, CancellationToken cancellationToken = default) =>
        ReadAsync(() => m_names.EnumerateAsync(path, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        ReadAsync(() => m_names.ReadLinkAsync(path, cancellationToken), cancellationToken);

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The mode is Append: a handle writes at the offsets it names.</exception>
    /// <exception cref="ArgumentException">The mode writes and the access does not.</exception>
    public async ValueTask<IFileHandle> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken = default)
    {
        if (mode == FileMode.Append)
            throw new ArgumentOutOfRangeException(nameof(mode), "A handle writes at the offsets it names; there is no append mode.");
        if (mode is FileMode.Create or FileMode.CreateNew or FileMode.Truncate && access == FileAccess.Read)
            throw new ArgumentException($"The mode {mode} writes and the access {access} does not.", nameof(access));
        if (m_writer is null)
        {
            if (mode != FileMode.Open || access != FileAccess.Read)
                throw ReadOnly();
            var (_, inode) = await m_names.RequireAsync(path, cancellationToken).ConfigureAwait(false);
            if (inode.Kind != FileSystemEntryKind.File)
                throw new FileSystemException(FileSystemError.NotAFile, $"{path} is no file.");
            return new Ext4FileHandle(m_volume, inode, await Ext4DataMap.ReadAsync(m_volume, inode, cancellationToken).ConfigureAwait(false));
        }
        return await ChangeAsync<IFileHandle>(async () =>
        {
            var found = await m_names.ResolveAsync(path, cancellationToken).ConfigureAwait(false);
            if (found is { } existing && existing.Inode.Kind != FileSystemEntryKind.File)
                throw new FileSystemException(FileSystemError.NotAFile, $"{path} is no file.");
            if (found is null && mode is FileMode.Open or FileMode.Truncate)
                throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {path}.");
            if (found is not null && mode == FileMode.CreateNew)
                throw new FileSystemException(FileSystemError.AlreadyExists, $"Something is at {path}.");
            uint number;
            if (found is { } file)
            {
                number = file.Inode.Number;
                if (mode is FileMode.Create or FileMode.Truncate && file.Inode.Size != 0)
                    await m_writer.ChangeAsync((transaction, allocator, now) =>
                        m_writer.SetLengthAsync(transaction, allocator, now, number, 0, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var (_, parent, _, name) = await m_names.ParentAsync(path, cancellationToken).ConfigureAwait(false);
                number = await m_writer.ChangeAsync((transaction, allocator, now) =>
                    m_writer.CreateAsync(transaction, allocator, now, parent.Number, name, false, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            var inode = await m_volume.ReadInodeAsync(number, cancellationToken).ConfigureAwait(false);
            return new Ext4WritableHandle(this, number, inode.Size, access.HasFlag(FileAccess.Read), access.HasFlag(FileAccess.Write));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        ChangeAsync(async () =>
        {
            var current = "/";
            foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Ext4Namespace.Join(current, component);
                var found = await m_names.ResolveAsync(next, cancellationToken).ConfigureAwait(false);
                if (found is { } existing)
                {
                    if (existing.Inode.Kind != FileSystemEntryKind.Directory)
                        throw new FileSystemException(FileSystemError.NotADirectory, $"{next} is no directory.");
                    current = existing.Path;
                    continue;
                }
                var (_, parent, _, name) = await m_names.ParentAsync(next, cancellationToken).ConfigureAwait(false);
                await m_writer!.ChangeAsync((transaction, allocator, now) =>
                    m_writer.CreateAsync(transaction, allocator, now, parent.Number, name, true, cancellationToken), cancellationToken).ConfigureAwait(false);
                current = next;
            }
            var (resolved, inode) = await m_names.RequireAsync(current, cancellationToken).ConfigureAwait(false);
            return Ext4Namespace.Entry(resolved, inode);
        }, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        await ChangeAsync(async () =>
        {
            var (_, parent, _, name) = await m_names.ParentAsync(path, cancellationToken).ConfigureAwait(false);
            var (_, inode) = await m_names.RequireAsync(path, cancellationToken).ConfigureAwait(false);
            if (inode.Kind == FileSystemEntryKind.Directory && (await Ext4Directory.ReadAsync(m_volume, inode, cancellationToken).ConfigureAwait(false)).Count > 0)
                throw new FileSystemException(FileSystemError.NotEmpty, $"{path} has entries.");
            return await m_writer!.ChangeAsync((transaction, allocator, now) =>
                m_writer.DeleteAsync(transaction, allocator, now, parent.Number, name, cancellationToken), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> RenameAsync(string source, string destination, CancellationToken cancellationToken = default) =>
        ChangeAsync(async () =>
        {
            var (fromPath, fromParent, _, fromName) = await m_names.ParentAsync(source, cancellationToken).ConfigureAwait(false);
            var (moved, movedInode) = await m_names.RequireAsync(source, cancellationToken).ConfigureAwait(false);
            var (toPath, toParent, toLeaf, toName) = await m_names.ParentAsync(destination, cancellationToken).ConfigureAwait(false);
            if (await m_names.FindAsync(toParent, toName, cancellationToken).ConfigureAwait(false) is not null)
                throw new FileSystemException(FileSystemError.AlreadyExists, $"Something is at {destination}.");
            if (movedInode.Kind == FileSystemEntryKind.Directory && (toPath + "/").StartsWith(moved + "/", StringComparison.Ordinal))
                throw new FileSystemException(FileSystemError.InvalidName, $"{source} would move into itself.");
            await m_writer!.ChangeAsync((transaction, allocator, now) =>
                m_writer.RenameAsync(transaction, allocator, now, fromParent.Number, fromName, toParent.Number, toName, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var target = Ext4Namespace.Join(toPath, toLeaf);
            return Ext4Namespace.Entry(target, await m_volume.ReadInodeAsync(movedInode.Number, cancellationToken).ConfigureAwait(false));
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Every change is durable when it completes; a flush flushes the resource once more.</remarks>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (m_writer is null)
            return;
        await ChangeAsync(async () =>
        {
            await m_volume.Disk.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<FileSystemCheckReport> CheckAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(() => m_names.CheckAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Reads a file of a writable mount as its inode is now.
    /// </summary>
    /// <param name="inode">The file's inode.</param>
    /// <param name="offset">The first byte.</param>
    /// <param name="buffer">What to fill.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes read and the file's length.</returns>
    internal ValueTask<(int Read, long Length)> ReadFileAsync(uint inode, long offset, Memory<byte> buffer, CancellationToken cancellationToken) =>
        ReadAsync(async () =>
        {
            var current = await m_volume.ReadInodeAsync(inode, cancellationToken).ConfigureAwait(false);
            var map = await Ext4DataMap.ReadAsync(m_volume, current, cancellationToken).ConfigureAwait(false);
            return (await new Ext4FileHandle(m_volume, current, map).ReadAsync(offset, buffer, cancellationToken).ConfigureAwait(false), current.Size);
        }, cancellationToken);

    /// <summary>
    /// Writes a file of a writable mount.
    /// </summary>
    /// <param name="inode">The file's inode.</param>
    /// <param name="offset">The first byte.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The file's length.</returns>
    internal ValueTask<long> WriteFileAsync(uint inode, long offset, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        ChangeAsync(() => m_writer!.ChangeAsync((transaction, allocator, now) =>
            m_writer.WriteAsync(transaction, allocator, now, inode, offset, bytes, cancellationToken), cancellationToken), cancellationToken);

    /// <summary>
    /// Cuts or grows a file of a writable mount.
    /// </summary>
    /// <param name="inode">The file's inode.</param>
    /// <param name="length">The new length.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The file's length.</returns>
    internal ValueTask<long> SetFileLengthAsync(uint inode, long length, CancellationToken cancellationToken) =>
        ChangeAsync(() => m_writer!.ChangeAsync((transaction, allocator, now) =>
            m_writer.SetLengthAsync(transaction, allocator, now, inode, length, cancellationToken), cancellationToken), cancellationToken);

    /// <summary>
    /// The failure of a change on a read-only mount.
    /// </summary>
    /// <returns>The failure.</returns>
    internal static FileSystemException ReadOnly() =>
        new(FileSystemError.ReadOnly, "The volume is mounted read-only: its resource refuses writes, or the provider does not write what it uses.");

    // A read: alone on a writable mount, at once on a read-only one.
    private async ValueTask<T> ReadAsync<T>(Func<ValueTask<T>> read, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        if (m_writer is null)
            return await read().ConfigureAwait(false);
        await m_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(m_disposed != 0, this);
            return await read().ConfigureAwait(false);
        }
        finally
        {
            m_lock.Release();
        }
    }

    // A change: refused on a read-only mount, alone on a writable one.
    private ValueTask<T> ChangeAsync<T>(Func<ValueTask<T>> change, CancellationToken cancellationToken) =>
        m_writer is null ? throw ReadOnly() : ReadAsync(change, cancellationToken);

    #endregion

    #region IAsyncDisposable

    /// <summary>
    /// Unmounts the volume: on a writable mount, after the change in progress, the superblock says the journal needs no
    /// recovery and the resource is flushed; the resource stays open.
    /// </summary>
    /// <returns>When the volume is unmounted.</returns>
    public async ValueTask DisposeAsync()
    {
        if (m_writer is null)
        {
            Interlocked.Exchange(ref m_disposed, 1);
            return;
        }
        await m_lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref m_disposed, 1) != 0)
                return;
            m_volume.Superblock.NeedsRecovery = false;
            await m_volume.WriteSuperblockAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            m_lock.Release();
        }
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public FileSystemVolume Volume { get; }

    #endregion
}
