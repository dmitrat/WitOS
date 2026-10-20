using System;
using System.Collections.Generic;
using System.Threading;
using OutWit.OS.Communication;
using OutWit.OS.Networking;
using OutWit.OS.Resources;

namespace OutWit.OS.Network.Server;

/// <summary>
/// The stack and its thread (plan step W1.2a): one thread owns the adapter's replies and the stack's timers. It waits on
/// the packet channel, on an event the sessions set and until the stack's next deadline; it hands received frames to the
/// stack, runs what is due and sends what the stack queued. Sessions call the stack under the same lock and set the event
/// when a call moved the stack's next deadline before the one the thread waits for. A socket's readiness goes to the
/// session that owns it.
/// </summary>
internal sealed class StackHost : IDisposable
{
    #region Constants

    private const string PREFIX = "[NETWORK] ";

    // ETIMEDOUT: a wait's deadline passed.
    private const int TIMED_OUT = 110;

    #endregion

    #region Fields

    private readonly object m_gate = new();
    private readonly AdapterLink m_link;
    private readonly NetworkStack m_stack;
    private readonly KernelHandle m_wake;
    private readonly Dictionary<int, SocketConnection> m_owners = [];
    private readonly ulong m_frequency;
    private Thread? m_thread;
    private long m_waitingUntil = long.MaxValue;
    private bool m_stopping;
    private bool m_leased;
    private Exception? m_failure;

    #endregion

    #region Constructors

    /// <summary>
    /// The stack on an adapter, its thread not started.
    /// </summary>
    /// <param name="link">The adapter, which the host takes.</param>
    /// <exception cref="CapabilityException">The wake event could not be made.</exception>
    public StackHost(AdapterLink link)
    {
        m_link = link;
        m_frequency = Clock.Frequency();
        m_stack = new NetworkStack(link, Now);
        m_stack.SocketReady += Ready;
        m_wake = Event.Create();
    }

    #endregion

    #region Functions

    /// <summary>
    /// Starts the stack's thread and the DHCP client.
    /// </summary>
    public void Start()
    {
        lock (m_gate)
            m_stack.StartDhcp();
        m_thread = new Thread(Run) { IsBackground = true, Name = "network stack" };
        m_thread.Start();
    }

    /// <summary>
    /// Waits until DHCP configured the interface or the deadline passes.
    /// </summary>
    /// <param name="milliseconds">How long to wait.</param>
    /// <returns>Whether the interface has a lease.</returns>
    public bool WaitForLease(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        lock (m_gate)
        {
            for (; ; )
            {
                if (m_stack.Dhcp.IsBound || m_failure is not null)
                    return m_stack.Dhcp.IsBound;
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                    return false;
                Monitor.Wait(m_gate, (int)Math.Min(left, int.MaxValue));
            }
        }
    }

    /// <summary>
    /// Calls the stack under its lock for a session, then sends what the call queued and wakes the stack's thread when
    /// the call moved the next deadline before the one it waits for.
    /// </summary>
    /// <typeparam name="T">The call's result.</typeparam>
    /// <param name="call">The call.</param>
    /// <returns>Its result.</returns>
    public T Call<T>(Func<NetworkStack, T> call)
    {
        lock (m_gate)
        {
            var result = call(m_stack);
            m_link.Flush();
            if (m_stack.NextDeadline < m_waitingUntil)
                Event.Set(m_wake);
            return result;
        }
    }

    /// <summary>
    /// Opens a socket for a session, which owns it.
    /// </summary>
    /// <param name="owner">The session.</param>
    /// <param name="stream">A TCP socket, otherwise a UDP one.</param>
    /// <param name="socket">The socket's identifier.</param>
    /// <returns>Zero or an errno value.</returns>
    public int Open(SocketConnection owner, bool stream, out int socket)
    {
        lock (m_gate)
        {
            var error = stream ? m_stack.OpenTcp(out socket) : m_stack.OpenUdp(out socket);
            if (error == 0)
                m_owners[socket] = owner;
            return error;
        }
    }

    /// <summary>
    /// Takes a connection a session's listening socket accepted; the session owns it.
    /// </summary>
    /// <param name="owner">The session.</param>
    /// <param name="listener">The listening socket.</param>
    /// <param name="socket">The connection's socket.</param>
    /// <param name="peer">The connection's peer.</param>
    /// <returns>Zero, AGAIN or another errno value.</returns>
    public int Accept(SocketConnection owner, int listener, out int socket, out SocketAddress peer)
    {
        lock (m_gate)
        {
            peer = SocketAddress.None;
            var error = m_stack.Accept(listener, out socket);
            if (error != 0)
                return error;
            m_owners[socket] = owner;
            var tcp = m_stack.FindTcp(socket)!;
            peer = SocketAddress.Inet(tcp.RemoteAddress, tcp.RemotePort);
            return 0;
        }
    }

    /// <summary>
    /// Closes a session's socket.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <returns>Zero or an errno value.</returns>
    public int Close(int socket)
    {
        lock (m_gate)
        {
            m_owners.Remove(socket);
            return m_stack.Close(socket);
        }
    }

    /// <summary>
    /// Stops the stack's thread; the adapter's channel closes with the host.
    /// </summary>
    public void Stop()
    {
        lock (m_gate)
            m_stopping = true;
        Event.Set(m_wake);
        m_thread?.Join();
    }

    // The stack's thread: replies, timers and the frames the stack queued, until the host stops or the adapter fails.
    private void Run()
    {
        try
        {
            for (; ; )
            {
                long until;
                lock (m_gate)
                {
                    if (m_stopping)
                        return;
                    m_stack.Advance();
                    m_link.Flush();
                    Report();
                    until = m_stack.NextDeadline;
                    m_waitingUntil = until;
                }
                int index;
                try
                {
                    index = Event.Wait(until == long.MaxValue ? Event.INFINITE : Ticks(until), m_link.Endpoint, m_wake);
                }
                catch (CapabilityException exception) when (exception.Error == TIMED_OUT)
                {
                    continue;
                }
                if (index != 0)
                    continue;
                using var message = m_link.Channel.Receive();
                lock (m_gate)
                    m_link.Handle(message, frame => m_stack.Receive(frame));
            }
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            Console.WriteLine($"{PREFIX}{m_link.Device}: the stack's thread stopped: {exception.Message}");
            lock (m_gate)
            {
                m_failure = exception;
                Monitor.PulseAll(m_gate);
            }
        }
    }

    // The lease's line when DHCP took or lost one, and a wakeup for WaitForLease.
    private void Report()
    {
        var leased = m_stack.Dhcp.IsBound;
        if (leased == m_leased)
            return;
        m_leased = leased;
        if (leased && m_stack.Dhcp.Lease is { } lease)
            Console.WriteLine($"{PREFIX}{m_link.Device}: leased {m_stack.Address}/{Prefix(m_stack.Mask)} from {lease.ServerId} for {lease.LeaseSeconds} s, " +
                $"router {m_stack.Gateway}, DNS {string.Join(", ", m_stack.DnsServers)}");
        else
            Console.WriteLine($"{PREFIX}{m_link.Device}: the lease ended");
        Monitor.PulseAll(m_gate);
    }

    // A socket's readiness may have changed: its owner hears of it.
    private void Ready(int socket)
    {
        if (m_owners.TryGetValue(socket, out var owner))
            owner.Notify((uint)socket, m_stack.Poll(socket));
    }

    // The monotonic clock in milliseconds.
    private long Now()
    {
        var ticks = Clock.Monotonic();
        return (long)(ticks / m_frequency * 1000 + ticks % m_frequency * 1000 / m_frequency);
    }

    // A deadline in milliseconds as the clock's ticks.
    private ulong Ticks(long milliseconds)
    {
        var value = (ulong)Math.Max(milliseconds, 0);
        return value / 1000 * m_frequency + value % 1000 * m_frequency / 1000;
    }

    private static int Prefix(Ipv4Address mask) => System.Numerics.BitOperations.PopCount(mask.Value);

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        m_wake.Dispose();
        m_link.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The adapter.
    /// </summary>
    public AdapterLink Link => m_link;

    /// <summary>
    /// The stack's counters; read under no lock, for a report.
    /// </summary>
    public NetworkStatistics Statistics => m_stack.Statistics;

    #endregion
}
