using System;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.Storage;

/// <summary>
/// A window of a block storage resource (storage document §24–25, plan step D3.1): a partition, whose holder has
/// authority over its range alone. It shares the resource's blocks and capabilities, does not own the resource, and
/// passes a flush through.
/// </summary>
public sealed class PartitionBlockStorage : BlockStorageBase
{
    #region Fields

    private readonly IBlockStorage m_disk;
    private readonly long m_start;

    #endregion

    #region Constructors

    /// <summary>
    /// The window of the resource's bytes from start for length.
    /// </summary>
    /// <param name="disk">The resource, which the window does not own.</param>
    /// <param name="start">The window's first byte, aligned.</param>
    /// <param name="length">The window's bytes, aligned.</param>
    /// <param name="isReadOnly">Whether the window refuses writes the resource would take.</param>
    /// <exception cref="ArgumentException">The window is not aligned or lies beyond the resource.</exception>
    public PartitionBlockStorage(IBlockStorage disk, long start, long length, bool isReadOnly = false)
        : base(disk.Info with { Length = length, IsReadOnly = disk.Info.IsReadOnly || isReadOnly })
    {
        var alignment = disk.Info.RequiredAlignment;
        if (start < 0 || start % alignment != 0 || start > disk.Info.Length - length)
            throw new ArgumentException($"A window of {length} bytes at {start} does not fit {disk.Info.Length} bytes aligned to {alignment}.");
        m_disk = disk;
        m_start = start;
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    protected override ValueTask ReadCoreAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken) =>
        m_disk.ReadAsync(m_start + offset, buffer, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask WriteCoreAsync(long offset, ReadOnlyMemory<byte> buffer, WriteOptions options,
        CancellationToken cancellationToken) =>
        m_disk.WriteAsync(m_start + offset, buffer, options, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask FlushCoreAsync(CancellationToken cancellationToken) => m_disk.FlushAsync(cancellationToken);

    /// <inheritdoc />
    protected override ValueTask DiscardCoreAsync(long offset, long length, CancellationToken cancellationToken) =>
        m_disk.DiscardAsync(m_start + offset, length, cancellationToken);

    #endregion

    #region Properties

    /// <summary>
    /// The window's first byte in the resource.
    /// </summary>
    public long Start => m_start;

    #endregion
}
