namespace OutWit.OS.Storage;

/// <summary>
/// What a block storage resource is and what it supports (storage document §9). Nothing here is a filesystem's rule:
/// a block size need not be a power of two or within 512–4096 bytes (§10); a filesystem provider checks what it needs.
/// </summary>
public sealed record BlockStorageInfo
{
    #region Properties

    /// <summary>
    /// The bytes the resource holds, a multiple of LogicalBlockSize.
    /// </summary>
    public required long Length { get; init; }

    /// <summary>
    /// The unit of addressing.
    /// </summary>
    public required int LogicalBlockSize { get; init; }

    /// <summary>
    /// The unit the medium writes atomically, a multiple of LogicalBlockSize.
    /// </summary>
    public int PhysicalBlockSize { get; init; }

    /// <summary>
    /// The alignment of every offset and length; LogicalBlockSize unless the resource asks for more.
    /// </summary>
    public int RequiredAlignment { get; init; }

    /// <summary>
    /// The transfer the resource serves best.
    /// </summary>
    public int OptimalTransferSize { get; init; }

    /// <summary>
    /// The largest transfer one request may carry; zero for no limit.
    /// </summary>
    public int MaxTransferSize { get; init; }

    /// <summary>
    /// The requests the resource serves at once; it queues the others.
    /// </summary>
    public int MaxOutstandingRequests { get; init; } = 1;

    /// <summary>
    /// Whether writes and discards are refused.
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// Whether a flush reaches a volatile cache of the device; without one, every completed write is durable already.
    /// </summary>
    public bool SupportsFlush { get; init; }

    /// <summary>
    /// Whether a Durable write is durable when it completes without a flush.
    /// </summary>
    public bool SupportsForceUnitAccess { get; init; }

    /// <summary>
    /// Whether DiscardAsync reaches the device.
    /// </summary>
    public bool SupportsDiscard { get; init; }

    #endregion
}
