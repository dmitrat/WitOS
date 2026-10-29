using System;
using System.Threading;
using OutWit.OS.Communication;
using OutWit.OS.Resources;
using OutWit.OS.Terminals;

namespace OutWit.OS.Terminal.Server;

/// <summary>
/// One client's session (witos/terminal.h): its session channel, served on a thread of its own, its events channel
/// when it asked for events, which the terminal's notifications go out on, and its window. A session is the console's,
/// a pseudo-terminal's slave side (a CONNECT with PSEUDO) or a pseudo-terminal's master (OPEN_PSEUDO, E4.1). A request
/// is checked against the window before anything is done; a read that waits is the terminal's.
/// </summary>
internal sealed class TerminalConnection : ITerminalSession
{
    #region Constants

    private const string PREFIX = "[TERMINAL] ";

    #endregion

    #region Fields

    private readonly TerminalHub m_hub; // the console's hub, or the slave hub of the pseudo-terminal
    private readonly PseudoTerminal? m_pseudo; // the pseudo-terminal of a slave or master session
    private readonly bool m_master;
    private readonly Channel m_session;
    private readonly Channel? m_events;
    private readonly MemoryMapping m_window;
    private readonly object m_eventsGate = new();
    private volatile bool m_stopped;

    #endregion

    #region Constructors

    private TerminalConnection(TerminalHub hub, PseudoTerminal? pseudo, bool master, Channel session, Channel? events, MemoryMapping window)
    {
        m_hub = hub;
        m_pseudo = pseudo;
        m_master = master;
        m_session = session;
        m_events = events;
        m_window = window;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Accepts a CONNECT or an OPEN_PSEUDO on the clients' endpoint: the session's end, the window and the events' end in
    /// its handles. A refused request closes them, which the client meets as the session's end.
    /// </summary>
    /// <param name="message">The message, whose handles the connection takes.</param>
    /// <param name="console">The console's terminal.</param>
    /// <param name="pseudo">The pseudo-terminals.</param>
    /// <returns>Whether a session started.</returns>
    public static bool Accept(ChannelMessage message, TerminalHub console, PseudoTerminals pseudo)
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
        var master = request.Operation == TerminalMessages.OPEN_PSEUDO;
        var shape = request.Operation switch
        {
            TerminalMessages.CONNECT => (request.Flags & ~TerminalMessages.PSEUDO) == 0 && message.Handles.Count is 2 or 3 &&
                ((request.Flags & TerminalMessages.PSEUDO) != 0 || request.Offset == 0),
            TerminalMessages.OPEN_PSEUDO => request.Flags == 0 && request.Offset == 0 && message.Handles.Count == 3,
            _ => false
        };
        if (!shape || request.Length != TerminalMessages.WINDOW_BYTES)
        {
            message.Dispose();
            return false;
        }
        PseudoTerminal? terminal = null;
        if (master)
            terminal = pseudo.Open();
        else if ((request.Flags & TerminalMessages.PSEUDO) != 0)
            terminal = pseudo.Find(request.Offset);
        if ((master || (request.Flags & TerminalMessages.PSEUDO) != 0) && terminal is null)
        {
            message.Dispose(); // every number taken, or no such pseudo-terminal
            return false;
        }
        var hub = terminal?.Slave ?? console;
        var session = new Channel(message.Handles[0]);
        var events = message.Handles.Count == 3 ? new Channel(message.Handles[2]) : null;
        try
        {
            var window = MemoryMapping.Map(message.Handles[1], 0, TerminalMessages.WINDOW_BYTES, MemoryMapping.READ_WRITE);
            message.Handles[1].Dispose(); // the mapping holds the object
            var connection = new TerminalConnection(hub, terminal, master, session, events, window);
            if (master)
            {
                terminal!.Open(connection);
            }
            else if (!hub.Add(connection))
            {
                window.Dispose();
                session.Dispose();
                events?.Dispose();
                return false;
            }
            new Thread(connection.Serve) { IsBackground = true, Name = master ? "pseudo-terminal master" : "terminal session" }.Start();
            return true;
        }
        catch (CapabilityException exception)
        {
            Console.WriteLine($"{PREFIX}a client's window could not be mapped: {exception.Message}");
            session.Dispose();
            events?.Dispose();
            message.Handles[1].Dispose();
            if (master)
                terminal!.Close();
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
        lock (m_eventsGate)
            m_stopped = true;
        if (m_master)
            m_pseudo!.Close();
        else
            m_hub.Remove(this);
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
                    var nonblocking = (request.Flags & TerminalMessages.NONBLOCK) != 0;
                    var error = m_master
                        ? m_pseudo!.Read((int)request.Length, nonblocking, out var bytes)
                        : m_hub.Read((int)request.Length, nonblocking, out bytes);
                    if (error != 0)
                        return new TerminalReply(request.Operation, error);
                    bytes.CopyTo(m_window.Writable[(int)request.Offset..]);
                    return new TerminalReply(request.Operation, 0, bytes.Length);
                }
            case TerminalMessages.WRITE:
                {
                    if (request.Flags != 0 || request.Length <= 0 || !inWindow)
                        return new TerminalReply(request.Operation, TerminalMessages.INVALID);
                    var bytes = m_window.Bytes.Slice((int)request.Offset, (int)request.Length);
                    if (m_master)
                    {
                        m_pseudo!.Type(bytes);
                        return new TerminalReply(request.Operation, 0, request.Length);
                    }
                    var error = m_hub.Write(bytes);
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
            case TerminalMessages.SET_SIZE:
                {
                    if (request.Length != 0)
                        return new TerminalReply(request.Operation, TerminalMessages.INVALID);
                    if (m_pseudo is null)
                        return new TerminalReply(request.Operation, TerminalMessages.NOT_A_PSEUDO_TERMINAL);
                    var (columns, rows) = ((ushort)(request.Flags & 0xFFFF), (ushort)(request.Flags >> 16));
                    if (columns == 0 || rows == 0)
                        return new TerminalReply(request.Operation, TerminalMessages.INVALID);
                    m_hub.Resize(columns, rows);
                    return new TerminalReply(request.Operation, 0);
                }
            case TerminalMessages.GET_NUMBER:
                if (request.Flags != 0 || request.Length != 0)
                    return new TerminalReply(request.Operation, TerminalMessages.INVALID);
                return m_pseudo is null
                    ? new TerminalReply(request.Operation, TerminalMessages.NOT_A_PSEUDO_TERMINAL)
                    : new TerminalReply(request.Operation, 0, m_pseudo.Number);
            case TerminalMessages.POLL:
                return request.Flags != 0 || request.Length != 0
                    ? new TerminalReply(request.Operation, TerminalMessages.INVALID)
                    : new TerminalReply(request.Operation, 0, 0, m_master ? m_pseudo!.Poll() : m_hub.Poll());
            case TerminalMessages.FLUSH:
                if ((request.Flags & ~(TerminalMessages.INPUT | TerminalMessages.OUTPUT)) != 0 || request.Length != 0)
                    return new TerminalReply(request.Operation, TerminalMessages.INVALID);
                if ((request.Flags & TerminalMessages.INPUT) != 0)
                    m_hub.FlushInput();
                if (m_master && (request.Flags & TerminalMessages.OUTPUT) != 0)
                    m_pseudo!.FlushOutput();
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
