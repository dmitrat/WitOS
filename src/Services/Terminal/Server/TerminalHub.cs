using System;
using System.Collections.Generic;
using System.Threading;
using OutWit.OS.Terminals;

namespace OutWit.OS.Terminal.Server;

/// <summary>
/// A terminal (plan steps N4.3, C2.2 and E4.1): the discipline, the links to its devices, the size and the sessions,
/// under one lock. The console's hub has the consoles' links, each pump bringing the bytes its device received; a
/// pseudo-terminal's slave hub has the master as its one link. The sessions read, write and set the attributes; a
/// signal character reaches every session as an event and ends the reads that wait. The consoles mirror each other:
/// every write and every echo goes to each link in turn, the main one first, and the size is the main console's (RFC
/// 0020 §31: a session may have mirrored endpoints); a pseudo-terminal's size is the master's to set.
/// </summary>
internal sealed class TerminalHub
{
    #region Constants

    private const ushort DEFAULT_COLUMNS = 80;
    private const ushort DEFAULT_ROWS = 24;

    #endregion

    #region Fields

    private readonly object m_gate = new();
    private readonly LineDiscipline m_discipline = new();
    private readonly List<ITerminalSession> m_sessions = [];
    private readonly List<ITerminalLink> m_links = [];
    private int m_interrupts;
    private bool m_stopped;
    private bool m_hungUp;

    #endregion

    #region Functions

    /// <summary>
    /// Takes a link to a device; the first link that reports a size gives the terminal its size.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <param name="columns">The columns the device reports, zero for none.</param>
    /// <param name="rows">The rows the device reports, zero for none.</param>
    public void Attach(ITerminalLink link, int columns = 0, int rows = 0)
    {
        lock (m_gate)
        {
            m_links.Add(link);
            if (!Sized && columns > 0 && rows > 0)
            {
                (Columns, Rows) = ((ushort)Math.Min(columns, ushort.MaxValue), (ushort)Math.Min(rows, ushort.MaxValue));
                Sized = true;
            }
        }
    }

    /// <summary>
    /// Takes the bytes a device received (a link's pump, a master's write): the discipline's translations, the echo to
    /// the devices, the waiting readers woken, and the sessions told of input and signals.
    /// </summary>
    /// <param name="raw">The bytes.</param>
    public void Input(byte[] raw)
    {
        byte[] echo;
        uint[] signals;
        bool ready;
        ITerminalSession[] sessions;
        lock (m_gate)
        {
            (echo, signals) = m_discipline.Receive(raw);
            if (signals.Length > 0)
                m_interrupts++;
            ready = m_discipline.InputReady;
            Monitor.PulseAll(m_gate);
            sessions = [.. m_sessions];
        }
        if (echo.Length > 0)
            EchoLinks(ProcessOutput(echo));
        foreach (var signal in signals)
            foreach (var session in sessions)
                session.Notify(signal);
        if (ready)
            foreach (var session in sessions)
                session.Notify(TerminalMessages.EVENT_INPUT);
    }

    /// <summary>
    /// A session's read: what the discipline gives, waiting for it unless asked not to; once the terminal hung up, the
    /// end of the file when nothing is left.
    /// </summary>
    /// <param name="limit">The bytes at most.</param>
    /// <param name="nonblocking">Answer at once.</param>
    /// <param name="bytes">The bytes read.</param>
    /// <returns>Zero, or the errno value: EAGAIN, EINTR when a signal character came while waiting, ESHUTDOWN.</returns>
    public int Read(int limit, bool nonblocking, out byte[] bytes)
    {
        lock (m_gate)
        {
            var interrupts = m_interrupts;
            for (; ; )
            {
                if (m_stopped)
                {
                    bytes = [];
                    return TerminalMessages.SHUTDOWN;
                }
                if (m_discipline.TryRead(limit, out bytes))
                    return 0;
                if (m_hungUp)
                {
                    bytes = [];
                    return 0;
                }
                if (nonblocking)
                    return TerminalMessages.AGAIN;
                Monitor.Wait(m_gate);
                if (m_interrupts != interrupts)
                    return TerminalMessages.INTERRUPTED;
            }
        }
    }

    /// <summary>
    /// A session's write: the bytes through output processing to every device, the sessions' writes one at a time.
    /// </summary>
    /// <param name="bytes">The program's bytes.</param>
    /// <returns>Zero, ESHUTDOWN, or EIO once the terminal hung up.</returns>
    public int Write(ReadOnlySpan<byte> bytes)
    {
        byte[] output;
        lock (m_gate)
        {
            if (m_stopped)
                return TerminalMessages.SHUTDOWN;
            if (m_hungUp)
                return TerminalMessages.IO_ERROR;
            output = m_discipline.ProcessOutput(bytes);
        }
        WriteLinks(output);
        return 0;
    }

    /// <summary>
    /// The attributes.
    /// </summary>
    public TerminalAttributes GetAttributes()
    {
        lock (m_gate)
            return m_discipline.Attributes;
    }

    /// <summary>
    /// Sets the attributes, as tcsetattr does for every process at once.
    /// </summary>
    /// <param name="attributes">The attributes.</param>
    /// <param name="flushInput">TCSAFLUSH.</param>
    public void SetAttributes(TerminalAttributes attributes, bool flushInput)
    {
        bool ready;
        ITerminalSession[] sessions;
        lock (m_gate)
        {
            m_discipline.SetAttributes(attributes, flushInput);
            ready = m_discipline.InputReady;
            Monitor.PulseAll(m_gate); // a reader may return under the new mode
            sessions = [.. m_sessions];
        }
        if (ready)
            foreach (var session in sessions)
                session.Notify(TerminalMessages.EVENT_INPUT);
    }

    /// <summary>
    /// Drops what was typed and not read.
    /// </summary>
    public void FlushInput()
    {
        lock (m_gate)
            m_discipline.FlushInput();
    }

    /// <summary>
    /// What poll reports: INPUT_READY when a read would return now, OUTPUT_READY always, and HANGUP with INPUT_READY
    /// once the terminal hung up, since a read then answers the end of the file at once.
    /// </summary>
    public uint Poll()
    {
        lock (m_gate)
        {
            if (m_hungUp)
                return TerminalMessages.OUTPUT_READY | TerminalMessages.INPUT_READY | TerminalMessages.HANGUP;
            return TerminalMessages.OUTPUT_READY | (m_discipline.InputReady ? TerminalMessages.INPUT_READY : 0);
        }
    }

    /// <summary>
    /// A new size (E4.1: a pseudo-terminal's master sets it); the sessions get EVENT_SIZE when it changed.
    /// </summary>
    /// <param name="columns">The columns, at least one.</param>
    /// <param name="rows">The rows, at least one.</param>
    public void Resize(ushort columns, ushort rows)
    {
        ITerminalSession[] sessions;
        lock (m_gate)
        {
            var changed = columns != Columns || rows != Rows;
            (Columns, Rows, Sized) = (columns, rows, true);
            if (!changed)
                return;
            sessions = [.. m_sessions];
        }
        foreach (var session in sessions)
            session.Notify(TerminalMessages.EVENT_SIZE);
    }

    /// <summary>
    /// The device went (E4.1: a pseudo-terminal's master): every read that waits ends with the end of the file, writes
    /// answer EIO, and the sessions get EVENT_HANGUP.
    /// </summary>
    public void Hangup()
    {
        ITerminalSession[] sessions;
        lock (m_gate)
        {
            if (m_hungUp)
                return;
            m_hungUp = true;
            Monitor.PulseAll(m_gate);
            sessions = [.. m_sessions];
        }
        foreach (var session in sessions)
            session.Notify(TerminalMessages.EVENT_HANGUP);
    }

    /// <summary>
    /// A session joined.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>Whether the terminal still serves; false once stopped or hung up.</returns>
    public bool Add(ITerminalSession session)
    {
        int count;
        lock (m_gate)
        {
            if (m_stopped || m_hungUp)
                return false;
            m_sessions.Add(session);
            count = m_sessions.Count;
        }
        SessionsChanged?.Invoke(count);
        return true;
    }

    /// <summary>
    /// A session ended.
    /// </summary>
    /// <param name="session">The session.</param>
    public void Remove(ITerminalSession session)
    {
        int count;
        lock (m_gate)
        {
            if (!m_sessions.Remove(session))
                return;
            count = m_sessions.Count;
        }
        SessionsChanged?.Invoke(count);
    }

    /// <summary>
    /// Stops: every read that waits ends with ESHUTDOWN, no session joins, and the sessions are told.
    /// </summary>
    /// <returns>The sessions that were live.</returns>
    public ITerminalSession[] Stop()
    {
        lock (m_gate)
        {
            m_stopped = true;
            Monitor.PulseAll(m_gate);
            return [.. m_sessions];
        }
    }

    // The bytes to every device in turn, the main one first; a link that went takes nothing.
    private void WriteLinks(byte[] output)
    {
        ITerminalLink[] links;
        lock (m_gate)
            links = [.. m_links];
        foreach (var link in links)
            link.Write(output);
    }

    private void EchoLinks(byte[] output)
    {
        ITerminalLink[] links;
        lock (m_gate)
            links = [.. m_links];
        foreach (var link in links)
            link.Echo(output);
    }

    private byte[] ProcessOutput(byte[] echo)
    {
        lock (m_gate)
            return m_discipline.ProcessOutput(echo);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The columns.
    /// </summary>
    public ushort Columns { get; private set; } = DEFAULT_COLUMNS;

    /// <summary>
    /// The rows.
    /// </summary>
    public ushort Rows { get; private set; } = DEFAULT_ROWS;

    /// <summary>
    /// Whether a device or a master gave the terminal its size.
    /// </summary>
    public bool Sized { get; private set; }

    /// <summary>
    /// The devices attached.
    /// </summary>
    public int Links
    {
        get
        {
            lock (m_gate)
                return m_links.Count;
        }
    }

    /// <summary>
    /// The sessions, after each join and each end (E4.1: a pseudo-terminal counts its slave's).
    /// </summary>
    public event Action<int>? SessionsChanged;

    #endregion
}
