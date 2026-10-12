using System;
using System.Threading;
using System.Threading.Tasks;
using OutWit.Common.Fat.Devices;
using OutWit.OS.Storage;

namespace OutWit.OS.FileSystems.Fat;

/// <summary>
/// OutWit.Common.Fat's block device over a WitOS block storage resource (storage document §15, plan step D4.1): a
/// sector is a logical block, and a request is the resource's at the sector's byte offset. The resource stays its
/// owner's: the device's end leaves it open.
/// </summary>
internal sealed class FatBlockDevice : BlockDeviceBase
{
    #region Fields

    private readonly IBlockStorage m_storage;

    #endregion

    #region Constructors

    private FatBlockDevice(IBlockStorage storage)
        : base(storage.Info.LogicalBlockSize, storage.Info.Length / storage.Info.LogicalBlockSize, storage.Info.IsReadOnly)
    {
        m_storage = storage;
    }

    #endregion

    #region Functions

    /// <summary>
    /// The device of a resource whose logical block the library takes as a sector: a power of two of 512 to 4096 bytes,
    /// with no larger alignment.
    /// </summary>
    /// <param name="storage">The resource.</param>
    /// <returns>The device.</returns>
    /// <exception cref="FileSystemException">The resource's blocks are no sectors of the library (Unsupported).</exception>
    public static FatBlockDevice Over(IBlockStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var info = storage.Info;
        if (!IsValidSectorSize(info.LogicalBlockSize) || info.RequiredAlignment != info.LogicalBlockSize)
            throw new FileSystemException(FileSystemError.Unsupported, $"Blocks of {info.LogicalBlockSize} bytes aligned to " +
                $"{info.RequiredAlignment} are no FAT sectors: a power of two of {MIN_SECTOR_SIZE} to {MAX_SECTOR_SIZE} bytes.");
        return new FatBlockDevice(storage);
    }

    /// <inheritdoc />
    protected override ValueTask ReadSectorsAsync(long sector, Memory<byte> buffer, CancellationToken cancellationToken) =>
        m_storage.ReadAsync(sector * SectorSize, buffer, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask WriteSectorsAsync(long sector, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        m_storage.WriteAsync(sector * SectorSize, buffer, WriteOptions.None, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask FlushSectorsAsync(CancellationToken cancellationToken) => m_storage.FlushAsync(cancellationToken);

    #endregion
}
