using System;
using System.Collections.Generic;
using System.Threading;
using OutWit.OS.Terminals;

namespace OutWit.OS.Terminal.Server;

/// <summary>
/// The one terminal (plan steps N4.3 and C2.2): the discipline, the links to the consoles, the size and the sessions,
/// under one lock. Each link's pump brings the bytes its device received; the sessions read, write and set the
/// attributes; a signal character reaches every session as an event and ends the reads that wait. The consoles mirror
/// each other: every write and every echo goes to each link in turn, the main one first, and the size is the main
/// console's (RFC 0020 §31: a session may have mirrored endpoints).
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
    private readonly List<TerminalConnection> m_sessions = [];
    private readonly List<ConsoleLink> m_links = [];
    private int m_interrupts;
    private bool m_stopped;

    #endregion

    #region Functions

    /// <summary>
    /// Takes a link to a console; the first link that reports a size gives the terminal its size.
    /// </summary>
    /// <param name="link">The link.</param>
    public void Attach(ConsoleLink link)
    {
        lock (m_gate)
        {
            m_links.Add(link);
            if (!Sized && link.Size.Columns > 0 && link.Size.Rows > 0)
            {
                (Columns, Rows) = ((ushort)Math.Min(link.Size.Columns, ushort.MaxValue), (ushort)Math.Min(link.Size.Rows, ushort.MaxValue));
                Sized = true;
            }
        }
    }

    /// <summary>
    /// Takes the bytes a device received (a link's pump): the discipline's translations, the echo to the consoles, the
    /// waiting readers woken, and the sessions told of input and signals.
    /// </summary>
    /// <param name="raw">The bytes.</param>
    public void Input(byte[] raw)
    {
        byte[] echo;
        uint[] signals;
        bool ready;
        TerminalConnection[] sessions;
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
            WriteLinks(ProcessOutput(echo));
        foreach (var signal in signals)
            foreach (var session in sessions)
                session.Notify(signal);
        if (ready)
            foreach (var session in sessions)
                session.Notify(TerminalMessages.EVENT_INPUT);
    }

    /// <summary>
    /// A session's read: what the discipline gives, waiting for it unless asked not to.
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
                if (nonblocking)
                    return TerminalMessages.AGAIN;
                Monitor.Wait(m_gate);
                if (m_interrupts != interrupts)
                    return TerminalMessages.INTERRUPTED;
            }
        }
    }

    /// <summary>
    /// A session's write: the bytes through output processing to every console, the sessions' writes one at a time.
    /// </summary>
    /// <param name="bytes">The program's bytes.</param>
    /// <returns>Zero, or ESHUTDOWN.</returns>
    public int Write(ReadOnlySpan<byte> bytes)
    {
        byte[] output;
        lock (m_gate)
        {
            if (m_stopped)
                return TerminalMessages.SHUTDOWN;
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
        TerminalConnection[] sessions;
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
    /// What poll reports: INPUT_READY when a read would return now, OUTPUT_READY always.
    /// </summary>
    public uint Poll()
    {
        lock (m_gate)
            return TerminalMessages.OUTPUT_READY | (m_discipline.InputReady ? TerminalMessages.INPUT_READY : 0);
    }

    /// <summary>
    /// A session joined.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>Whether the terminal still serves; false once stopped.</returns>
    public bool Add(TerminalConnection session)
    {
        lock (m_gate)
        {
            if (m_stopped)
                return false;
            m_sessions.Add(session);
            return true;
        }
    }

    /// <summary>
    /// A session ended.
    /// </summary>
    /// <param name="session">The session.</param>
    public void Remove(TerminalConnection session)
    {
        lock (m_gate)
            m_sessions.Remove(session);
    }

    /// <summary>
    /// Stops: every read that waits ends with ESHUTDOWN, no session joins, and the sessions are told.
    /// </summary>
    /// <returns>The sessions that were live.</returns>
    public TerminalConnection[] Stop()
    {
        lock (m_gate)
        {
            m_stopped = true;
            Monitor.PulseAll(m_gate);
            return [.. m_sessions];
        }
    }

    // The bytes to every console in turn, the main one first; a link that went takes nothing.
    private void WriteLinks(byte[] output)
    {
        ConsoleLink[] links;
        lock (m_gate)
            links = [.. m_links];
        foreach (var link in links)
            link.Write(output);
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
    /// Whether a console gave the terminal its size.
    /// </summary>
    public bool Sized { get; private set; }

    /// <summary>
    /// The consoles attached.
    /// </summary>
    public int Links
    {
        get
        {
            lock (m_gate)
                return m_links.Count;
        }
    }

    #endregion
}
