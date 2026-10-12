using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.Namespaces;

namespace OutWit.OS.Namespace.Server;

/// <summary>
/// One client's session with the namespace (witos/namespace.h, plan step D5.1): the files and directories it opened,
/// and each request answered as POSIX answers it — the mount a path lies on, the volume's operation, and the error as
/// the one errno value a filesystem's error has.
/// </summary>
internal sealed class NamespaceSession : IAsyncDisposable
{
    #region Constants

    private const int PATH_LIMIT = 4096;

    #endregion

    #region Fields

    private readonly MountTable m_mounts;
    private readonly INamespaceWindow m_window;
    private readonly Dictionary<long, Opened> m_opened = [];
    private long m_next = 1;

    #endregion

    #region Constructors

    public NamespaceSession(MountTable mounts, INamespaceWindow window)
    {
        m_mounts = mounts;
        m_window = window;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Serves one request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public async ValueTask<NamespaceReply> HandleAsync(NamespaceRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return request.Operation switch
            {
                NamespaceMessages.MOUNTS => Mounts(request),
                NamespaceMessages.OPEN => await OpenAsync(request, cancellationToken).ConfigureAwait(false),
                NamespaceMessages.CLOSE => await CloseAsync(request).ConfigureAwait(false),
                NamespaceMessages.READ => await ReadAsync(request, cancellationToken).ConfigureAwait(false),
                NamespaceMessages.WRITE => await WriteAsync(request, cancellationToken).ConfigureAwait(false),
                NamespaceMessages.STAT => await StatAsync(request, cancellationToken).ConfigureAwait(false),
                NamespaceMessages.FSTAT => await FileStatAsync(request, cancellationToken).ConfigureAwait(false),
                NamespaceMessages.LIST => await ListAsync(request, cancellationToken).ConfigureAwait(false),
                NamespaceMessages.MKDIR => await MakeDirectoryAsync(request, cancellationToken).ConfigureAwait(false),
                NamespaceMessages.REMOVE => await RemoveAsync(request, cancellationToken).ConfigureAwait(false),
                NamespaceMessages.RENAME => await RenameAsync(request, cancellationToken).ConfigureAwait(false),
                NamespaceMessages.TRUNCATE => await TruncateAsync(request, cancellationToken).ConfigureAwait(false),
                NamespaceMessages.SYNC => await SyncAsync(request, cancellationToken).ConfigureAwait(false),
                NamespaceMessages.READLINK => await ReadLinkAsync(request, cancellationToken).ConfigureAwait(false),
                _ => Fail(request, Errno.NOT_IMPLEMENTED)
            };
        }
        catch (Exception exception) when (exception is FileSystemException or ArgumentException or NotSupportedException)
        {
            return Fail(request, Errno.Of(exception));
        }
    }

    private NamespaceReply Mounts(NamespaceRequest request)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(m_mounts.Mounts.Select(mount => mount.Point + "\0")));
        if (bytes.Length > m_window.Length)
            return Fail(request, Errno.NAME_TOO_LONG);
        m_window.Write(0, bytes);
        return new NamespaceReply(request.Operation, 0, Length: bytes.Length);
    }

    // A file or a directory opened: the flags as FileStream's modes, a directory read alone, its listing taken later.
    private async ValueTask<NamespaceReply> OpenAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        var path = Path(request, 0, request.Length);
        var (mount, inner) = Resolve(path);
        var flags = request.Flags;
        var writes = (flags & NamespaceMessages.WRITE_ACCESS) != 0 || (flags & NamespaceMessages.TRUNCATE_FILE) != 0;
        var entry = await mount.Volume.GetEntryAsync(inner, cancellationToken).ConfigureAwait(false) ?? Root(mount, inner);
        if (entry is { IsDirectory: true })
        {
            if (writes || (flags & NamespaceMessages.CREATE) != 0 && (flags & NamespaceMessages.EXCLUSIVE) != 0)
                return Fail(request, writes ? Errno.IS_DIRECTORY : Errno.EXISTS);
            return Register(request, new Opened(mount, inner, null), Stat(mount, entry));
        }
        if ((flags & NamespaceMessages.DIRECTORY) != 0)
            return Fail(request, entry is null ? Errno.NOT_FOUND : Errno.NOT_DIRECTORY);
        var mode = (flags & NamespaceMessages.CREATE) == 0
            ? (flags & NamespaceMessages.TRUNCATE_FILE) != 0 ? FileMode.Truncate : FileMode.Open
            : (flags & NamespaceMessages.EXCLUSIVE) != 0 ? FileMode.CreateNew
            : (flags & NamespaceMessages.TRUNCATE_FILE) != 0 ? FileMode.Create : FileMode.OpenOrCreate;
        var access = (flags & NamespaceMessages.READ_ACCESS) != 0 && writes ? FileAccess.ReadWrite : writes ? FileAccess.Write : FileAccess.Read;
        var file = await mount.Volume.OpenAsync(inner, mode, access, cancellationToken).ConfigureAwait(false);
        entry = await mount.Volume.GetEntryAsync(inner, cancellationToken).ConfigureAwait(false);
        return Register(request, new Opened(mount, inner, file), Stat(mount, entry!));
    }

    private async ValueTask<NamespaceReply> CloseAsync(NamespaceRequest request)
    {
        if (!m_opened.Remove(request.Handle, out var opened))
            return Fail(request, Errno.BAD_HANDLE);
        if (opened.File is not null)
            await opened.File.DisposeAsync().ConfigureAwait(false);
        return new NamespaceReply(request.Operation, 0);
    }

    private async ValueTask<NamespaceReply> ReadAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        if (File(request) is not { } file)
            return Fail(request, Errno.BAD_HANDLE);
        var buffer = new byte[Math.Clamp(request.Length, 0, m_window.Length)];
        var read = await file.ReadAsync(request.Offset, buffer, cancellationToken).ConfigureAwait(false);
        m_window.Write(0, buffer.AsSpan(0, read));
        return new NamespaceReply(request.Operation, 0, Length: read);
    }

    private async ValueTask<NamespaceReply> WriteAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        if (File(request) is not { } file)
            return Fail(request, Errno.BAD_HANDLE);
        if (request.Length < 0 || request.Length > m_window.Length)
            return Fail(request, Errno.INVALID);
        await file.WriteAsync(request.Offset, m_window.Read(0, (int)request.Length), cancellationToken).ConfigureAwait(false);
        return await FileStatAsync(request, cancellationToken).ConfigureAwait(false) with { Operation = request.Operation, Length = request.Length };
    }

    private async ValueTask<NamespaceReply> StatAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        var (mount, inner) = Resolve(Path(request, 0, request.Length));
        var entry = await mount.Volume.GetEntryAsync(inner, cancellationToken).ConfigureAwait(false) ?? Root(mount, inner);
        return entry is null ? Fail(request, Errno.NOT_FOUND) : new NamespaceReply(request.Operation, 0, Stat: Stat(mount, Current(mount, inner, entry)));
    }

    private async ValueTask<NamespaceReply> FileStatAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        if (!m_opened.TryGetValue(request.Handle, out var opened))
            return Fail(request, Errno.BAD_HANDLE);
        var entry = await opened.Mount.Volume.GetEntryAsync(opened.Path, cancellationToken).ConfigureAwait(false) ?? Root(opened.Mount, opened.Path);
        return entry is null
            ? Fail(request, Errno.NOT_FOUND)
            : new NamespaceReply(request.Operation, 0, Stat: Stat(opened.Mount, Current(opened.Mount, opened.Path, entry)));
    }

    // An entry as the session's own handles of it see it: a provider that records a file's size when its handle
    // flushes (FAT) reports the handle's length, as POSIX's stat does after a write.
    private FileSystemEntry Current(MountTable.Mount mount, string path, FileSystemEntry entry)
    {
        foreach (var opened in m_opened.Values)
        {
            if (opened.File is { } file && opened.Mount == mount && opened.Path == path)
                return entry with { Length = file.Length };
        }
        return entry;
    }

    // A directory's entries from the one the offset names, as many as the window holds; the listing is taken at the
    // first request and again whenever a client starts over.
    private async ValueTask<NamespaceReply> ListAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        if (!m_opened.TryGetValue(request.Handle, out var opened) || opened.File is not null)
            return Fail(request, opened is null ? Errno.BAD_HANDLE : Errno.NOT_DIRECTORY);
        if (request.Offset == 0 || opened.Listing is null)
            opened.Listing = await opened.Mount.Volume.EnumerateAsync(opened.Path, cancellationToken).ConfigureAwait(false);
        var listing = opened.Listing;
        var at = 0;
        var index = (int)Math.Clamp(request.Offset, 0, listing.Count);
        var record = new byte[m_window.Length];
        for (; index < listing.Count; index++)
        {
            var entry = listing[index];
            var bytes = NamespaceMessages.WriteEntry(record.AsSpan(at), Id(entry), Kind(entry), entry.Name);
            if (bytes == 0)
                break;
            at += bytes;
        }
        m_window.Write(0, record.AsSpan(0, at));
        return new NamespaceReply(request.Operation, 0, Offset: index < listing.Count ? index : 0, Length: at);
    }

    private async ValueTask<NamespaceReply> MakeDirectoryAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        var (mount, inner) = Resolve(Path(request, 0, request.Length));
        if (inner == "/" || await mount.Volume.GetEntryAsync(inner, cancellationToken).ConfigureAwait(false) is not null)
            return Fail(request, Errno.EXISTS);
        var parent = Parent(inner);
        if (parent != "/" && await mount.Volume.GetEntryAsync(parent, cancellationToken).ConfigureAwait(false) is not { IsDirectory: true })
            return Fail(request, Errno.NOT_FOUND);
        var made = await mount.Volume.CreateDirectoryAsync(inner, cancellationToken).ConfigureAwait(false);
        return new NamespaceReply(request.Operation, 0, Stat: Stat(mount, made));
    }

    private async ValueTask<NamespaceReply> RemoveAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        var (mount, inner) = Resolve(Path(request, 0, request.Length));
        if (inner == "/")
            return Fail(request, Errno.BUSY);
        var entry = await mount.Volume.GetEntryAsync(inner, cancellationToken).ConfigureAwait(false);
        if (entry is null)
            return Fail(request, Errno.NOT_FOUND);
        var directory = (request.Flags & NamespaceMessages.DIRECTORY) != 0;
        if (directory != entry.IsDirectory)
            return Fail(request, directory ? Errno.NOT_DIRECTORY : Errno.IS_DIRECTORY);
        await mount.Volume.DeleteAsync(inner, cancellationToken).ConfigureAwait(false);
        return new NamespaceReply(request.Operation, 0);
    }

    // A rename as POSIX's: a file replaces a file, a directory an empty directory; a volume's entry stays on it.
    private async ValueTask<NamespaceReply> RenameAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        var bytes = m_window.Read(0, (int)Math.Clamp(request.Length, 0, m_window.Length));
        var separator = Array.IndexOf(bytes, (byte)0);
        if (separator < 0)
            return Fail(request, Errno.INVALID);
        var (fromMount, from) = Resolve(Encoding.UTF8.GetString(bytes, 0, separator));
        var end = Array.IndexOf(bytes, (byte)0, separator + 1);
        var (toMount, to) = Resolve(Encoding.UTF8.GetString(bytes, separator + 1, (end < 0 ? bytes.Length : end) - separator - 1));
        if (fromMount != toMount)
            return Fail(request, Errno.CROSS_DEVICE);
        var volume = fromMount.Volume;
        var source = await volume.GetEntryAsync(from, cancellationToken).ConfigureAwait(false);
        if (source is null)
            return Fail(request, Errno.NOT_FOUND);
        if (from == to)
            return new NamespaceReply(request.Operation, 0);
        if (await volume.GetEntryAsync(to, cancellationToken).ConfigureAwait(false) is { } target)
        {
            if (target.IsDirectory != source.IsDirectory)
                return Fail(request, target.IsDirectory ? Errno.IS_DIRECTORY : Errno.NOT_DIRECTORY);
            await volume.DeleteAsync(to, cancellationToken).ConfigureAwait(false); // not atomic, unlike POSIX's
        }
        await volume.RenameAsync(from, to, cancellationToken).ConfigureAwait(false);
        return new NamespaceReply(request.Operation, 0);
    }

    private async ValueTask<NamespaceReply> TruncateAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        if (File(request) is not { } file)
            return Fail(request, Errno.BAD_HANDLE);
        await file.SetLengthAsync(request.Length, cancellationToken).ConfigureAwait(false);
        return await FileStatAsync(request, cancellationToken).ConfigureAwait(false) with { Operation = request.Operation };
    }

    private async ValueTask<NamespaceReply> SyncAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        if (!m_opened.TryGetValue(request.Handle, out var opened))
            return Fail(request, Errno.BAD_HANDLE);
        if (opened.File is not null)
            await opened.File.FlushAsync(cancellationToken).ConfigureAwait(false);
        else
            await opened.Mount.Volume.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new NamespaceReply(request.Operation, 0);
    }

    private async ValueTask<NamespaceReply> ReadLinkAsync(NamespaceRequest request, CancellationToken cancellationToken)
    {
        var (mount, inner) = Resolve(Path(request, 0, request.Length));
        var target = Encoding.UTF8.GetBytes(await mount.Volume.ReadLinkAsync(inner, cancellationToken).ConfigureAwait(false));
        if (target.Length > m_window.Length)
            return Fail(request, Errno.NAME_TOO_LONG);
        m_window.Write(0, target);
        return new NamespaceReply(request.Operation, 0, Length: target.Length);
    }

    private NamespaceReply Register(NamespaceRequest request, Opened opened, NamespaceStat stat)
    {
        var handle = m_next++;
        m_opened[handle] = opened;
        return new NamespaceReply(request.Operation, 0, Handle: handle, Stat: stat);
    }

    // A file the session opened; null for no such handle, a directory's a NotAFile failure.
    private IFileHandle? File(NamespaceRequest request) =>
        m_opened.TryGetValue(request.Handle, out var opened)
            ? opened.File ?? throw new FileSystemException(FileSystemError.NotAFile, "The handle is a directory's.")
            : null;

    // A path of the window: absolute, of at most PATH_LIMIT bytes.
    private string Path(NamespaceRequest request, int offset, long length)
    {
        if (length <= 0 || length > Math.Min(PATH_LIMIT, m_window.Length - offset))
            throw new FileSystemException(FileSystemError.InvalidName, $"A path of {length} bytes.");
        var path = Encoding.UTF8.GetString(m_window.Read(offset, (int)length));
        return path.Length > 0 && path[0] == '/' && !path.Contains('\0')
            ? path
            : throw new FileSystemException(FileSystemError.InvalidName, "The path is not absolute.");
    }

    private (MountTable.Mount Mount, string Path) Resolve(string path) =>
        m_mounts.Resolve(path) ?? throw new FileSystemException(FileSystemError.NotFound, $"No volume holds {path}.");

    // A mount point's root, where a provider gives no entry for "/".
    private static FileSystemEntry? Root(MountTable.Mount mount, string inner) => inner == "/"
        ? new FileSystemEntry("/", "", FileSystemEntryKind.Directory, 0, FileSystemAttributes.None, null, null, null)
        : null;

    private static string Parent(string path)
    {
        var slash = path.TrimEnd('/').LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static NamespaceStat Stat(MountTable.Mount mount, FileSystemEntry entry)
    {
        var unit = Math.Max(512, mount.Volume.Volume.AllocationUnit);
        var size = entry.IsDirectory ? unit : entry.Length;
        var readOnly = (entry.Attributes & FileSystemAttributes.ReadOnly) != 0;
        var mode = entry.Unix?.Mode ?? (entry.IsDirectory ? (readOnly ? 0x16D : 0x1ED) : readOnly ? 0x124 : 0x1A4); // 0555 0755 0444 0644
        var modified = entry.Modified ?? entry.Created ?? DateTimeOffset.UnixEpoch;
        return new NamespaceStat(Kind(entry), (uint)mode, size, Id(entry), (uint)(entry.Unix?.Links ?? (entry.IsDirectory ? 2 : 1)), mount.Index,
            (uint)(entry.Unix?.UserId ?? 0), (uint)(entry.Unix?.GroupId ?? 0), (uint)unit, (size + unit - 1) / unit * unit / 512,
            entry.Accessed ?? modified, modified, modified);
    }

    private static uint Kind(FileSystemEntry entry) => entry.Kind switch
    {
        FileSystemEntryKind.File => NamespaceMessages.KIND_FILE,
        FileSystemEntryKind.Directory => NamespaceMessages.KIND_DIRECTORY,
        FileSystemEntryKind.SymbolicLink => NamespaceMessages.KIND_LINK,
        _ => NamespaceMessages.KIND_OTHER
    };

    // The entry's number: its inode where the format has one, otherwise a stable hash of its path (FNV-1a).
    private static long Id(FileSystemEntry entry)
    {
        if (entry.Unix is { } unix)
            return unix.Id;
        var hash = 14695981039346656037UL;
        foreach (var value in Encoding.UTF8.GetBytes(entry.Path.ToUpperInvariant()))
            hash = (hash ^ value) * 1099511628211UL;
        return (long)(hash >> 1) | 1;
    }

    private static NamespaceReply Fail(NamespaceRequest request, int error) => new(request.Operation, error);

    #endregion

    #region IAsyncDisposable

    /// <summary>
    /// Closes everything the session opened.
    /// </summary>
    /// <returns>When every file is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (var opened in m_opened.Values)
        {
            if (opened.File is not null)
                await opened.File.DisposeAsync().ConfigureAwait(false);
        }
        m_opened.Clear();
    }

    #endregion

    #region Nested Types

    // A file or a directory the session opened; a directory's listing as the client reads it.
    private sealed class Opened(MountTable.Mount mount, string path, IFileHandle? file)
    {
        public MountTable.Mount Mount { get; } = mount;

        public string Path { get; } = path;

        public IFileHandle? File { get; } = file;

        public IReadOnlyList<FileSystemEntry>? Listing { get; set; }
    }

    #endregion
}
