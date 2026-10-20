using System;
using OutWit.OS.Communication;
using OutWit.OS.Resources;

namespace OutWit.OS.Networking;

/// <summary>
/// A client of the network service (witos/socket.h, plan step W1.2a): one session with its window and its events
/// channel, each request answered in turn. The device manager's probe uses it; a process's libc speaks the same protocol
/// in C (W2.1).
/// </summary>
public sealed class SocketClient : IDisposable
{
    #region Constants

    /// <summary>
    /// The client's window.
    /// </summary>
    public const int WINDOW_BYTES = 4 * MemoryObject.PAGE;

    #endregion

    #region Fields

    private readonly Channel m_session;
    private readonly Channel m_events;
    private readonly KernelHandle m_windowObject;
    private readonly MemoryMapping m_window;

    #endregion

    #region Constructors

    private SocketClient(Channel session, Channel events, KernelHandle windowObject, MemoryMapping window)
    {
        m_session = session;
        m_events = events;
        m_windowObject = windowObject;
        m_window = window;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Connects to the service: a session channel, a window of the client's own and an events channel, their ends moved
    /// in a CONNECT on the clients' endpoint.
    /// </summary>
    /// <param name="clients">The service's clients' endpoint, with SEND and DUPLICATE; it stays the caller's.</param>
    /// <returns>The client.</returns>
    /// <exception cref="CapabilityException">A capability could not be had or the message not sent.</exception>
    public static SocketClient Connect(KernelHandle clients)
    {
        var (session, sessionTheirs) = Channel.Create();
        var (events, eventsTheirs) = Channel.Create();
        var windowObject = MemoryObject.Create(WINDOW_BYTES);
        try
        {
            var window = MemoryMapping.Map(windowObject, 0, WINDOW_BYTES, MemoryMapping.READ_WRITE);
            try
            {
                using var sender = new Channel(clients.Duplicate(Rights.Send));
                using var windowTheirs = windowObject.Duplicate(Rights.Map | Rights.Write | Rights.Transfer);
                sender.Send(SocketMessages.Encode(new SocketRequest(SocketMessages.CONNECT, Length: WINDOW_BYTES)),
                    sessionTheirs, windowTheirs, eventsTheirs);
                return new SocketClient(session, events, windowObject, window);
            }
            catch
            {
                window.Dispose();
                throw;
            }
        }
        catch
        {
            sessionTheirs.Dispose();
            eventsTheirs.Dispose();
            windowObject.Dispose();
            events.Dispose();
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The interface and its lease.
    /// </summary>
    /// <param name="configuration">The configuration; null on an error.</param>
    /// <returns>Zero or the service's errno value.</returns>
    public int GetConfiguration(out SocketConfiguration? configuration)
    {
        var reply = Request(new SocketRequest(SocketMessages.CONFIGURATION, Length: SocketMessages.CONFIGURATION_SIZE));
        configuration = reply.Error == 0 ? SocketMessages.ReadConfiguration(m_window.Bytes[..SocketMessages.CONFIGURATION_SIZE]) : null;
        return reply.Error;
    }

    /// <summary>
    /// Opens a socket.
    /// </summary>
    /// <param name="type">STREAM or DATAGRAM.</param>
    /// <param name="socket">The socket.</param>
    /// <returns>Zero or the service's errno value.</returns>
    public int Open(ulong type, out uint socket)
    {
        var reply = Request(new SocketRequest(SocketMessages.OPEN, Argument0: SocketMessages.INET, Argument1: type));
        socket = reply.Socket;
        return reply.Error;
    }

    /// <summary>
    /// Binds a socket.
    /// </summary>
    /// <returns>Zero or the service's errno value.</returns>
    public int Bind(uint socket, SocketAddress address) =>
        Request(new SocketRequest(SocketMessages.BIND, Socket: socket, Address: address)).Error;

    /// <summary>
    /// Connects a socket to a peer.
    /// </summary>
    /// <returns>Zero or the service's errno value.</returns>
    public int ConnectTo(uint socket, SocketAddress address) =>
        Request(new SocketRequest(SocketMessages.CONNECT_TO, Socket: socket, Address: address)).Error;

    /// <summary>
    /// Sends bytes, to an address or to the peer.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="bytes">The bytes, at most the window's.</param>
    /// <param name="address">The destination, or SocketAddress.None for the peer.</param>
    /// <param name="sent">The bytes sent.</param>
    /// <returns>Zero or the service's errno value.</returns>
    public int Send(uint socket, ReadOnlySpan<byte> bytes, SocketAddress address, out int sent)
    {
        if (bytes.Length > WINDOW_BYTES)
            throw new ArgumentOutOfRangeException(nameof(bytes), "A send holds at most the window's bytes.");
        bytes.CopyTo(m_window.Writable);
        var reply = Request(new SocketRequest(SocketMessages.SEND, Socket: socket, Length: bytes.Length, Address: address));
        sent = reply.Error == 0 ? (int)reply.Length : 0;
        return reply.Error;
    }

    /// <summary>
    /// Receives a datagram, or the bytes a stream holds; EAGAIN when none waits.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="buffer">Where the bytes go, at most the window's.</param>
    /// <param name="received">The bytes written.</param>
    /// <param name="source">The datagram's source.</param>
    /// <returns>Zero or the service's errno value.</returns>
    public int Receive(uint socket, Span<byte> buffer, out int received, out SocketAddress source)
    {
        var limit = Math.Min(buffer.Length, WINDOW_BYTES);
        var reply = Request(new SocketRequest(SocketMessages.RECEIVE, Socket: socket, Length: limit));
        received = reply.Error == 0 ? (int)reply.Length : 0;
        source = reply.Address;
        m_window.Bytes[..received].CopyTo(buffer);
        return reply.Error;
    }

    /// <summary>
    /// Closes a socket.
    /// </summary>
    /// <returns>Zero or the service's errno value.</returns>
    public int Close(uint socket) => Request(new SocketRequest(SocketMessages.CLOSE, Socket: socket)).Error;

    /// <summary>
    /// Waits for a readiness event.
    /// </summary>
    /// <param name="deadline">The absolute monotonic deadline; Event.INFINITE for none.</param>
    /// <returns>The socket and its readiness, or null at the deadline.</returns>
    public (uint Socket, uint Readiness)? WaitEvent(ulong deadline)
    {
        try
        {
            Event.Wait(deadline, m_events.Endpoint);
        }
        catch (CapabilityException)
        {
            return null; // the deadline
        }
        using var message = m_events.Receive();
        return SocketMessages.DecodeEvent(message.Bytes);
    }

    private SocketReply Request(SocketRequest request)
    {
        m_session.Send(SocketMessages.Encode(request));
        using var message = m_session.Receive();
        var reply = SocketMessages.DecodeReply(message.Bytes);
        if (reply.Operation != request.Operation)
            throw new FormatException($"The network service answered operation {reply.Operation} to {request.Operation}.");
        return reply;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Ends the session: its channels close, the service closes its sockets, and the window goes.
    /// </summary>
    public void Dispose()
    {
        m_session.Dispose();
        m_events.Dispose();
        m_window.Dispose();
        m_windowObject.Dispose();
    }

    #endregion
}
