using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace WitOS.Dev.Host;

/// <summary>
/// The runner's time server (plan step H7.4): an SNTP server on a free UDP port of the host's loopback, which the guest
/// reaches at <see cref="GUEST_ADDRESS"/> and the same port, since QEMU's user networking sends a datagram to its host
/// alias to the host's loopback. It answers each request as a server of stratum 1 does (RFC 5905's layout, written here
/// apart from the guest's client) with the host's time plus an offset, so that the guest's clock shows whether the
/// answer set it.
/// </summary>
internal sealed class TimePeer : IDisposable
{
    #region Constants

    /// <summary>
    /// QEMU's user networking's host alias, whose datagrams reach the host's loopback.
    /// </summary>
    public const string GUEST_ADDRESS = "10.0.2.2";

    private const int PACKET_BYTES = 48;
    private const ulong NTP_UNIX_SECONDS = 2_208_988_800;
    private const ulong TICKS_PER_SECOND = (ulong)TimeSpan.TicksPerSecond;

    #endregion

    #region Fields

    private readonly UdpClient m_socket;
    private readonly CancellationTokenSource m_stop = new();
    private readonly Task m_serving;
    private int m_answered;

    #endregion

    #region Constructors

    /// <summary>
    /// Starts the server.
    /// </summary>
    /// <param name="offset">What the server adds to the host's time in every answer.</param>
    public TimePeer(TimeSpan offset)
    {
        Offset = offset;
        m_socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)m_socket.Client.LocalEndPoint!).Port;
        m_serving = ServeAsync();
    }

    #endregion

    #region Functions

    /// <summary>
    /// The time the server gives at a moment of the host's.
    /// </summary>
    /// <param name="host">The host's time.</param>
    /// <returns>The server's.</returns>
    public DateTime ServerTime(DateTime host) => host + Offset;

    private async Task ServeAsync()
    {
        while (!m_stop.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try
            {
                request = await m_socket.ReceiveAsync(m_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (m_stop.IsCancellationRequested)
                    return;
                continue; // an ICMP error of an earlier answer's peer, which Windows reports on the next receive
            }
            var received = ServerTime(DateTime.UtcNow);
            // A client's request (mode 3) of at least the header; anything else gets no answer.
            if (request.Buffer.Length < PACKET_BYTES || (request.Buffer[0] & 7) != 3)
                continue;
            var reply = new byte[PACKET_BYTES];
            var version = (request.Buffer[0] >> 3) & 7;
            reply[0] = (byte)((version is 3 or 4 ? version : 4) << 3 | 4); // no leap indicator, the client's version, mode 4
            reply[1] = 1; // stratum 1
            reply[2] = request.Buffer[2]; // the client's poll
            reply[3] = unchecked((byte)-20); // a precision of about a microsecond
            "WITR"u8.CopyTo(reply.AsSpan(12)); // a reference clock's identifier of four characters
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(16), Timestamp(received)); // the reference time
            request.Buffer.AsSpan(40, 8).CopyTo(reply.AsSpan(24)); // the origin: the request's transmit timestamp
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(32), Timestamp(received));
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(40), Timestamp(ServerTime(DateTime.UtcNow)));
            try
            {
                await m_socket.SendAsync(reply, request.RemoteEndPoint, m_stop.Token);
                Interlocked.Increment(ref m_answered);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
    }

    // A time as NTP's 64-bit timestamp: seconds since 1900 in era 0 or 1, and their fraction.
    private static ulong Timestamp(DateTime time)
    {
        var ticks = (ulong)(time - DateTime.UnixEpoch).Ticks;
        var seconds = (ticks / TICKS_PER_SECOND + NTP_UNIX_SECONDS) & 0xFFFF_FFFF;
        var fraction = (ticks % TICKS_PER_SECOND << 32) / TICKS_PER_SECOND;
        return seconds << 32 | fraction;
    }

    /// <summary>
    /// Stops the server.
    /// </summary>
    public void Dispose()
    {
        m_stop.Cancel();
        m_socket.Dispose();
        try
        {
            m_serving.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
        m_stop.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The server's port on the host's loopback and at <see cref="GUEST_ADDRESS"/>.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// What the server adds to the host's time.
    /// </summary>
    public TimeSpan Offset { get; }

    /// <summary>
    /// The requests answered.
    /// </summary>
    public int Answered => Volatile.Read(ref m_answered);

    #endregion
}
