using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OutWit.Common.Fat;
using OutWit.Common.Fat.Model;
using OutWit.OS.Storage;

namespace OutWit.OS.FileSystems.Fat;

/// <summary>
/// FAT12, FAT16, FAT32 and exFAT through OutWit.Common.Fat, an independent portable library the adapter reaches
/// unchanged (storage document §14–16, plan step D4.1): a volume mounted from a WitOS block storage resource, whatever
/// device serves it (§71). The library serves one caller at a time, so the filesystem does: every operation and every
/// handle's waits for the one before (§52 allows a serialized prototype). Times are UTC.
/// </summary>
public sealed class FatFileSystem : IFileSystem
{
    #region Fields

    private readonly FatVolume m_volume;
    private readonly SemaphoreSlim m_lock = new(1, 1);
    private int m_disposed;

    #endregion

    #region Constructors

    private FatFileSystem(FatVolume volume, string? label)
    {
        m_volume = volume;
        var info = volume.Info;
        Volume = new FileSystemVolume(Format(info.Kind), label, info.TotalSectors * info.SectorSize, info.ClusterSize, volume.IsReadOnly);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Whether a resource holds a FAT volume at its start (storage document §58: detection is not authority).
    /// </summary>
    /// <param name="storage">The resource.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>Whether the library recognizes a volume there.</returns>
    public static async ValueTask<bool> DetectAsync(IBlockStorage storage, CancellationToken cancellationToken = default)
    {
        FatBlockDevice device;
        try
        {
            device = FatBlockDevice.Over(storage);
        }
        catch (FileSystemException)
        {
            return false;
        }
        await using (device)
        {
            try
            {
                return await FatDetector.DetectVolumeAsync(device, cancellationToken).ConfigureAwait(false) is not null;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Mounts the volume at the start of a resource, read-only when the resource is.
    /// </summary>
    /// <param name="storage">The resource; it stays its owner's.</param>
    /// <param name="cancellationToken">Cancels the mount.</param>
    /// <returns>The filesystem.</returns>
    /// <exception cref="FileSystemException">The resource holds no FAT volume, or one the library cannot serve.</exception>
    public static async ValueTask<FatFileSystem> MountAsync(IBlockStorage storage, CancellationToken cancellationToken = default)
    {
        var device = FatBlockDevice.Over(storage);
        try
        {
            // The volume owns the device, whose end leaves the resource open.
            var volume = await FatErrors.RunAsync(() => FatVolume.MountAsync(device, new FatVolumeOptions { Clock = UtcClock.INSTANCE },
                cancellationToken)).ConfigureAwait(false);
            try
            {
                var label = await FatErrors.RunAsync(() => volume.GetLabelAsync(cancellationToken)).ConfigureAwait(false);
                return new FatFileSystem(volume, label);
            }
            catch
            {
                await volume.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await device.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Formats a resource as the library chooses for its size (storage document §59), then mounts it.
    /// </summary>
    /// <param name="storage">A writable resource; what it held is lost.</param>
    /// <param name="label">The volume's label, or null.</param>
    /// <param name="cancellationToken">Cancels the format.</param>
    /// <returns>The empty filesystem.</returns>
    /// <exception cref="FileSystemException">The resource is read-only, or no FAT format fits it.</exception>
    public static async ValueTask<FatFileSystem> FormatAsync(IBlockStorage storage, string? label = null,
        CancellationToken cancellationToken = default)
    {
        await using (var device = FatBlockDevice.Over(storage))
        {
            await FatErrors.RunAsync(() => FatFormatter.FormatAsync(device, new FatFormatOptions { Label = label, Clock = UtcClock.INSTANCE },
                cancellationToken)).ConfigureAwait(false);
        }
        return await MountAsync(storage, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<FileSystemEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) =>
        LockedAsync(async () => await m_volume.GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is { } entry ? Entry(entry) : null,
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FileSystemEntry>> EnumerateAsync(string path, CancellationToken cancellationToken = default) =>
        LockedAsync<IReadOnlyList<FileSystemEntry>>(async () =>
        {
            var entries = new List<FileSystemEntry>();
            await foreach (var entry in m_volume.EnumerateAsync(path, false, cancellationToken).ConfigureAwait(false))
                entries.Add(Entry(entry));
            return entries;
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>FAT records no links: an entry that exists is NotALink.</remarks>
    public async ValueTask<string> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is null
            ? throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {path}.")
            : throw new FileSystemException(FileSystemError.NotALink, $"{path} is no symbolic link: FAT records none.");

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The mode is Append: a handle writes at the offsets it names.</exception>
    public ValueTask<IFileHandle> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken = default)
    {
        if (mode == FileMode.Append)
            throw new ArgumentOutOfRangeException(nameof(mode), "A handle writes at the offsets it names; there is no append mode.");
        return LockedAsync<IFileHandle>(async () =>
            new FatFileHandle(this, await m_volume.OpenAsync(path, mode, access, cancellationToken).ConfigureAwait(false)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        LockedAsync(async () => Entry(await m_volume.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false)), cancellationToken);

    /// <inheritdoc />
    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        LockedAsync(() => m_volume.DeleteAsync(path, false, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public ValueTask<FileSystemEntry> RenameAsync(string source, string destination, CancellationToken cancellationToken = default) =>
        LockedAsync(async () => Entry(await m_volume.MoveAsync(source, destination, false, cancellationToken).ConfigureAwait(false)),
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// FAT records read-only alone (N4.1): a mode without a write bit sets the attribute, one with the owner's clears it,
    /// and the provider reports 0555/0755 for a directory and 0444/0644 for a file afterwards; a mode that asks for
    /// anything else, which the format cannot keep, is NotPermitted, as Linux's vfat refuses a mode its masks cannot
    /// represent with EPERM.
    /// </remarks>
    public ValueTask<FileSystemEntry> SetModeAsync(string path, int mode, CancellationToken cancellationToken = default) =>
        LockedAsync(async () =>
        {
            var entry = await m_volume.GetEntryAsync(path, cancellationToken).ConfigureAwait(false)
                ?? throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {path}.");
            var readOnly = (mode & 0x92) == 0; // 0222: no write bit at all
            var kept = entry.IsDirectory ? (readOnly ? 0x16D : 0x1ED) : readOnly ? 0x124 : 0x1A4; // 0555 0755 0444 0644
            if ((mode & 0xFFF) != kept)
                throw new FileSystemException(FileSystemError.NotPermitted,
                    $"FAT records read-only alone: the mode {Convert.ToString(mode & 0xFFF, 8)} is not {Convert.ToString(kept, 8)}.");
            var attributes = readOnly ? entry.Attributes | FatAttributes.ReadOnly : entry.Attributes & ~FatAttributes.ReadOnly;
            if (attributes != entry.Attributes)
                await m_volume.SetAttributesAsync(path, attributes, cancellationToken).ConfigureAwait(false);
            return Entry(await m_volume.GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? entry);
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The library records the times a write sets and offers no way to set them (N4.1): the change is NotPermitted, which
    /// a program sees as EPERM, the answer a volume without POSIX metadata gives on Linux and the one System.Native's
    /// CopyFile tolerates for the copy's metadata.
    /// </remarks>
    public async ValueTask<FileSystemEntry> SetTimesAsync(string path, DateTimeOffset? accessed, DateTimeOffset? modified,
        CancellationToken cancellationToken = default) =>
        await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is null
            ? throw new FileSystemException(FileSystemError.NotFound, $"Nothing is at {path}.")
            : throw new FileSystemException(FileSystemError.NotPermitted, "FAT through its library records the times a write sets; none is set by hand.");

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
        LockedAsync(() => m_volume.FlushAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public ValueTask<FileSystemCheckReport> CheckAsync(CancellationToken cancellationToken = default) =>
        LockedAsync(async () =>
        {
            var report = await FatChecker.CheckAsync(m_volume, cancellationToken).ConfigureAwait(false);
            return new FileSystemCheckReport([.. report.Problems.Select(problem => problem.Path is null ? problem.Message : $"{problem.Path}: {problem.Message}")],
                report.Files, report.Directories);
        }, cancellationToken);

    /// <summary>
    /// Runs an operation of the library alone, translating its failures.
    /// </summary>
    /// <typeparam name="T">The operation's result.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">Cancels the wait for the operations before.</param>
    /// <returns>Its result.</returns>
    internal async ValueTask<T> LockedAsync<T>(Func<ValueTask<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        await m_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(m_disposed != 0, this);
            return await FatErrors.RunAsync(operation).ConfigureAwait(false);
        }
        finally
        {
            m_lock.Release();
        }
    }

    /// <summary>
    /// Runs an operation of the library that returns nothing alone, translating its failures.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">Cancels the wait for the operations before.</param>
    /// <returns>When it completed.</returns>
    internal async ValueTask LockedAsync(Func<ValueTask> operation, CancellationToken cancellationToken)
    {
        await LockedAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string Format(FatKind kind) => kind switch
    {
        FatKind.Fat12 => "FAT12",
        FatKind.Fat16 => "FAT16",
        FatKind.Fat32 => "FAT32",
        _ => "exFAT"
    };

    private static FileSystemEntry Entry(FatDirectoryEntry entry) => new(entry.Path, entry.Name,
        entry.IsDirectory ? FileSystemEntryKind.Directory : FileSystemEntryKind.File,
        entry.IsDirectory ? 0 : entry.Length, Attributes(entry.Attributes), Time(entry.Created), Time(entry.Modified), Time(entry.Accessed));

    private static FileSystemAttributes Attributes(FatAttributes attributes) =>
        ((attributes & FatAttributes.ReadOnly) != 0 ? FileSystemAttributes.ReadOnly : 0) |
        ((attributes & FatAttributes.Hidden) != 0 ? FileSystemAttributes.Hidden : 0) |
        ((attributes & FatAttributes.System) != 0 ? FileSystemAttributes.System : 0) |
        ((attributes & FatAttributes.Archive) != 0 ? FileSystemAttributes.Archive : 0);

    // A FAT time without a zone is UTC on WitOS's volumes; exFAT's with an offset comes as UTC already.
    private static DateTimeOffset? Time(DateTime? time) => time is { } value
        ? new DateTimeOffset(value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc))
        : null;

    #endregion

    #region IAsyncDisposable

    /// <summary>
    /// Flushes and unmounts the volume, once, after the operation in progress; the resource stays open.
    /// </summary>
    /// <returns>When the volume is unmounted.</returns>
    public async ValueTask DisposeAsync()
    {
        await m_lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref m_disposed, 1) != 0)
                return;
            await FatErrors.RunAsync(() => m_volume.DisposeAsync()).ConfigureAwait(false);
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
