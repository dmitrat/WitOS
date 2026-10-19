namespace OutWit.OS.Networking;

/// <summary>
/// The errno values the stack and the socket protocol answer (plan step W1.2a), Linux's numbers, which the libc returns
/// as they are.
/// </summary>
public static class NetworkErrors
{
    #region Constants

    /// <summary>
    /// EBADF: no such socket.
    /// </summary>
    public const int BAD_SOCKET = 9;

    /// <summary>
    /// EAGAIN: the operation would wait; a readiness event follows.
    /// </summary>
    public const int AGAIN = 11;

    /// <summary>
    /// EPIPE: the connection's writing is shut down or over.
    /// </summary>
    public const int BROKEN_PIPE = 32;

    /// <summary>
    /// ECONNRESET: the peer reset the connection.
    /// </summary>
    public const int CONNECTION_RESET = 104;

    /// <summary>
    /// EISCONN: the socket is connected already.
    /// </summary>
    public const int IS_CONNECTED = 106;

    /// <summary>
    /// ETIMEDOUT: the peer stopped answering.
    /// </summary>
    public const int TIMED_OUT = 110;

    /// <summary>
    /// EALREADY: a connection is under way.
    /// </summary>
    public const int ALREADY = 114;

    /// <summary>
    /// EINPROGRESS: the connection started; the socket becomes writable once it is established.
    /// </summary>
    public const int IN_PROGRESS = 115;

    /// <summary>
    /// EINVAL.
    /// </summary>
    public const int INVALID = 22;

    /// <summary>
    /// EDESTADDRREQ: a datagram without a destination on an unconnected socket.
    /// </summary>
    public const int DESTINATION_REQUIRED = 89;

    /// <summary>
    /// EMSGSIZE: a datagram larger than a frame carries.
    /// </summary>
    public const int MESSAGE_SIZE = 90;

    /// <summary>
    /// EPROTONOSUPPORT.
    /// </summary>
    public const int PROTOCOL_NOT_SUPPORTED = 93;

    /// <summary>
    /// EOPNOTSUPP.
    /// </summary>
    public const int NOT_SUPPORTED = 95;

    /// <summary>
    /// EAFNOSUPPORT.
    /// </summary>
    public const int FAMILY_NOT_SUPPORTED = 97;

    /// <summary>
    /// EADDRINUSE.
    /// </summary>
    public const int ADDRESS_IN_USE = 98;

    /// <summary>
    /// EADDRNOTAVAIL: an address that is not the stack's.
    /// </summary>
    public const int ADDRESS_NOT_AVAILABLE = 99;

    /// <summary>
    /// ENETUNREACH: no address yet, or no route.
    /// </summary>
    public const int NETWORK_UNREACHABLE = 101;

    /// <summary>
    /// ENOBUFS: the sockets are exhausted.
    /// </summary>
    public const int NO_BUFFERS = 105;

    /// <summary>
    /// ENOTCONN.
    /// </summary>
    public const int NOT_CONNECTED = 107;

    /// <summary>
    /// ECONNREFUSED: the peer refused a connection, or reported its port unreachable.
    /// </summary>
    public const int CONNECTION_REFUSED = 111;

    #endregion
}
