using System;
using System.Threading;
using OutWit.OS.Communication;
using OutWit.OS.Resources;
using OutWit.OS.Terminals;

namespace OutWit.OS.Terminal.Server;

/// <summary>
/// One client's session (witos/terminal.h): its session channel, served on a thread of its own, its events channel
/// when it asked for events, which the hub's notifications go out on, and its window. A request is checked against the window before anything
/// is done; a read that waits is the hub's.
/// </summary>
internal sealed class TerminalConnection
{
    #region Constants

    private const string PREFIX = "[TERMINAL] ";

    #endregion

    #region Fields

    private readonly TerminalHub m_hub;
    private readonly Channel m_session;
    private readonly Channel? m_events;
    private readonly MemoryMapping m_window;
    private readonly object m_eventsGate = new();
    private volatile bool m_stopped;

    #endregion

    #region Constructors

    private TerminalConnection(TerminalHub hub, Channel session, Channel? events, MemoryMapping window)
    {
        m_hub = hub;
        m_session = session;
        m_events = events;
        m_window = window;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Accepts a CONNECT on the clients' endpoint: the session's end, the events' end and the window in its handles.
    /// </summary>
    /// <param name="message">The message, whose handles the connection takes.</param>
    /// <param name="hub">The terminal.</param>
    /// <returns>Whether a session started.</returns>
    public static bool Accept(ChannelMessage message, TerminalHub hub)
    {
        TerminalRequest request;
        try
        {
            request = TerminalMessages.DecodeRequest(message.Bytes);
        }
        catch (FormatException)
        {
            message.Dispose();
            return false;
        }
        if (request.Operation != TerminalMessages.CONNECT || message.Handles.Count is not (2 or 3) || request.Length != TerminalMessages.WINDOW_BYTES)
        {
            message.Dispose();
            return false;
        }
        var session = new Channel(message.Handles[0]);
        var events = message.Handles.Count == 3 ? new Channel(message.Handles[2]) : null;
        try
        {
            var window = MemoryMapping.Map(message.Handles[1], 0, TerminalMessages.WINDOW_BYTES, MemoryMapping.READ_WRITE);
            message.Handles[1].Dispose(); // the mapping holds the object
            var connection = new TerminalConnection(hub, session, events, window);
            if (!hub.Add(connection))
            {
                window.Dispose();
                session.Dispose();
                events?.Dispose();
                return false;
            }
            new Thread(connection.Serve) { IsBackground = true, Name = "terminal session" }.Start();
            return true;
        }
        catch (CapabilityException exception)
        {
            Console.WriteLine($"{PREFIX}a client's window could not be mapped: {exception.Message}");
            session.Dispose();
            events?.Dispose();
            message.Handles[1].Dispose();
            return false;
        }
    }

    /// <summary>
    /// Tells the client something on its events channel; a full queue or a client that went loses the event.
    /// </summary>
    /// <param name="kind">EVENT_INPUT, EVENT_INTERRUPT, EVENT_QUIT, EVENT_SIZE or EVENT_HANGUP.</param>
    public void Notify(uint kind)
    {
        lock (m_eventsGate)
        {
            if (m_stopped || m_events is null)
                return;
            try
            {
                m_events.Send(TerminalMessages.EncodeEvent(kind));
            }
            catch (CapabilityException)
            {
                // BUSY: four events wait already, and the client looks at the terminal when it takes them; PEER_CLOSED: gone.
            }
        }
    }

    /// <summary>
    /// Ends the session: the serving thread answers ESHUTDOWN from now on and the events stop.
    /// </summary>
    public void Stop()
    {
        lock (m_eventsGate)
            m_stopped = true;
    }

    // The session's requests until the client closes its end or disconnects.
    private void Serve()
    {
        try
        {
            for (; ; )
            {
                TerminalReply reply;
                using (var message = m_session.Receive())
                {
                    TerminalRequest request;
                    try
                    {
                        request = TerminalMessages.DecodeRequest(message.Bytes);
                    }
                    catch (FormatException)
                    {
                        m_session.Send(TerminalMessages.Encode(new TerminalReply(0, TerminalMessages.PROTOCOL_ERROR)));
                        continue;
                    }
                    reply = m_stopped ? new TerminalReply(request.Operation, TerminalMessages.SHUTDOWN) : Handle(request);
                    if (request.Operation == TerminalMessages.DISCONNECT)
                    {
                        m_session.Send(TerminalMessages.Encode(reply));
                        break;
                    }
                }
                m_session.Send(TerminalMessages.Encode(reply));
            }
        }
        catch (CapabilityException exception) when (exception.Error == Channel.PEER_CLOSED)
        {
            // The client closed its end.
        }
        catch (Exception exception)
        {
            Console.WriteLine($"{PREFIX}a session failed: {exception.GetType().Name}: {exception.Message}");
        }
        m_hub.Remove(this);
        lock (m_eventsGate)
            m_stopped = true;
        m_window.Dispose();
        m_events?.Dispose();
        m_session.Dispose();
    }

    private TerminalReply Handle(TerminalRequest request)
    {
        var inWindow = request.Offset >= 0 && request.Length >= 0 && request.Offset <= TerminalMessages.WINDOW_BYTES - request.Length;
        switch (request.Operation)
        {
            case TerminalMessages.READ:
                {
                    if ((request.Flags & ~TerminalMessages.NONBLOCK) != 0 || request.Length <= 0 || !inWindow)
                        return new TerminalReply(request.Operation, TerminalMessages.INVALID);
                    var error = m_hub.Read((int)request.Length, (request.Flags & TerminalMessages.NONBLOCK) != 0, out var bytes);
                    if (error != 0)
                        return new TerminalReply(request.Operation, error);
                    bytes.CopyTo(m_window.Writable[(int)request.Offset..]);
                    return new TerminalReply(request.Operation, 0, bytes.Length);
                }
            case TerminalMessages.WRITE:
                {
                    if (request.Flags != 0 || request.Length <= 0 || !inWindow)
                        return new TerminalReply(request.Operation, TerminalMessages.INVALID);
                    var error = m_hub.Write(m_window.Bytes.Slice((int)request.Offset, (int)request.Length));
                    return error != 0 ? new TerminalReply(request.Operation, error) : new TerminalReply(request.Operation, 0, request.Length);
                }
            case TerminalMessages.GET_ATTRIBUTES:
                if (request.Flags != 0 || request.Length != TerminalMessages.ATTRIBUTES_SIZE || !inWindow)
                    return new TerminalReply(request.Operation, TerminalMessages.INVALID);
                TerminalMessages.Encode(m_hub.GetAttributes(), m_window.Writable[(int)request.Offset..]);
                return new TerminalReply(request.Operation, 0, TerminalMessages.ATTRIBUTES_SIZE);
            case TerminalMessages.SET_ATTRIBUTES:
                if (request.Flags > TerminalMessages.AFTER_FLUSH || request.Length != TerminalMessages.ATTRIBUTES_SIZE || !inWindow)
                    return new TerminalReply(request.Operation, TerminalMessages.INVALID);
                m_hub.SetAttributes(TerminalMessages.DecodeAttributes(m_window.Bytes[(int)request.Offset..]),
                    request.Flags == TerminalMessages.AFTER_FLUSH);
                return new TerminalReply(request.Operation, 0);
            case TerminalMessages.GET_SIZE:
                return request.Flags != 0 || request.Length != 0
                    ? new TerminalReply(request.Operation, TerminalMessages.INVALID)
                    : new TerminalReply(request.Operation, 0, 0, 0, m_hub.Columns, m_hub.Rows);
            case TerminalMessages.POLL:
                return request.Flags != 0 || request.Length != 0
                    ? new TerminalReply(request.Operation, TerminalMessages.INVALID)
                    : new TerminalReply(request.Operation, 0, 0, m_hub.Poll());
            case TerminalMessages.FLUSH:
                if ((request.Flags & ~(TerminalMessages.INPUT | TerminalMessages.OUTPUT)) != 0 || request.Length != 0)
                    return new TerminalReply(request.Operation, TerminalMessages.INVALID);
                if ((request.Flags & TerminalMessages.INPUT) != 0)
                    m_hub.FlushInput();
                return new TerminalReply(request.Operation, 0);
            case TerminalMessages.DRAIN:
            case TerminalMessages.DISCONNECT:
                return request.Flags != 0 || request.Length != 0
                    ? new TerminalReply(request.Operation, TerminalMessages.INVALID)
                    : new TerminalReply(request.Operation, 0);
            default:
                return new TerminalReply(request.Operation, TerminalMessages.UNKNOWN_OPERATION);
        }
    }

    #endregion
}
