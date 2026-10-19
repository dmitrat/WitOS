using System.Collections.Generic;

namespace OutWit.OS.Networking;

/// <summary>
/// A UDP socket of the stack (plan step W1.2a): its local address and port once bound, its peer once connected, and a
/// bounded queue of received datagrams, beyond which the newest are dropped, as a full socket buffer drops them.
/// </summary>
public sealed class UdpSocket
{
    #region Constants

    /// <summary>
    /// The datagrams a socket queues at most.
    /// </summary>
    public const int QUEUE = 128;

    /// <summary>
    /// The bytes a socket queues at most.
    /// </summary>
    public const int QUEUE_BYTES = 256 * 1024;

    #endregion

    #region Fields

    private readonly Queue<Datagram> m_received = new();
    private int m_receivedBytes;

    #endregion

    #region Constructors

    internal UdpSocket(int id)
    {
        Id = id;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Queues a datagram unless the queue is full.
    /// </summary>
    /// <param name="datagram">The datagram.</param>
    /// <returns>Whether it was queued.</returns>
    internal bool Enqueue(Datagram datagram)
    {
        if (m_received.Count >= QUEUE || m_receivedBytes + datagram.Payload.Length > QUEUE_BYTES)
        {
            Dropped++;
            return false;
        }
        m_received.Enqueue(datagram);
        m_receivedBytes += datagram.Payload.Length;
        return true;
    }

    /// <summary>
    /// Takes the oldest datagram, or looks at it.
    /// </summary>
    /// <param name="peek">Whether it stays queued.</param>
    /// <returns>The datagram, or null when none waits.</returns>
    internal Datagram? Take(bool peek)
    {
        if (m_received.Count == 0)
            return null;
        if (peek)
            return m_received.Peek();
        var datagram = m_received.Dequeue();
        m_receivedBytes -= datagram.Payload.Length;
        return datagram;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The socket's identifier in the stack.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// The local address: Any until bound to one of the stack's.
    /// </summary>
    public Ipv4Address LocalAddress { get; internal set; }

    /// <summary>
    /// The local port: zero until bound.
    /// </summary>
    public ushort LocalPort { get; internal set; }

    /// <summary>
    /// The peer's address once connected.
    /// </summary>
    public Ipv4Address RemoteAddress { get; internal set; }

    /// <summary>
    /// The peer's port once connected; zero while not.
    /// </summary>
    public ushort RemotePort { get; internal set; }

    /// <summary>
    /// Whether a peer is set.
    /// </summary>
    public bool IsConnected => RemotePort != 0;

    /// <summary>
    /// The datagrams waiting.
    /// </summary>
    public int Pending => m_received.Count;

    /// <summary>
    /// The datagrams dropped because the queue was full.
    /// </summary>
    public long Dropped { get; private set; }

    /// <summary>
    /// The error a connected socket learned from an ICMP report (ECONNREFUSED), returned once by the next call.
    /// </summary>
    public int PendingError { get; internal set; }

    /// <summary>
    /// Whether SO_BROADCAST is set: datagrams to a broadcast address are allowed.
    /// </summary>
    public bool Broadcast { get; internal set; }

    /// <summary>
    /// SO_REUSEADDR.
    /// </summary>
    public bool ReuseAddress { get; internal set; }

    #endregion
}

/// <summary>
/// A received datagram: its payload and where it came from.
/// </summary>
/// <param name="Source">The sender's address.</param>
/// <param name="SourcePort">The sender's port.</param>
/// <param name="Payload">The payload.</param>
public sealed record Datagram(Ipv4Address Source, ushort SourcePort, byte[] Payload);
