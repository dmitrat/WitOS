using System;
using System.Collections.Generic;
using System.Threading;
using OutWit.OS.Terminals;

namespace OutWit.OS.Terminal.Server;

/// <summary>
/// A pseudo-terminal (plan step E4.1, witos/terminal.h, RFC 0020 §27): its slave side is a terminal of its own — a
/// <see cref="TerminalHub"/> whose one link is this object — and its master side is a session's. What the master
/// writes is typed at the slave's discipline; what the slave's programs write, after output processing, and the
/// discipline's echo queue here for the master to read. A program's write waits while <see cref="OUTPUT_LIMIT"/> bytes
/// wait unread, as Linux's buffer of a pty makes a writer wait; the echo, which the master's own write produces, never
/// waits and is dropped beyond <see cref="ECHO_LIMIT"/>. Once a slave session joined and the last one left, the
/// master's read of nothing answers EIO and its poll says HANGUP; once the master went, the slave hangs up. The
/// pseudo-terminal ends once its master and its last slave session went.
/// </summary>
internal sealed class PseudoTerminal : ITerminalLink
{
    #region Constants

    /// <summary>
    /// The bytes that may wait for the master before a program's write waits.
    /// </summary>
    public const int OUTPUT_LIMIT = 64 * 1024;

    /// <summary>
    /// The bytes beyond which the echo is dropped.
    /// </summary>
    public const int ECHO_LIMIT = 256 * 1024;

    #endregion

    #region Fields

    private readonly object m_gate = new();
    private readonly Queue<byte[]> m_output = new();
    private readonly Action<PseudoTerminal> m_ended;
    private int m_head; // the bytes of the first chunk the master read already
    private int m_queued;
    private ITerminalSession? m_master;
    private bool m_masterGone;
    private bool m_slaveJoined;
    private int m_slaves;
    private bool m_stopped;
    private bool m_endReported;

    #endregion

    #region Constructors

    /// <summary>
    /// A pseudo-terminal whose master is still to connect.
    /// </summary>
    /// <param name="number">Its number, the N of /dev/pts/N.</param>
    /// <param name="ended">Told once, when the master and the last slave session went.</param>
    public PseudoTerminal(int number, Action<PseudoTerminal> ended)
    {
        Number = number;
        m_ended = ended;
        Slave = new TerminalHub();
        Slave.Attach(this);
        Slave.SessionsChanged += SlavesChanged;
    }

    #endregion

    #region Functions

    /// <summary>
    /// The master's session, which the pseudo-terminal tells of output and of the slave side's end.
    /// </summary>
    /// <param name="master">The session.</param>
    public void Open(ITerminalSession master)
    {
        lock (m_gate)
            m_master = master;
    }

    /// <inheritdoc/>
    public void Write(ReadOnlySpan<byte> bytes) => Append(bytes, true);

    /// <inheritdoc/>
    public void Echo(ReadOnlySpan<byte> bytes) => Append(bytes, false);

    /// <summary>
    /// The master's read: the slave's output, waiting for it unless asked not to.
    /// </summary>
    /// <param name="limit">The bytes at most.</param>
    /// <param name="nonblocking">Answer at once.</param>
    /// <param name="bytes">The bytes read.</param>
    /// <returns>Zero, or the errno value: EAGAIN, EIO once the slave side went, ESHUTDOWN.</returns>
    public int Read(int limit, bool nonblocking, out byte[] bytes)
    {
        lock (m_gate)
        {
            for (; ; )
            {
                if (m_stopped)
                {
                    bytes = [];
                    return TerminalMessages.SHUTDOWN;
                }
                if (m_queued > 0)
                {
                    bytes = Take(limit);
                    Monitor.PulseAll(m_gate); // a writer may have room now
                    return 0;
                }
                if (SlaveGone)
                {
                    bytes = [];
                    return TerminalMessages.IO_ERROR;
                }
                if (nonblocking)
                {
                    bytes = [];
                    return TerminalMessages.AGAIN;
                }
                Monitor.Wait(m_gate);
            }
        }
    }

    /// <summary>
    /// The master's write: the bytes typed at the slave's discipline.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    public void Type(ReadOnlySpan<byte> bytes) => Slave.Input(bytes.ToArray());

    /// <summary>
    /// What the master's poll reports: INPUT_READY when output waits, OUTPUT_READY always, HANGUP with INPUT_READY once
    /// the slave side went, since a read then answers at once.
    /// </summary>
    public uint Poll()
    {
        lock (m_gate)
        {
            var flags = TerminalMessages.OUTPUT_READY | (m_queued > 0 ? TerminalMessages.INPUT_READY : 0);
            return SlaveGone ? flags | TerminalMessages.INPUT_READY | TerminalMessages.HANGUP : flags;
        }
    }

    /// <summary>
    /// Drops the output the master did not read.
    /// </summary>
    public void FlushOutput()
    {
        lock (m_gate)
        {
            m_output.Clear();
            (m_head, m_queued) = (0, 0);
            Monitor.PulseAll(m_gate);
        }
    }

    /// <summary>
    /// The master went: the output is dropped, the writers that wait return, and the slave hangs up.
    /// </summary>
    public void Close()
    {
        lock (m_gate)
        {
            m_masterGone = true;
            m_master = null;
            m_output.Clear();
            (m_head, m_queued) = (0, 0);
            Monitor.PulseAll(m_gate);
        }
        Slave.Hangup();
        ReportEnd();
    }

    /// <summary>
    /// Stops: the master's reads and the writers that wait return.
    /// </summary>
    public void Stop()
    {
        lock (m_gate)
        {
            m_stopped = true;
            Monitor.PulseAll(m_gate);
        }
    }

    // The slave's output for the master: a program's write waits for room, the echo does not.
    private void Append(ReadOnlySpan<byte> bytes, bool wait)
    {
        if (bytes.Length == 0)
            return;
        ITerminalSession? master;
        lock (m_gate)
        {
            while (wait && m_queued >= OUTPUT_LIMIT && !m_masterGone && !m_stopped)
                Monitor.Wait(m_gate);
            if (m_masterGone || m_stopped || (!wait && m_queued + bytes.Length > ECHO_LIMIT))
                return;
            m_output.Enqueue(bytes.ToArray());
            m_queued += bytes.Length;
            Monitor.PulseAll(m_gate);
            master = m_master;
        }
        master?.Notify(TerminalMessages.EVENT_INPUT);
    }

    // Up to limit bytes off the queue's head.
    private byte[] Take(int limit)
    {
        var bytes = new byte[Math.Min(limit, m_queued)];
        var done = 0;
        while (done < bytes.Length)
        {
            var chunk = m_output.Peek();
            var piece = Math.Min(chunk.Length - m_head, bytes.Length - done);
            chunk.AsSpan(m_head, piece).CopyTo(bytes.AsSpan(done));
            done += piece;
            m_head += piece;
            if (m_head == chunk.Length)
            {
                m_output.Dequeue();
                m_head = 0;
            }
        }
        m_queued -= bytes.Length;
        return bytes;
    }

    private void SlavesChanged(int count)
    {
        ITerminalSession? master;
        bool gone;
        lock (m_gate)
        {
            m_slaves = count;
            if (count > 0)
                m_slaveJoined = true;
            gone = SlaveGone;
            if (gone)
                Monitor.PulseAll(m_gate);
            master = m_master;
        }
        if (gone)
            master?.Notify(TerminalMessages.EVENT_HANGUP);
        ReportEnd();
    }

    // Once the master and the last slave session went, the registry hears it once.
    private void ReportEnd()
    {
        lock (m_gate)
        {
            if (!m_masterGone || m_slaves > 0 || m_endReported)
                return;
            m_endReported = true;
        }
        m_ended(this);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The number, the N of /dev/pts/N.
    /// </summary>
    public int Number { get; }

    /// <summary>
    /// The slave side: the terminal its programs connect to.
    /// </summary>
    public TerminalHub Slave { get; }

    /// <summary>
    /// The master's session, until it went.
    /// </summary>
    public ITerminalSession? Master
    {
        get
        {
            lock (m_gate)
                return m_master;
        }
    }

    // A slave session joined and the last one left (under the gate).
    private bool SlaveGone => m_slaveJoined && m_slaves == 0;

    #endregion
}
