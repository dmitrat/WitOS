using System;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.FileSystems;

/// <summary>
/// A disk whose power goes after a number of writes (storage document §50): the writes before the cut reach the
/// medium in their order, every write after it is lost, and the caller is not told.
/// </summary>
internal sealed class CrashingBlockStorage : BlockStorageBase
{
    #region Fields

    private readonly MemoryBlockStorage m_disk;
    private int m_budget;

    #endregion

    #region Constructors

    public CrashingBlockStorage(MemoryBlockStorage disk, int budget)
        : base(disk.Info with { SupportsFlush = true })
    {
        m_disk = disk;
        m_budget = budget;
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    protected override ValueTask ReadCoreAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken) =>
        m_disk.ReadAsync(offset, buffer, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask WriteCoreAsync(long offset, ReadOnlyMemory<byte> buffer, WriteOptions options, CancellationToken cancellationToken)
    {
        Writes++;
        return m_budget-- > 0 ? m_disk.WriteAsync(offset, buffer, options, cancellationToken) : default;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The writes asked for, those lost among them.
    /// </summary>
    public int Writes { get; private set; }

    #endregion
}
