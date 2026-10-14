using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Memory;

/// <summary>
/// A volume in memory (plan step N4.1): what the namespace service mounts at /tmp, there whatever disks the machine has,
/// and a provider the tests of the contract run without a device. It records what tmpfs records: names, kinds, sizes,
/// modes and times; its files last while the volume does, and their data count against its capacity, past which a
/// write is NoSpace. One operation runs at a time, as the other providers serialize (storage document §52).
/// </summary>
public sealed class MemoryFileSystem : IFileSystem
{
    #region Constants

    /// <summary>A new file's permission bits: 0644.</summary>
    public const int FILE_MODE = 0x1A4;

    /// <summary>A new directory's permission bits: 0755.</summary>
    public const int DIRECTORY_MODE = 0x1ED;

    private const int UNIT = 4096;
    private const int NAME_LIMIT = 255;

    #endregion

    #region Fields

    private readonly Node m_root;
    private readonly TimeProvider m_clock;
    private readonly SemaphoreSlim m_lock = new(1, 1);
    private long m_used;
    private long m_next = 2;
    private int m_disposed;

    #endregion

    #region Constructors

    /// <summary>
    /// An empty volume.
    /// </summary>
    /// <param name="capacity">The bytes its files may hold in all.</param>
    /// <param name="label">Its label, or null.</param>
    /// <param name="clock">The clock of the times it records; the system's by default.</param>
    public MemoryFileSystem(long capacity, string? label = null, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        m_clock = clock ?? TimeProvider.System;
        m_root = new Node(1, "", null, true, DIRECTORY_MODE, m_clock.GetUtcNow());
        Volume = new FileSystemVolume("tmpfs", label, capacity, UNIT, false);
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    public ValueTask<FileSystemEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) =>
        LockedAsync(() => Find(path) is { } node ? Entry(node) : null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FileSystemEntry>> EnumerateAsync(string path, CancellationToken cancellationToken = default) =>
        LockedAsync<IReadOnlyList<FileSystemEntry>>(() => RequireDirectory(path).Children.Select(Entry).ToList(), cancellationToken);

    /// <inheritdoc />
    /// <remarks>The volume records no links: an entry that exists is NotALink.</remarks>
    public ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        LockedAsync<string>(() => Find(path) is null
            ? throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {path}.")
            : throw new FileSystemException(FileSystemError.NotALink, $"{path} is no symbolic link: the volume records none."), cancellationToken);

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The mode is Append: a handle writes at the offsets it names.</exception>
    /// <exception cref="ArgumentException">The mode writes and the access does not.</exception>
    public ValueTask<IFileHandle> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken = default)
    {
        if (mode == FileMode.Append)
            throw new ArgumentOutOfRangeException(nameof(mode), "A handle writes at the offsets it names; there is no append mode.");
        if (mode is FileMode.Create or FileMode.CreateNew or FileMode.Truncate && access == FileAccess.Read)
            throw new ArgumentException($"The mode {mode} writes and the access {access} does not.", nameof(access));
        return LockedAsync<IFileHandle>(() =>
        {
            var (parent, name) = Parent(path);
            var node = parent.Child(name);
            if (node is { IsDirectory: true })
                throw new FileSystemException(FileSystemError.NotAFile, $"{path} is no file.");
            if (node is null && mode is FileMode.Open or FileMode.Truncate)
                throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {path}.");
            if (node is not null && mode == FileMode.CreateNew)
                throw new FileSystemException(FileSystemError.AlreadyExists, $"Something is at {path}.");
            var now = m_clock.GetUtcNow();
            if (node is null)
            {
                node = new Node(m_next++, name, parent, false, FILE_MODE, now);
                parent.Children.Add(node);
                parent.Modified = parent.Changed = now;
            }
            else if (mode is FileMode.Create or FileMode.Truncate)
            {
                SetLength(node, 0, now);
            }
            node.Handles++;
            return new MemoryFileHandle(this, node, access.HasFlag(FileAccess.Read), access.HasFlag(FileAccess.Write));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        LockedAsync(() =>
        {
            var current = m_root;
            foreach (var component in Components(path))
            {
                var next = current.Child(component);
                if (next is { IsDirectory: false })
                    throw new FileSystemException(FileSystemError.NotADirectory, $"{component} under {Path(current)} is no directory.");
                if (next is null)
                {
                    var now = m_clock.GetUtcNow();
                    next = new Node(m_next++, component, current, true, DIRECTORY_MODE, now);
                    current.Children.Add(next);
                    current.Modified = current.Changed = now;
                }
                current = next;
            }
            return Entry(current);
        }, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        await LockedAsync(() =>
        {
            var node = Find(path) ?? throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {path}.");
            if (node.Parent is null)
                throw new FileSystemException(FileSystemError.InvalidName, "The root is not deleted.");
            if (node.IsDirectory && node.Children.Count > 0)
                throw new FileSystemException(FileSystemError.NotEmpty, $"{path} has entries.");
            var now = m_clock.GetUtcNow();
            node.Parent.Children.Remove(node);
            node.Parent.Modified = node.Parent.Changed = now;
            node.Parent = null;
            if (!node.IsDirectory && node.Handles == 0)
                Release(node);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> RenameAsync(string source, string destination, CancellationToken cancellationToken = default) =>
        LockedAsync(() =>
        {
            var node = Find(source) ?? throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {source}.");
            if (node.Parent is null)
                throw new FileSystemException(FileSystemError.InvalidName, "The root is not moved.");
            var (parent, name) = Parent(destination);
            if (parent.Child(name) is not null)
                throw new FileSystemException(FileSystemError.AlreadyExists, $"Something is at {destination}.");
            for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor == node)
                    throw new FileSystemException(FileSystemError.InvalidName, $"{source} would move into itself.");
            }
            var now = m_clock.GetUtcNow();
            node.Parent.Children.Remove(node);
            node.Parent.Modified = node.Parent.Changed = now;
            node.Parent = parent;
            node.Name = name;
            node.Changed = now;
            parent.Children.Add(node);
            parent.Modified = parent.Changed = now;
            return Entry(node);
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>The volume records a mode: the bits are kept as given.</remarks>
    public ValueTask<FileSystemEntry> SetModeAsync(string path, int mode, CancellationToken cancellationToken = default) =>
        LockedAsync(() =>
        {
            var node = Find(path) ?? throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {path}.");
            node.Mode = mode & 0xFFF;
            node.Changed = m_clock.GetUtcNow();
            return Entry(node);
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Nothing is below the memory: a flush has nothing to write.</remarks>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<FileSystemCheckReport> CheckAsync(CancellationToken cancellationToken = default) =>
        LockedAsync(() =>
        {
            int files = 0, directories = 0;
            var problems = new List<string>();
            long used = 0;
            Walk(m_root, node =>
            {
                if (node.IsDirectory)
                    directories++;
                else
                {
                    files++;
                    used += node.Length;
                }
            });
            if (used != m_used)
                problems.Add($"The files hold {used} bytes and the volume counts {m_used}.");
            return new FileSystemCheckReport(problems, files, directories);
        }, cancellationToken);

    /// <summary>
    /// Reads a file's bytes for a handle.
    /// </summary>
    internal ValueTask<int> ReadAsync(Node node, long offset, Memory<byte> buffer, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            if (offset < 0 || offset >= node.Length || buffer.Length == 0)
                return 0;
            var count = (int)Math.Min(buffer.Length, node.Length - offset);
            node.Data.AsSpan((int)offset, count).CopyTo(buffer.Span);
            return count;
        }, cancellationToken);

    /// <summary>
    /// Writes a file's bytes for a handle, growing the file past its end; the gap reads as zeros.
    /// </summary>
    internal ValueTask<long> WriteAsync(Node node, long offset, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            var now = m_clock.GetUtcNow();
            var end = offset + bytes.Length;
            if (end > node.Length)
                SetLength(node, end, now);
            bytes.Span.CopyTo(node.Data.AsSpan((int)offset));
            node.Modified = node.Changed = now;
            return node.Length;
        }, cancellationToken);

    /// <summary>
    /// Cuts or grows a file for a handle.
    /// </summary>
    internal ValueTask<long> SetLengthAsync(Node node, long length, CancellationToken cancellationToken) =>
        LockedAsync(() =>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            SetLength(node, length, m_clock.GetUtcNow());
            return node.Length;
        }, cancellationToken);

    /// <summary>
    /// A handle closed: a file nobody names any more gives its bytes back.
    /// </summary>
    internal async ValueTask CloseAsync(Node node) =>
        await LockedAsync(() =>
        {
            if (--node.Handles == 0 && node.Parent is null)
                Release(node);
            return true;
        }, CancellationToken.None).ConfigureAwait(false);

    // The new length within the capacity, zeros where the file grew.
    private void SetLength(Node node, long length, DateTimeOffset now)
    {
        if (length > int.MaxValue - UNIT)
            throw new FileSystemException(FileSystemError.TooLarge, $"A file of {length} bytes.");
        var delta = length - node.Length;
        if (delta > 0 && m_used + delta > Volume.Length)
            throw new FileSystemException(FileSystemError.NoSpace, $"The volume holds {m_used} of {Volume.Length} bytes.");
        if (length > node.Data.Length)
        {
            var capacity = Math.Max(length, Math.Min(int.MaxValue - UNIT, Math.Max(node.Data.Length * 2L, UNIT)));
            Array.Resize(ref node.Data, (int)capacity);
        }
        else if (length < node.Length)
        {
            node.Data.AsSpan((int)length, (int)(node.Length - length)).Clear();
        }
        node.Length = length;
        m_used += delta;
        node.Modified = node.Changed = now;
    }

    private void Release(Node node)
    {
        m_used -= node.Length;
        node.Length = 0;
        node.Data = [];
    }

    private Node? Find(string path)
    {
        var current = m_root;
        foreach (var component in Components(path))
        {
            current = current.Child(component);
            if (current is null)
                return null;
        }
        return current;
    }

    private Node RequireDirectory(string path)
    {
        var node = Find(path) ?? throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {path}.");
        return node.IsDirectory ? node : throw new FileSystemException(FileSystemError.NotADirectory, $"{path} is no directory.");
    }

    // A path's parent directory, which must exist, and its last component.
    private (Node Parent, string Name) Parent(string path)
    {
        var components = Components(path);
        if (components.Length == 0)
            throw new FileSystemException(FileSystemError.InvalidName, "The root has no parent.");
        var parent = m_root;
        for (var i = 0; i < components.Length - 1; i++)
        {
            parent = parent.Child(components[i]) ?? throw new FileSystemException(FileSystemError.NotFound, $"No directory holds {path}.");
            if (!parent.IsDirectory)
                throw new FileSystemException(FileSystemError.NotADirectory, $"{components[i]} on the way to {path} is no directory.");
        }
        return (parent, components[^1]);
    }

    private static string[] Components(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var component in components)
        {
            if (component is "." or ".." || component.Contains('\0') || System.Text.Encoding.UTF8.GetByteCount(component) > NAME_LIMIT)
                throw new FileSystemException(FileSystemError.InvalidName, $"'{component}' is no name of the volume.");
        }
        return components;
    }

    private static void Walk(Node node, Action<Node> visit)
    {
        visit(node);
        foreach (var child in node.Children)
            Walk(child, visit);
    }

    private static string Path(Node node)
    {
        if (node.Parent is null)
            return "/";
        var parts = new List<string>();
        for (var current = node; current?.Parent is not null; current = current.Parent)
            parts.Add(current.Name);
        parts.Reverse();
        return "/" + string.Join('/', parts);
    }

    private static FileSystemEntry Entry(Node node) => new(Path(node), node.Name, node.IsDirectory ? FileSystemEntryKind.Directory : FileSystemEntryKind.File,
        node.IsDirectory ? 0 : node.Length, FileSystemAttributes.None, node.Created, node.Modified, node.Accessed,
        new FileSystemUnixMetadata(node.Mode, 0, 0, node.IsDirectory ? 2 + node.Children.Count(child => child.IsDirectory) : 1, node.Id));

    private async ValueTask<T> LockedAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        await m_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(m_disposed != 0, this);
            return operation();
        }
        finally
        {
            m_lock.Release();
        }
    }

    #endregion

    #region IAsyncDisposable

    /// <summary>
    /// Ends the volume; what it held is gone.
    /// </summary>
    /// <returns>When it ended.</returns>
    public async ValueTask DisposeAsync()
    {
        await m_lock.WaitAsync().ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref m_disposed, 1);
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

    #region Nested Types

    /// <summary>
    /// A file or a directory of the volume.
    /// </summary>
    internal sealed class Node(long id, string name, Node? parent, bool isDirectory, int mode, DateTimeOffset now)
    {
        public long Id { get; } = id;

        public string Name { get; set; } = name;

        public Node? Parent { get; set; } = parent;

        public bool IsDirectory { get; } = isDirectory;

        public int Mode { get; set; } = mode;

        public DateTimeOffset Created { get; } = now;

        public DateTimeOffset Modified { get; set; } = now;

        public DateTimeOffset Accessed { get; } = now;

        public DateTimeOffset Changed { get; set; } = now;

        public List<Node> Children { get; } = [];

        public byte[] Data = [];

        public long Length { get; set; }

        public int Handles { get; set; }

        public Node? Child(string name) => Children.Find(child => string.Equals(child.Name, name, StringComparison.Ordinal));
    }

    #endregion
}
