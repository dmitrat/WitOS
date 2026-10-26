using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.Storage;

/// <summary>
/// A cache over a block storage resource (storage document §35–36, plan step D3.1), itself a resource, chosen where the
/// medium is slow rather than everywhere. It holds the least recently used blocks of its block size (the resource's
/// RequiredAlignment unless given, a multiple of it) up to its capacity, in segments of a mebibyte it takes as it fills,
/// so that a large cache costs no object per block (plan step H5.3: the namespace service's volumes). Writing through, a
/// write reaches the resource before it completes and refreshes the blocks the cache holds; a block it does not hold
/// enters only when the write covers it whole. Writing back, a write stays in the cache, dirty, until an eviction, a flush
/// or the cache's end writes it, and a write that covers part of a block the cache does not hold reads that block first;
/// a flush writes every dirty block in ascending order and then flushes the resource, so no later write is ordered before
/// it (§48), and a Durable write goes through at once. A run of missed blocks is read in requests of the resource's
/// transfer limit; the resource's last block may be shorter than the block size. Requests are served one at a time.
/// </summary>
public sealed class CachedBlockStorage : BlockStorageBase
{
    #region Constants

    private const int SEGMENT_BYTES = 1 << 20;

    private const int NONE = -1;

    #endregion

    #region Fields

    private readonly IBlockStorage m_inner;
    private readonly int m_capacity;
    private readonly bool m_writeBack;
    private readonly bool m_leaveOpen;
    private readonly int m_block;
    private readonly int m_transfer;
    private readonly int m_segmentBlocks;
    private readonly Dictionary<long, int> m_slots = [];
    private readonly List<byte[]> m_segments = [];
    private readonly long[] m_blocks;
    private readonly int[] m_older;
    private readonly int[] m_newer;
    private readonly bool[] m_dirty;
    private readonly Stack<int> m_free = new();
    private readonly SemaphoreSlim m_gate = new(1, 1);
    private int m_used;
    private int m_newest = NONE;
    private int m_oldest = NONE;

    #endregion

    #region Constructors

    /// <summary>
    /// A cache over the resource.
    /// </summary>
    /// <param name="inner">The resource.</param>
    /// <param name="capacity">The blocks the cache holds.</param>
    /// <param name="writeBack">Whether writes stay in the cache until a flush.</param>
    /// <param name="leaveOpen">Whether the cache's end leaves the resource open.</param>
    /// <param name="blockSize">The cache's block: zero for the resource's RequiredAlignment, otherwise a multiple of it
    /// within the resource's transfer limit.</param>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is not positive, or the block is no multiple of the
    /// alignment or exceeds the transfer limit.</exception>
    public CachedBlockStorage(IBlockStorage inner, int capacity = 1024, bool writeBack = false, bool leaveOpen = false, int blockSize = 0)
        : base(inner.Info with { MaxOutstandingRequests = 1 })
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        var alignment = Info.RequiredAlignment;
        var block = blockSize == 0 ? alignment : blockSize;
        var transfer = Info.MaxTransferSize == 0 ? int.MaxValue - int.MaxValue % block : Info.MaxTransferSize;
        if (block <= 0 || block % alignment != 0 || block > transfer)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"A cache block of {block} bytes over a resource aligned to {alignment} with transfers of {transfer}.");
        m_inner = inner;
        m_capacity = capacity;
        m_writeBack = writeBack;
        m_leaveOpen = leaveOpen;
        m_block = block;
        m_transfer = transfer - transfer % block;
        m_segmentBlocks = Math.Max(1, SEGMENT_BYTES / block);
        m_blocks = new long[capacity];
        m_older = new int[capacity];
        m_newer = new int[capacity];
        m_dirty = new bool[capacity];
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    protected override async ValueTask ReadCoreAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await m_gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var last = (offset + buffer.Length - 1) / m_block;
            for (var at = 0; at < buffer.Length;)
            {
                var block = (offset + at) / m_block;
                if (m_slots.TryGetValue(block, out var slot))
                {
                    Touch(slot);
                    at += CopyOut(slot, offset + at, buffer.Span[at..]);
                    Hits++;
                    continue;
                }
                // A run of misses goes to the resource in reads of its transfer limit and into the cache block by block.
                var run = 1;
                while (block + run <= last && (run + 1) * (long)m_block <= m_transfer && !m_slots.ContainsKey(block + run))
                    run++;
                var start = block * m_block;
                var length = (int)(Math.Min(start + run * (long)m_block, Info.Length) - start);
                var bytes = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    await m_inner.ReadAsync(start, bytes.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    for (var i = 0; i < run; i++)
                    {
                        var part = Math.Min(m_block, length - i * m_block);
                        var filled = await TakeSlotAsync(block + i, cancellationToken).ConfigureAwait(false);
                        bytes.AsSpan(i * m_block, part).CopyTo(Bytes(filled).Span);
                    }
                    var within = (int)(offset + at - start);
                    var copied = Math.Min(length - within, buffer.Length - at);
                    bytes.AsSpan(within, copied).CopyTo(buffer.Span[at..]);
                    at += copied;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bytes);
                }
                Misses += run;
            }
        }
        finally
        {
            m_gate.Release();
        }
    }

    /// <inheritdoc />
    protected override async ValueTask WriteCoreAsync(long offset, ReadOnlyMemory<byte> buffer, WriteOptions options,
        CancellationToken cancellationToken)
    {
        await m_gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var through = !m_writeBack || (options & WriteOptions.Durable) != 0;
            if (through)
                await m_inner.WriteAsync(offset, buffer, options, cancellationToken).ConfigureAwait(false);
            for (var at = 0; at < buffer.Length;)
            {
                var block = (offset + at) / m_block;
                var start = block * m_block;
                var within = (int)(offset + at - start);
                var length = Math.Min(BlockLength(block) - within, buffer.Length - at);
                var whole = within == 0 && length == BlockLength(block);
                if (!m_slots.TryGetValue(block, out var slot))
                {
                    if (through && !whole)
                    {
                        at += length; // written through and not held: the resource has it
                        continue;
                    }
                    slot = await TakeSlotAsync(block, cancellationToken).ConfigureAwait(false);
                    if (!whole)
                        await m_inner.ReadAsync(start, Bytes(slot)[..BlockLength(block)], cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Touch(slot);
                }
                buffer.Slice(at, length).CopyTo(Bytes(slot)[within..]);
                if (!through)
                    SetDirty(slot, true);
                else if (whole)
                    SetDirty(slot, false);
                at += length;
            }
        }
        finally
        {
            m_gate.Release();
        }
    }

    /// <inheritdoc />
    protected override async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        await m_gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteDirtyAsync(cancellationToken).ConfigureAwait(false);
            await m_inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            m_gate.Release();
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DiscardCoreAsync(long offset, long length, CancellationToken cancellationToken)
    {
        await m_gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var block = offset / m_block; length > 0 && block <= (offset + length - 1) / m_block; block++)
            {
                if (!m_slots.TryGetValue(block, out var slot))
                    continue;
                var start = block * m_block;
                var whole = offset <= start && start + BlockLength(block) <= offset + length;
                // A block the discard covers in part keeps its other bytes: a dirty one reaches the resource first.
                if (!whole && m_dirty[slot])
                    await m_inner.WriteAsync(start, Bytes(slot)[..BlockLength(block)].ToArray(), WriteOptions.None, cancellationToken).ConfigureAwait(false);
                Forget(slot); // a discarded block's dirty bytes are no longer wanted
            }
            await m_inner.DiscardAsync(offset, length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            m_gate.Release();
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeCoreAsync()
    {
        await m_gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Info.IsReadOnly)
                await WriteDirtyAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            m_gate.Release();
        }
        if (!m_leaveOpen)
            await m_inner.DisposeAsync().ConfigureAwait(false);
        m_gate.Dispose();
    }

    // A slot for the block as the most recently used: one a discard freed, a new one while the cache fills, otherwise the
    // least recently used one, written first when it is dirty.
    private async ValueTask<int> TakeSlotAsync(long block, CancellationToken cancellationToken)
    {
        int slot;
        if (m_free.Count > 0)
        {
            slot = m_free.Pop();
        }
        else if (m_used < m_capacity)
        {
            slot = m_used++;
            if (slot / m_segmentBlocks == m_segments.Count)
                m_segments.Add(new byte[(long)Math.Min(m_segmentBlocks, m_capacity - slot) * m_block]);
        }
        else
        {
            slot = m_oldest;
            if (m_dirty[slot])
                await m_inner.WriteAsync(m_blocks[slot] * m_block, Bytes(slot)[..BlockLength(m_blocks[slot])].ToArray(), WriteOptions.None,
                    cancellationToken).ConfigureAwait(false);
            Unlink(slot);
            m_slots.Remove(m_blocks[slot]);
            SetDirty(slot, false);
        }
        m_blocks[slot] = block;
        m_slots[block] = slot;
        Link(slot);
        return slot;
    }

    // Every dirty block in ascending order, runs of adjacent blocks in writes of the transfer limit.
    private async ValueTask WriteDirtyAsync(CancellationToken cancellationToken)
    {
        if (DirtyCount == 0)
            return;
        var dirty = new List<int>();
        for (var slot = m_newest; slot != NONE; slot = m_older[slot])
        {
            if (m_dirty[slot])
                dirty.Add(slot);
        }
        dirty.Sort((a, b) => m_blocks[a].CompareTo(m_blocks[b]));
        for (var i = 0; i < dirty.Count;)
        {
            var run = 1;
            while (i + run < dirty.Count && m_blocks[dirty[i + run]] == m_blocks[dirty[i]] + run && (run + 1) * (long)m_block <= m_transfer)
                run++;
            var start = m_blocks[dirty[i]] * m_block;
            var bytes = new byte[Math.Min(start + run * (long)m_block, Info.Length) - start];
            for (var j = 0; j < run; j++)
                Bytes(dirty[i + j])[..BlockLength(m_blocks[dirty[i + j]])].CopyTo(bytes.AsMemory(j * m_block));
            await m_inner.WriteAsync(start, bytes, WriteOptions.None, cancellationToken).ConfigureAwait(false);
            for (var j = 0; j < run; j++)
                SetDirty(dirty[i + j], false);
            i += run;
        }
    }

    // The part of the slot's block from the position to the block's end or the buffer's, copied out.
    private int CopyOut(int slot, long position, Span<byte> buffer)
    {
        var block = m_blocks[slot];
        var within = (int)(position - block * m_block);
        var length = Math.Min(BlockLength(block) - within, buffer.Length);
        Bytes(slot).Span.Slice(within, length).CopyTo(buffer);
        return length;
    }

    // The block's bytes on the resource: the block size, but for a last block the resource ends within.
    private int BlockLength(long block) => (int)Math.Min(m_block, Info.Length - block * m_block);

    private Memory<byte> Bytes(int slot) => m_segments[slot / m_segmentBlocks].AsMemory(slot % m_segmentBlocks * m_block, m_block);

    private void SetDirty(int slot, bool dirty)
    {
        if (m_dirty[slot] != dirty)
            DirtyCount += dirty ? 1 : -1;
        m_dirty[slot] = dirty;
    }

    // The slot out of the cache, free for the next block: off the order, out of the index, clean.
    private void Forget(int slot)
    {
        Unlink(slot);
        m_slots.Remove(m_blocks[slot]);
        SetDirty(slot, false);
        m_free.Push(slot);
    }

    private void Touch(int slot)
    {
        Unlink(slot);
        Link(slot);
    }

    // The slot at the most recently used end of the order.
    private void Link(int slot)
    {
        m_older[slot] = m_newest;
        m_newer[slot] = NONE;
        if (m_newest != NONE)
            m_newer[m_newest] = slot;
        m_newest = slot;
        if (m_oldest == NONE)
            m_oldest = slot;
    }

    private void Unlink(int slot)
    {
        var older = m_older[slot];
        var newer = m_newer[slot];
        if (older != NONE)
            m_newer[older] = newer;
        else if (m_oldest == slot)
            m_oldest = newer;
        if (newer != NONE)
            m_older[newer] = older;
        else if (m_newest == slot)
            m_newest = older;
        m_older[slot] = m_newer[slot] = NONE;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The blocks read from the cache.
    /// </summary>
    public long Hits { get; private set; }

    /// <summary>
    /// The blocks the cache had to read from the resource.
    /// </summary>
    public long Misses { get; private set; }

    /// <summary>
    /// The blocks written to the cache and not yet to the resource.
    /// </summary>
    public int DirtyCount { get; private set; }

    /// <summary>
    /// The cache's block in bytes.
    /// </summary>
    public int BlockSize => m_block;

    /// <summary>
    /// The bytes the cache holds at most.
    /// </summary>
    public long CapacityBytes => (long)m_capacity * m_block;

    #endregion
}
