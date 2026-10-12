using System;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.Storage;

/// <summary>
/// A block storage resource (storage document §7–8, plan step D3.1): bytes addressed by offset, with no shared position,
/// read and written asynchronously, several requests outstanding at once, and a flush that is a durability barrier. An
/// offset and a length are multiples of Info.RequiredAlignment and lie within Info.Length.
/// </summary>
public interface IBlockStorage : IAsyncDisposable
{
    #region Functions

    /// <summary>
    /// Reads buffer.Length bytes at the offset.
    /// </summary>
    /// <param name="offset">The first byte, aligned.</param>
    /// <param name="buffer">The bytes to fill, an aligned length.</param>
    /// <param name="cancellationToken">Cancels the request before it reaches the device.</param>
    /// <returns>When the bytes are in the buffer.</returns>
    ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the bytes at the offset.
    /// </summary>
    /// <param name="offset">The first byte, aligned.</param>
    /// <param name="buffer">The bytes, an aligned length.</param>
    /// <param name="options">Durable for a write that is durable when it completes (FUA, §47).</param>
    /// <param name="cancellationToken">Cancels the request before it reaches the device.</param>
    /// <returns>When the device accepted the bytes, or holds them durably with Durable.</returns>
    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, WriteOptions options = WriteOptions.None,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes every write that completed before the call durable (§45): a barrier no later write is ordered before (§48).
    /// </summary>
    /// <param name="cancellationToken">Cancels the request before it reaches the device.</param>
    /// <returns>When the writes are durable.</returns>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the device the range's contents are no longer needed (§44), where Info.SupportsDiscard says it can.
    /// </summary>
    /// <param name="offset">The first byte, aligned.</param>
    /// <param name="length">The bytes, an aligned length.</param>
    /// <param name="cancellationToken">Cancels the request before it reaches the device.</param>
    /// <returns>When the device took the hint.</returns>
    ValueTask DiscardAsync(long offset, long length, CancellationToken cancellationToken = default);

    #endregion

    #region Properties

    /// <summary>
    /// What the resource is and what it supports (§9).
    /// </summary>
    BlockStorageInfo Info { get; }

    #endregion
}
