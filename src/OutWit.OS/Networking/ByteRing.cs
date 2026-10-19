using System;

namespace OutWit.OS.Networking;

/// <summary>
/// A byte queue of a bounded capacity (plan step W1.2b): a TCP connection's send and receive buffers. Its storage grows by
/// doubling up to the capacity, so an idle connection holds little; bytes are appended at the tail, read or skipped at
/// the head, and read at an offset from the head for retransmission.
/// </summary>
public sealed class ByteRing
{
    #region Constants

    private const int INITIAL = 4096;

    #endregion

    #region Fields

    private byte[] m_bytes;
    private int m_head;

    #endregion

    #region Constructors

    /// <summary>
    /// An empty queue.
    /// </summary>
    /// <param name="capacity">The most bytes it holds.</param>
    public ByteRing(int capacity)
    {
        Capacity = capacity;
        m_bytes = new byte[Math.Min(INITIAL, capacity)];
    }

    #endregion

    #region Functions

    /// <summary>
    /// Appends as many bytes as there is room for.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The bytes appended.</returns>
    public int Append(ReadOnlySpan<byte> bytes)
    {
        var count = Math.Min(bytes.Length, Free);
        if (count == 0)
            return 0;
        Reserve(Count + count);
        var tail = (m_head + Count) % m_bytes.Length;
        var first = Math.Min(count, m_bytes.Length - tail);
        bytes[..first].CopyTo(m_bytes.AsSpan(tail));
        bytes[first..count].CopyTo(m_bytes);
        Count += count;
        return count;
    }

    /// <summary>
    /// Copies bytes from an offset after the head without taking them.
    /// </summary>
    /// <param name="offset">The offset from the head.</param>
    /// <param name="target">Where the bytes go; as many as it holds and the queue has after the offset.</param>
    /// <returns>The bytes copied.</returns>
    public int Peek(int offset, Span<byte> target)
    {
        var count = Math.Min(target.Length, Count - offset);
        if (count <= 0)
            return 0;
        var start = (m_head + offset) % m_bytes.Length;
        var first = Math.Min(count, m_bytes.Length - start);
        m_bytes.AsSpan(start, first).CopyTo(target);
        m_bytes.AsSpan(0, count - first).CopyTo(target[first..]);
        return count;
    }

    /// <summary>
    /// Takes bytes from the head into a target.
    /// </summary>
    /// <param name="target">Where the bytes go.</param>
    /// <returns>The bytes taken.</returns>
    public int Read(Span<byte> target)
    {
        var count = Peek(0, target);
        Skip(count);
        return count;
    }

    /// <summary>
    /// Drops bytes from the head.
    /// </summary>
    /// <param name="count">The bytes, at most Count.</param>
    public void Skip(int count)
    {
        count = Math.Min(count, Count);
        Count -= count;
        m_head = Count == 0 ? 0 : (m_head + count) % m_bytes.Length;
    }

    // Grows the storage to hold the bytes, keeping them in order from index zero.
    private void Reserve(int needed)
    {
        if (needed <= m_bytes.Length)
            return;
        var size = m_bytes.Length;
        while (size < needed)
            size = Math.Min(size * 2, Capacity);
        var bytes = new byte[size];
        Peek(0, bytes);
        m_bytes = bytes;
        m_head = 0;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The most bytes the queue holds.
    /// </summary>
    public int Capacity { get; }

    /// <summary>
    /// The bytes it holds.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// The room left.
    /// </summary>
    public int Free => Capacity - Count;

    #endregion
}
