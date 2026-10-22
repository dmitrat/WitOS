using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using OutWit.OS.Networking;

namespace OutWit.OS.Tests.Networking;

/// <summary>
/// A simulated Ethernet segment for the stack's tests (plan step W1.2a): stacks on links whose frames reach every other
/// link of the segment when <see cref="Pump"/> runs, a manual clock in milliseconds, and an optional DHCP server that
/// answers as QEMU's does (10.0.2.2 serving 10.0.2.15 with DNS 10.0.2.3). For TCP (W1.2b) it loses frames at a rate and
/// swaps frames in pairs, from seeded randomness, so that a lossy run repeats. Its optional IPv6 router (W4.3) answers
/// as slirp's does: a router advertisement from fe80::2 for a solicitation, with the prefix fec0::/64, the DNS server
/// fec0::3 and its hardware address, and neighbour advertisements for its addresses.
/// </summary>
internal sealed class SimulatedNetwork
{
    #region Fields

    private readonly List<SimulatedLink> m_links = [];
    private readonly Queue<(SimulatedLink From, byte[] Frame)> m_frames = new();
    private readonly Random m_medium = new(7);
    private (SimulatedLink From, byte[] Frame)? m_held;

    #endregion

    #region Functions

    /// <summary>
    /// A stack on a new link of the segment.
    /// </summary>
    /// <param name="mac">The link's address.</param>
    /// <param name="mtu">The link's MTU.</param>
    /// <returns>The stack.</returns>
    public NetworkStack AddStack(ulong mac, int mtu = 1500)
    {
        var link = new SimulatedLink(this, new MacAddress(mac), mtu);
        var stack = new NetworkStack(link, () => Now, new Random(1));
        link.Stack = stack;
        m_links.Add(link);
        return stack;
    }

    /// <summary>
    /// Delivers frames until none is left, advancing every stack between rounds; at most a bound of rounds, so a loop of
    /// frames fails a test instead of hanging it.
    /// </summary>
    public void Pump()
    {
        for (var round = 0; round < 1000; round++)
        {
            foreach (var link in m_links)
                link.Stack!.Advance();
            if (m_frames.Count == 0 && m_held is { } held)
            {
                m_frames.Enqueue(held); // a frame held for a swap goes once nothing follows it
                m_held = null;
            }
            if (m_frames.Count == 0 && m_links.TrueForAll(link => link.Stack!.NextDeadline > Now))
                return; // nothing on the segment, and no stack with the loopback's datagrams left for its next advance (P3)
            while (m_frames.Count > 0)
            {
                var (from, frame) = m_frames.Dequeue();
                Captured.Add(frame);
                if (DhcpServer && (TryAnswerDhcp(frame) || TryAnswerArp(frame)))
                    continue;
                if (Ipv6Router && TryAnswerIpv6(frame))
                    continue;
                foreach (var link in m_links)
                    if (link != from)
                        link.Stack!.Receive(frame);
            }
        }
        throw new InvalidOperationException("The segment did not settle.");
    }

    /// <summary>
    /// Moves the clock and pumps.
    /// </summary>
    /// <param name="milliseconds">The time that passes.</param>
    public void Wait(long milliseconds)
    {
        Now += milliseconds;
        Pump();
    }

    internal void Transmit(SimulatedLink from, ReadOnlySpan<byte> frame)
    {
        if (DropNext > 0)
        {
            DropNext--;
            return;
        }
        if (LossRate > 0 && m_medium.NextDouble() < LossRate)
        {
            Lost++;
            return;
        }
        if (m_held is { } held)
        {
            m_frames.Enqueue((from, frame.ToArray()));
            m_frames.Enqueue(held);
            m_held = null;
            return;
        }
        if (SwapRate > 0 && m_medium.NextDouble() < SwapRate)
        {
            m_held = (from, frame.ToArray());
            return;
        }
        m_frames.Enqueue((from, frame.ToArray()));
    }

    // QEMU's DHCP server: an OFFER for a DISCOVER and an ACK for a REQUEST, from 10.0.2.2 to the broadcast address.
    private bool TryAnswerDhcp(byte[] frame)
    {
        if (!EthernetHeader.TryRead(frame, out var ethernet) || ethernet.Type != EthernetHeader.IPV4 ||
            !Ipv4Header.TryRead(frame.AsSpan(EthernetHeader.SIZE), out var ip) || ip.Protocol != Ipv4Header.UDP)
            return false;
        var segment = frame.AsSpan(EthernetHeader.SIZE + ip.HeaderLength, ip.PayloadLength);
        if (!UdpHeader.TryRead(segment, ip.Source, ip.Destination, out var udp) || udp.DestinationPort != DhcpMessage.SERVER_PORT ||
            !DhcpMessage.TryRead(segment[UdpHeader.SIZE..udp.Length], out var request))
            return false;
        var type = request!.MessageType == DhcpMessage.DISCOVER ? DhcpMessage.OFFER : DhcpMessage.ACK;
        var reply = DhcpReply(request.TransactionId, request.ClientMac, type);
        var datagram = new byte[UdpHeader.SIZE + reply.Length];
        reply.CopyTo(datagram, UdpHeader.SIZE);
        UdpHeader.Write(datagram, SERVER, Ipv4Address.Broadcast, DhcpMessage.SERVER_PORT, DhcpMessage.CLIENT_PORT);
        var packet = new byte[Ipv4Header.SIZE + datagram.Length];
        Ipv4Header.Write(packet, SERVER, Ipv4Address.Broadcast, Ipv4Header.UDP, 1, datagram.Length);
        datagram.CopyTo(packet, Ipv4Header.SIZE);
        var answer = new byte[EthernetHeader.SIZE + packet.Length];
        new EthernetHeader(MacAddress.Broadcast, SERVER_MAC, EthernetHeader.IPV4).Write(answer);
        packet.CopyTo(answer, EthernetHeader.SIZE);
        Answers++;
        foreach (var link in m_links)
            link.Stack!.Receive(answer);
        return true;
    }

    // slirp answers ARP for its gateway.
    private bool TryAnswerArp(byte[] frame)
    {
        if (!EthernetHeader.TryRead(frame, out var ethernet) || ethernet.Type != EthernetHeader.ARP ||
            !ArpPacket.TryRead(frame.AsSpan(EthernetHeader.SIZE), out var arp) || arp.Operation != ArpPacket.REQUEST || arp.TargetIp != SERVER)
            return false;
        var answer = new byte[EthernetHeader.SIZE + ArpPacket.SIZE];
        new EthernetHeader(arp.SenderMac, SERVER_MAC, EthernetHeader.ARP).Write(answer);
        new ArpPacket(ArpPacket.REPLY, SERVER_MAC, SERVER, arp.SenderMac, arp.SenderIp).Write(answer.AsSpan(EthernetHeader.SIZE));
        foreach (var link in m_links)
            link.Stack!.Receive(answer);
        return true;
    }

    // slirp's IPv6 router: a router advertisement to all nodes for a solicitation, a neighbour advertisement for a
    // solicitation of one of its addresses.
    private bool TryAnswerIpv6(byte[] frame)
    {
        if (!EthernetHeader.TryRead(frame, out var ethernet) || ethernet.Type != EthernetHeader.IPV6 ||
            !Ipv6Header.TryRead(frame.AsSpan(EthernetHeader.SIZE), out var ip) || ip.NextHeader != Ipv6Header.ICMPV6)
            return false;
        var message = frame.AsSpan(EthernetHeader.SIZE + Ipv6Header.SIZE, ip.PayloadLength);
        if (!Icmpv6.TryRead(message, ip.Source, ip.Destination, out var type, out _))
            return false;
        byte[] answer;
        Ipv6Address source, destination;
        if (type == Icmpv6.ROUTER_SOLICITATION)
        {
            source = ROUTER;
            destination = Ipv6Address.AllNodes;
            answer = Icmpv6.RouterAdvertisement(1800, PREFIX6, 64, 86400, 14400, SERVER_MAC, 1500, [DNS6], source, destination);
            RouterAdvertisements++;
        }
        else if (type == Icmpv6.NEIGHBOUR_SOLICITATION && message.Length >= 24 && !ip.Source.IsAny &&
            Ipv6Address.Read(message[8..]) is var target && (target == ROUTER || target == HOST6 || target == DNS6))
        {
            source = target;
            destination = ip.Source;
            answer = Icmpv6.NeighbourAdvertisement(target, Icmpv6.SOLICITED_FLAG | Icmpv6.OVERRIDE_FLAG, SERVER_MAC, source, destination);
        }
        else
        {
            return false;
        }
        var packet = new byte[Ipv6Header.SIZE + answer.Length];
        Ipv6Header.Write(packet, source, destination, Ipv6Header.ICMPV6, answer.Length, Ipv6Header.DISCOVERY_HOP_LIMIT);
        answer.CopyTo(packet, Ipv6Header.SIZE);
        var reply = new byte[EthernetHeader.SIZE + packet.Length];
        new EthernetHeader(destination.IsMulticast ? destination.MulticastMac : ethernet.Source, SERVER_MAC, EthernetHeader.IPV6).Write(reply);
        packet.CopyTo(reply, EthernetHeader.SIZE);
        foreach (var link in m_links)
            link.Stack!.Receive(reply);
        return true;
    }

    private static byte[] DhcpReply(uint transactionId, MacAddress client, byte type)
    {
        var bytes = new byte[300];
        bytes[0] = DhcpMessage.BOOT_REPLY;
        bytes[1] = 1;
        bytes[2] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), transactionId);
        LEASED.Write(bytes.AsSpan(16));
        client.Write(bytes.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(236), 0x6382_5363);
        byte[] options = [53, 1, type, 54, 4, 10, 0, 2, 2, 51, 4, 0, 0, 0x0E, 0x10, 1, 4, 255, 255, 255, 0, 3, 4, 10, 0, 2, 2, 6, 4, 10, 0, 2, 3, 255];
        options.CopyTo(bytes, 240);
        return bytes;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The address QEMU's DHCP server leases first.
    /// </summary>
    public static Ipv4Address LEASED { get; } = new(10, 0, 2, 15);

    /// <summary>
    /// QEMU's gateway and DHCP server.
    /// </summary>
    public static Ipv4Address SERVER { get; } = new(10, 0, 2, 2);

    /// <summary>
    /// The DHCP server's hardware address, slirp's for the gateway.
    /// </summary>
    public static MacAddress SERVER_MAC { get; } = MacAddress.Read([0x52, 0x55, 10, 0, 2, 2]);

    /// <summary>
    /// slirp's IPv6: the router's link-local address, its prefix, its address on it and its DNS server.
    /// </summary>
    public static Ipv6Address ROUTER { get; } = Ipv6Address.Parse("fe80::2");

    /// <inheritdoc cref="ROUTER" />
    public static Ipv6Address PREFIX6 { get; } = Ipv6Address.Parse("fec0::");

    /// <inheritdoc cref="ROUTER" />
    public static Ipv6Address HOST6 { get; } = Ipv6Address.Parse("fec0::2");

    /// <inheritdoc cref="ROUTER" />
    public static Ipv6Address DNS6 { get; } = Ipv6Address.Parse("fec0::3");

    /// <summary>
    /// Whether the IPv6 router answers.
    /// </summary>
    public bool Ipv6Router { get; set; }

    /// <summary>
    /// The router advertisements the router sent.
    /// </summary>
    public int RouterAdvertisements { get; private set; }

    /// <summary>
    /// The clock in milliseconds.
    /// </summary>
    public long Now { get; set; }

    /// <summary>
    /// Whether the DHCP server answers.
    /// </summary>
    public bool DhcpServer { get; set; }

    /// <summary>
    /// The DHCP server's answers.
    /// </summary>
    public int Answers { get; private set; }

    /// <summary>
    /// Frames the segment drops next, as a lossy medium does.
    /// </summary>
    public int DropNext { get; set; }

    /// <summary>
    /// The share of frames the segment loses.
    /// </summary>
    public double LossRate { get; set; }

    /// <summary>
    /// The share of frames the segment delivers after the frame that follows them.
    /// </summary>
    public double SwapRate { get; set; }

    /// <summary>
    /// The frames the loss rate took.
    /// </summary>
    public int Lost { get; private set; }

    /// <summary>
    /// Every frame the segment carried, in order.
    /// </summary>
    public List<byte[]> Captured { get; } = [];

    #endregion
}

/// <summary>
/// A link of the simulated segment.
/// </summary>
internal sealed class SimulatedLink(SimulatedNetwork network, MacAddress address, int mtu) : ILink
{
    #region Functions

    /// <inheritdoc />
    public void Transmit(ReadOnlySpan<byte> frame) => network.Transmit(this, frame);

    #endregion

    #region Properties

    /// <inheritdoc />
    public MacAddress Address { get; } = address;

    /// <inheritdoc />
    public int Mtu { get; } = mtu;

    /// <summary>
    /// The stack on the link.
    /// </summary>
    public NetworkStack? Stack { get; set; }

    #endregion
}
