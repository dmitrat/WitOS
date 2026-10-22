using System;
using System.Collections.Generic;
using System.Linq;

namespace OutWit.OS.Networking;

/// <summary>
/// The managed TCP/IP stack of the network service (plan steps W1.2a and W1.2b, W0's decision 2): one Ethernet interface
/// over an <see cref="ILink"/> and the loopback interface, IPv4 with ARP, ICMP's echo and port unreachable, UDP and TCP
/// sockets and the DHCP client; since W4.3 IPv6 beside it (<c>NetworkStack.Ipv6.cs</c>): neighbour discovery, a
/// link-local address from the hardware address and a global one from the router's advertisement, ICMPv6, and sockets
/// of both families, every address an <see cref="Ipv6Address"/> with IPv4 in its mapped form, so that an AF_INET6 socket
/// serves both as Linux's dual-stack sockets do. It knows no channel and no device: frames come in through
/// <see cref="Receive"/>, go out through the link, time comes from the clock it is given, and <see cref="Advance"/> runs
/// its timers once <see cref="NextDeadline"/> has passed. It is not thread-safe: the service calls it under one lock.
/// </summary>
public sealed partial class NetworkStack
{
    #region Constants

    /// <summary>
    /// The first ephemeral port.
    /// </summary>
    public const ushort EPHEMERAL_FIRST = 49152;

    /// <summary>
    /// The sockets the stack holds at most.
    /// </summary>
    public const int SOCKETS = 1024;

    /// <summary>
    /// Readiness: data or a connection to take (POLLIN).
    /// </summary>
    public const uint READABLE = 0x1;

    /// <summary>
    /// Readiness: room to send (POLLOUT).
    /// </summary>
    public const uint WRITABLE = 0x4;

    /// <summary>
    /// Readiness: an error to report (POLLERR).
    /// </summary>
    public const uint ERROR = 0x8;

    /// <summary>
    /// Readiness: the connection ended both ways, or failed (POLLHUP).
    /// </summary>
    public const uint HANGUP = 0x10;

    private const long ARP_RETRY_MILLISECONDS = 1000;
    private const int ARP_ATTEMPTS = 3;
    private const long ARP_LIFETIME_MILLISECONDS = 300_000;
    private const int ARP_PENDING_PACKETS = 4;
    private const byte ECHO_REPLY = 0;
    private const byte UNREACHABLE = 3;
    private const byte PORT_UNREACHABLE = 3;
    private const byte ECHO_REQUEST = 8;

    #endregion

    #region Fields

    private readonly ILink m_link;
    private readonly Func<long> m_clock;
    private readonly Random m_random;
    private readonly Dictionary<Ipv4Address, (MacAddress Mac, long Expires)> m_neighbours = [];
    private readonly Dictionary<Ipv4Address, PendingResolution> m_pending = [];
    private readonly Dictionary<int, UdpSocket> m_udp = [];
    private readonly Queue<byte[]> m_loopback = new();
    private readonly List<Ipv4Address> m_dns = [];
    private ushort m_identification;
    private ushort m_nextEphemeral = EPHEMERAL_FIRST;
    private int m_nextSocket = 1;

    #endregion

    #region Constructors

    /// <summary>
    /// A stack on a link, unconfigured until <see cref="Configure"/> or a DHCP lease.
    /// </summary>
    /// <param name="link">The link it transmits on.</param>
    /// <param name="clock">The time in milliseconds, monotonic.</param>
    /// <param name="random">The randomness of DHCP's transaction identifiers and TCP's initial sequence numbers; a seeded one
    /// in tests.</param>
    public NetworkStack(ILink link, Func<long> clock, Random? random = null)
    {
        m_link = link;
        m_clock = clock;
        m_random = random ?? Random.Shared;
        Dhcp = new DhcpClient(this, m_random);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Configures the interface.
    /// </summary>
    /// <param name="address">The interface's address.</param>
    /// <param name="mask">The subnet's mask.</param>
    /// <param name="gateway">The router for destinations off the subnet; Any for none.</param>
    /// <param name="dns">The DNS servers.</param>
    public void Configure(Ipv4Address address, Ipv4Address mask, Ipv4Address gateway, IReadOnlyList<Ipv4Address> dns)
    {
        var changed = address != Address || mask != Mask || gateway != Gateway || !Enumerable.SequenceEqual(m_dns, dns);
        Address = address;
        Mask = mask;
        Gateway = gateway;
        m_dns.Clear();
        m_dns.AddRange(dns);
        if (changed)
            ConfigurationChanged?.Invoke();
    }

    /// <summary>
    /// Forgets the interface's configuration (a lease that ended).
    /// </summary>
    public void Unconfigure() => Configure(Ipv4Address.Any, Ipv4Address.Any, Ipv4Address.Any, []);

    /// <summary>
    /// Starts the DHCP client.
    /// </summary>
    public void StartDhcp() => Dhcp.Start(m_clock());

    /// <summary>
    /// Takes a frame the link received: ARP and IPv4 to the stack's address, a broadcast or, while DHCP has no lease,
    /// to the DHCP client; IPv6 to the stack's addresses or to a multicast group it listens to (W4.3); anything else is
    /// dropped.
    /// </summary>
    /// <param name="frame">An Ethernet II frame.</param>
    public void Receive(ReadOnlySpan<byte> frame)
    {
        if (!EthernetHeader.TryRead(frame, out var ethernet) ||
            (ethernet.Destination != m_link.Address && ethernet.Destination != MacAddress.Broadcast && !ethernet.Destination.IsIpv6Multicast))
        {
            Statistics.FramesDropped++;
            return;
        }
        Statistics.FramesReceived++;
        var payload = frame[EthernetHeader.SIZE..];
        if (ethernet.Type == EthernetHeader.ARP)
            ReceiveArp(payload);
        else if (ethernet.Type == EthernetHeader.IPV4)
            ReceiveIpv4(payload, fromLoopback: false);
        else if (ethernet.Type == EthernetHeader.IPV6)
            ReceiveIpv6(payload, fromLoopback: false);
        else
            Statistics.FramesDropped++;
    }

    /// <summary>
    /// Runs what is due: datagrams the loopback queued, ARP's and neighbour discovery's retries and expiries, the DHCP
    /// client's, the IPv6 host's and TCP's timers.
    /// </summary>
    public void Advance()
    {
        var now = m_clock();
        // The loopback's queue as it was at entry: what a received packet's answer queues waits for the next Advance, so
        // that two connections answering each other cannot keep one Advance going (P3).
        for (var queued = m_loopback.Count; queued > 0; queued--)
        {
            var packet = m_loopback.Dequeue();
            if (packet[0] >> 4 == 6)
                ReceiveIpv6(packet, fromLoopback: true);
            else
                ReceiveIpv4(packet, fromLoopback: true);
        }
        foreach (var (address, pending) in new List<KeyValuePair<Ipv4Address, PendingResolution>>(m_pending))
        {
            if (now < pending.NextAttempt)
                continue;
            if (++pending.Attempts >= ARP_ATTEMPTS)
            {
                Statistics.PacketsUnresolved += pending.Packets.Count;
                m_pending.Remove(address);
                continue;
            }
            pending.NextAttempt = now + ARP_RETRY_MILLISECONDS;
            SendArpRequest(address);
        }
        Dhcp.Advance(now);
        AdvanceIpv6(now);
        AdvanceTcp(now);
    }

    #region UDP

    /// <summary>
    /// Opens a UDP socket.
    /// </summary>
    /// <param name="socket">The socket's identifier.</param>
    /// <param name="ipv6">An AF_INET6 socket, which takes both families until IPV6_V6ONLY is set (W4.3); otherwise an
    /// AF_INET one.</param>
    /// <returns>Zero or an errno value.</returns>
    public int OpenUdp(out int socket, bool ipv6 = false)
    {
        socket = 0;
        if (m_udp.Count + m_tcp.Count >= SOCKETS)
            return NetworkErrors.NO_BUFFERS;
        socket = m_nextSocket++;
        m_udp[socket] = new UdpSocket(socket) { Family = ipv6 ? SocketMessages.INET6 : SocketMessages.INET };
        return 0;
    }

    /// <summary>
    /// Binds a socket to a local address and port: Any or one of the stack's, and a free port or zero for an ephemeral one.
    /// </summary>
    /// <returns>Zero or an errno value.</returns>
    public int Bind(int socket, Ipv4Address address, ushort port) => Bind(socket, Ipv6Address.Map(address), port);

    /// <summary>
    /// Binds a socket to a local address and port: Any or one of the stack's of a family the socket takes (an IPv4
    /// address in its mapped form, W4.3), and a free port or zero for an ephemeral one. A port is free for a socket when
    /// no other socket's binding overlaps its own, as Linux decides: the unspecified address of a dual-stack socket
    /// overlaps every other binding of the port, that of an AF_INET or an IPV6_V6ONLY socket its family's alone.
    /// </summary>
    /// <returns>Zero or an errno value.</returns>
    public int Bind(int socket, Ipv6Address address, ushort port)
    {
        if (m_tcp.TryGetValue(socket, out var tcp))
            return BindTcp(tcp, address, port);
        if (!m_udp.TryGetValue(socket, out var udp))
            return NetworkErrors.BAD_SOCKET;
        if (udp.LocalPort != 0)
            return NetworkErrors.INVALID;
        if (!Bindable(address, udp.Family, udp.V6Only))
            return NetworkErrors.ADDRESS_NOT_AVAILABLE;
        if (port == 0)
            port = EphemeralPort();
        else if (PortTaken(port, address, udp.Family, udp.V6Only, udp.ReuseAddress, udp.ReusePort))
            return NetworkErrors.ADDRESS_IN_USE;
        if (port == 0)
            return NetworkErrors.ADDRESS_IN_USE;
        udp.LocalAddress = address;
        udp.LocalPort = port;
        return 0;
    }

    /// <summary>
    /// Sets a datagram socket's peer: datagrams go there by default and come only from there; port zero disconnects. A
    /// stream socket starts its connection and answers IN_PROGRESS: it becomes writable once established, or reports its
    /// error.
    /// </summary>
    /// <returns>Zero or an errno value.</returns>
    public int Connect(int socket, Ipv4Address address, ushort port) => Connect(socket, Ipv6Address.Map(address), port);

    /// <inheritdoc cref="Connect(int, Ipv4Address, ushort)" />
    public int Connect(int socket, Ipv6Address address, ushort port)
    {
        if (m_tcp.TryGetValue(socket, out var tcp))
            return port == 0 ? NetworkErrors.INVALID : ConnectTcp(tcp, address, port);
        if (!m_udp.TryGetValue(socket, out var udp))
            return NetworkErrors.BAD_SOCKET;
        if (port == 0)
        {
            udp.RemoteAddress = Ipv6Address.Any;
            udp.RemotePort = 0;
            return 0;
        }
        if (!Takes(address, udp.Family, udp.V6Only))
            return NetworkErrors.NETWORK_UNREACHABLE;
        if (udp.LocalPort == 0 && Bind(socket, Ipv6Address.Any, 0) is var error and not 0)
            return error;
        udp.RemoteAddress = address;
        udp.RemotePort = port;
        return 0;
    }

    /// <summary>
    /// Sends a datagram to an address, or to the peer when none is given; an unbound socket takes an ephemeral port.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="payload">The datagram's payload.</param>
    /// <param name="address">The destination, or Any for the peer.</param>
    /// <param name="port">The destination's port, or zero for the peer.</param>
    /// <returns>Zero or an errno value.</returns>
    public int SendTo(int socket, ReadOnlySpan<byte> payload, Ipv4Address address, ushort port) =>
        SendTo(socket, payload, Ipv6Address.Map(address), port);

    /// <inheritdoc cref="SendTo(int, ReadOnlySpan{byte}, Ipv4Address, ushort)" />
    public int SendTo(int socket, ReadOnlySpan<byte> payload, Ipv6Address address, ushort port)
    {
        if (!m_udp.TryGetValue(socket, out var udp))
            return NetworkErrors.BAD_SOCKET;
        if (TakeError(udp) is var pending and not 0)
            return pending;
        if (port == 0)
        {
            if (!udp.IsConnected)
                return NetworkErrors.DESTINATION_REQUIRED;
            address = udp.RemoteAddress;
            port = udp.RemotePort;
        }
        var ipv4 = address.IsMapped;
        if (payload.Length > m_link.Mtu - (ipv4 ? Ipv4Header.SIZE : Ipv6Header.SIZE) - UdpHeader.SIZE)
            return NetworkErrors.MESSAGE_SIZE;
        if (ipv4 && address.MappedIpv4 == Ipv4Address.Broadcast && !udp.Broadcast)
            return NetworkErrors.NOT_SUPPORTED;
        if (!Takes(address, udp.Family, udp.V6Only))
            return NetworkErrors.NETWORK_UNREACHABLE;
        var source = SourceFor(address);
        if (source.IsAny)
            return NetworkErrors.NETWORK_UNREACHABLE;
        if (udp.LocalPort == 0 && Bind(socket, Ipv6Address.Any, 0) is var error and not 0)
            return error;
        var segment = new byte[UdpHeader.SIZE + payload.Length];
        payload.CopyTo(segment.AsSpan(UdpHeader.SIZE));
        UdpHeader.Write(segment, source, address, udp.LocalPort, port);
        return SendIp(source, address, Ipv4Header.UDP, segment) ? 0 : NetworkErrors.NETWORK_UNREACHABLE;
    }

    /// <summary>
    /// Takes the oldest datagram a socket received, or looks at it; a datagram longer than the buffer is cut to it.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="buffer">Where the payload goes.</param>
    /// <param name="peek">Whether the datagram stays queued.</param>
    /// <param name="received">The datagram, when one waited.</param>
    /// <returns>Zero, AGAIN when none waits, or another errno value.</returns>
    public int ReceiveFrom(int socket, Span<byte> buffer, bool peek, out Datagram? received)
    {
        received = null;
        if (!m_udp.TryGetValue(socket, out var udp))
            return NetworkErrors.BAD_SOCKET;
        if (TakeError(udp) is var pending and not 0)
            return pending;
        received = udp.Take(peek);
        if (received is null)
            return NetworkErrors.AGAIN;
        received.Payload.AsSpan(0, Math.Min(buffer.Length, received.Payload.Length)).CopyTo(buffer);
        return 0;
    }

    /// <summary>
    /// Sets SO_BROADCAST.
    /// </summary>
    /// <returns>Zero or an errno value.</returns>
    public int SetBroadcast(int socket, bool enabled)
    {
        if (!m_udp.TryGetValue(socket, out var udp))
            return NetworkErrors.BAD_SOCKET;
        udp.Broadcast = enabled;
        return 0;
    }

    /// <summary>
    /// Sets SO_REUSEADDR: a bound port may be bound again by another socket that set it too, and a datagram goes to one
    /// of them, a connected one first.
    /// </summary>
    /// <returns>Zero or an errno value.</returns>
    public int SetReuseAddress(int socket, bool enabled)
    {
        if (m_tcp.TryGetValue(socket, out var tcp))
        {
            tcp.ReuseAddress = enabled;
            return 0;
        }
        if (!m_udp.TryGetValue(socket, out var udp))
            return NetworkErrors.BAD_SOCKET;
        udp.ReuseAddress = enabled;
        return 0;
    }

    /// <summary>
    /// Sets SO_REUSEPORT: sockets that all set it share a port, listening ones too, and each datagram or connection goes
    /// to one of them, chosen by the sender's address and port, as Linux spreads them over its group.
    /// </summary>
    /// <returns>Zero or an errno value.</returns>
    public int SetReusePort(int socket, bool enabled)
    {
        if (m_tcp.TryGetValue(socket, out var tcp))
        {
            tcp.ReusePort = enabled;
            return 0;
        }
        if (!m_udp.TryGetValue(socket, out var udp))
            return NetworkErrors.BAD_SOCKET;
        udp.ReusePort = enabled;
        return 0;
    }

    /// <summary>
    /// Sets IPV6_V6ONLY of an AF_INET6 socket (W4.3): set, the socket takes no IPv4; clear (the default, as Linux's
    /// bindv6only), it serves both families with IPv4 peers in their mapped form. Linux refuses the change once the
    /// socket is bound.
    /// </summary>
    /// <returns>Zero, NO_OPTION for an AF_INET socket, INVALID for a bound one, or BAD_SOCKET.</returns>
    public int SetV6Only(int socket, bool enabled)
    {
        if (m_tcp.TryGetValue(socket, out var tcp))
        {
            if (tcp.Family != SocketMessages.INET6)
                return NetworkErrors.NO_OPTION;
            if (tcp.LocalPort != 0)
                return NetworkErrors.INVALID;
            tcp.V6Only = enabled;
            return 0;
        }
        if (!m_udp.TryGetValue(socket, out var udp))
            return NetworkErrors.BAD_SOCKET;
        if (udp.Family != SocketMessages.INET6)
            return NetworkErrors.NO_OPTION;
        if (udp.LocalPort != 0)
            return NetworkErrors.INVALID;
        udp.V6Only = enabled;
        return 0;
    }

    /// <summary>
    /// Takes a socket's pending error (SO_ERROR).
    /// </summary>
    /// <returns>The error, zero for none; BAD_SOCKET for no such socket.</returns>
    public int TakeError(int socket) => m_tcp.TryGetValue(socket, out var tcp) ? TakeError(tcp)
        : m_udp.TryGetValue(socket, out var udp) ? TakeError(udp) : NetworkErrors.BAD_SOCKET;

    /// <summary>
    /// A socket of the stack.
    /// </summary>
    /// <param name="socket">The socket's identifier.</param>
    /// <returns>The socket, or null.</returns>
    public UdpSocket? FindUdp(int socket) => m_udp.GetValueOrDefault(socket);

    /// <summary>
    /// A socket's readiness: for a datagram socket READABLE with a datagram waiting or an error pending, WRITABLE always,
    /// ERROR with an error pending; for a stream socket as Linux's poll reports it, arming the readiness events of what
    /// it lacks; zero for no such socket.
    /// </summary>
    public uint Poll(int socket)
    {
        if (m_tcp.TryGetValue(socket, out var tcp))
            return PollTcp(tcp);
        if (!m_udp.TryGetValue(socket, out var udp))
            return 0;
        return WRITABLE | (udp.Pending > 0 ? READABLE : 0) | (udp.PendingError != 0 ? READABLE | ERROR : 0);
    }

    /// <summary>
    /// The bytes a read would take now (FIONREAD): a stream's bytes received in order, a datagram socket's oldest
    /// datagram's; zero for no such socket.
    /// </summary>
    public int Available(int socket) => m_tcp.TryGetValue(socket, out var tcp) ? tcp.Receive.Count
        : m_udp.TryGetValue(socket, out var udp) ? udp.NextLength : 0;

    /// <summary>
    /// Closes a socket: a datagram socket's queued datagrams go with it; a connection finishes on its own (or is reset
    /// when bytes it received are unread) and its identifier is no longer the application's.
    /// </summary>
    /// <returns>Zero or an errno value.</returns>
    public int Close(int socket) => m_tcp.TryGetValue(socket, out var tcp) ? CloseTcp(tcp)
        : m_udp.Remove(socket) ? 0 : NetworkErrors.BAD_SOCKET;

    #endregion

    internal void SendDhcpBroadcast(byte[] message)
    {
        var segment = new byte[UdpHeader.SIZE + message.Length];
        message.CopyTo(segment, UdpHeader.SIZE);
        UdpHeader.Write(segment, Ipv4Address.Any, Ipv4Address.Broadcast, DhcpMessage.CLIENT_PORT, DhcpMessage.SERVER_PORT);
        var datagram = BuildIpv4(Ipv4Address.Any, Ipv4Address.Broadcast, Ipv4Header.UDP, segment);
        TransmitFrame(MacAddress.Broadcast, EthernetHeader.IPV4, datagram);
    }

    internal void SendDhcpUnicast(Ipv4Address server, byte[] message)
    {
        var segment = new byte[UdpHeader.SIZE + message.Length];
        message.CopyTo(segment, UdpHeader.SIZE);
        UdpHeader.Write(segment, Address, server, DhcpMessage.CLIENT_PORT, DhcpMessage.SERVER_PORT);
        SendIpv4(Address, server, Ipv4Header.UDP, segment);
    }

    private void ReceiveArp(ReadOnlySpan<byte> payload)
    {
        if (!ArpPacket.TryRead(payload, out var arp))
        {
            Statistics.FramesDropped++;
            return;
        }
        if (arp.SenderIp != Ipv4Address.Any)
            Learn(arp.SenderIp, arp.SenderMac);
        if (arp.Operation == ArpPacket.REQUEST && Address != Ipv4Address.Any && arp.TargetIp == Address)
        {
            var reply = new byte[ArpPacket.SIZE];
            new ArpPacket(ArpPacket.REPLY, m_link.Address, Address, arp.SenderMac, arp.SenderIp).Write(reply);
            TransmitFrame(arp.SenderMac, EthernetHeader.ARP, reply);
        }
    }

    // A neighbour's address: cached, and the packets that waited for it sent.
    private void Learn(Ipv4Address address, MacAddress mac)
    {
        m_neighbours[address] = (mac, m_clock() + ARP_LIFETIME_MILLISECONDS);
        if (!m_pending.Remove(address, out var pending))
            return;
        foreach (var datagram in pending.Packets)
            TransmitFrame(mac, EthernetHeader.IPV4, datagram);
    }

    private void ReceiveIpv4(ReadOnlySpan<byte> bytes, bool fromLoopback)
    {
        if (!Ipv4Header.TryRead(bytes, out var ip))
        {
            Statistics.PacketsDropped++;
            return;
        }
        if (ip.IsFragment)
        {
            Statistics.FragmentsDropped++;
            return;
        }
        var toUs = fromLoopback || (Address != Ipv4Address.Any && ip.Destination == Address) || ip.Destination == Ipv4Address.Broadcast ||
            (Address != Ipv4Address.Any && ip.Destination.Value == (Address.Value | ~Mask.Value));
        var forDhcp = Address == Ipv4Address.Any && ip.Protocol == Ipv4Header.UDP;
        if (!toUs && !forDhcp)
        {
            Statistics.PacketsDropped++;
            return;
        }
        Statistics.PacketsReceived++;
        var payload = bytes.Slice(ip.HeaderLength, ip.PayloadLength);
        switch (ip.Protocol)
        {
            case Ipv4Header.ICMP when toUs:
                ReceiveIcmp(ip, payload);
                break;
            case Ipv4Header.UDP:
                ReceiveUdp(ip, payload, bytes[..ip.TotalLength]);
                break;
            case Ipv4Header.TCP when toUs:
                ReceiveTcp(ip, payload);
                break;
            default:
                Statistics.PacketsDropped++;
                break;
        }
    }

    private void ReceiveIcmp(Ipv4Header ip, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8 || InternetChecksum.Compute(payload) != 0)
        {
            Statistics.PacketsDropped++;
            return;
        }
        if (payload[0] == ECHO_REQUEST && ip.Destination != Ipv4Address.Broadcast)
        {
            var reply = payload.ToArray();
            reply[0] = ECHO_REPLY;
            reply[2] = reply[3] = 0;
            var checksum = InternetChecksum.Compute(reply);
            reply[2] = (byte)(checksum >> 8);
            reply[3] = (byte)checksum;
            SendIpv4(ip.Destination, ip.Source, Ipv4Header.ICMP, reply);
            return;
        }
        // A port unreachable report for a datagram a connected socket sent: its peer refused it.
        if (payload[0] == UNREACHABLE && payload[1] == PORT_UNREACHABLE && payload.Length >= 8 + Ipv4Header.SIZE + UdpHeader.SIZE &&
            Ipv4Header.TryReadPrefix(payload[8..], out var original) && original.Protocol == Ipv4Header.UDP)
        {
            var udp = payload[(8 + original.HeaderLength)..];
            var sourcePort = (ushort)((udp[0] << 8) | udp[1]);
            var destinationPort = (ushort)((udp[2] << 8) | udp[3]);
            RefuseConnected(sourcePort, Ipv6Address.Map(original.Destination), destinationPort);
        }
    }

    // A port unreachable report (ICMP or ICMPv6) for a datagram a connected socket sent: its peer refused it.
    private void RefuseConnected(ushort sourcePort, Ipv6Address destination, ushort destinationPort)
    {
        foreach (var socket in m_udp.Values)
        {
            if (socket.LocalPort == sourcePort && socket.IsConnected && socket.RemotePort == destinationPort &&
                socket.RemoteAddress == destination)
            {
                socket.PendingError = NetworkErrors.CONNECTION_REFUSED;
                SocketReady?.Invoke(socket.Id);
            }
        }
    }

    private void ReceiveUdp(Ipv4Header ip, ReadOnlySpan<byte> segment, ReadOnlySpan<byte> datagram)
    {
        if (!UdpHeader.TryRead(segment, ip.Source, ip.Destination, out var udp))
        {
            Statistics.PacketsDropped++;
            return;
        }
        var payload = segment[UdpHeader.SIZE..udp.Length];
        if (udp.DestinationPort == DhcpMessage.CLIENT_PORT && udp.SourcePort == DhcpMessage.SERVER_PORT)
        {
            if (DhcpMessage.TryRead(payload, out var message))
                Dhcp.Receive(message!, m_clock());
            return;
        }
        if (Address == Ipv4Address.Any && !ip.Destination.IsLoopback)
            return;
        if (DeliverUdp(Ipv6Address.Map(ip.Source), Ipv6Address.Map(ip.Destination), udp, payload))
            return;
        Statistics.PortsUnreachable++;
        if (ip.Destination != Ipv4Address.Broadcast && !ip.Destination.IsMulticast)
            SendPortUnreachable(ip, datagram);
    }

    // A datagram to the socket that takes it: bound to its port at its destination or the unspecified address, of a
    // family that takes the destination's, and connected to its source if connected; a connected socket is the more
    // specific match, and sockets that share the port through SO_REUSEPORT take the sender's datagrams in turn of its
    // address and port.
    private bool DeliverUdp(Ipv6Address source, Ipv6Address destination, UdpHeader udp, ReadOnlySpan<byte> payload)
    {
        UdpSocket? target = null;
        var group = new List<UdpSocket>();
        foreach (var socket in m_udp.Values)
        {
            if (socket.LocalPort != udp.DestinationPort || !Takes(destination, socket.Family, socket.V6Only) ||
                (!socket.LocalAddress.IsAny && socket.LocalAddress != destination) ||
                (socket.IsConnected && (socket.RemoteAddress != source || socket.RemotePort != udp.SourcePort)))
                continue;
            if (target is null || socket.IsConnected)
                target = socket;
            if (!socket.IsConnected && socket.ReusePort)
                group.Add(socket);
        }
        if (target is { IsConnected: false, ReusePort: true } && group.Count > 1)
            target = group[GroupIndex(source, udp.SourcePort, group.Count)];
        if (target is null)
            return false;
        var wasEmpty = target.Pending == 0;
        if (target.Enqueue(new Datagram(source, udp.SourcePort, payload.ToArray())) && wasEmpty)
            SocketReady?.Invoke(target.Id);
        return true;
    }

    private void SendPortUnreachable(Ipv4Header ip, ReadOnlySpan<byte> datagram)
    {
        var quoted = datagram[..Math.Min(datagram.Length, ip.HeaderLength + 8)];
        var message = new byte[8 + quoted.Length];
        message[0] = UNREACHABLE;
        message[1] = PORT_UNREACHABLE;
        quoted.CopyTo(message.AsSpan(8));
        var checksum = InternetChecksum.Compute(message);
        message[2] = (byte)(checksum >> 8);
        message[3] = (byte)checksum;
        SendIpv4(ip.Destination, ip.Source, Ipv4Header.ICMP, message);
    }

    // A transport segment routed by its destination's family: an IPv4 address in its mapped form over IPv4, any other
    // over IPv6 (W4.3).
    private bool SendIp(Ipv6Address source, Ipv6Address destination, byte protocol, byte[] segment) => destination.IsMapped
        ? SendIpv4(source.MappedIpv4, destination.MappedIpv4, protocol, segment)
        : SendIpv6(source, destination, protocol, segment);

    // A datagram routed: to the loopback queue for 127.0.0.0/8 and the stack's own address, to the link's broadcast for a
    // broadcast, otherwise to the next hop — the destination on the subnet, the gateway off it — resolved by ARP.
    private bool SendIpv4(Ipv4Address source, Ipv4Address destination, byte protocol, byte[] segment)
    {
        var datagram = BuildIpv4(source, destination, protocol, segment);
        if (destination.IsLoopback || (destination == Address && Address != Ipv4Address.Any))
        {
            m_loopback.Enqueue(datagram);
            return true;
        }
        if (destination == Ipv4Address.Broadcast || (Address != Ipv4Address.Any && destination.Value == (Address.Value | ~Mask.Value)))
        {
            TransmitFrame(MacAddress.Broadcast, EthernetHeader.IPV4, datagram);
            return true;
        }
        Ipv4Address hop;
        if (destination.IsInSubnet(Address, Mask))
            hop = destination;
        else if (Gateway != Ipv4Address.Any)
            hop = Gateway;
        else
            return false;
        if (m_neighbours.TryGetValue(hop, out var neighbour) && neighbour.Expires > m_clock())
        {
            TransmitFrame(neighbour.Mac, EthernetHeader.IPV4, datagram);
            return true;
        }
        if (!m_pending.TryGetValue(hop, out var pending))
        {
            pending = new PendingResolution { NextAttempt = m_clock() + ARP_RETRY_MILLISECONDS };
            m_pending[hop] = pending;
            SendArpRequest(hop);
        }
        if (pending.Packets.Count >= ARP_PENDING_PACKETS)
        {
            pending.Packets.RemoveAt(0);
            Statistics.PacketsUnresolved++;
        }
        pending.Packets.Add(datagram);
        return true;
    }

    private byte[] BuildIpv4(Ipv4Address source, Ipv4Address destination, byte protocol, byte[] segment)
    {
        var datagram = new byte[Ipv4Header.SIZE + segment.Length];
        Ipv4Header.Write(datagram, source, destination, protocol, m_identification++, segment.Length);
        segment.CopyTo(datagram, Ipv4Header.SIZE);
        Statistics.PacketsSent++;
        return datagram;
    }

    private void SendArpRequest(Ipv4Address target)
    {
        var request = new byte[ArpPacket.SIZE];
        new ArpPacket(ArpPacket.REQUEST, m_link.Address, Address, MacAddress.None, target).Write(request);
        TransmitFrame(MacAddress.Broadcast, EthernetHeader.ARP, request);
    }

    private void TransmitFrame(MacAddress destination, ushort type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[EthernetHeader.SIZE + payload.Length];
        new EthernetHeader(destination, m_link.Address, type).Write(frame);
        payload.CopyTo(frame.AsSpan(EthernetHeader.SIZE));
        Statistics.FramesSent++;
        m_link.Transmit(frame);
    }

    private ushort EphemeralPort()
    {
        for (var tried = 0; tried <= ushort.MaxValue - EPHEMERAL_FIRST; tried++)
        {
            var port = m_nextEphemeral;
            m_nextEphemeral = m_nextEphemeral == ushort.MaxValue ? EPHEMERAL_FIRST : (ushort)(m_nextEphemeral + 1);
            if (!PortTaken(port, Ipv6Address.Any, SocketMessages.INET6, v6only: false, reuse: false, reusePort: false))
                return port;
        }
        return 0;
    }

    // Whether a port is bound where a socket of a family would bind it; with SO_REUSEADDR or SO_REUSEPORT on both sockets
    // it may be bound again, as Linux allows. The unspecified address of a dual-stack socket overlaps every binding of the
    // port, so an ephemeral port is one no socket holds.
    private bool PortTaken(ushort port, Ipv6Address address, ushort family, bool v6only, bool reuse, bool reusePort)
    {
        foreach (var socket in m_udp.Values)
        {
            if (socket.LocalPort == port && !(reuse && socket.ReuseAddress) && !(reusePort && socket.ReusePort) &&
                Overlaps(socket.LocalAddress, socket.Family, socket.V6Only, address, family, v6only))
                return true;
        }
        return false;
    }

    // A sender's member of a group of sockets that share a port (SO_REUSEPORT): the same one for every datagram or
    // connection of the same address and port. An IPv4 sender's mapped address hashes as its four bytes did.
    private static int GroupIndex(Ipv6Address source, ushort port, int count)
    {
        var hash = (uint)source.Low ^ (uint)(source.Low >> 32) ^ (uint)source.High ^ (uint)(source.High >> 32);
        if (source.IsMapped)
            hash = source.MappedIpv4.Value;
        return (int)(((hash * 2654435761U) ^ port) % (uint)count);
    }

    #region Addresses

    // Whether a socket of a family takes a destination or a sender of an address's family: an AF_INET socket IPv4 alone
    // (the mapped form), an AF_INET6 one IPv6 and, unless IPV6_V6ONLY, IPv4 too (W4.3).
    private static bool Takes(Ipv6Address address, ushort family, bool v6only) =>
        address.IsMapped ? family == SocketMessages.INET || !v6only : family == SocketMessages.INET6;

    // Whether two bindings of one port overlap, as Linux's bind decides: equal addresses, the unspecified address
    // against an address of a family it takes, two unspecified addresses that share a family.
    private static bool Overlaps(Ipv6Address first, ushort firstFamily, bool firstV6Only, Ipv6Address second, ushort secondFamily, bool secondV6Only)
    {
        if (!first.IsAny && !second.IsAny)
            return first == second;
        if (first.IsAny && second.IsAny)
        {
            var firstIpv4 = firstFamily == SocketMessages.INET || !firstV6Only;
            var secondIpv4 = secondFamily == SocketMessages.INET || !secondV6Only;
            return (firstIpv4 && secondIpv4) || (firstFamily == SocketMessages.INET6 && secondFamily == SocketMessages.INET6);
        }
        return first.IsAny ? Takes(second, firstFamily, firstV6Only) : Takes(first, secondFamily, secondV6Only);
    }

    // Whether a socket of a family may bind an address: the unspecified one, a loopback's, the interface's IPv4 address
    // (mapped) or one of its IPv6 addresses, of a family the socket takes.
    private bool Bindable(Ipv6Address address, ushort family, bool v6only)
    {
        if (address.IsAny)
            return true;
        if (!Takes(address, family, v6only))
            return false;
        if (address.IsMapped)
            return address.MappedIpv4.IsLoopback || (Address != Ipv4Address.Any && address.MappedIpv4 == Address);
        return address.IsLoopback || IsIpv6Assigned(address);
    }

    // The source address for a destination: the loopback's for a loopback, the interface's address of the destination's
    // family otherwise (the link-local one for a link-local or multicast destination); Any when the stack has none, which
    // makes the destination unreachable.
    private Ipv6Address SourceFor(Ipv6Address destination)
    {
        if (destination.IsMapped)
        {
            if (destination.MappedIpv4.IsLoopback)
                return Ipv6Address.Map(Ipv4Address.Loopback);
            return Address == Ipv4Address.Any ? Ipv6Address.Any : Ipv6Address.Map(Address);
        }
        if (destination.IsLoopback || IsIpv6Assigned(destination))
            return destination;
        if (destination.IsLinkLocal || destination.IsMulticast)
            return m_linkLocalReady ? m_linkLocal : Ipv6Address.Any;
        return m_globalReady ? m_global : Ipv6Address.Any;
    }

    #endregion

    private static int TakeError(UdpSocket udp)
    {
        var error = udp.PendingError;
        udp.PendingError = 0;
        return error;
    }

    #endregion

    #region Events

    /// <summary>
    /// A socket's readiness may have changed: a datagram arrived on an empty queue, or an error is pending.
    /// </summary>
    public event Action<int>? SocketReady;

    /// <summary>
    /// The interface's address, mask, gateway or DNS servers changed (W4.2): a lease taken, renewed with other values or
    /// lost. The service tells every session, whose libc tells the netlink sockets that listen (RTM_NEWADDR and the rest).
    /// </summary>
    public event Action? ConfigurationChanged;

    #endregion

    #region Properties

    /// <summary>
    /// The interface's address; Any while unconfigured.
    /// </summary>
    public Ipv4Address Address { get; private set; }

    /// <summary>
    /// The subnet's mask.
    /// </summary>
    public Ipv4Address Mask { get; private set; }

    /// <summary>
    /// The router; Any for none.
    /// </summary>
    public Ipv4Address Gateway { get; private set; }

    /// <summary>
    /// The DNS servers.
    /// </summary>
    public IReadOnlyList<Ipv4Address> DnsServers => m_dns;

    /// <summary>
    /// The adapter's address.
    /// </summary>
    public MacAddress LinkAddress => m_link.Address;

    /// <summary>
    /// The DHCP client.
    /// </summary>
    public DhcpClient Dhcp { get; }

    /// <summary>
    /// When <see cref="Advance"/> is next due: now while the loopback holds datagrams; long.MaxValue for never.
    /// </summary>
    public long NextDeadline
    {
        get
        {
            if (m_loopback.Count > 0)
                return m_clock();
            var deadline = Math.Min(Math.Min(Dhcp.NextDeadline, NextTcpDeadline()), NextIpv6Deadline());
            foreach (var pending in m_pending.Values)
                deadline = Math.Min(deadline, pending.NextAttempt);
            return deadline;
        }
    }

    /// <summary>
    /// The stack's counters.
    /// </summary>
    public NetworkStatistics Statistics { get; } = new();

    #endregion

    private sealed class PendingResolution
    {
        public List<byte[]> Packets { get; } = [];

        public int Attempts { get; set; }

        public long NextAttempt { get; set; }
    }
}

/// <summary>
/// The stack's counters (plan step W1.2a).
/// </summary>
public sealed class NetworkStatistics
{
    /// <summary>
    /// Frames the link delivered to the stack.
    /// </summary>
    public long FramesReceived { get; set; }

    /// <summary>
    /// Frames for another address or of another type.
    /// </summary>
    public long FramesDropped { get; set; }

    /// <summary>
    /// Frames the stack transmitted.
    /// </summary>
    public long FramesSent { get; set; }

    /// <summary>
    /// IPv4 datagrams the stack took.
    /// </summary>
    public long PacketsReceived { get; set; }

    /// <summary>
    /// IPv4 datagrams invalid, not for the stack or of a protocol it does not serve.
    /// </summary>
    public long PacketsDropped { get; set; }

    /// <summary>
    /// IPv4 datagrams the stack built.
    /// </summary>
    public long PacketsSent { get; set; }

    /// <summary>
    /// Fragments, which the stack does not reassemble.
    /// </summary>
    public long FragmentsDropped { get; set; }

    /// <summary>
    /// Datagrams whose next hop ARP did not resolve.
    /// </summary>
    public long PacketsUnresolved { get; set; }

    /// <summary>
    /// UDP datagrams to a port no socket bound.
    /// </summary>
    public long PortsUnreachable { get; set; }

    /// <summary>
    /// IPv6 (W4.3): neighbour solicitations sent, neighbour advertisements sent, router advertisements taken, and packets
    /// whose next hop neighbour discovery did not resolve.
    /// </summary>
    public long NeighbourSolicitations { get; set; }

    /// <inheritdoc cref="NeighbourSolicitations" />
    public long NeighbourAdvertisements { get; set; }

    /// <inheritdoc cref="NeighbourSolicitations" />
    public long RouterAdvertisements { get; set; }

    /// <inheritdoc cref="NeighbourSolicitations" />
    public long PacketsUnresolved6 { get; set; }

    /// <summary>
    /// IPv6 addresses duplicate address detection found in use, left unassigned (W4.3).
    /// </summary>
    public long DuplicateAddresses { get; set; }

    /// <summary>
    /// TCP: segments sent, segments sent again, retransmission timeouts, fast retransmits, resets sent, and connection
    /// requests a full backlog dropped.
    /// </summary>
    public long SegmentsSent { get; set; }

    /// <inheritdoc cref="SegmentsSent" />
    public long SegmentsRetransmitted { get; set; }

    /// <inheritdoc cref="SegmentsSent" />
    public long Timeouts { get; set; }

    /// <inheritdoc cref="SegmentsSent" />
    public long FastRetransmits { get; set; }

    /// <inheritdoc cref="SegmentsSent" />
    public long ResetsSent { get; set; }

    /// <inheritdoc cref="SegmentsSent" />
    public long ConnectionsDropped { get; set; }

    /// <summary>
    /// TCP: zero-window probes sent, and acknowledgments of unacceptable segments the rate limit held back (P3; Linux's
    /// TCPACKSkipped counters).
    /// </summary>
    public long WindowProbes { get; set; }

    /// <inheritdoc cref="WindowProbes" />
    public long AcknowledgmentsSkipped { get; set; }
}
