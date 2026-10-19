using System;
using System.Collections.Generic;

namespace OutWit.OS.Networking;

/// <summary>
/// TCP (plan step W1.2b): RFC 9293's connections and their segment processing, the retransmission timer of RFC 6298 with
/// Karn's algorithm, congestion control by RFC 5681 with NewReno's fast recovery (RFC 6582) and an initial window of ten
/// segments (RFC 6928), the maximum segment size and window scaling (RFC 7323) options, delayed acknowledgments,
/// Nagle's algorithm, zero-window probes, RFC 5961's challenge acknowledgments for a reset or a SYN inside the window,
/// and listening sockets with a backlog. A socket's calls never wait: what would wait answers AGAIN, and the socket's
/// readiness event follows the change.
/// </summary>
public sealed partial class NetworkStack
{
    #region Constants

    /// <summary>
    /// A connection's send and receive buffers.
    /// </summary>
    public const int TCP_BUFFER_BYTES = 256 * 1024;

    /// <summary>
    /// The most connections a listening socket holds for the application, accepted or in SYN-RECEIVED.
    /// </summary>
    public const int MAX_BACKLOG = 128;

    // The shift of the windows this side advertises: a buffer of 256 KiB in 65535 << 3.
    private const int RECEIVE_SCALE = 3;

    // RFC 6298's initial timeout, Linux's floor of 200 ms (RFC 6298 asks one second; Linux, the reference the guest's
    // programs know, uses 200 ms), and its ceiling.
    private const long RTO_INITIAL = 1000;
    private const long RTO_MIN = 200;
    private const long RTO_MAX = 60_000;

    // Linux's tcp_syn_retries and tcp_retries2.
    private const int SYN_RETRIES = 6;
    private const int DATA_RETRIES = 15;

    // Linux's delayed acknowledgment's floor.
    private const long DELAYED_ACK_MILLISECONDS = 40;

    // Linux's TCP_TIMEWAIT_LEN, and its tcp_fin_timeout for a closed socket waiting for its peer's FIN.
    private const long TIME_WAIT_MILLISECONDS = 60_000;
    private const long FIN_TIMEOUT_MILLISECONDS = 60_000;

    // The segments a connection keeps beyond a hole, and their bytes.
    private const int OUT_OF_ORDER_SEGMENTS = 64;
    private const int OUT_OF_ORDER_BYTES = TCP_BUFFER_BYTES;

    // RFC 6928's initial window, in segments; RFC 9293's default maximum segment size.
    private const int INITIAL_WINDOW_SEGMENTS = 10;
    private const int DEFAULT_MSS = 536;

    // The congestion window's ceiling, far beyond what a buffer holds.
    private const int MAX_CWND = 16 * TCP_BUFFER_BYTES;

    #endregion

    #region Fields

    private readonly Dictionary<int, TcpSocket> m_tcp = [];

    #endregion

    #region Functions

    /// <summary>
    /// Opens a TCP socket.
    /// </summary>
    /// <param name="socket">The socket's identifier.</param>
    /// <returns>Zero or an errno value.</returns>
    public int OpenTcp(out int socket)
    {
        socket = 0;
        if (m_udp.Count + m_tcp.Count >= SOCKETS)
            return NetworkErrors.NO_BUFFERS;
        socket = m_nextSocket++;
        m_tcp[socket] = new TcpSocket(socket, TCP_BUFFER_BYTES, TCP_BUFFER_BYTES);
        return 0;
    }

    /// <summary>
    /// A TCP socket of the stack.
    /// </summary>
    /// <param name="socket">The socket's identifier.</param>
    /// <returns>The socket, or null.</returns>
    public TcpSocket? FindTcp(int socket) => m_tcp.GetValueOrDefault(socket);

    /// <summary>
    /// Makes a socket listen; an unbound one takes an ephemeral port, as Linux's listen does.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="backlog">The connections it holds for the application; bounded to 1..MAX_BACKLOG.</param>
    /// <returns>Zero or an errno value.</returns>
    public int Listen(int socket, int backlog)
    {
        if (!m_tcp.TryGetValue(socket, out var tcp))
            return m_udp.ContainsKey(socket) ? NetworkErrors.NOT_SUPPORTED : NetworkErrors.BAD_SOCKET;
        if (tcp.State != TcpState.Listen && (tcp.State != TcpState.Closed || tcp.RemotePort != 0))
            return NetworkErrors.INVALID;
        if (tcp.LocalPort == 0 && BindTcp(tcp, Ipv4Address.Any, 0) is var error and not 0)
            return error;
        tcp.Backlog = Math.Clamp(backlog, 1, MAX_BACKLOG);
        tcp.State = TcpState.Listen;
        return 0;
    }

    /// <summary>
    /// Takes a connection a listening socket accepted.
    /// </summary>
    /// <param name="listener">The listening socket.</param>
    /// <param name="socket">The connection's socket.</param>
    /// <returns>Zero, AGAIN when none waits, or another errno value.</returns>
    public int Accept(int listener, out int socket)
    {
        socket = 0;
        if (!m_tcp.TryGetValue(listener, out var tcp))
            return m_udp.ContainsKey(listener) ? NetworkErrors.NOT_SUPPORTED : NetworkErrors.BAD_SOCKET;
        if (tcp.State != TcpState.Listen)
            return NetworkErrors.INVALID;
        if (tcp.Accepted.Count == 0)
        {
            tcp.WaitsToRead = true;
            return NetworkErrors.AGAIN;
        }
        var connection = tcp.Accepted.Dequeue();
        connection.Parent = null;
        socket = connection.Id;
        return 0;
    }

    /// <summary>
    /// Queues bytes on a connection: as many as its send buffer holds.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="sent">The bytes queued.</param>
    /// <returns>Zero, AGAIN when the buffer is full or the connection not yet established, or another errno value.</returns>
    public int SendStream(int socket, ReadOnlySpan<byte> bytes, out int sent)
    {
        sent = 0;
        if (!m_tcp.TryGetValue(socket, out var tcp))
            return NetworkErrors.BAD_SOCKET;
        if (TakeError(tcp) is var pending and not 0)
            return pending;
        if (tcp.State is TcpState.SynSent or TcpState.SynReceived)
        {
            tcp.WaitsToWrite = true;
            return NetworkErrors.AGAIN;
        }
        if (tcp.State is TcpState.Closed or TcpState.Listen && tcp.RemotePort == 0)
            return NetworkErrors.NOT_CONNECTED;
        if (tcp.WriteShut || tcp.State is not (TcpState.Established or TcpState.CloseWait))
            return NetworkErrors.BROKEN_PIPE;
        sent = tcp.Send.Append(bytes);
        if (sent < bytes.Length)
            tcp.WaitsToWrite = true;
        if (sent == 0 && bytes.Length > 0)
            return NetworkErrors.AGAIN;
        Output(tcp);
        return 0;
    }

    /// <summary>
    /// Reads bytes a connection received in order; zero bytes at the end of the stream.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="peek">Whether the bytes stay.</param>
    /// <param name="received">The bytes read.</param>
    /// <returns>Zero, AGAIN when none waits, or another errno value.</returns>
    public int ReceiveStream(int socket, Span<byte> buffer, bool peek, out int received)
    {
        received = 0;
        if (!m_tcp.TryGetValue(socket, out var tcp))
            return NetworkErrors.BAD_SOCKET;
        if (tcp.Receive.Count > 0 && buffer.Length > 0)
        {
            received = peek ? tcp.Receive.Peek(0, buffer) : tcp.Receive.Read(buffer);
            if (!peek)
                UpdateWindow(tcp);
            return 0;
        }
        if (TakeError(tcp) is var pending and not 0)
            return pending;
        if (tcp.FinReceived || tcp.ReadShut || buffer.Length == 0)
            return 0;
        if (tcp.State is TcpState.Closed or TcpState.Listen)
            return tcp.RemotePort != 0 ? 0 : NetworkErrors.NOT_CONNECTED;
        tcp.WaitsToRead = true;
        return NetworkErrors.AGAIN;
    }

    /// <summary>
    /// Shuts a connection's reading, writing or both down: reads end once the bytes received are read, and a FIN follows
    /// the bytes queued.
    /// </summary>
    /// <returns>Zero or an errno value.</returns>
    public int Shutdown(int socket, bool read, bool write)
    {
        if (!m_tcp.TryGetValue(socket, out var tcp))
            return m_udp.ContainsKey(socket) ? NetworkErrors.NOT_SUPPORTED : NetworkErrors.BAD_SOCKET;
        if (!tcp.IsConnected && tcp.State is not (TcpState.SynSent or TcpState.SynReceived))
            return NetworkErrors.NOT_CONNECTED;
        if (read && !tcp.ReadShut)
        {
            tcp.ReadShut = true;
            SocketReady?.Invoke(tcp.Id);
        }
        if (write && !tcp.WriteShut)
        {
            tcp.WriteShut = true;
            Output(tcp);
        }
        return 0;
    }

    /// <summary>
    /// Sets TCP_NODELAY.
    /// </summary>
    /// <returns>Zero or an errno value.</returns>
    public int SetNoDelay(int socket, bool enabled)
    {
        if (!m_tcp.TryGetValue(socket, out var tcp))
            return m_udp.ContainsKey(socket) ? NetworkErrors.NOT_SUPPORTED : NetworkErrors.BAD_SOCKET;
        tcp.NoDelay = enabled;
        if (enabled)
            Output(tcp);
        return 0;
    }

    private int BindTcp(TcpSocket tcp, Ipv4Address address, ushort port)
    {
        if (tcp.LocalPort != 0)
            return NetworkErrors.INVALID;
        if (address != Ipv4Address.Any && address != Address && !address.IsLoopback)
            return NetworkErrors.ADDRESS_NOT_AVAILABLE;
        if (port == 0)
            port = EphemeralTcpPort();
        else if (TcpPortTaken(port, address, tcp.ReuseAddress, tcp.ReusePort))
            return NetworkErrors.ADDRESS_IN_USE;
        if (port == 0)
            return NetworkErrors.ADDRESS_IN_USE;
        tcp.LocalAddress = address;
        tcp.LocalPort = port;
        return 0;
    }

    // Starts a connection: SYN-SENT, answered at once with IN_PROGRESS; the socket becomes writable when it is
    // established, or reports its error.
    private int ConnectTcp(TcpSocket tcp, Ipv4Address address, ushort port)
    {
        if (tcp.State == TcpState.SynSent)
            return NetworkErrors.ALREADY;
        if (tcp.IsConnected || tcp.State == TcpState.SynReceived)
            return NetworkErrors.IS_CONNECTED;
        if (tcp.State == TcpState.Listen || tcp.RemotePort != 0)
            return NetworkErrors.INVALID;
        if (Address == Ipv4Address.Any && !address.IsLoopback)
            return NetworkErrors.NETWORK_UNREACHABLE;
        if (address == Ipv4Address.Broadcast || address.IsMulticast || address == Ipv4Address.Any)
            return NetworkErrors.NETWORK_UNREACHABLE;
        if (tcp.LocalPort == 0 && BindTcp(tcp, Ipv4Address.Any, 0) is var error and not 0)
            return error;
        var now = m_clock();
        if (tcp.LocalAddress == Ipv4Address.Any)
            tcp.LocalAddress = address.IsLoopback ? Ipv4Address.Loopback : Address;
        else if (tcp.LocalAddress.IsLoopback != address.IsLoopback)
            return NetworkErrors.NETWORK_UNREACHABLE;
        tcp.RemoteAddress = address;
        tcp.RemotePort = port;
        tcp.Iss = (uint)m_random.NextInt64();
        tcp.SndUna = tcp.Iss;
        tcp.SndNxt = tcp.Iss + 1;
        tcp.SndMax = tcp.Iss + 1;
        tcp.Recover = tcp.Iss;
        tcp.RcvScale = RECEIVE_SCALE;
        tcp.Mss = LocalMss();
        tcp.Rto = RTO_INITIAL;
        tcp.State = TcpState.SynSent;
        tcp.Timing = true;
        tcp.TimedSequence = tcp.Iss;
        tcp.TimedAt = now;
        tcp.RetransmitAt = now + tcp.Rto;
        if (!Transmit(tcp, tcp.Iss, TcpHeader.SYN, []))
        {
            tcp.State = TcpState.Closed;
            tcp.RemotePort = 0;
            tcp.RetransmitAt = long.MaxValue;
            return NetworkErrors.NETWORK_UNREACHABLE;
        }
        return NetworkErrors.IN_PROGRESS;
    }

    // Closes a TCP socket for the application: a listener's pending connections are reset; a connection with unread bytes
    // is reset, as Linux resets it; any other finishes on its own, its FIN after the bytes queued, and goes when it ends.
    private int CloseTcp(TcpSocket tcp)
    {
        switch (tcp.State)
        {
            case TcpState.Listen:
                foreach (var connection in new List<TcpSocket>(m_tcp.Values))
                {
                    if (connection.Parent == tcp)
                    {
                        Reset(connection);
                        m_tcp.Remove(connection.Id);
                    }
                }
                m_tcp.Remove(tcp.Id);
                return 0;
            case TcpState.Closed or TcpState.SynSent:
                m_tcp.Remove(tcp.Id);
                return 0;
        }
        if (tcp.Receive.Count > 0 || tcp.OutOfOrderBytes > 0)
        {
            Reset(tcp);
            m_tcp.Remove(tcp.Id);
            return 0;
        }
        tcp.Orphan = true;
        if (!tcp.WriteShut)
        {
            tcp.WriteShut = true;
            Output(tcp);
        }
        if (tcp.State == TcpState.FinWait2)
            tcp.LingerAt = m_clock() + FIN_TIMEOUT_MILLISECONDS;
        return 0;
    }

    // A TCP socket's readiness, which also arms the events of what it lacks.
    private uint PollTcp(TcpSocket tcp)
    {
        uint readiness = 0;
        if (tcp.State == TcpState.Listen)
        {
            if (tcp.Accepted.Count > 0)
                readiness |= READABLE;
            else
                tcp.WaitsToRead = true;
            return readiness;
        }
        if (tcp.PendingError != 0)
            readiness |= ERROR;
        if (tcp.Receive.Count > 0 || tcp.FinReceived || tcp.ReadShut)
            readiness |= READABLE;
        if (tcp.State == TcpState.Closed)
            readiness |= tcp.RemotePort != 0 ? READABLE | WRITABLE | HANGUP : WRITABLE | HANGUP;
        else if (tcp.State is TcpState.Established or TcpState.CloseWait && !tcp.WriteShut && tcp.Send.Free > 0)
            readiness |= WRITABLE;
        if (tcp.FinReceived && tcp.WriteShut)
            readiness |= HANGUP;
        if ((readiness & READABLE) == 0)
            tcp.WaitsToRead = true;
        if ((readiness & WRITABLE) == 0)
            tcp.WaitsToWrite = true;
        return readiness;
    }

    // A segment for this host: its connection's, a listener's, or answered with a reset.
    private void ReceiveTcp(Ipv4Header ip, ReadOnlySpan<byte> segment)
    {
        if (ip.Destination == Ipv4Address.Broadcast || ip.Destination.IsMulticast ||
            !TcpHeader.TryRead(segment, ip.Source, ip.Destination, out var header))
        {
            Statistics.PacketsDropped++;
            return;
        }
        var payload = segment[header.HeaderLength..];
        TcpSocket? listener = null;
        var group = new List<TcpSocket>();
        foreach (var tcp in m_tcp.Values)
        {
            if (tcp.LocalPort != header.DestinationPort)
                continue;
            if (tcp.State == TcpState.Listen)
            {
                if (tcp.LocalAddress == ip.Destination || (tcp.LocalAddress == Ipv4Address.Any && listener is null))
                    listener = tcp;
                if (tcp.ReusePort)
                    group.Add(tcp);
                continue;
            }
            if (tcp.State != TcpState.Closed && tcp.RemotePort == header.SourcePort && tcp.RemoteAddress == ip.Source &&
                tcp.LocalAddress == ip.Destination)
            {
                if (tcp.State == TcpState.SynSent)
                    SynSentSegment(tcp, header, payload);
                else
                    Segment(tcp, header, payload);
                return;
            }
        }
        // Listeners that share the port through SO_REUSEPORT take the connections in turn of the sender's address and port,
        // among those bound as specifically as the one found.
        if (listener is { ReusePort: true })
        {
            group.RemoveAll(member => member.LocalAddress != listener.LocalAddress);
            if (group.Count > 1)
                listener = group[GroupIndex(ip.Source, header.SourcePort, group.Count)];
        }
        if (listener is not null)
            ListenSegment(listener, ip, header);
        else if (!header.Has(TcpHeader.RST))
            SendReset(ip.Destination, ip.Source, header, payload.Length);
    }

    // A segment to a listening socket: a SYN makes a connection in SYN-RECEIVED while the backlog has room.
    private void ListenSegment(TcpSocket listener, Ipv4Header ip, TcpHeader header)
    {
        if (header.Has(TcpHeader.RST))
            return;
        if (header.Has(TcpHeader.ACK))
        {
            SendReset(ip.Destination, ip.Source, header, 0);
            return;
        }
        if (!header.Has(TcpHeader.SYN))
            return;
        if (listener.Accepted.Count + listener.Embryonic >= listener.Backlog || m_udp.Count + m_tcp.Count >= SOCKETS)
        {
            Statistics.ConnectionsDropped++;
            return;
        }
        var now = m_clock();
        var tcp = new TcpSocket(m_nextSocket++, TCP_BUFFER_BYTES, TCP_BUFFER_BYTES)
        {
            LocalAddress = ip.Destination,
            LocalPort = listener.LocalPort,
            RemoteAddress = ip.Source,
            RemotePort = header.SourcePort,
            ReuseAddress = listener.ReuseAddress,
            ReusePort = listener.ReusePort,
            NoDelay = listener.NoDelay,
            Parent = listener,
            Irs = header.Sequence,
            RcvNxt = header.Sequence + 1,
            Iss = (uint)m_random.NextInt64(),
            State = TcpState.SynReceived,
            Rto = RTO_INITIAL
        };
        m_tcp[tcp.Id] = tcp;
        listener.Embryonic++;
        tcp.SndUna = tcp.Iss;
        tcp.SndNxt = tcp.Iss + 1;
        tcp.SndMax = tcp.Iss + 1;
        tcp.Recover = tcp.Iss;
        tcp.SndWnd = header.Window;
        tcp.SndWl1 = header.Sequence;
        tcp.SndWl2 = tcp.Iss;
        tcp.Mss = Math.Min(header.Mss != 0 ? header.Mss : DEFAULT_MSS, LocalMss());
        tcp.SndScale = Math.Max(header.WindowScale, 0);
        tcp.RcvScale = header.WindowScale >= 0 ? RECEIVE_SCALE : 0;
        tcp.Timing = true;
        tcp.TimedSequence = tcp.Iss;
        tcp.TimedAt = now;
        tcp.RetransmitAt = now + tcp.Rto;
        Transmit(tcp, tcp.Iss, TcpHeader.SYN | TcpHeader.ACK, []);
    }

    // A segment in SYN-SENT (RFC 9293 section 3.10.7.3).
    private void SynSentSegment(TcpSocket tcp, TcpHeader header, ReadOnlySpan<byte> payload)
    {
        var acknowledges = header.Has(TcpHeader.ACK);
        if (acknowledges && (SequenceLe(header.Acknowledgment, tcp.Iss) || SequenceGt(header.Acknowledgment, tcp.SndMax)))
        {
            if (!header.Has(TcpHeader.RST))
                SendReset(tcp.LocalAddress, tcp.RemoteAddress, header, payload.Length);
            return;
        }
        if (header.Has(TcpHeader.RST))
        {
            if (acknowledges)
                Fail(tcp, NetworkErrors.CONNECTION_REFUSED);
            return;
        }
        if (!header.Has(TcpHeader.SYN))
            return;
        var now = m_clock();
        tcp.Irs = header.Sequence;
        tcp.RcvNxt = header.Sequence + 1;
        tcp.Mss = Math.Min(header.Mss != 0 ? header.Mss : DEFAULT_MSS, LocalMss());
        if (header.WindowScale >= 0)
            tcp.SndScale = header.WindowScale;
        else
            tcp.SndScale = tcp.RcvScale = 0;
        if (!acknowledges)
        {
            // A simultaneous open: both sides' SYNs crossed.
            tcp.State = TcpState.SynReceived;
            tcp.SndWnd = header.Window;
            Transmit(tcp, tcp.Iss, TcpHeader.SYN | TcpHeader.ACK, []);
            return;
        }
        if (tcp.Timing)
            UpdateRtt(tcp, now - tcp.TimedAt);
        tcp.Timing = false;
        tcp.SndUna = header.Acknowledgment;
        tcp.SndWnd = header.Window; // a SYN's window is never scaled
        tcp.SndWl1 = header.Sequence;
        tcp.SndWl2 = header.Acknowledgment;
        tcp.Retries = 0;
        tcp.RetransmitAt = long.MaxValue;
        Establish(tcp);
        SendAck(tcp);
        SocketReady?.Invoke(tcp.Id);
    }

    // A segment of a synchronized connection or one in SYN-RECEIVED (RFC 9293 section 3.10.7.4).
    private void Segment(TcpSocket tcp, TcpHeader header, ReadOnlySpan<byte> payload)
    {
        var now = m_clock();
        var length = payload.Length + (header.Has(TcpHeader.SYN) ? 1 : 0) + (header.Has(TcpHeader.FIN) ? 1 : 0);
        var window = SequenceGt(tcp.AdvertisedEdge, tcp.RcvNxt) ? tcp.AdvertisedEdge - tcp.RcvNxt : 0;
        if (!Acceptable(tcp.RcvNxt, window, header.Sequence, length))
        {
            if (!header.Has(TcpHeader.RST))
            {
                if (tcp.State == TcpState.TimeWait && header.Has(TcpHeader.FIN))
                    tcp.LingerAt = now + TIME_WAIT_MILLISECONDS;
                SendAck(tcp);
            }
            return;
        }
        if (header.Has(TcpHeader.RST))
        {
            if (header.Sequence != tcp.RcvNxt)
            {
                SendAck(tcp); // RFC 5961's challenge
                return;
            }
            if (tcp.Parent is not null)
            {
                tcp.Parent.Embryonic--;
                m_tcp.Remove(tcp.Id);
                return;
            }
            if (tcp.State is TcpState.TimeWait or TcpState.LastAck or TcpState.Closing)
                End(tcp);
            else
                Fail(tcp, NetworkErrors.CONNECTION_RESET);
            return;
        }
        if (header.Has(TcpHeader.SYN))
        {
            if (tcp.State == TcpState.SynReceived && header.Sequence == tcp.Irs)
                Transmit(tcp, tcp.Iss, TcpHeader.SYN | TcpHeader.ACK, []); // the peer did not hear our answer
            else
                SendAck(tcp); // RFC 5961's challenge
            return;
        }
        if (!header.Has(TcpHeader.ACK))
            return;
        if (tcp.State == TcpState.SynReceived)
        {
            if (!SequenceGt(header.Acknowledgment, tcp.SndUna) || SequenceGt(header.Acknowledgment, tcp.SndMax))
            {
                SendReset(tcp.LocalAddress, tcp.RemoteAddress, header, payload.Length);
                return;
            }
            if (tcp.Timing)
                UpdateRtt(tcp, now - tcp.TimedAt);
            tcp.Timing = false;
            tcp.SndUna = header.Acknowledgment;
            tcp.SndWnd = (uint)header.Window << tcp.SndScale;
            tcp.SndWl1 = header.Sequence;
            tcp.SndWl2 = header.Acknowledgment;
            tcp.Retries = 0;
            tcp.RetransmitAt = long.MaxValue;
            Establish(tcp);
            if (tcp.Parent is { } parent)
            {
                parent.Embryonic--;
                parent.Accepted.Enqueue(tcp);
                parent.WaitsToRead = false;
                SocketReady?.Invoke(parent.Id);
            }
            else
            {
                SocketReady?.Invoke(tcp.Id);
            }
        }
        else if (!Acknowledge(tcp, header, payload.Length, now))
        {
            return;
        }
        var ackNow = false;
        var received = false;
        var dataEnd = header.Sequence + (uint)payload.Length;
        if (payload.Length > 0 && tcp.State is TcpState.Established or TcpState.FinWait1 or TcpState.FinWait2)
            (ackNow, received) = ReceiveData(tcp, header.Sequence, payload);
        if (header.Has(TcpHeader.FIN) && dataEnd == tcp.RcvNxt && !tcp.FinReceived &&
            tcp.State is TcpState.Established or TcpState.FinWait1 or TcpState.FinWait2)
        {
            tcp.RcvNxt++;
            tcp.FinReceived = true;
            ackNow = true;
            received = true;
            switch (tcp.State)
            {
                case TcpState.Established:
                    tcp.State = TcpState.CloseWait;
                    break;
                case TcpState.FinWait1:
                    if (tcp.FinSent && SequenceGt(tcp.SndUna, tcp.FinSequence))
                        EnterTimeWait(tcp, now);
                    else
                        tcp.State = TcpState.Closing;
                    break;
                case TcpState.FinWait2:
                    EnterTimeWait(tcp, now);
                    break;
            }
        }
        else if (header.Has(TcpHeader.FIN) && tcp.FinReceived)
        {
            ackNow = true; // a FIN again: our acknowledgment was lost
            if (tcp.State == TcpState.TimeWait)
                tcp.LingerAt = now + TIME_WAIT_MILLISECONDS;
        }
        if (received && !tcp.Orphan)
        {
            tcp.WaitsToRead = false;
            SocketReady?.Invoke(tcp.Id);
        }
        if (ackNow)
        {
            SendAck(tcp);
        }
        else if (payload.Length > 0)
        {
            // Every second full segment at once, otherwise within the delayed acknowledgment's time.
            if (++tcp.UnacknowledgedSegments >= 2)
                SendAck(tcp);
            else if (tcp.DelayedAckAt == long.MaxValue)
                tcp.DelayedAckAt = now + DELAYED_ACK_MILLISECONDS;
        }
        if (m_tcp.ContainsKey(tcp.Id))
            Output(tcp);
    }

    // The acknowledgment of a synchronized connection's segment: the window, new data acknowledged, duplicates and the
    // FIN's acknowledgment. Whether processing goes on.
    private bool Acknowledge(TcpSocket tcp, TcpHeader header, int payloadBytes, long now)
    {
        var ack = header.Acknowledgment;
        if (SequenceGt(ack, tcp.SndMax))
        {
            SendAck(tcp);
            return false;
        }
        if (SequenceLt(ack, tcp.SndUna))
            return true; // an old duplicate: its data may still be new
        var windowChanged = false;
        if (SequenceLt(tcp.SndWl1, header.Sequence) || (tcp.SndWl1 == header.Sequence && SequenceLe(tcp.SndWl2, ack)))
        {
            var window = (uint)header.Window << tcp.SndScale;
            windowChanged = window != tcp.SndWnd;
            tcp.SndWnd = window;
            tcp.SndWl1 = header.Sequence;
            tcp.SndWl2 = ack;
            if (window > 0)
                tcp.PersistAt = long.MaxValue;
            else
                tcp.Retries = 0; // a peer that answers with a closed window is alive
        }
        if (SequenceGt(ack, tcp.SndUna))
            NewAck(tcp, ack, now);
        else if (payloadBytes == 0 && !windowChanged && tcp.SndMax != tcp.SndUna && !header.Has(TcpHeader.FIN))
            DuplicateAck(tcp);
        if (tcp.FinSent && SequenceGt(tcp.SndUna, tcp.FinSequence))
        {
            switch (tcp.State)
            {
                case TcpState.FinWait1:
                    tcp.State = TcpState.FinWait2;
                    if (tcp.Orphan)
                        tcp.LingerAt = now + FIN_TIMEOUT_MILLISECONDS;
                    break;
                case TcpState.Closing:
                    EnterTimeWait(tcp, now);
                    break;
                case TcpState.LastAck:
                    End(tcp);
                    return false;
            }
        }
        return true;
    }

    // New data acknowledged: the send buffer, the round trip, the congestion window and the timer.
    private void NewAck(TcpSocket tcp, uint ack, long now)
    {
        var acked = (int)(ack - tcp.SndUna);
        var dataAcked = tcp.FinSent && SequenceGt(ack, tcp.FinSequence) ? acked - 1 : acked;
        tcp.Send.Skip(dataAcked);
        tcp.SndUna = ack;
        if (SequenceLt(tcp.SndNxt, ack))
            tcp.SndNxt = ack;
        if (tcp.Timing && SequenceGt(ack, tcp.TimedSequence))
        {
            UpdateRtt(tcp, now - tcp.TimedAt);
            tcp.Timing = false;
        }
        tcp.Retries = 0;
        if (tcp.InRecovery)
        {
            if (SequenceGe(ack, tcp.Recover))
            {
                tcp.InRecovery = false;
                tcp.Cwnd = tcp.Ssthresh;
                tcp.DuplicateAcks = 0;
            }
            else
            {
                // NewReno's partial acknowledgment: the next hole goes again, the window deflates by what was acknowledged.
                RetransmitFirst(tcp, now);
                tcp.Cwnd = Math.Max(tcp.Cwnd - dataAcked + tcp.Mss, tcp.Mss);
            }
        }
        else
        {
            tcp.DuplicateAcks = 0;
            tcp.Cwnd = Math.Min(tcp.Cwnd < tcp.Ssthresh
                ? tcp.Cwnd + Math.Min(dataAcked, tcp.Mss)
                : tcp.Cwnd + Math.Max(1, tcp.Mss * tcp.Mss / tcp.Cwnd), MAX_CWND);
        }
        tcp.RetransmitAt = tcp.SndUna == tcp.SndMax ? long.MaxValue : now + tcp.Rto;
        if (dataAcked > 0 && tcp.WaitsToWrite && tcp.Send.Free > 0 && !tcp.Orphan)
        {
            tcp.WaitsToWrite = false;
            SocketReady?.Invoke(tcp.Id);
        }
    }

    // A duplicate acknowledgment: the third enters fast recovery (RFC 6582 section 3.2), each later one inflates the window.
    private void DuplicateAck(TcpSocket tcp)
    {
        tcp.DuplicateAcks++;
        if (tcp.InRecovery)
        {
            tcp.Cwnd = Math.Min(tcp.Cwnd + tcp.Mss, MAX_CWND);
            return;
        }
        if (tcp.DuplicateAcks != 3 || !SequenceGt(tcp.SndUna, tcp.Recover))
            return;
        var flight = (int)(tcp.SndMax - tcp.SndUna);
        tcp.Ssthresh = Math.Max(flight / 2, 2 * tcp.Mss);
        tcp.Recover = tcp.SndMax;
        tcp.InRecovery = true;
        RetransmitFirst(tcp, m_clock());
        tcp.Cwnd = tcp.Ssthresh + 3 * tcp.Mss;
        Statistics.FastRetransmits++;
    }

    // Data of a synchronized connection: in order into the receive buffer with what waited beyond it, beyond a hole kept
    // for later. Whether to acknowledge at once, and whether the application has something new to read.
    private (bool AckNow, bool Received) ReceiveData(TcpSocket tcp, uint sequence, ReadOnlySpan<byte> data)
    {
        if (SequenceLt(sequence, tcp.RcvNxt))
        {
            var skip = (int)(tcp.RcvNxt - sequence);
            if (skip >= data.Length)
                return (true, false);
            data = data[skip..];
            sequence = tcp.RcvNxt;
        }
        if (sequence != tcp.RcvNxt)
        {
            KeepOutOfOrder(tcp, sequence, data);
            return (true, false); // a duplicate acknowledgment tells the sender of the hole
        }
        var hadHoles = tcp.OutOfOrderBytes > 0;
        var appended = Take(tcp, data);
        while (appended == data.Length && TakeOutOfOrder(tcp, out var next))
        {
            data = next;
            appended = Take(tcp, data);
        }
        return (hadHoles || appended < data.Length, true);
    }

    // Bytes in order: into the receive buffer, or acknowledged and dropped once the application shut reading down.
    private static int Take(TcpSocket tcp, ReadOnlySpan<byte> data)
    {
        var taken = tcp.ReadShut ? data.Length : tcp.Receive.Append(data);
        tcp.RcvNxt += (uint)taken;
        return taken;
    }

    private static void KeepOutOfOrder(TcpSocket tcp, uint sequence, ReadOnlySpan<byte> data)
    {
        if (tcp.OutOfOrder.Count >= OUT_OF_ORDER_SEGMENTS || tcp.OutOfOrderBytes + data.Length > OUT_OF_ORDER_BYTES ||
            tcp.OutOfOrder.ContainsKey(sequence))
            return;
        tcp.OutOfOrder[sequence] = data.ToArray();
        tcp.OutOfOrderBytes += data.Length;
    }

    // The kept segment that continues the stream, cut to what is new; segments wholly before RCV.NXT go.
    private static bool TakeOutOfOrder(TcpSocket tcp, out byte[] next)
    {
        next = [];
        for (var found = true; found;)
        {
            found = false;
            foreach (var (sequence, bytes) in tcp.OutOfOrder)
            {
                if (SequenceGt(sequence, tcp.RcvNxt))
                    continue;
                tcp.OutOfOrder.Remove(sequence);
                tcp.OutOfOrderBytes -= bytes.Length;
                var skip = (int)(tcp.RcvNxt - sequence);
                if (skip < bytes.Length)
                {
                    next = bytes[skip..];
                    return true;
                }
                found = true;
                break;
            }
        }
        return false;
    }

    // Sends what the windows allow: data in segments of the MSS (Nagle's algorithm holding a small one back while data
    // is in flight, unless TCP_NODELAY or the stream ends), then the FIN once every byte went.
    private void Output(TcpSocket tcp)
    {
        if (tcp.State is not (TcpState.Established or TcpState.CloseWait or TcpState.FinWait1 or TcpState.Closing or TcpState.LastAck))
            return;
        var now = m_clock();
        for (; ; )
        {
            var offset = (int)(tcp.SndNxt - tcp.SndUna);
            var unsent = tcp.Send.Count - offset;
            var window = (int)Math.Min(tcp.SndWnd, (uint)tcp.Cwnd);
            var usable = window - offset;
            if (unsent > 0)
            {
                var length = Math.Min(Math.Min(unsent, tcp.Mss), usable);
                if (length <= 0)
                {
                    if (tcp.SndWnd == 0 && offset == 0 && tcp.PersistAt == long.MaxValue)
                        tcp.PersistAt = now + tcp.Rto;
                    return;
                }
                if (length < tcp.Mss && length == unsent && offset > 0 && !tcp.NoDelay && !tcp.WriteShut)
                    return;
                SendData(tcp, offset, length, now);
                continue;
            }
            if (tcp.WriteShut && unsent == 0 && (!tcp.FinSent || tcp.SndNxt == tcp.FinSequence))
            {
                if (!tcp.FinSent)
                {
                    tcp.FinSent = true;
                    tcp.FinSequence = tcp.SndNxt;
                    tcp.State = tcp.State == TcpState.CloseWait ? TcpState.LastAck : tcp.State == TcpState.Established ? TcpState.FinWait1 : tcp.State;
                }
                Transmit(tcp, tcp.SndNxt, TcpHeader.FIN | TcpHeader.ACK, []);
                tcp.SndNxt++;
                if (SequenceGt(tcp.SndNxt, tcp.SndMax))
                    tcp.SndMax = tcp.SndNxt;
                if (tcp.RetransmitAt == long.MaxValue)
                    tcp.RetransmitAt = now + tcp.Rto;
            }
            return;
        }
    }

    // A data segment from an offset of the send buffer at SND.NXT.
    private void SendData(TcpSocket tcp, int offset, int length, long now)
    {
        var payload = new byte[length];
        tcp.Send.Peek(offset, payload);
        var last = offset + length == tcp.Send.Count;
        var retransmission = SequenceLt(tcp.SndNxt, tcp.SndMax);
        Transmit(tcp, tcp.SndNxt, (byte)(TcpHeader.ACK | (last ? TcpHeader.PSH : 0)), payload);
        if (!tcp.Timing && !retransmission)
        {
            tcp.Timing = true;
            tcp.TimedSequence = tcp.SndNxt;
            tcp.TimedAt = now;
        }
        tcp.SndNxt += (uint)length;
        if (SequenceGt(tcp.SndNxt, tcp.SndMax))
            tcp.SndMax = tcp.SndNxt;
        if (tcp.RetransmitAt == long.MaxValue)
            tcp.RetransmitAt = now + tcp.Rto;
        if (retransmission)
            Statistics.SegmentsRetransmitted++;
    }

    // The oldest unacknowledged segment again (fast retransmit, NewReno's partial acknowledgment), or the FIN alone.
    private void RetransmitFirst(TcpSocket tcp, long now)
    {
        var length = Math.Min(tcp.Mss, tcp.Send.Count);
        if (tcp.Timing && SequenceGe(tcp.TimedSequence, tcp.SndUna))
            tcp.Timing = false; // Karn: a retransmitted segment times nothing
        if (length > 0)
        {
            var payload = new byte[length];
            tcp.Send.Peek(0, payload);
            Transmit(tcp, tcp.SndUna, TcpHeader.ACK, payload);
        }
        else if (tcp.FinSent)
        {
            Transmit(tcp, tcp.FinSequence, TcpHeader.FIN | TcpHeader.ACK, []);
        }
        Statistics.SegmentsRetransmitted++;
        tcp.RetransmitAt = now + tcp.Rto;
    }

    // The TCP timers that are due: retransmission, delayed acknowledgments, zero-window probes, TIME-WAIT and an orphan's
    // FIN-WAIT-2.
    private void AdvanceTcp(long now)
    {
        if (m_tcp.Count == 0)
            return;
        foreach (var tcp in new List<TcpSocket>(m_tcp.Values))
        {
            if (!m_tcp.ContainsKey(tcp.Id))
                continue;
            if (now >= tcp.LingerAt)
            {
                End(tcp);
                continue;
            }
            if (now >= tcp.RetransmitAt)
                RetransmissionTimeout(tcp, now);
            if (!m_tcp.ContainsKey(tcp.Id))
                continue;
            if (now >= tcp.DelayedAckAt)
                SendAck(tcp);
            if (now >= tcp.PersistAt)
            {
                tcp.PersistAt = long.MaxValue;
                if (tcp.SndWnd == 0 && tcp.Send.Count > (int)(tcp.SndNxt - tcp.SndUna))
                    SendData(tcp, (int)(tcp.SndNxt - tcp.SndUna), 1, now); // the probe, which the retransmission timer repeats
            }
        }
    }

    // The retransmission timer expired (RFC 6298 section 5, RFC 5681 section 3.1): the timeout doubles, the window falls
    // to one segment and everything unacknowledged goes again; past the retries the connection fails.
    private void RetransmissionTimeout(TcpSocket tcp, long now)
    {
        tcp.Rto = Math.Min(tcp.Rto * 2, RTO_MAX);
        tcp.Timing = false;
        if (tcp.State is TcpState.SynSent or TcpState.SynReceived)
        {
            if (++tcp.Retries > SYN_RETRIES)
            {
                if (tcp.Parent is { } parent)
                {
                    parent.Embryonic--;
                    m_tcp.Remove(tcp.Id);
                }
                else
                {
                    Fail(tcp, NetworkErrors.TIMED_OUT);
                }
                return;
            }
            Transmit(tcp, tcp.Iss, tcp.State == TcpState.SynSent ? TcpHeader.SYN : (byte)(TcpHeader.SYN | TcpHeader.ACK), []);
            tcp.RetransmitAt = now + tcp.Rto;
            return;
        }
        if (++tcp.Retries > DATA_RETRIES)
        {
            Reset(tcp);
            Fail(tcp, NetworkErrors.TIMED_OUT);
            return;
        }
        var flight = (int)(tcp.SndMax - tcp.SndUna);
        tcp.Ssthresh = Math.Max(flight / 2, 2 * tcp.Mss);
        tcp.Cwnd = tcp.Mss;
        tcp.InRecovery = false;
        tcp.DuplicateAcks = 0;
        tcp.Recover = tcp.SndMax;
        tcp.SndNxt = tcp.SndUna;
        tcp.RetransmitAt = long.MaxValue;
        Statistics.Timeouts++;
        if (tcp.SndWnd == 0 && tcp.Send.Count > 0)
            SendData(tcp, 0, 1, now); // a zero-window probe again
        else
            Output(tcp);
        if (tcp.RetransmitAt == long.MaxValue && tcp.SndUna != tcp.SndMax)
            tcp.RetransmitAt = now + tcp.Rto;
    }

    // RFC 6298 section 2: the smoothed round trip and its variation; the timeout between Linux's floor and the ceiling.
    private static void UpdateRtt(TcpSocket tcp, long sample)
    {
        sample = Math.Max(sample, 1);
        if (tcp.Srtt < 0)
        {
            tcp.Srtt = sample;
            tcp.Rttvar = sample / 2;
        }
        else
        {
            tcp.Rttvar = (3 * tcp.Rttvar + Math.Abs(tcp.Srtt - sample)) / 4;
            tcp.Srtt = (7 * tcp.Srtt + sample) / 8;
        }
        tcp.Rto = Math.Clamp(tcp.Srtt + Math.Max(1, 4 * tcp.Rttvar), RTO_MIN, RTO_MAX);
    }

    // The connection is open: the congestion window starts at ten segments, the slow start threshold as high as it goes.
    private static void Establish(TcpSocket tcp)
    {
        tcp.State = TcpState.Established;
        tcp.Cwnd = Math.Min(INITIAL_WINDOW_SEGMENTS * tcp.Mss, MAX_CWND);
        tcp.Ssthresh = MAX_CWND;
        tcp.SndNxt = tcp.SndUna;
        tcp.SndMax = tcp.SndUna;
    }

    private void EnterTimeWait(TcpSocket tcp, long now)
    {
        tcp.State = TcpState.TimeWait;
        tcp.RetransmitAt = long.MaxValue;
        tcp.PersistAt = long.MaxValue;
        tcp.LingerAt = now + TIME_WAIT_MILLISECONDS;
    }

    // The connection ended without an error: an orphan goes, a socket the application holds stays closed.
    private void End(TcpSocket tcp)
    {
        tcp.RetransmitAt = tcp.DelayedAckAt = tcp.PersistAt = tcp.LingerAt = long.MaxValue;
        if (tcp.Orphan)
        {
            m_tcp.Remove(tcp.Id);
            return;
        }
        tcp.State = TcpState.Closed;
        SocketReady?.Invoke(tcp.Id);
    }

    // The connection failed: a reset, a refusal or a timeout, which the next call returns once.
    private void Fail(TcpSocket tcp, int error)
    {
        tcp.PendingError = error;
        tcp.Send.Skip(tcp.Send.Count);
        tcp.OutOfOrder.Clear();
        tcp.OutOfOrderBytes = 0;
        End(tcp);
    }

    // Resets a connection: an RST at SND.NXT.
    private void Reset(TcpSocket tcp)
    {
        if (tcp.State is TcpState.Closed or TcpState.Listen)
            return;
        TransmitRaw(tcp.LocalAddress, tcp.RemoteAddress, tcp.LocalPort, tcp.RemotePort, tcp.SndNxt, tcp.RcvNxt,
            TcpHeader.RST | TcpHeader.ACK, 0, []);
    }

    // The answer to a segment nobody takes (RFC 9293 section 3.10.7.1).
    private void SendReset(Ipv4Address local, Ipv4Address remote, TcpHeader header, int payloadBytes)
    {
        Statistics.ResetsSent++;
        if (header.Has(TcpHeader.ACK))
        {
            TransmitRaw(local, remote, header.DestinationPort, header.SourcePort, header.Acknowledgment, 0, TcpHeader.RST, 0, []);
            return;
        }
        var length = (uint)payloadBytes + (header.Has(TcpHeader.SYN) ? 1U : 0U) + (header.Has(TcpHeader.FIN) ? 1U : 0U);
        TransmitRaw(local, remote, header.DestinationPort, header.SourcePort, 0, header.Sequence + length,
            TcpHeader.RST | TcpHeader.ACK, 0, []);
    }

    private void SendAck(TcpSocket tcp) => Transmit(tcp, tcp.SndNxt, TcpHeader.ACK, []);

    // After the application read: a window update once the window grew by a segment or half the buffer, whichever is
    // less (the receiver's silly window avoidance, RFC 9293 section 3.8.6.2.2).
    private void UpdateWindow(TcpSocket tcp)
    {
        if (!tcp.IsConnected || tcp.FinReceived)
            return;
        var advertised = SequenceGt(tcp.AdvertisedEdge, tcp.RcvNxt) ? (int)(tcp.AdvertisedEdge - tcp.RcvNxt) : 0;
        if (WindowBytes(tcp) - advertised >= Math.Min(tcp.Receive.Capacity / 2, tcp.Mss))
            SendAck(tcp);
    }

    // The window this side may advertise: the receive buffer's room, within what its shift expresses.
    private static int WindowBytes(TcpSocket tcp) => Math.Min(tcp.Receive.Free, ushort.MaxValue << tcp.RcvScale);

    // A segment of a connection, acknowledging RCV.NXT with the window: a SYN carries the MSS and, when the connection
    // scales its windows, the shift, and its window is never scaled.
    private bool Transmit(TcpSocket tcp, uint sequence, byte flags, ReadOnlySpan<byte> payload)
    {
        var syn = (flags & TcpHeader.SYN) != 0;
        var windowBytes = WindowBytes(tcp);
        var edge = tcp.RcvNxt + (uint)windowBytes;
        if (SequenceLt(edge, tcp.AdvertisedEdge))
        {
            // The right edge never moves back (RFC 9293 section 3.8.6.2.2).
            edge = tcp.AdvertisedEdge;
            windowBytes = (int)(edge - tcp.RcvNxt);
        }
        var window = syn ? (ushort)Math.Min(windowBytes, ushort.MaxValue) : (ushort)Math.Min(windowBytes >> tcp.RcvScale, ushort.MaxValue);
        var ack = (flags & TcpHeader.ACK) != 0;
        if (ack)
        {
            tcp.AdvertisedEdge = tcp.RcvNxt + (syn ? window : (uint)window << tcp.RcvScale);
            tcp.DelayedAckAt = long.MaxValue;
            tcp.UnacknowledgedSegments = 0;
        }
        var offerScale = syn && (tcp.State == TcpState.SynSent || tcp.RcvScale > 0);
        return TransmitRaw(tcp.LocalAddress, tcp.RemoteAddress, tcp.LocalPort, tcp.RemotePort, sequence, ack ? tcp.RcvNxt : 0, flags, window,
            payload, syn ? (ushort)LocalMss() : (ushort)0, offerScale ? tcp.RcvScale : -1);
    }

    private bool TransmitRaw(Ipv4Address local, Ipv4Address remote, ushort localPort, ushort remotePort, uint sequence, uint acknowledgment,
        byte flags, ushort window, ReadOnlySpan<byte> payload, ushort mss = 0, int scale = -1)
    {
        var header = TcpHeader.SIZE + TcpHeader.OptionBytes(mss, scale);
        var segment = new byte[header + payload.Length];
        payload.CopyTo(segment.AsSpan(header));
        TcpHeader.Write(segment, local, remote, localPort, remotePort, sequence, acknowledgment, flags, window, mss, scale);
        Statistics.SegmentsSent++;
        return SendIpv4(local, remote, Ipv4Header.TCP, segment);
    }

    // The largest segment over the link: its MTU less the IPv4 and TCP headers.
    private int LocalMss() => m_link.Mtu - Ipv4Header.SIZE - TcpHeader.SIZE;

    private ushort EphemeralTcpPort()
    {
        for (var tried = 0; tried <= ushort.MaxValue - EPHEMERAL_FIRST; tried++)
        {
            var port = m_nextEphemeral;
            m_nextEphemeral = m_nextEphemeral == ushort.MaxValue ? EPHEMERAL_FIRST : (ushort)(m_nextEphemeral + 1);
            if (!TcpPortTaken(port, Ipv4Address.Any, reuse: false, reusePort: false))
                return port;
        }
        return 0;
    }

    // Whether a TCP port is bound at an address: as Linux's bind decides, sockets that all set SO_REUSEADDR share a port
    // unless one of them listens, and sockets that all set SO_REUSEPORT share it, listening or not.
    private bool TcpPortTaken(ushort port, Ipv4Address address, bool reuse, bool reusePort)
    {
        foreach (var socket in m_tcp.Values)
        {
            if (socket.LocalPort == port && !(reuse && socket.ReuseAddress && socket.State != TcpState.Listen) &&
                !(reusePort && socket.ReusePort) &&
                (socket.LocalAddress == Ipv4Address.Any || address == Ipv4Address.Any || socket.LocalAddress == address))
                return true;
        }
        return false;
    }

    private static int TakeError(TcpSocket tcp)
    {
        var error = tcp.PendingError;
        tcp.PendingError = 0;
        return error;
    }

    // Whether a segment of a length at a sequence number falls in the receive window (RFC 9293 section 3.10.7.4).
    private static bool Acceptable(uint next, uint window, uint sequence, int length)
    {
        if (length == 0)
            return window == 0 ? sequence == next : SequenceGe(sequence, next) && SequenceLt(sequence, next + window);
        if (window == 0)
            return false;
        var last = sequence + (uint)length - 1;
        return (SequenceGe(sequence, next) && SequenceLt(sequence, next + window)) ||
            (SequenceGe(last, next) && SequenceLt(last, next + window));
    }

    // The TCP deadline that comes first.
    private long NextTcpDeadline()
    {
        var deadline = long.MaxValue;
        foreach (var tcp in m_tcp.Values)
            deadline = Math.Min(Math.Min(Math.Min(deadline, tcp.RetransmitAt), Math.Min(tcp.DelayedAckAt, tcp.PersistAt)), tcp.LingerAt);
        return deadline;
    }

    private static bool SequenceLt(uint a, uint b) => (int)(a - b) < 0;
    private static bool SequenceLe(uint a, uint b) => (int)(a - b) <= 0;
    private static bool SequenceGt(uint a, uint b) => (int)(a - b) > 0;
    private static bool SequenceGe(uint a, uint b) => (int)(a - b) >= 0;

    #endregion
}
