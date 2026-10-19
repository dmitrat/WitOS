using System.Collections.Generic;

namespace OutWit.OS.Networking;

/// <summary>
/// A TCP connection's states (RFC 9293 section 3.3.2).
/// </summary>
public enum TcpState
{
    /// <summary>No connection: a socket not yet connected or listening, or one whose connection ended.</summary>
    Closed,

    /// <summary>Waiting for a connection request.</summary>
    Listen,

    /// <summary>A connection request sent, waiting for its answer.</summary>
    SynSent,

    /// <summary>A connection request received and answered, waiting for the acknowledgment.</summary>
    SynReceived,

    /// <summary>Open: data flows both ways.</summary>
    Established,

    /// <summary>This side's FIN sent, not yet acknowledged.</summary>
    FinWait1,

    /// <summary>This side's FIN acknowledged, waiting for the peer's.</summary>
    FinWait2,

    /// <summary>The peer's FIN received, waiting for this side to close.</summary>
    CloseWait,

    /// <summary>Both FINs sent, this side's not yet acknowledged.</summary>
    Closing,

    /// <summary>The peer's FIN received and this side's sent, waiting for its acknowledgment.</summary>
    LastAck,

    /// <summary>Both FINs acknowledged; the connection's identity is held for twice the segment lifetime.</summary>
    TimeWait
}

/// <summary>
/// A TCP socket and its connection's control block (RFC 9293 section 3.3.1; plan step W1.2b): its addresses, its state,
/// the send and receive sequence variables, its buffers, its timers and its congestion state (RFC 5681, RFC 6582). A
/// listening socket holds the connections it accepted until the application takes them. The stack alone changes it.
/// </summary>
public sealed class TcpSocket
{
    #region Constructors

    internal TcpSocket(int id, int sendBytes, int receiveBytes)
    {
        Id = id;
        Send = new ByteRing(sendBytes);
        Receive = new ByteRing(receiveBytes);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The socket's identifier.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// The connection's state.
    /// </summary>
    public TcpState State { get; internal set; }

    /// <summary>
    /// The local address: Any until bound to one or connected.
    /// </summary>
    public Ipv4Address LocalAddress { get; internal set; }

    /// <summary>
    /// The local port: zero until bound.
    /// </summary>
    public ushort LocalPort { get; internal set; }

    /// <summary>
    /// The peer's address.
    /// </summary>
    public Ipv4Address RemoteAddress { get; internal set; }

    /// <summary>
    /// The peer's port: zero without a peer.
    /// </summary>
    public ushort RemotePort { get; internal set; }

    /// <summary>
    /// SO_REUSEADDR.
    /// </summary>
    public bool ReuseAddress { get; internal set; }

    /// <summary>
    /// TCP_NODELAY: small segments go at once instead of waiting for the acknowledgment of what is in flight (Nagle,
    /// RFC 9293 section 3.7.4).
    /// </summary>
    public bool NoDelay { get; internal set; }

    /// <summary>
    /// The error a reset, a refusal or a timeout left, returned once by the next call or SO_ERROR.
    /// </summary>
    public int PendingError { get; internal set; }

    /// <summary>
    /// Whether the application closed the socket: the connection finishes on its own and then goes.
    /// </summary>
    public bool Orphan { get; internal set; }

    /// <summary>
    /// Whether the application shut its reading down.
    /// </summary>
    public bool ReadShut { get; internal set; }

    /// <summary>
    /// Whether the application shut its writing down: a FIN follows the data queued.
    /// </summary>
    public bool WriteShut { get; internal set; }

    /// <summary>
    /// Whether the peer's FIN arrived in order: the end of the stream once the bytes before it are read.
    /// </summary>
    public bool FinReceived { get; internal set; }

    /// <summary>
    /// Whether the connection is established or past it in a state that sends or receives data.
    /// </summary>
    public bool IsConnected => State is TcpState.Established or TcpState.CloseWait or TcpState.FinWait1 or TcpState.FinWait2 or
        TcpState.Closing or TcpState.LastAck or TcpState.TimeWait;

    /// <summary>
    /// The bytes the application queued and the peer has not acknowledged, from SND.UNA.
    /// </summary>
    public ByteRing Send { get; }

    /// <summary>
    /// The bytes received in order and not yet read.
    /// </summary>
    public ByteRing Receive { get; }

    // A listening socket's accepted connections, and those still in SYN-RECEIVED.
    internal Queue<TcpSocket> Accepted { get; } = new();
    internal int Backlog { get; set; }
    internal int Embryonic { get; set; }
    internal TcpSocket? Parent { get; set; }

    // The send sequence variables: the initial sequence number, the oldest unacknowledged, the next to send, the highest
    // sent, the peer's window (scaled) and the segment that last updated it; the FIN's sequence number once it is sent.
    internal uint Iss { get; set; }
    internal uint SndUna { get; set; }
    internal uint SndNxt { get; set; }
    internal uint SndMax { get; set; }
    internal uint SndWnd { get; set; }
    internal uint SndWl1 { get; set; }
    internal uint SndWl2 { get; set; }
    internal int SndScale { get; set; }
    internal bool FinSent { get; set; }
    internal uint FinSequence { get; set; }

    // The receive sequence variables and the shift of the windows this side advertises.
    internal uint Irs { get; set; }
    internal uint RcvNxt { get; set; }
    internal int RcvScale { get; set; }
    internal uint AdvertisedEdge { get; set; }
    internal SortedDictionary<uint, byte[]> OutOfOrder { get; } = [];
    internal int OutOfOrderBytes { get; set; }

    // The largest segment this side sends.
    internal int Mss { get; set; }

    // Congestion control (RFC 5681) and NewReno's fast recovery (RFC 6582).
    internal int Cwnd { get; set; }
    internal int Ssthresh { get; set; }
    internal int DuplicateAcks { get; set; }
    internal bool InRecovery { get; set; }
    internal uint Recover { get; set; }

    // The retransmission timer (RFC 6298) with one segment timed at a time (Karn's algorithm).
    internal long Srtt { get; set; } = -1;
    internal long Rttvar { get; set; }
    internal long Rto { get; set; }
    internal bool Timing { get; set; }
    internal uint TimedSequence { get; set; }
    internal long TimedAt { get; set; }
    internal int Retries { get; set; }

    // The timers' deadlines in milliseconds; long.MaxValue when one is off.
    internal long RetransmitAt { get; set; } = long.MaxValue;
    internal long DelayedAckAt { get; set; } = long.MaxValue;
    internal long PersistAt { get; set; } = long.MaxValue;
    internal long LingerAt { get; set; } = long.MaxValue;

    // Delayed acknowledgments: full segments received since the last acknowledgment.
    internal int UnacknowledgedSegments { get; set; }

    // Whether the application met a full send buffer or an empty receive buffer, or polled without room or data: the
    // change it waits for raises the socket's readiness.
    internal bool WaitsToWrite { get; set; }
    internal bool WaitsToRead { get; set; }

    #endregion
}
