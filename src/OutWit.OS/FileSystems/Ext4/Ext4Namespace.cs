using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// An ext volume's namespace: paths resolved component by component from the root without following a link, entries
/// as the contract reports them, links' targets, and the check that walks the whole tree.
/// </summary>
internal sealed class Ext4Namespace
{
    #region Constants

    private const int FAST_LINK = Ext4Inode.BLOCK_BYTES;
    private const int NAME_LIMIT = 255;

    #endregion

    #region Fields

    private readonly Ext4Volume m_volume;

    #endregion

    #region Constructors

    public Ext4Namespace(Ext4Volume volume)
    {
        m_volume = volume;
    }

    #endregion

    #region Functions

    /// <summary>
    /// The entry a path names, or null when nothing is there.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The path as resolved and the inode, or null.</returns>
    /// <exception cref="FileSystemException">A directory on the way is no directory (NotADirectory), or the path climbs
    /// above the root (InvalidName).</exception>
    public async ValueTask<(string Path, Ext4Inode Inode)?> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stack = new List<(string Name, Ext4Inode Inode)>();
        var root = await m_volume.ReadInodeAsync(Ext4Superblock.ROOT_INODE, cancellationToken).ConfigureAwait(false);
        var current = root;
        foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
                continue;
            if (component == "..")
            {
                if (stack.Count == 0)
                    throw new FileSystemException(FileSystemError.InvalidName, $"The path '{path}' climbs above the root.");
                stack.RemoveAt(stack.Count - 1);
                current = stack.Count == 0 ? root : stack[^1].Inode;
                continue;
            }
            if (current.Kind != FileSystemEntryKind.Directory)
                throw new FileSystemException(FileSystemError.NotADirectory, $"The path '{path}' runs through a {current.Kind}.");
            var found = await FindAsync(current, Encoding.UTF8.GetBytes(component), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The entry a path names.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The path as resolved and the inode.</returns>
    /// <exception cref="FileSystemException">Nothing is there (NotFound).</exception>
    public async ValueTask<(string Path, Ext4Inode Inode)> RequireAsync(string path, CancellationToken cancellationToken) =>
        await ResolveAsync(path, cancellationToken).ConfigureAwait(false) ??
        throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {path}.");

    /// <summary>
    /// The directory a path's last component would live in, and that component as a name the format holds.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The parent's path and inode, and the name.</returns>
    /// <exception cref="FileSystemException">The path is the root or names no valid entry (InvalidName), or its parent is
    /// missing (NotFound) or no directory (NotADirectory).</exception>
    public async ValueTask<(string ParentPath, Ext4Inode Parent, string Name, byte[] NameBytes)> ParentAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var name = trimmed[(slash + 1)..];
        var bytes = Encoding.UTF8.GetBytes(name);
        if (name.Length == 0 || name is "." or ".." || bytes.Length > NAME_LIMIT || name.Contains('\0'))
            throw new FileSystemException(FileSystemError.InvalidName, $"'{path}' names no entry the volume can hold.");
        var (parentPath, parent) = await RequireAsync(slash <= 0 ? "/" : trimmed[..slash], cancellationToken).ConfigureAwait(false);
        if (parent.Kind != FileSystemEntryKind.Directory)
            throw new FileSystemException(FileSystemError.NotADirectory, $"The parent of '{path}' is no directory.");
        return (parentPath, parent, name, bytes);
    }

    /// <summary>
    /// The inode a directory's entry of a name names.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The inode's number, or null.</returns>
    public async ValueTask<uint?> FindAsync(Ext4Inode directory, byte[] name, CancellationToken cancellationToken)
    {
        foreach (var entry in await Ext4Directory.ReadAsync(m_volume, directory, cancellationToken).ConfigureAwait(false))
        {
            if (entry.NameBytes.AsSpan().SequenceEqual(name))
                return entry.Inode;
        }
        return null;
    }

    /// <summary>
    /// A directory's entries.
    /// </summary>
    /// <param name="path">The directory.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The entries.</returns>
    public async ValueTask<IReadOnlyList<FileSystemEntry>> EnumerateAsync(string path, CancellationToken cancellationToken)
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

    /// <summary>
    /// A link's target: a fast link's from i_block, a slow one's from its block.
    /// </summary>
    /// <param name="path">The link.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The target.</returns>
    public async ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken)
    {
        var (_, inode) = await RequireAsync(path, cancellationToken).ConfigureAwait(false);
        if (inode.Kind != FileSystemEntryKind.SymbolicLink)
            throw new FileSystemException(FileSystemError.NotALink, $"{path} is no symbolic link.");
        var attributeSectors = inode.ExtendedAttributeBlock != 0 ? (ulong)(m_volume.Superblock.BlockSize / 512) : 0;
        if (inode.Size < FAST_LINK && inode.Sectors == attributeSectors && !inode.HasExtents)
            return Encoding.UTF8.GetString(inode.Block, 0, (int)inode.Size);
        if (inode.Size > m_volume.Superblock.BlockSize)
            throw new FileSystemException(FileSystemError.Corrupt, $"The link {path} records a target of {inode.Size} bytes.");
        var target = new byte[inode.Size];
        await new Ext4FileHandle(m_volume, inode, await Ext4DataMap.ReadAsync(m_volume, inode, cancellationToken).ConfigureAwait(false))
            .ReadAsync(0, target, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(target);
    }

    /// <summary>
    /// Walks the tree from the root: every directory's blocks, every inode's record and every file's map are read and
    /// checked; a directory reached twice is a cycle.
    /// </summary>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>What the walk found.</returns>
    public async ValueTask<FileSystemCheckReport> CheckAsync(CancellationToken cancellationToken)
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
    /// A path in a directory.
    /// </summary>
    /// <param name="directory">The directory's path.</param>
    /// <param name="name">The name.</param>
    /// <returns>The path.</returns>
    public static string Join(string directory, string name) => directory == "/" ? "/" + name : directory + "/" + name;

    /// <summary>
    /// An inode as the contract reports it.
    /// </summary>
    /// <param name="path">Its path.</param>
    /// <param name="inode">The inode.</param>
    /// <returns>The entry.</returns>
    public static FileSystemEntry Entry(string path, Ext4Inode inode)
    {
        var slash = path.LastIndexOf('/');
        return new FileSystemEntry(path, path == "/" ? "" : path[(slash + 1)..], inode.Kind,
            inode.Kind == FileSystemEntryKind.Directory ? 0 : inode.Size, FileSystemAttributes.None, inode.Created, inode.Modified, inode.Accessed,
            new FileSystemUnixMetadata(inode.Mode & 0xFFF, inode.UserId, inode.GroupId, inode.Links, inode.Number));
    }

    #endregion
}
