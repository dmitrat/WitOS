using System;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.Storage;

/// <summary>
/// What every block storage resource checks before it serves a request (storage document §7–9, plan step D3.1): the
/// resource is not disposed, the range is aligned and within it, a write or a discard reaches a writable resource, a
/// discard one that supports it, and a cancelled request goes no further. A derived resource serves checked requests.
/// </summary>
public abstract class BlockStorageBase : IBlockStorage
{
    #region Fields

    private int m_disposed;

    #endregion

    #region Constructors

    /// <summary>
    /// Takes the resource's information, completing what it leaves out: the required alignment and the physical block
    /// default to the logical block, the optimal transfer to the required alignment.
    /// </summary>
    /// <param name="info">The resource's information.</param>
    /// <exception cref="ArgumentException">A size is not positive or not a multiple of the logical block.</exception>
    protected BlockStorageBase(BlockStorageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var block = info.LogicalBlockSize;
        var alignment = info.RequiredAlignment == 0 ? block : info.RequiredAlignment;
        var physical = info.PhysicalBlockSize == 0 ? block : info.PhysicalBlockSize;
        if (block <= 0 || info.Length < 0 || info.Length % block != 0 || alignment % block != 0 || physical % block != 0 ||
            alignment <= 0 || info.MaxTransferSize < 0 || info.MaxTransferSize % alignment != 0 || info.MaxOutstandingRequests <= 0)
            throw new ArgumentException($"Block storage of {info.Length} bytes in blocks of {block} with alignment {alignment}, " +
                $"physical blocks of {physical} and transfers of {info.MaxTransferSize} is not consistent.", nameof(info));
        Info = info with
        {
            RequiredAlignment = alignment,
            PhysicalBlockSize = physical,
            OptimalTransferSize = info.OptimalTransferSize == 0 ? alignment : info.OptimalTransferSize
        };
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    public ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckRange(offset, buffer.Length);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadCoreAsync(offset, buffer, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, WriteOptions options = WriteOptions.None,
        CancellationToken cancellationToken = default)
    {
        CheckWritable();
        CheckRange(offset, buffer.Length);
        cancellationToken.ThrowIfCancellationRequested();
        return WriteCoreAsync(offset, buffer, options, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return FlushCoreAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DiscardAsync(long offset, long length, CancellationToken cancellationToken = default)
    {
        CheckWritable();
        if (!Info.SupportsDiscard)
            throw new NotSupportedException("The resource does not support discard.");
        CheckRange(offset, length);
        cancellationToken.ThrowIfCancellationRequested();
        return DiscardCoreAsync(offset, length, cancellationToken);
    }

    /// <summary>
    /// Serves a checked read.
    /// </summary>
    /// <param name="offset">The first byte, aligned and within the resource.</param>
    /// <param name="buffer">The bytes to fill.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>When the bytes are in the buffer.</returns>
    protected abstract ValueTask ReadCoreAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Serves a checked write.
    /// </summary>
    /// <param name="offset">The first byte, aligned and within the resource.</param>
    /// <param name="buffer">The bytes.</param>
    /// <param name="options">How the write completes.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>When the write completed as the options ask.</returns>
    protected abstract ValueTask WriteCoreAsync(long offset, ReadOnlyMemory<byte> buffer, WriteOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Serves a flush; a resource without a volatile cache has nothing to do.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>When the completed writes are durable.</returns>
    protected virtual ValueTask FlushCoreAsync(CancellationToken cancellationToken) => default;

    /// <summary>
    /// Serves a checked discard of a resource that supports it.
    /// </summary>
    /// <param name="offset">The first byte, aligned and within the resource.</param>
    /// <param name="length">The bytes, aligned.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>When the device took the hint.</returns>
    protected virtual ValueTask DiscardCoreAsync(long offset, long length, CancellationToken cancellationToken) => default;

    /// <summary>
    /// Releases what the resource holds, once.
    /// </summary>
    /// <returns>When it is released.</returns>
    protected virtual ValueTask DisposeCoreAsync() => default;

    private void CheckWritable()
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        if (Info.IsReadOnly)
            throw new NotSupportedException("The resource is read-only.");
    }

    private void CheckRange(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        var alignment = Info.RequiredAlignment;
        if (offset < 0 || length < 0 || offset % alignment != 0 || length % alignment != 0)
            throw new ArgumentException($"A request of {length} bytes at {offset} is not aligned to {alignment} bytes.");
        if (offset > Info.Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"A request of {length} bytes at {offset} lies beyond {Info.Length} bytes.");
        if (Info.MaxTransferSize != 0 && length > Info.MaxTransferSize)
            throw new ArgumentOutOfRangeException(nameof(length), $"A request of {length} bytes exceeds the transfer limit of {Info.MaxTransferSize}.");
    }

    #endregion

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
            return;
        await DisposeCoreAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public BlockStorageInfo Info { get; }

    #endregion
}
