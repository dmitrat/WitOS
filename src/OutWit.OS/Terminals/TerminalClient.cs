using System;
using OutWit.OS.Communication;
using OutWit.OS.Resources;

namespace OutWit.OS.Terminals;

/// <summary>
/// A client of the terminal service (witos/terminal.h, plan step N4.3a): one session with its window and its events
/// channel, each request answered in turn. The device manager's probe uses it; a process's libc speaks the same
/// protocol in C.
/// </summary>
public sealed class TerminalClient : IDisposable
{
    #region Fields

    private readonly Channel m_session;
    private readonly Channel m_events;
    private readonly KernelHandle m_windowObject;
    private readonly MemoryMapping m_window;

    #endregion

    #region Constructors

    private TerminalClient(Channel session, Channel events, KernelHandle windowObject, MemoryMapping window)
    {
        m_session = session;
        m_events = events;
        m_windowObject = windowObject;
        m_window = window;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Connects to the service: a session channel, a window of the client's own and an events channel, their ends
    /// moved in a CONNECT on the clients' endpoint.
    /// </summary>
    /// <param name="clients">The service's clients' endpoint, with SEND and DUPLICATE; it stays the caller's.</param>
    /// <returns>The client.</returns>
    /// <exception cref="CapabilityException">A capability could not be had or the message not sent.</exception>
    public static TerminalClient Connect(KernelHandle clients)
    {
        var (session, sessionTheirs) = Channel.Create();
        var (events, eventsTheirs) = Channel.Create();
        var windowObject = MemoryObject.Create(TerminalMessages.WINDOW_BYTES);
        try
        {
            var window = MemoryMapping.Map(windowObject, 0, TerminalMessages.WINDOW_BYTES, MemoryMapping.READ_WRITE);
            try
            {
                using var sender = new Channel(clients.Duplicate(Rights.Send));
                using var windowTheirs = windowObject.Duplicate(Rights.Map | Rights.Write | Rights.Transfer);
                sender.Send(TerminalMessages.Encode(new TerminalRequest(TerminalMessages.CONNECT, 0, TerminalMessages.WINDOW_BYTES)),
                    sessionTheirs, windowTheirs, eventsTheirs);
                return new TerminalClient(session, events, windowObject, window);
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
    /// Reads what the discipline gives, waiting for it unless asked not to.
    /// </summary>
    /// <param name="buffer">Where the bytes go, at most the window's bytes.</param>
    /// <param name="nonblocking">Answer at once: EAGAIN when nothing is there.</param>
    /// <param name="count">The bytes read; zero at the end of the file.</param>
    /// <returns>Zero or the service's errno value.</returns>
    public int Read(Span<byte> buffer, bool nonblocking, out int count)
    {
        var limit = Math.Min(buffer.Length, TerminalMessages.WINDOW_BYTES);
        var reply = Request(new TerminalRequest(TerminalMessages.READ, nonblocking ? TerminalMessages.NONBLOCK : 0, limit));
        count = reply.Error == 0 ? (int)reply.Length : 0;
        if (count > 0)
            m_window.Bytes[..count].CopyTo(buffer);
        return reply.Error;
    }

    /// <summary>
    /// Writes bytes through the discipline to the device, in pieces of the window.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>Zero or the service's errno value.</returns>
    public int Write(ReadOnlySpan<byte> bytes)
    {
        for (var done = 0; done < bytes.Length;)
        {
            var piece = Math.Min(bytes.Length - done, TerminalMessages.WINDOW_BYTES);
            bytes.Slice(done, piece).CopyTo(m_window.Writable);
            var reply = Request(new TerminalRequest(TerminalMessages.WRITE, 0, piece));
            if (reply.Error != 0)
                return reply.Error;
            done += piece;
        }
        return 0;
    }

    /// <summary>
    /// The attributes.
    /// </summary>
    /// <param name="attributes">The attributes; null on an error.</param>
    /// <returns>Zero or the service's errno value.</returns>
    public int GetAttributes(out TerminalAttributes? attributes)
    {
        var reply = Request(new TerminalRequest(TerminalMessages.GET_ATTRIBUTES, 0, TerminalMessages.ATTRIBUTES_SIZE));
        attributes = reply.Error == 0 ? TerminalMessages.DecodeAttributes(m_window.Bytes) : null;
        return reply.Error;
    }

    /// <summary>
    /// Sets the attributes.
    /// </summary>
    /// <param name="attributes">The attributes.</param>
    /// <param name="when">NOW, AFTER_OUTPUT or AFTER_FLUSH.</param>
    /// <returns>Zero or the service's errno value.</returns>
    public int SetAttributes(TerminalAttributes attributes, uint when)
    {
        TerminalMessages.Encode(attributes, m_window.Writable);
        return Request(new TerminalRequest(TerminalMessages.SET_ATTRIBUTES, when, TerminalMessages.ATTRIBUTES_SIZE)).Error;
    }

    /// <summary>
    /// The size.
    /// </summary>
    /// <param name="columns">The columns.</param>
    /// <param name="rows">The rows.</param>
    /// <returns>Zero or the service's errno value.</returns>
    public int GetSize(out int columns, out int rows)
    {
        var reply = Request(new TerminalRequest(TerminalMessages.GET_SIZE, 0, 0));
        (columns, rows) = (reply.Columns, reply.Rows);
        return reply.Error;
    }

    /// <summary>
    /// What poll reports.
    /// </summary>
    /// <returns>INPUT_READY and OUTPUT_READY as they apply; zero on an error.</returns>
    public uint Poll()
    {
        var reply = Request(new TerminalRequest(TerminalMessages.POLL, 0, 0));
        return reply.Error == 0 ? reply.Flags : 0;
    }

    /// <summary>
    /// Waits for an event of the service: input, a signal, a size change, a hang-up.
    /// </summary>
    /// <param name="deadline">The absolute monotonic deadline; Event.INFINITE for none.</param>
    /// <returns>The event's kind, or zero at the deadline.</returns>
    public uint WaitEvent(ulong deadline)
    {
        try
        {
            Event.Wait(deadline, m_events.Endpoint);
        }
        catch (CapabilityException)
        {
            return 0; // the deadline
        }
        using var message = m_events.Receive();
        return TerminalMessages.DecodeEvent(message.Bytes);
    }

    private TerminalReply Request(TerminalRequest request)
    {
        m_session.Send(TerminalMessages.Encode(request));
        using var message = m_session.Receive();
        var reply = TerminalMessages.DecodeReply(message.Bytes);
        if (reply.Operation != request.Operation)
            throw new FormatException($"The terminal service answered operation {reply.Operation} to {request.Operation}.");
        return reply;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Ends the session: its channels close, and the window goes.
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
