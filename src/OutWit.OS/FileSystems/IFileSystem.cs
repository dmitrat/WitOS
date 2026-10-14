using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems;

/// <summary>
/// A mounted filesystem (storage document §20, plan step D4.1): the semantic operations of a volume over the block
/// storage resource it was mounted from, whatever its on-disk format. A path is absolute within the volume, its
/// components separated by '/'; "/" is the root. A failure of the filesystem is a <see cref="FileSystemException"/>
/// whose error says what went wrong, the same for every provider.
/// </summary>
public interface IFileSystem : IAsyncDisposable
{
    #region Functions

    /// <summary>
    /// The entry at a path.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The entry, or null when nothing is there.</returns>
    ValueTask<FileSystemEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// The entries of a directory, without "." and "..".
    /// </summary>
    /// <param name="path">The directory.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The entries, in the directory's order.</returns>
    ValueTask<IReadOnlyList<FileSystemEntry>> EnumerateAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// The target a symbolic link records, as it records it; a lookup never follows a link (D4.2).
    /// </summary>
    /// <param name="path">The link.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The target.</returns>
    ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a file, creating it when the mode says so, as <see cref="FileStream"/> does.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="mode">Whether the file must exist, must not, or is created when missing, and whether it is cut to nothing.</param>
    /// <param name="access">Whether the handle reads, writes or both.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The handle.</returns>
    ValueTask<IFileHandle> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a directory and every missing directory above it.
    /// </summary>
    /// <param name="path">The directory.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>The directory, new or already there.</returns>
    ValueTask<FileSystemEntry> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file or an empty directory.
    /// </summary>
    /// <param name="path">The file or directory.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <returns>When the entry is gone.</returns>
    ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames or moves a file or a directory into an existing directory; nothing may be at the destination.
    /// </summary>
    /// <param name="source">What to move.</param>
    /// <param name="destination">Its new path.</param>
    /// <param name="cancellationToken">Cancels the move.</param>
    /// <returns>The entry in its new place.</returns>
    ValueTask<FileSystemEntry> RenameAsync(string source, string destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the permission bits of a file or a directory (plan step N4.1), as chmod does: a format that records a mode
    /// keeps the bits; a format that records read-only alone (FAT) takes a mode that says only that and refuses any other
    /// with <see cref="FileSystemError.Unsupported"/>.
    /// </summary>
    /// <param name="path">The file or directory.</param>
    /// <param name="mode">The permission bits (07777), without the type.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The entry as it is afterwards.</returns>
    ValueTask<FileSystemEntry> SetModeAsync(string path, int mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the access and modification times of a file or a directory (plan step N4.1), as utimensat does; a time left
    /// null stays. A format that records no settable times (FAT through its library) refuses with
    /// <see cref="FileSystemError.NotPermitted"/>, as a volume that keeps no POSIX metadata answers EPERM on Linux.
    /// </summary>
    /// <param name="path">The file or directory.</param>
    /// <param name="accessed">The access time, or null to leave it.</param>
    /// <param name="modified">The modification time, or null to leave it.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The entry as it is afterwards.</returns>
    ValueTask<FileSystemEntry> SetTimesAsync(string path, DateTimeOffset? accessed, DateTimeOffset? modified, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes everything the filesystem holds back and flushes the block storage below: what completed before is durable
    /// when the flush completes.
    /// </summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>When it is durable.</returns>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the volume's structures after a flush, changing nothing (storage document §72–73).
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>What the check found.</returns>
    ValueTask<FileSystemCheckReport> CheckAsync(CancellationToken cancellationToken = default);

    #endregion

    #region Properties

    /// <summary>
    /// What the volume is.
    /// </summary>
    FileSystemVolume Volume { get; }

    #endregion
}
