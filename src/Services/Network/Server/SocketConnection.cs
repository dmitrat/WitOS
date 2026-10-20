using System;
using System.Collections.Generic;
using System.Threading;
using OutWit.OS.Communication;
using OutWit.OS.Networking;
using OutWit.OS.Resources;

namespace OutWit.OS.Network.Server;

/// <summary>
/// A client connected (plan steps W1.2a and W1.2b, witos/socket.h): its session channel, its window and its events
/// channel, served on a thread of its own, one request at a time, as the namespace service serves its sessions. No
/// request waits: one that would answers EAGAIN, and a readiness event follows on the events channel. A full events queue
/// drops the event, since the events it holds wake the client already, and a client tries its request again after any
/// event. The session serves datagram and stream sockets alike, each kind's operations as Linux's sockets of the kind
/// answer them. The sockets a session opened close when the client closes its end or the service stops, after which
/// every request is refused with ESHUTDOWN.
/// </summary>
internal sealed class SocketConnection
{
    #region Constants

    private const string PREFIX = "[NETWORK] ";

    private const int SHUTDOWN = 108; // ESHUTDOWN

    private const int PROTOCOL_ERROR = 71; // EPROTO

    private const int NO_OPTION = 92; // ENOPROTOOPT

    private const int SOCKET_TYPE_NOT_SUPPORTED = 94; // ESOCKTNOSUPPORT

    private const ulong TCP = 6;

    private const ulong UDP = 17;

    // Linux's default buffer sizes, which SO_SNDBUF and SO_RCVBUF report: the service keeps a datagram queue of its own.
    private const ulong BUFFER_BYTES = 212992;

    #endregion

    #region Fields

    private static readonly List<SocketConnection> m_live = [];

    private static int m_undelivered; // readiness events no session's queue took, which the stop line reports

    private readonly Channel m_channel;
    private readonly Channel m_events;
    private readonly KernelHandle m_object;
    private readonly MemoryMapping m_window;
    private readonly StackHost m_host;
    private readonly Dictionary<int, bool> m_sockets = []; // each socket of the session, and whether it is a stream
    private readonly object m_gate = new();
    private bool m_stopped;

    #endregion

    #region Constructors

    private SocketConnection(Channel channel, Channel events, KernelHandle memoryObject, MemoryMapping window, StackHost host)
    {
        m_channel = channel;
        m_events = events;
        m_object = memoryObject;
        m_window = window;
        m_host = host;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Accepts a CONNECT and serves the session on a thread of its own.
    /// </summary>
    /// <param name="message">The CONNECT: the session's end, the window's object and the events' end in its handle slots.</param>
    /// <param name="host">The stack.</param>
    /// <returns>Whether the client connected.</returns>
    public static bool Accept(ChannelMessage message, StackHost host)
    {
        SocketRequest request;
        try
        {
            request = SocketMessages.DecodeRequest(message.Bytes);
        }
        catch (FormatException)
        {
            message.Dispose();
            return false;
        }
        if (request.Operation != SocketMessages.CONNECT || message.Handles.Count != 3 || request.Length <= 0 ||
            request.Length > SocketMessages.WINDOW_BYTES || request.Length % MemoryObject.PAGE != 0)
        {
            message.Dispose();
            return false;
        }
        var channel = new Channel(message.Handles[0]);
        var events = new Channel(message.Handles[2]);
        try
        {
            var window = MemoryMapping.Map(message.Handles[1], 0, (int)request.Length, MemoryMapping.READ_WRITE);
            var connection = new SocketConnection(channel, events, message.Handles[1], window, host);
            lock (m_live)
                m_live.Add(connection);
            new Thread(connection.Serve) { IsBackground = true, Name = "network session" }.Start();
            return true;
        }
        catch (CapabilityException exception)
        {
            Console.WriteLine($"{PREFIX}a client's window could not be mapped: {exception.Message}");
            message.Handles[1].Dispose();
            events.Dispose();
            channel.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Stops every session: each finishes the request it is serving, closes its sockets and refuses every later request.
    /// </summary>
    /// <returns>The sessions stopped.</returns>
    public static int StopAll()
    {
        SocketConnection[] live;
        lock (m_live)
            live = [.. m_live];
        foreach (var connection in live)
        {
            lock (connection.m_gate)
            {
                connection.m_stopped = true;
                connection.CloseAll();
            }
        }
        return live.Length;
    }

    /// <summary>
    /// Tells the client a socket's readiness; called under the stack's lock.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="readiness">Its readiness bits.</param>
    public void Notify(uint socket, uint readiness)
    {
        try
        {
            m_events.Send(SocketMessages.EncodeEvent(socket, readiness));
        }
        catch (CapabilityException)
        {
            // A full queue wakes the client already; a closed one ends the session on its own thread.
            Interlocked.Increment(ref m_undelivered);
        }
    }

    // The session's requests until the client closes its end.
    private void Serve()
    {
        try
        {
            for (; ; )
            {
                SocketReply reply;
                using (var message = m_channel.Receive())
                {
                    SocketRequest request;
                    try
                    {
                        request = SocketMessages.DecodeRequest(message.Bytes);
                    }
                    catch (FormatException)
                    {
                        m_channel.Send(SocketMessages.Encode(new SocketReply(0, PROTOCOL_ERROR)));
                        continue;
                    }
                    lock (m_gate)
                    {
                        try
                        {
                            reply = m_stopped ? new SocketReply(request.Operation, SHUTDOWN) : Handle(request);
                        }
                        catch (CapabilityException exception)
                        {
                            // The adapter's channel failed under a call: the client gets EIO, the log the failure.
                            Console.WriteLine($"{PREFIX}request {request.Operation} failed: {exception.Message}");
                            reply = new SocketReply(request.Operation, 5);
                        }
                    }
                }
                m_channel.Send(SocketMessages.Encode(reply));
            }
        }
        catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
        {
        }
        finally
        {
            lock (m_live)
                m_live.Remove(this);
            lock (m_gate)
                CloseAll();
            m_window.Dispose();
            m_object.Dispose();
            m_events.Dispose();
            m_channel.Dispose();
        }
    }

    private SocketReply Handle(SocketRequest request)
    {
        var operation = request.Operation;
        if (operation == SocketMessages.OPEN)
            return Open(request);
        if (operation == SocketMessages.CONFIGURATION)
            return Configuration(request);
        if (operation is < SocketMessages.BIND or > SocketMessages.SET_OPTION)
            return new SocketReply(operation, NetworkErrors.NOT_SUPPORTED);
        var socket = (int)request.Socket;
        if (!m_sockets.TryGetValue(socket, out var stream))
            return new SocketReply(operation, NetworkErrors.BAD_SOCKET);
        var address = request.Address;
        switch (operation)
        {
            case SocketMessages.BIND:
                return address.Family != SocketMessages.INET
                    ? new SocketReply(operation, NetworkErrors.FAMILY_NOT_SUPPORTED)
                    : new SocketReply(operation, m_host.Call(stack => stack.Bind(socket, address.Address, address.Port)));
            case SocketMessages.CONNECT_TO:
                // AF_UNSPEC dissolves a datagram socket's association, as Linux's connect does.
                if (address.Family == SocketMessages.UNSPECIFIED && !stream)
                    return new SocketReply(operation, m_host.Call(stack => stack.Connect(socket, Ipv4Address.Any, 0)));
                if (address.Family != SocketMessages.INET)
                    return new SocketReply(operation, stream ? NetworkErrors.INVALID : NetworkErrors.FAMILY_NOT_SUPPORTED);
                return address.Port == 0
                    ? new SocketReply(operation, NetworkErrors.INVALID)
                    : new SocketReply(operation, m_host.Call(stack => stack.Connect(socket, address.Address, address.Port)));
            case SocketMessages.LISTEN:
                return stream
                    ? new SocketReply(operation, m_host.Call(stack => stack.Listen(socket, (int)Math.Min(request.Argument0, int.MaxValue))))
                    : new SocketReply(operation, NetworkErrors.NOT_SUPPORTED);
            case SocketMessages.ACCEPT:
                return Accept(request, socket, stream);
            case SocketMessages.SEND:
                return stream ? SendStream(request, socket) : Send(request, socket);
            case SocketMessages.RECEIVE:
                return stream ? ReceiveStream(request, socket) : Receive(request, socket);
            case SocketMessages.SHUTDOWN:
                return Shutdown(request, socket, stream);
            case SocketMessages.CLOSE:
                m_sockets.Remove(socket);
                return new SocketReply(operation, m_host.Close(socket));
            case SocketMessages.NAME:
                return stream ? StreamName(request, socket) : Name(request, socket);
            case SocketMessages.POLL:
                return m_host.Call(stack => new SocketReply(operation, 0, Length: stack.Available(socket), Value: stack.Poll(socket)));
            case SocketMessages.GET_OPTION:
                return GetOption(request, socket, stream);
            default:
                return SetOption(request, socket, stream);
        }
    }

    // OPEN: an AF_INET stream (TCP) or datagram (UDP) socket.
    private SocketReply Open(SocketRequest request)
    {
        if (request.Argument0 != SocketMessages.INET)
            return new SocketReply(request.Operation, NetworkErrors.FAMILY_NOT_SUPPORTED);
        if (request.Argument1 is not SocketMessages.STREAM and not SocketMessages.DATAGRAM)
            return new SocketReply(request.Operation, SOCKET_TYPE_NOT_SUPPORTED);
        var stream = request.Argument1 == SocketMessages.STREAM;
        if (request.Argument2 != 0 && request.Argument2 != (stream ? TCP : UDP))
            return new SocketReply(request.Operation, NetworkErrors.PROTOCOL_NOT_SUPPORTED);
        var error = m_host.Open(this, stream, out var socket);
        if (error != 0)
            return new SocketReply(request.Operation, error);
        m_sockets[socket] = stream;
        return new SocketReply(request.Operation, 0, (uint)socket);
    }

    // ACCEPT: a connection a listening socket holds, with its peer; the session owns it.
    private SocketReply Accept(SocketRequest request, int socket, bool stream)
    {
        if (!stream)
            return new SocketReply(request.Operation, NetworkErrors.NOT_SUPPORTED);
        var error = m_host.Accept(this, socket, out var accepted, out var peer);
        if (error != 0)
            return new SocketReply(request.Operation, error);
        m_sockets[accepted] = true;
        return new SocketReply(request.Operation, 0, (uint)accepted, Address: peer);
    }

    // SEND on a stream: as many bytes of the window as the send buffer holds; an address is ignored, as Linux's TCP
    // ignores it.
    private SocketReply SendStream(SocketRequest request, int socket)
    {
        if (request.Length < 0 || (request.Length > 0 && !InWindow(request.Window, request.Length)))
            return new SocketReply(request.Operation, NetworkErrors.INVALID);
        var start = (int)request.Window;
        var length = (int)request.Length;
        var sent = 0;
        var error = m_host.Call(stack => stack.SendStream(socket, length == 0 ? [] : m_window.Bytes.Slice(start, length), out sent));
        return error == 0 ? new SocketReply(request.Operation, 0, Length: sent) : new SocketReply(request.Operation, error);
    }

    // RECEIVE on a stream: the bytes received in order, at most the request's; zero at the end of the stream.
    private SocketReply ReceiveStream(SocketRequest request, int socket)
    {
        if (request.Length < 0 || !InWindow(request.Window, Math.Max(request.Length, 1)))
            return new SocketReply(request.Operation, NetworkErrors.INVALID);
        var start = (int)request.Window;
        var length = (int)request.Length;
        var peek = (request.Flags & SocketMessages.PEEK) != 0;
        var received = 0;
        var error = m_host.Call(stack => stack.ReceiveStream(socket, m_window.Writable.Slice(start, length), peek, out received));
        if (error != 0)
            return new SocketReply(request.Operation, error);
        var tcp = m_host.Call(stack => stack.FindTcp(socket));
        return new SocketReply(request.Operation, 0, Length: received, Value: (ulong)received,
            Address: tcp is null ? SocketAddress.None : SocketAddress.Inet(tcp.RemoteAddress, tcp.RemotePort));
    }

    // SHUTDOWN: SHUT_RD, SHUT_WR or SHUT_RDWR of a connection.
    private SocketReply Shutdown(SocketRequest request, int socket, bool stream)
    {
        if (request.Argument0 > SocketMessages.SHUT_BOTH)
            return new SocketReply(request.Operation, NetworkErrors.INVALID);
        if (!stream)
            return new SocketReply(request.Operation, NetworkErrors.NOT_SUPPORTED);
        var read = request.Argument0 != SocketMessages.SHUT_WRITE;
        var write = request.Argument0 != SocketMessages.SHUT_READ;
        return new SocketReply(request.Operation, m_host.Call(stack => stack.Shutdown(socket, read, write)));
    }

    // SEND on a datagram socket: a datagram from the window, to the request's address or the peer.
    private SocketReply Send(SocketRequest request, int socket)
    {
        if (!InWindow(request.Window, request.Length))
            return new SocketReply(request.Operation, NetworkErrors.INVALID);
        var address = request.Address;
        if (address.Family is not SocketMessages.UNSPECIFIED and not SocketMessages.INET)
            return new SocketReply(request.Operation, NetworkErrors.FAMILY_NOT_SUPPORTED);
        var start = (int)request.Window;
        var length = (int)request.Length;
        var to = address.Family == SocketMessages.INET ? address.Address : Ipv4Address.Any;
        var port = address.Family == SocketMessages.INET ? address.Port : (ushort)0;
        if (address.Family == SocketMessages.INET && port == 0)
            return new SocketReply(request.Operation, NetworkErrors.INVALID);
        var error = m_host.Call(stack => stack.SendTo(socket, m_window.Bytes.Slice(start, length), to, port));
        return error == 0 ? new SocketReply(request.Operation, 0, Length: length) : new SocketReply(request.Operation, error);
    }

    // RECEIVE on a datagram socket: the oldest datagram into the window, cut to the request's bytes, with its whole
    // length and its source.
    private SocketReply Receive(SocketRequest request, int socket)
    {
        if (request.Length < 0 || !InWindow(request.Window, Math.Max(request.Length, 1)))
            return new SocketReply(request.Operation, NetworkErrors.INVALID);
        var start = (int)request.Window;
        var length = (int)request.Length;
        var peek = (request.Flags & SocketMessages.PEEK) != 0;
        Datagram? datagram = null;
        var error = m_host.Call(stack => stack.ReceiveFrom(socket, m_window.Writable.Slice(start, length), peek, out datagram));
        if (error != 0 || datagram is null)
            return new SocketReply(request.Operation, error != 0 ? error : NetworkErrors.AGAIN);
        return new SocketReply(request.Operation, 0, Length: Math.Min(length, datagram.Payload.Length), Value: (ulong)datagram.Payload.Length,
            Address: SocketAddress.Inet(datagram.Source, datagram.SourcePort));
    }

    // NAME of a datagram socket: its local address, or its peer's.
    private SocketReply Name(SocketRequest request, int socket)
    {
        var udp = m_host.Call(stack => stack.FindUdp(socket));
        if (udp is null)
            return new SocketReply(request.Operation, NetworkErrors.BAD_SOCKET);
        if ((request.Flags & SocketMessages.PEER) != 0)
            return udp.IsConnected
                ? new SocketReply(request.Operation, 0, Address: SocketAddress.Inet(udp.RemoteAddress, udp.RemotePort))
                : new SocketReply(request.Operation, NetworkErrors.NOT_CONNECTED);
        return new SocketReply(request.Operation, 0, Address: SocketAddress.Inet(udp.LocalAddress, udp.LocalPort));
    }

    // NAME of a stream socket: its local address, or its connected peer's.
    private SocketReply StreamName(SocketRequest request, int socket)
    {
        var tcp = m_host.Call(stack => stack.FindTcp(socket));
        if (tcp is null)
            return new SocketReply(request.Operation, NetworkErrors.BAD_SOCKET);
        if ((request.Flags & SocketMessages.PEER) != 0)
            return tcp.IsConnected || tcp.State == TcpState.SynReceived
                ? new SocketReply(request.Operation, 0, Address: SocketAddress.Inet(tcp.RemoteAddress, tcp.RemotePort))
                : new SocketReply(request.Operation, NetworkErrors.NOT_CONNECTED);
        return new SocketReply(request.Operation, 0, Address: SocketAddress.Inet(tcp.LocalAddress, tcp.LocalPort));
    }

    // GET_OPTION: SOL_SOCKET's options and TCP_NODELAY.
    private SocketReply GetOption(SocketRequest request, int socket, bool stream)
    {
        var (level, name) = (request.Argument0, request.Argument1);
        var udp = stream ? null : m_host.Call(stack => stack.FindUdp(socket));
        var tcp = stream ? m_host.Call(stack => stack.FindTcp(socket)) : null;
        if (udp is null && tcp is null)
            return new SocketReply(request.Operation, NetworkErrors.BAD_SOCKET);
        ulong? value = (level, name) switch
        {
            (SocketMessages.SOCKET_LEVEL, SocketMessages.TYPE) => stream ? SocketMessages.STREAM : SocketMessages.DATAGRAM,
            (SocketMessages.SOCKET_LEVEL, SocketMessages.ERROR) => (ulong)m_host.Call(stack => stack.TakeError(socket)),
            (SocketMessages.SOCKET_LEVEL, SocketMessages.BROADCAST) when udp is not null => udp.Broadcast ? 1UL : 0UL,
            (SocketMessages.SOCKET_LEVEL, SocketMessages.REUSE_ADDRESS) => (udp?.ReuseAddress ?? tcp!.ReuseAddress) ? 1UL : 0UL,
            (SocketMessages.SOCKET_LEVEL, SocketMessages.REUSE_PORT) => (udp?.ReusePort ?? tcp!.ReusePort) ? 1UL : 0UL,
            (SocketMessages.SOCKET_LEVEL, SocketMessages.SEND_BUFFER or SocketMessages.RECEIVE_BUFFER) => stream
                ? NetworkStack.TCP_BUFFER_BYTES
                : BUFFER_BYTES,
            (SocketMessages.SOCKET_LEVEL, SocketMessages.KEEP_ALIVE) when tcp is not null => 0UL,
            (SocketMessages.SOCKET_LEVEL, SocketMessages.ACCEPTS_CONNECTIONS) => tcp?.State == TcpState.Listen ? 1UL : 0UL,
            (SocketMessages.TCP_LEVEL, SocketMessages.NO_DELAY) when tcp is not null => tcp.NoDelay ? 1UL : 0UL,
            _ => null
        };
        return value is { } got ? new SocketReply(request.Operation, 0, Value: got) : new SocketReply(request.Operation, NO_OPTION);
    }

    // SET_OPTION: SO_BROADCAST, SO_REUSEADDR, SO_REUSEPORT and TCP_NODELAY change the socket; the buffer sizes are the service's and
    // stay; keep-alive probes are not served yet.
    private SocketReply SetOption(SocketRequest request, int socket, bool stream)
    {
        var enabled = request.Argument2 != 0;
        return (request.Argument0, request.Argument1) switch
        {
            (SocketMessages.SOCKET_LEVEL, SocketMessages.BROADCAST) when !stream =>
                new SocketReply(request.Operation, m_host.Call(stack => stack.SetBroadcast(socket, enabled))),
            (SocketMessages.SOCKET_LEVEL, SocketMessages.REUSE_ADDRESS) =>
                new SocketReply(request.Operation, m_host.Call(stack => stack.SetReuseAddress(socket, enabled))),
            (SocketMessages.SOCKET_LEVEL, SocketMessages.REUSE_PORT) =>
                new SocketReply(request.Operation, m_host.Call(stack => stack.SetReusePort(socket, enabled))),
            (SocketMessages.SOCKET_LEVEL, SocketMessages.SEND_BUFFER or SocketMessages.RECEIVE_BUFFER) => new SocketReply(request.Operation, 0),
            (SocketMessages.TCP_LEVEL, SocketMessages.NO_DELAY) when stream =>
                new SocketReply(request.Operation, m_host.Call(stack => stack.SetNoDelay(socket, enabled))),
            _ => new SocketReply(request.Operation, NO_OPTION),
        };
    }

    // CONFIGURATION: the interface and its lease into the window.
    private SocketReply Configuration(SocketRequest request)
    {
        if (request.Length < SocketMessages.CONFIGURATION_SIZE || !InWindow(request.Window, SocketMessages.CONFIGURATION_SIZE))
            return new SocketReply(request.Operation, NetworkErrors.INVALID);
        var configuration = m_host.Call(stack => new SocketConfiguration(stack.Address, stack.Mask, stack.Gateway, [.. stack.DnsServers],
            stack.LinkAddress, m_host.Mtu, stack.Dhcp.IsBound));
        SocketMessages.WriteConfiguration(m_window.Writable[(int)request.Window..], configuration);
        return new SocketReply(request.Operation, 0, Length: SocketMessages.CONFIGURATION_SIZE);
    }

    // Whether bytes at an offset lie in the window.
    private bool InWindow(long offset, long length) => offset >= 0 && length > 0 && offset <= m_window.Length - length;

    // Closes the session's sockets.
    private void CloseAll()
    {
        foreach (var socket in m_sockets.Keys)
            m_host.Close(socket);
        m_sockets.Clear();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The readiness events the service could not send, for a full queue or a closed session: a full queue's client asks
    /// again after the events it holds.
    /// </summary>
    public static int UndeliveredEvents => Volatile.Read(ref m_undelivered);

    #endregion
}
