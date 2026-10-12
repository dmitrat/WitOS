using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.Storage;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// ext4, ext3 and ext2 read by a managed provider (storage document §17–19, plan step D4.2): the on-disk format, not
/// Linux's VFS. Mounted read-only: the superblock and the group descriptors are checked at the mount, every inode,
/// extent block and directory block when it is read, and nothing the volume holds is trusted before (§72). A lookup
/// never follows a symbolic link. Writing with the journal is D4.3; until then every change is ReadOnly.
/// </summary>
public sealed class Ext4FileSystem : IFileSystem
{
    #region Constants

    private const ushort MAGIC = 0xEF53;
    private const int FAST_LINK = Ext4Inode.BLOCK_BYTES;

    #endregion

    #region Fields

    private readonly Ext4Volume m_volume;

    #endregion

    #region Constructors

    private Ext4FileSystem(Ext4Volume volume)
    {
        m_volume = volume;
        var superblock = volume.Superblock;
        Volume = new FileSystemVolume(superblock.Format, superblock.Label.Length == 0 ? null : superblock.Label,
            (long)superblock.BlocksCount * superblock.BlockSize, superblock.BlockSize, true);
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
    /// Mounts the volume at the start of a resource, read-only.
    /// </summary>
    /// <param name="storage">The resource; it stays its owner's.</param>
    /// <param name="cancellationToken">Cancels the mount.</param>
    /// <returns>The filesystem.</returns>
    /// <exception cref="FileSystemException">No ext volume the provider serves is there (Unsupported), or it is damaged (Corrupt).</exception>
    public static async ValueTask<Ext4FileSystem> MountAsync(IBlockStorage storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var volume = await Ext4Volume.OpenAsync(storage, cancellationToken).ConfigureAwait(false);
        var root = await volume.ReadInodeAsync(Ext4Superblock.ROOT_INODE, cancellationToken).ConfigureAwait(false);
        if (root.Kind != FileSystemEntryKind.Directory)
            throw new FileSystemException(FileSystemError.Corrupt, "The volume's root inode is no directory.");
        return new Ext4FileSystem(volume);
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default)
    {
        var found = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        return found is { } entry ? Entry(entry.Path, entry.Inode) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<FileSystemEntry>> EnumerateAsync(string path, CancellationToken cancellationToken = default)
    {
        var (directoryPath, directory) = await RequireAsync(path, cancellationToken).ConfigureAwait(false);
        if (directory.Kind != FileSystemEntryKind.Directory)
            throw new FileSystemException(FileSystemError.NotADirectory, $"{path} is no directory.");
        var entries = new List<FileSystemEntry>();
        foreach (var entry in await Ext4Directory.ReadAsync(m_volume, directory, cancellationToken).ConfigureAwait(false))
        {
            var inode = await m_volume.ReadInodeAsync(entry.Inode, cancellationToken).ConfigureAwait(false);
            entries.Add(Entry(Join(directoryPath, entry.Name), inode));
        }
        return entries;
    }

    /// <inheritdoc />
    public async ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken = default)
    {
        var (_, inode) = await RequireAsync(path, cancellationToken).ConfigureAwait(false);
        if (inode.Kind != FileSystemEntryKind.SymbolicLink)
            throw new FileSystemException(FileSystemError.NotALink, $"{path} is no symbolic link.");
        var attributeSectors = inode.ExtendedAttributeBlock != 0 ? (ulong)(m_volume.Superblock.BlockSize / 512) : 0;
        if (inode.Size < FAST_LINK && inode.Sectors == attributeSectors && !inode.HasExtents)
            return Encoding.UTF8.GetString(inode.Block, 0, (int)inode.Size); // a fast link: the target in i_block
        if (inode.Size > m_volume.Superblock.BlockSize)
            throw new FileSystemException(FileSystemError.Corrupt, $"The link {path} records a target of {inode.Size} bytes.");
        var target = new byte[inode.Size];
        await new Ext4FileHandle(m_volume, inode, await Ext4DataMap.ReadAsync(m_volume, inode, cancellationToken).ConfigureAwait(false))
            .ReadAsync(0, target, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(target);
    }

    /// <inheritdoc />
    public async ValueTask<IFileHandle> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken = default)
    {
        if (mode != FileMode.Open || access != FileAccess.Read)
            throw ReadOnly();
        var (_, inode) = await RequireAsync(path, cancellationToken).ConfigureAwait(false);
        if (inode.Kind != FileSystemEntryKind.File)
            throw new FileSystemException(FileSystemError.NotAFile, $"{path} is no file.");
        return new Ext4FileHandle(m_volume, inode, await Ext4DataMap.ReadAsync(m_volume, inode, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc />
    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> RenameAsync(string source, string destination, CancellationToken cancellationToken = default) =>
        throw ReadOnly();

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => default;

    /// <inheritdoc />
    /// <remarks>Walks the tree from the root: every directory's blocks, every inode's record and every file's map are
    /// read and checked; a directory reached twice is a cycle.</remarks>
    public async ValueTask<FileSystemCheckReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        var visited = new HashSet<uint>();
        var pending = new Queue<(string Path, uint Inode)>();
        pending.Enqueue(("/", Ext4Superblock.ROOT_INODE));
        int files = 0, directories = 0;
        while (pending.Count > 0)
        {
            var (path, number) = pending.Dequeue();
            try
            {
                var inode = await m_volume.ReadInodeAsync(number, cancellationToken).ConfigureAwait(false);
                switch (inode.Kind)
                {
                    case FileSystemEntryKind.Directory when !visited.Add(number):
                        problems.Add($"{path}: directory inode {number} is reached twice");
                        break;
                    case FileSystemEntryKind.Directory:
                        directories++;
                        foreach (var entry in await Ext4Directory.ReadAsync(m_volume, inode, cancellationToken).ConfigureAwait(false))
                            pending.Enqueue((Join(path, entry.Name), entry.Inode));
                        break;
                    case FileSystemEntryKind.File:
                        files++;
                        await Ext4DataMap.ReadAsync(m_volume, inode, cancellationToken).ConfigureAwait(false);
                        break;
                    case FileSystemEntryKind.SymbolicLink:
                        await ReadLinkAsync(path, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (FileSystemException exception)
            {
                problems.Add($"{path}: {exception.Message}");
            }
        }
        return new FileSystemCheckReport(problems, files, directories);
    }

    /// <summary>
    /// The failure of a change, until D4.3 writes.
    /// </summary>
    /// <returns>The failure.</returns>
    internal static FileSystemException ReadOnly() =>
        new(FileSystemError.ReadOnly, "The ext4 provider mounts read-only until it writes through the journal (D4.3).");

    // The entry a path names, or null when nothing is there; a directory on the way that is no directory fails.
    private async ValueTask<(string Path, Ext4Inode Inode)?> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stack = new List<(string Name, Ext4Inode Inode)>();
        var current = await m_volume.ReadInodeAsync(Ext4Superblock.ROOT_INODE, cancellationToken).ConfigureAwait(false);
        foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
                continue;
            if (component == "..")
            {
                if (stack.Count == 0)
                    throw new FileSystemException(FileSystemError.InvalidName, $"The path '{path}' climbs above the root.");
                stack.RemoveAt(stack.Count - 1);
                current = stack.Count == 0
                    ? await m_volume.ReadInodeAsync(Ext4Superblock.ROOT_INODE, cancellationToken).ConfigureAwait(false)
                    : stack[^1].Inode;
                continue;
            }
            if (current.Kind != FileSystemEntryKind.Directory)
                throw new FileSystemException(FileSystemError.NotADirectory, $"The path '{path}' runs through a {current.Kind}.");
            var name = Encoding.UTF8.GetBytes(component);
            uint? found = null;
            foreach (var entry in await Ext4Directory.ReadAsync(m_volume, current, cancellationToken).ConfigureAwait(false))
            {
                if (entry.NameBytes.AsSpan().SequenceEqual(name))
                {
                    found = entry.Inode;
                    break;
                }
            }
            if (found is null)
                return null;
            current = await m_volume.ReadInodeAsync(found.Value, cancellationToken).ConfigureAwait(false);
            stack.Add((component, current));
        }
        var parts = new string[stack.Count];
        for (var i = 0; i < parts.Length; i++)
            parts[i] = stack[i].Name;
        return ("/" + string.Join('/', parts), current);
    }

    private async ValueTask<(string Path, Ext4Inode Inode)> RequireAsync(string path, CancellationToken cancellationToken) =>
        await ResolveAsync(path, cancellationToken).ConfigureAwait(false) ??
        throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {path}.");

    private static string Join(string directory, string name) => directory == "/" ? "/" + name : directory + "/" + name;

    private static FileSystemEntry Entry(string path, Ext4Inode inode)
    {
        var slash = path.LastIndexOf('/');
        return new FileSystemEntry(path, path == "/" ? "" : path[(slash + 1)..], inode.Kind,
            inode.Kind == FileSystemEntryKind.Directory ? 0 : inode.Size, FileSystemAttributes.None, inode.Created, inode.Modified, inode.Accessed,
            new FileSystemUnixMetadata(inode.Mode & 0xFFF, inode.UserId, inode.GroupId, inode.Links, inode.Number));
    }

    #endregion

    #region IAsyncDisposable

    /// <summary>
    /// Unmounts the volume; the resource stays open, and nothing was written.
    /// </summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync() => default;

    #endregion

    #region Properties

    /// <inheritdoc />
    public FileSystemVolume Volume { get; }

    #endregion
}
