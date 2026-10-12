using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.Storage;

/// <summary>
/// A cache over a block storage resource (storage document §35–36, plan step D3.1), itself a resource, chosen where the
/// medium is slow rather than everywhere. It holds the least recently used blocks of RequiredAlignment bytes up to its
/// capacity. Writing through, a write reaches the resource before it completes. Writing back, a write stays in the cache,
/// dirty, until an eviction, a flush or the cache's end writes it; a flush writes every dirty block in ascending order and
/// then flushes the resource, so no later write is ordered before it (§48), and a Durable write goes through at once.
/// Requests are served one at a time.
/// </summary>
public sealed class CachedBlockStorage : BlockStorageBase
{
    #region Fields

    private readonly IBlockStorage m_inner;
    private readonly int m_capacity;
    private readonly bool m_writeBack;
    private readonly bool m_leaveOpen;
    private readonly int m_block;
    private readonly Dictionary<long, LinkedListNode<Entry>> m_entries = [];
    private readonly LinkedList<Entry> m_order = new();
    private readonly SemaphoreSlim m_gate = new(1, 1);

    #endregion

    #region Constructors

    /// <summary>
    /// A cache over the resource.
    /// </summary>
    /// <param name="inner">The resource.</param>
    /// <param name="capacity">The blocks the cache holds.</param>
    /// <param name="writeBack">Whether writes stay in the cache until a flush.</param>
    /// <param name="leaveOpen">Whether the cache's end leaves the resource open.</param>
    public CachedBlockStorage(IBlockStorage inner, int capacity = 1024, bool writeBack = false, bool leaveOpen = false)
        : base(inner.Info with { MaxOutstandingRequests = 1 })
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        m_inner = inner;
        m_capacity = capacity;
        m_writeBack = writeBack;
        m_leaveOpen = leaveOpen;
        m_block = Info.RequiredAlignment;
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    protected override async ValueTask ReadCoreAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await m_gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var at = 0; at < buffer.Length; at += m_block)
            {
                var block = (offset + at) / m_block;
                if (m_entries.TryGetValue(block, out var node))
                {
                    Touch(node);
                    node.Value.Bytes.CopyTo(buffer.Slice(at, m_block));
                    Hits++;
                    continue;
                }
                // A run of misses goes to the resource in one read and into the cache block by block.
                var run = 1;
                while (at + run * m_block < buffer.Length && !m_entries.ContainsKey(block + run))
                    run++;
                var target = buffer.Slice(at, run * m_block);
                await m_inner.ReadAsync(block * m_block, target, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < run; i++)
                    await InsertAsync(block + i, target.Slice(i * m_block, m_block).ToArray(), dirty: false, cancellationToken).ConfigureAwait(false);
                Misses += run;
                at += (run - 1) * m_block;
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
            for (var at = 0; at < buffer.Length; at += m_block)
            {
                var block = (offset + at) / m_block;
                var bytes = buffer.Slice(at, m_block).ToArray();
                if (m_entries.TryGetValue(block, out var node))
                {
                    node.Value.Bytes = bytes;
                    node.Value.Dirty = !through;
                    Touch(node);
                }
                else
                {
                    await InsertAsync(block, bytes, !through, cancellationToken).ConfigureAwait(false);
                }
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
            for (var block = offset / m_block; block < (offset + length) / m_block; block++)
            {
                if (m_entries.Remove(block, out var node))
                    m_order.Remove(node); // a discarded block's dirty bytes are no longer wanted
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

    // A block into the cache as the most recently used, evicting the least recently used beyond the capacity, written
    // first when it is dirty.
    private async ValueTask InsertAsync(long block, byte[] bytes, bool dirty, CancellationToken cancellationToken)
    {
        while (m_entries.Count >= m_capacity)
        {
            var oldest = m_order.Last!;
            if (oldest.Value.Dirty)
                await m_inner.WriteAsync(oldest.Value.Block * m_block, oldest.Value.Bytes, WriteOptions.None, cancellationToken).ConfigureAwait(false);
            m_order.RemoveLast();
            m_entries.Remove(oldest.Value.Block);
        }
        m_entries[block] = m_order.AddFirst(new Entry(block, bytes, dirty));
    }

    // Every dirty block in ascending order, runs of adjacent blocks in one write each.
    private async ValueTask WriteDirtyAsync(CancellationToken cancellationToken)
    {
        var dirty = new List<Entry>();
        foreach (var entry in m_order)
        {
            if (entry.Dirty)
                dirty.Add(entry);
        }
        dirty.Sort((a, b) => a.Block.CompareTo(b.Block));
        for (var i = 0; i < dirty.Count;)
        {
            var run = 1;
            while (i + run < dirty.Count && dirty[i + run].Block == dirty[i].Block + run)
                run++;
            var bytes = new byte[run * m_block];
            for (var j = 0; j < run; j++)
                dirty[i + j].Bytes.CopyTo(bytes, j * m_block);
            await m_inner.WriteAsync(dirty[i].Block * m_block, bytes, WriteOptions.None, cancellationToken).ConfigureAwait(false);
            for (var j = 0; j < run; j++)
                dirty[i + j].Dirty = false;
            i += run;
        }
    }

    private void Touch(LinkedListNode<Entry> node)
    {
        m_order.Remove(node);
        m_order.AddFirst(node);
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
    public int DirtyCount
    {
        get
        {
            var count = 0;
            foreach (var entry in m_order)
                count += entry.Dirty ? 1 : 0;
            return count;
        }
    }

    #endregion

    #region Types

    private sealed class Entry(long block, byte[] bytes, bool dirty)
    {
        public long Block { get; } = block;

        public byte[] Bytes { get; set; } = bytes;

        public bool Dirty { get; set; } = dirty;
    }

    #endregion
}
