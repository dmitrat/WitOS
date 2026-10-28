using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.Namespace.Server;

namespace OutWit.OS.Namespace.Volumes;

/// <summary>
/// /media (plan step E1, ADR 0032 decision 2): the directory the service serves from its start, below which every volume
/// that is not the system's mounts at its alias. It holds nothing of its own: its listing is the aliases mounted below it
/// now, each a directory a path below resolves to through the mount table, and it refuses every change.
/// </summary>
internal sealed class MediaDirectory : IFileSystem
{
    #region Constants

    // drwxr-xr-x, as Linux shows /media.
    private const int MODE = 0x1ED;

    #endregion

    #region Fields

    private readonly MountTable m_mounts;
    private readonly DateTimeOffset m_created = DateTimeOffset.UtcNow;

    #endregion

    #region Constructors

    /// <summary>
    /// The directory over the mount table whose /media points it lists.
    /// </summary>
    /// <param name="mounts">The namespace's mounts.</param>
    public MediaDirectory(MountTable mounts)
    {
        m_mounts = mounts;
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    public ValueTask<FileSystemEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(path == "/" ? Directory("/", "") : null);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FileSystemEntry>> EnumerateAsync(string path, CancellationToken cancellationToken = default) =>
        path == "/"
            ? ValueTask.FromResult<IReadOnlyList<FileSystemEntry>>([.. Names().Select(name => Directory("/" + name, name))])
            : throw new FileSystemException(FileSystemError.NotFound, $"/media holds no {path}.");

    /// <inheritdoc />
    public ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        throw new FileSystemException(FileSystemError.NotALink, $"/media{path} is no link.");

    /// <inheritdoc />
    public ValueTask<IFileHandle> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken = default) =>
        throw (path == "/" ? new FileSystemException(FileSystemError.NotAFile, "/media is a directory.") : Refused(path));

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw Refused(path);

    /// <inheritdoc />
    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default) => throw Refused(path);

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> RenameAsync(string source, string destination, CancellationToken cancellationToken = default) =>
        throw Refused(source);

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> SetModeAsync(string path, int mode, CancellationToken cancellationToken = default) => throw Refused(path);

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> SetTimesAsync(string path, DateTimeOffset? accessed, DateTimeOffset? modified,
        CancellationToken cancellationToken = default) => throw Refused(path);

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<FileSystemCheckReport> CheckAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new FileSystemCheckReport([], 0, 1));

    /// <inheritdoc />
    public ValueTask<FileSystemSpace> GetSpaceAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new FileSystemSpace(0, 0));

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // The aliases mounted below /media now.
    private IEnumerable<string> Names() =>
        m_mounts.Mounts.Select(mount => mount.Point).Where(point => point.StartsWith(MountTable.MEDIA + "/", StringComparison.Ordinal))
            .Select(point => point[(MountTable.MEDIA.Length + 1)..]).Order(StringComparer.Ordinal);

    private FileSystemEntry Directory(string path, string name) =>
        new(path, name, FileSystemEntryKind.Directory, 0, FileSystemAttributes.None, m_created, m_created, m_created,
            new FileSystemUnixMetadata(MODE, 0, 0, 2, 1));

    private static FileSystemException Refused(string path) =>
        new(FileSystemError.ReadOnly, $"/media{(path == "/" ? "" : path)} is the system's: a volume mounts below it, nothing is written in it.");

    #endregion

    #region Properties

    /// <inheritdoc />
    public FileSystemVolume Volume { get; } = new("media", null, 0, 4096, true);

    #endregion
}
