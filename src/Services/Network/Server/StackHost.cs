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
/// stack, runs what is due and sends what the stack queued. Without an adapter (W2.1a) the stack serves the loopback
/// alone, and the thread waits on the event and the deadline. Sessions call the stack under the same lock and set the event
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
    private readonly AdapterLink? m_adapter;
    private readonly ILink m_link;
    private readonly NetworkStack m_stack;
    private readonly KernelHandle m_wake;
    private readonly Dictionary<int, SocketConnection> m_owners = [];
    private readonly ulong m_frequency;
    private Thread? m_thread;
    private long m_waitingUntil = long.MaxValue;
    private bool m_stopping;
    private bool m_leased;
    private (Ipv6Address LinkLocal, Ipv6Address Global, Ipv6Address Router) m_reported6;
    private Exception? m_failure;

    #endregion

    #region Constructors

    /// <summary>
    /// The stack on an adapter, or on the loopback alone, its thread not started.
    /// </summary>
    /// <param name="adapter">The adapter, which the host takes; null for the loopback alone.</param>
    /// <exception cref="CapabilityException">The wake event could not be made.</exception>
    public StackHost(AdapterLink? adapter)
    {
        m_adapter = adapter;
        m_link = adapter ?? (ILink)new NoLink();
        m_frequency = Clock.Frequency();
        m_stack = new NetworkStack(m_link, Now);
        m_stack.SocketReady += Ready;
        m_stack.ConfigurationChanged += () => SocketConnection.NotifyAll(SocketMessages.CONFIGURATION_EVENT, 0);
        m_wake = Event.Create();
    }

    #endregion

    #region Functions

    /// <summary>
    /// Starts the stack's thread, the DHCP client and the IPv6 host (W4.3).
    /// </summary>
    public void Start()
    {
        if (m_adapter is not null)
        {
            lock (m_gate)
            {
                m_stack.StartDhcp();
                m_stack.StartIpv6();
            }
        }
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
            m_adapter?.Flush();
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
    /// <param name="ipv6">An AF_INET6 socket, otherwise an AF_INET one (W4.3).</param>
    /// <param name="socket">The socket's identifier.</param>
    /// <returns>Zero or an errno value.</returns>
    public int Open(SocketConnection owner, bool stream, bool ipv6, out int socket)
    {
        lock (m_gate)
        {
            var error = stream ? m_stack.OpenTcp(out socket, ipv6) : m_stack.OpenUdp(out socket, ipv6);
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
            peer = SocketAddress.Of(tcp.Family, tcp.RemoteAddress, tcp.RemotePort);
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
                    m_adapter?.Flush();
                    Report();
                    until = m_stack.NextDeadline;
                    m_waitingUntil = until;
                }
                int index;
                try
                {
                    var deadline = until == long.MaxValue ? Event.INFINITE : Ticks(until);
                    index = m_adapter is null ? Event.Wait(deadline, m_wake) + 1 : Event.Wait(deadline, m_adapter.Endpoint, m_wake);
                }
                catch (CapabilityException exception) when (exception.Error == TIMED_OUT)
                {
                    continue;
                }
                if (index != 0)
                    continue;
                using var message = m_adapter!.Channel.Receive();
                lock (m_gate)
                    m_adapter.Handle(message, frame => m_stack.Receive(frame));
            }
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            Console.WriteLine($"{PREFIX}{Device}: the stack's thread stopped: {exception.Message}");
            lock (m_gate)
            {
                m_failure = exception;
                Monitor.PulseAll(m_gate);
            }
        }
    }

    // The lease's line when DHCP took or lost one, and a wakeup for WaitForLease; the IPv6 addresses' line when the host
    // assigned one or learned its router (W4.3).
    private void Report()
    {
        var addresses = (m_stack.Ipv6LinkLocal, m_stack.Ipv6Global, m_stack.Ipv6Gateway);
        if (addresses != m_reported6)
        {
            m_reported6 = addresses;
            Console.WriteLine($"{PREFIX}{Device}: IPv6 link-local {Text(addresses.Item1)}, global {Text(addresses.Item2)}" +
                $"{(m_stack.Ipv6PrefixLength > 0 ? $" of {m_stack.Ipv6Prefix}/{m_stack.Ipv6PrefixLength}" : "")}, router {Text(addresses.Item3)}" +
                $"{(m_stack.Ipv6DnsServers.Count > 0 ? $", DNS {string.Join(", ", m_stack.Ipv6DnsServers)}" : "")}");
        }
        var leased = m_stack.Dhcp.IsBound;
        if (leased == m_leased)
            return;
        m_leased = leased;
        if (leased && m_stack.Dhcp.Lease is { } lease)
            Console.WriteLine($"{PREFIX}{Device}: leased {m_stack.Address}/{Prefix(m_stack.Mask)} from {lease.ServerId} for {lease.LeaseSeconds} s, " +
                $"router {m_stack.Gateway}, DNS {string.Join(", ", m_stack.DnsServers)}");
        else
            Console.WriteLine($"{PREFIX}{Device}: the lease ended");
        Monitor.PulseAll(m_gate);
    }

    private static string Text(Ipv6Address address) => address.IsAny ? "none" : address.ToString();

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
        m_adapter?.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The adapter; null for the loopback alone.
    /// </summary>
    public AdapterLink? Adapter => m_adapter;

    /// <summary>
    /// The adapter's device identity, or "the loopback" without one.
    /// </summary>
    public string Device => m_adapter?.Device ?? "the loopback";

    /// <summary>
    /// The interface's MTU.
    /// </summary>
    public int Mtu => m_link.Mtu;

    /// <summary>
    /// The stack's counters; read under no lock, for a report.
    /// </summary>
    public NetworkStatistics Statistics => m_stack.Statistics;

    #endregion

    #region Nested Types

    // The stack's link without an adapter (W2.1a): nothing leaves, since every destination but the loopback is
    // unreachable while the interface has no address.
    private sealed class NoLink : ILink
    {
        public void Transmit(ReadOnlySpan<byte> frame)
        {
        }

        public MacAddress Address => MacAddress.None;

        public int Mtu => 1500;
    }

    #endregion
}
