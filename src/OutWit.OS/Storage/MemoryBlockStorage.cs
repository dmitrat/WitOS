using System;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.Storage;

/// <summary>
/// Block storage in memory (storage document §107, step 2): the reference resource the conformance tests and the
/// providers above it run on. Every write is durable when it completes, so a flush has nothing to do.
/// </summary>
public sealed class MemoryBlockStorage : BlockStorageBase
{
    #region Fields

    private readonly byte[] m_bytes;
    private readonly Lock m_lock = new();

    #endregion

    #region Constructors

    /// <summary>
    /// Zeroed storage of the length given.
    /// </summary>
    /// <param name="length">The bytes, a multiple of the block size.</param>
    /// <param name="blockSize">The logical block size.</param>
    /// <param name="isReadOnly">Whether writes and discards are refused.</param>
    public MemoryBlockStorage(long length, int blockSize = 512, bool isReadOnly = false)
        : this(new byte[length], blockSize, isReadOnly)
    {
    }

    /// <summary>
    /// Storage over the bytes given, which it keeps.
    /// </summary>
    /// <param name="bytes">The contents, a multiple of the block size.</param>
    /// <param name="blockSize">The logical block size.</param>
    /// <param name="isReadOnly">Whether writes and discards are refused.</param>
    public MemoryBlockStorage(byte[] bytes, int blockSize = 512, bool isReadOnly = false)
        : base(new BlockStorageInfo
        {
            Length = bytes.LongLength,
            LogicalBlockSize = blockSize,
            IsReadOnly = isReadOnly,
            SupportsDiscard = true,
            MaxOutstandingRequests = int.MaxValue
        })
    {
        m_bytes = bytes;
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    protected override ValueTask ReadCoreAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        lock (m_lock)
            m_bytes.AsSpan((int)offset, buffer.Length).CopyTo(buffer.Span);
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask WriteCoreAsync(long offset, ReadOnlyMemory<byte> buffer, WriteOptions options,
        CancellationToken cancellationToken)
    {
        lock (m_lock)
            buffer.Span.CopyTo(m_bytes.AsSpan((int)offset));
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask DiscardCoreAsync(long offset, long length, CancellationToken cancellationToken)
    {
        lock (m_lock)
            m_bytes.AsSpan((int)offset, (int)length).Clear(); // discarded blocks read as zeros here
        return default;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The contents, for tests that look past the resource.
    /// </summary>
    public ReadOnlySpan<byte> Bytes => m_bytes;

    #endregion
}
