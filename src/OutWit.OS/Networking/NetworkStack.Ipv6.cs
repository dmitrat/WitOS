using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace OutWit.OS.Networking;

/// <summary>
/// IPv6 (plan step W4.3): the host of RFC 8200 and RFC 4861 on the stack's one interface. A link-local address from the
/// hardware address (RFC 4862's stateless autoconfiguration, with duplicate address detection), router solicitations
/// until a router advertises, a global address from the advertised prefix, the default router and the DNS servers it
/// names (RFC 8106), a neighbour cache that resolves next hops as ARP does for IPv4, ICMPv6's echo and port unreachable,
/// and the loopback <c>::1</c>. UDP and TCP segments reach the same sockets as IPv4's: every socket address is an
/// <see cref="Ipv6Address"/>, an IPv4 one in its mapped form. Extension headers are not read: a packet with one is dropped.
/// </summary>
public sealed partial class NetworkStack
{
    #region Constants

    // RFC 4861's RETRANS_TIMER and MAX_MULTICAST_SOLICIT, and the time a resolved neighbour is kept, as ARP's.
    private const long NEIGHBOUR_RETRY_MILLISECONDS = 1000;
    private const int NEIGHBOUR_ATTEMPTS = 3;
    private const long NEIGHBOUR_LIFETIME_MILLISECONDS = 300_000;

    // RFC 4862: one solicitation (DupAddrDetectTransmits), answered within RetransTimer, decides a tentative address.
    private const long DUPLICATE_DETECTION_MILLISECONDS = 1000;

    // RFC 4861's MAX_RTR_SOLICITATIONS and RTR_SOLICITATION_INTERVAL.
    private const int ROUTER_SOLICITATIONS = 3;
    private const long ROUTER_SOLICITATION_INTERVAL_MILLISECONDS = 4000;

    // The one prefix length stateless autoconfiguration serves (RFC 4862 section 5.5.3: the interface identifier's 64 bits).
    private const int AUTOCONFIGURED_PREFIX_LENGTH = 64;

    #endregion

    #region Fields

    private readonly Dictionary<Ipv6Address, (MacAddress Mac, long Expires)> m_neighbours6 = [];
    private readonly Dictionary<Ipv6Address, PendingResolution> m_pending6 = [];
    private readonly List<Ipv6Address> m_dns6 = [];
    private Ipv6Address m_linkLocal;
    private Ipv6Address m_global;
    private Ipv6Address m_prefix;
    private int m_prefixLength;
    private bool m_linkLocalReady;
    private bool m_globalReady;
    private long m_linkLocalAt = long.MaxValue; // when duplicate address detection of the tentative address ends
    private long m_globalAt = long.MaxValue;
    private int m_solicitationsLeft;
    private long m_solicitAt = long.MaxValue;
    private long m_routerExpires = long.MaxValue;

    #endregion

    #region Functions

    /// <summary>
    /// Starts the IPv6 host on the interface: the link-local address from the hardware address goes through duplicate
    /// address detection, then router solicitations ask for the prefix, the router and the DNS servers. The loopback
    /// <c>::1</c> works without this.
    /// </summary>
    public void StartIpv6()
    {
        if (!m_linkLocal.IsAny)
            return;
        m_linkLocal = Ipv6Address.LinkLocal(m_link.Address);
        m_linkLocalAt = m_clock() + DUPLICATE_DETECTION_MILLISECONDS;
        SendNeighbourSolicitation(m_linkLocal, Ipv6Address.Any);
    }

    // Whether an address is one the interface holds (loopback apart).
    private bool IsIpv6Assigned(Ipv6Address address) =>
        (m_linkLocalReady && address == m_linkLocal) || (m_globalReady && address == m_global);

    // Whether an address is one the interface is detecting duplicates of.
    private bool IsTentative(Ipv6Address address) =>
        (!m_linkLocal.IsAny && !m_linkLocalReady && address == m_linkLocal) || (!m_global.IsAny && !m_globalReady && address == m_global);

    // Whether a multicast address is the solicited-node address of one of the interface's addresses, assigned or tentative.
    private bool IsSolicitedNodeOfOurs(Ipv6Address address) =>
        (!m_linkLocal.IsAny && address == Ipv6Address.SolicitedNode(m_linkLocal)) || (!m_global.IsAny && address == Ipv6Address.SolicitedNode(m_global));

    private void ReceiveIpv6(ReadOnlySpan<byte> bytes, bool fromLoopback)
    {
        if (!Ipv6Header.TryRead(bytes, out var ip))
        {
            Statistics.PacketsDropped++;
            return;
        }
        var destination = ip.Destination;
        var toUs = fromLoopback
            ? destination.IsLoopback || IsIpv6Assigned(destination)
            : IsIpv6Assigned(destination) || destination == Ipv6Address.AllNodes || IsSolicitedNodeOfOurs(destination);
        if (!toUs)
        {
            Statistics.PacketsDropped++;
            return;
        }
        Statistics.PacketsReceived++;
        var payload = bytes.Slice(Ipv6Header.SIZE, ip.PayloadLength);
        var packet = bytes[..ip.TotalLength];
        switch (ip.NextHeader)
        {
            case Ipv6Header.ICMPV6:
                ReceiveIcmpv6(ip, payload, packet);
                break;
            case Ipv4Header.UDP:
                ReceiveUdp6(ip, payload, packet);
                break;
            case Ipv4Header.TCP when !destination.IsMulticast:
                ReceiveTcp(ip.Source, ip.Destination, payload);
                break;
            default:
                Statistics.PacketsDropped++;
                break;
        }
    }

    private void ReceiveIcmpv6(Ipv6Header ip, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> packet)
    {
        if (!Icmpv6.TryRead(payload, ip.Source, ip.Destination, out var type, out var code))
        {
            Statistics.PacketsDropped++;
            return;
        }
        switch (type)
        {
            case Icmpv6.ECHO_REQUEST:
                if (!ip.Destination.IsMulticast)
                    SendIpv6(ip.Destination, ip.Source, Ipv6Header.ICMPV6, Icmpv6.EchoReply(payload, ip.Destination, ip.Source));
                break;
            case Icmpv6.NEIGHBOUR_SOLICITATION when ip.HopLimit == Ipv6Header.DISCOVERY_HOP_LIMIT:
                NeighbourSolicited(ip, payload);
                break;
            case Icmpv6.NEIGHBOUR_ADVERTISEMENT when ip.HopLimit == Ipv6Header.DISCOVERY_HOP_LIMIT:
                NeighbourAdvertised(payload);
                break;
            case Icmpv6.ROUTER_ADVERTISEMENT when ip.HopLimit == Ipv6Header.DISCOVERY_HOP_LIMIT && ip.Source.IsLinkLocal:
                RouterAdvertised(ip, payload);
                break;
            case Icmpv6.DESTINATION_UNREACHABLE:
                Unreachable6(code, payload);
                break;
        }
    }

    // A neighbour solicitation (RFC 4861 section 7.2.3): for an address of ours, a solicited advertisement to the asker
    // (to all nodes when it asked from the unspecified address); for a tentative one from the unspecified address, another
    // node detecting duplicates of the same address, which leaves it unassigned (RFC 4862 section 5.4.3).
    private void NeighbourSolicited(Ipv6Header ip, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8 + Ipv6Address.SIZE)
            return;
        var target = Ipv6Address.Read(payload[8..]);
        if (LinkLayerOption(payload[(8 + Ipv6Address.SIZE)..], Icmpv6.OPTION_SOURCE_LINK_LAYER) is { } mac && !ip.Source.IsAny)
            Learn6(ip.Source, mac);
        if (IsTentative(target))
        {
            if (ip.Source.IsAny)
                FailTentative(target);
            return;
        }
        if (!IsIpv6Assigned(target))
            return;
        Statistics.NeighbourAdvertisements++;
        if (ip.Source.IsAny)
        {
            SendIpv6(target, Ipv6Address.AllNodes, Ipv6Header.ICMPV6,
                Icmpv6.NeighbourAdvertisement(target, Icmpv6.OVERRIDE_FLAG, m_link.Address, target, Ipv6Address.AllNodes), Ipv6Header.DISCOVERY_HOP_LIMIT);
            return;
        }
        SendIpv6(target, ip.Source, Ipv6Header.ICMPV6,
            Icmpv6.NeighbourAdvertisement(target, Icmpv6.SOLICITED_FLAG | Icmpv6.OVERRIDE_FLAG, m_link.Address, target, ip.Source), Ipv6Header.DISCOVERY_HOP_LIMIT);
    }

    // A neighbour advertisement: the target's hardware address, or a duplicate of a tentative address of ours.
    private void NeighbourAdvertised(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8 + Ipv6Address.SIZE)
            return;
        var target = Ipv6Address.Read(payload[8..]);
        if (IsTentative(target))
        {
            FailTentative(target);
            return;
        }
        if (LinkLayerOption(payload[(8 + Ipv6Address.SIZE)..], Icmpv6.OPTION_TARGET_LINK_LAYER) is { } mac)
            Learn6(target, mac);
    }

    // A router advertisement (RFC 4861 section 6.3.4): the default router while its lifetime runs, the on-link prefix, a
    // global address from an autonomous prefix of 64 bits (RFC 4862 section 5.5.3), and the DNS servers (RFC 8106).
    private void RouterAdvertised(Ipv6Header ip, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16)
            return;
        var now = m_clock();
        var routerLifetime = BinaryPrimitives.ReadUInt16BigEndian(payload[6..]);
        var changed = false;
        foreach (var (type, body) in Icmpv6.Options(payload[16..].ToArray()))
        {
            switch (type)
            {
                case Icmpv6.OPTION_SOURCE_LINK_LAYER when body.Length >= 6:
                    Learn6(ip.Source, Icmpv6.LinkLayer(body));
                    break;
                case Icmpv6.OPTION_PREFIX_INFORMATION when Icmpv6.TryPrefix(body, out var prefix, out var length, out var flags, out var valid, out _) &&
                    valid > 0 && !prefix.IsLinkLocal && !prefix.IsMulticast && length <= 128:
                    if ((flags & Icmpv6.ON_LINK_FLAG) != 0 && (m_prefix != prefix || m_prefixLength != length))
                    {
                        m_prefix = prefix;
                        m_prefixLength = length;
                        changed = true;
                    }
                    if ((flags & Icmpv6.AUTONOMOUS_FLAG) != 0 && length == AUTOCONFIGURED_PREFIX_LENGTH && m_global.IsAny)
                    {
                        m_global = Ipv6Address.FromPrefix(prefix, m_link.Address);
                        m_globalAt = now + DUPLICATE_DETECTION_MILLISECONDS;
                        SendNeighbourSolicitation(m_global, Ipv6Address.Any);
                    }
                    break;
                case Icmpv6.OPTION_RECURSIVE_DNS:
                    var servers = Icmpv6.DnsServers(body, out var lifetime);
                    if (lifetime == 0)
                        servers = [];
                    if (!m_dns6.SequenceEqual(servers))
                    {
                        m_dns6.Clear();
                        m_dns6.AddRange(servers);
                        changed = true;
                    }
                    break;
            }
        }
        if (routerLifetime > 0)
        {
            if (Ipv6Gateway != ip.Source)
            {
                Ipv6Gateway = ip.Source;
                changed = true;
            }
            m_routerExpires = now + routerLifetime * 1000L;
        }
        else if (Ipv6Gateway == ip.Source)
        {
            Ipv6Gateway = Ipv6Address.Any;
            m_routerExpires = long.MaxValue;
            changed = true;
        }
        m_solicitationsLeft = 0;
        m_solicitAt = long.MaxValue;
        Statistics.RouterAdvertisements++;
        if (changed)
            ConfigurationChanged?.Invoke();
    }

    // A destination unreachable report quoting what this host sent: a port unreachable for a datagram a connected socket
    // sent is its peer's refusal; any of them for a connection attempt ends the attempt with Linux's error (slirp answers
    // a SYN to an address the host cannot reach with "no route", which Linux's tcp_v6_err turns into ENETUNREACH, so that
    // a program moves to the next address).
    private void Unreachable6(byte code, ReadOnlySpan<byte> payload)
    {
        var quoted = payload[8..];
        if (quoted.Length < Ipv6Header.SIZE + 8 || quoted[0] >> 4 != 6)
            return;
        var source = Ipv6Address.Read(quoted[8..]);
        var destination = Ipv6Address.Read(quoted[24..]);
        var transport = quoted[Ipv6Header.SIZE..];
        var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(transport);
        var destinationPort = BinaryPrimitives.ReadUInt16BigEndian(transport[2..]);
        if (quoted[6] == Ipv4Header.UDP && code == Icmpv6.PORT_UNREACHABLE)
            RefuseConnected(sourcePort, destination, destinationPort);
        else if (quoted[6] == Ipv4Header.TCP && UnreachableError6(code) is var error and not 0)
            RefuseConnecting(source, sourcePort, destination, destinationPort, BinaryPrimitives.ReadUInt32BigEndian(transport[4..]), error);
    }

    // The error a connection attempt ends with for an ICMPv6 destination unreachable code, as Linux's icmpv6_err_convert
    // maps it: no route, administratively prohibited, beyond scope, address unreachable, port unreachable, and the
    // policy and reject codes.
    private static int UnreachableError6(byte code) => code switch
    {
        0 => NetworkErrors.NETWORK_UNREACHABLE,
        1 or 5 or 6 => NetworkErrors.ACCESS_DENIED,
        2 or 3 => NetworkErrors.HOST_UNREACHABLE,
        4 => NetworkErrors.CONNECTION_REFUSED,
        _ => 0
    };

    private void ReceiveUdp6(Ipv6Header ip, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> packet)
    {
        if (!UdpHeader.TryRead(payload, ip.Source, ip.Destination, out var udp))
        {
            Statistics.PacketsDropped++;
            return;
        }
        if (DeliverUdp(ip.Source, ip.Destination, udp, payload[UdpHeader.SIZE..udp.Length]))
            return;
        Statistics.PortsUnreachable++;
        if (!ip.Destination.IsMulticast)
            SendIpv6(ip.Destination, ip.Source, Ipv6Header.ICMPV6, Icmpv6.Unreachable(Icmpv6.PORT_UNREACHABLE, packet, ip.Destination, ip.Source));
    }

    // A packet routed: to the loopback queue for ::1 and the interface's own addresses, to the group's hardware address
    // for a multicast destination, otherwise to the next hop — the destination when link-local or on the advertised
    // prefix, the default router off it — resolved by neighbour discovery, with the packets that wait for it bounded as
    // ARP's are.
    private bool SendIpv6(Ipv6Address source, Ipv6Address destination, byte nextHeader, byte[] payload, byte hopLimit = Ipv6Header.DEFAULT_HOP_LIMIT)
    {
        var packet = new byte[Ipv6Header.SIZE + payload.Length];
        Ipv6Header.Write(packet, source, destination, nextHeader, payload.Length, hopLimit);
        payload.CopyTo(packet, Ipv6Header.SIZE);
        Statistics.PacketsSent++;
        if (destination.IsLoopback || IsIpv6Assigned(destination))
        {
            m_loopback.Enqueue(packet);
            return true;
        }
        if (destination.IsMulticast)
        {
            TransmitFrame(destination.MulticastMac, EthernetHeader.IPV6, packet);
            return true;
        }
        Ipv6Address hop;
        if (destination.IsLinkLocal || (m_prefixLength > 0 && destination.InPrefix(m_prefix, m_prefixLength)))
            hop = destination;
        else if (!Ipv6Gateway.IsAny)
            hop = Ipv6Gateway;
        else
            return false;
        var now = m_clock();
        if (m_neighbours6.TryGetValue(hop, out var neighbour) && neighbour.Expires > now)
        {
            TransmitFrame(neighbour.Mac, EthernetHeader.IPV6, packet);
            return true;
        }
        if (!m_pending6.TryGetValue(hop, out var pending))
        {
            pending = new PendingResolution { NextAttempt = now + NEIGHBOUR_RETRY_MILLISECONDS };
            m_pending6[hop] = pending;
            SendNeighbourSolicitation(hop, m_linkLocalReady ? m_linkLocal : source);
        }
        if (pending.Packets.Count >= ARP_PENDING_PACKETS)
        {
            pending.Packets.RemoveAt(0);
            Statistics.PacketsUnresolved6++;
        }
        pending.Packets.Add(packet);
        return true;
    }

    // A neighbour solicitation for a target to its solicited-node group; from the unspecified address for duplicate
    // address detection.
    private void SendNeighbourSolicitation(Ipv6Address target, Ipv6Address source)
    {
        var group = Ipv6Address.SolicitedNode(target);
        Statistics.NeighbourSolicitations++;
        SendIpv6(source, group, Ipv6Header.ICMPV6, Icmpv6.NeighbourSolicitation(target, m_link.Address, source, group), Ipv6Header.DISCOVERY_HOP_LIMIT);
    }

    private void SendRouterSolicitation() =>
        SendIpv6(m_linkLocal, Ipv6Address.AllRouters, Ipv6Header.ICMPV6, Icmpv6.RouterSolicitation(m_link.Address, m_linkLocal, Ipv6Address.AllRouters),
            Ipv6Header.DISCOVERY_HOP_LIMIT);

    // A neighbour's hardware address: cached, and the packets that waited for it sent.
    private void Learn6(Ipv6Address address, MacAddress mac)
    {
        m_neighbours6[address] = (mac, m_clock() + NEIGHBOUR_LIFETIME_MILLISECONDS);
        if (!m_pending6.Remove(address, out var pending))
            return;
        foreach (var packet in pending.Packets)
            TransmitFrame(mac, EthernetHeader.IPV6, packet);
    }

    // Duplicate address detection found the address in use: it stays unassigned (RFC 4862 section 5.4.5).
    private void FailTentative(Ipv6Address address)
    {
        Statistics.DuplicateAddresses++;
        if (address == m_linkLocal)
        {
            m_linkLocal = Ipv6Address.Any;
            m_linkLocalAt = long.MaxValue;
        }
        else
        {
            m_global = Ipv6Address.Any;
            m_globalAt = long.MaxValue;
        }
    }

    // The hardware address a link-layer option of a type carries among a message's options; null without one.
    private static MacAddress? LinkLayerOption(ReadOnlySpan<byte> options, byte type)
    {
        foreach (var (kind, body) in Icmpv6.Options(options.ToArray()))
        {
            if (kind == type && body.Length >= 6)
                return Icmpv6.LinkLayer(body);
        }
        return null;
    }

    // What is due: a tentative address assigned once nobody claimed it, the router solicitations, neighbour discovery's
    // retries, and the default router's lifetime.
    private void AdvanceIpv6(long now)
    {
        var changed = false;
        if (m_linkLocalAt <= now)
        {
            m_linkLocalAt = long.MaxValue;
            m_linkLocalReady = true;
            m_solicitationsLeft = ROUTER_SOLICITATIONS;
            m_solicitAt = now;
            changed = true;
        }
        if (m_globalAt <= now)
        {
            m_globalAt = long.MaxValue;
            m_globalReady = true;
            changed = true;
        }
        if (m_solicitationsLeft > 0 && m_solicitAt <= now)
        {
            SendRouterSolicitation();
            m_solicitationsLeft--;
            m_solicitAt = m_solicitationsLeft > 0 ? now + ROUTER_SOLICITATION_INTERVAL_MILLISECONDS : long.MaxValue;
        }
        foreach (var (address, pending) in new List<KeyValuePair<Ipv6Address, PendingResolution>>(m_pending6))
        {
            if (now < pending.NextAttempt)
                continue;
            if (++pending.Attempts >= NEIGHBOUR_ATTEMPTS)
            {
                Statistics.PacketsUnresolved6 += pending.Packets.Count;
                m_pending6.Remove(address);
                continue;
            }
            pending.NextAttempt = now + NEIGHBOUR_RETRY_MILLISECONDS;
            SendNeighbourSolicitation(address, m_linkLocalReady ? m_linkLocal : Ipv6Address.Any);
        }
        if (m_routerExpires <= now)
        {
            m_routerExpires = long.MaxValue;
            Ipv6Gateway = Ipv6Address.Any;
            changed = true;
        }
        if (changed)
            ConfigurationChanged?.Invoke();
    }

    private long NextIpv6Deadline()
    {
        var deadline = Math.Min(Math.Min(m_linkLocalAt, m_globalAt), m_routerExpires);
        if (m_solicitationsLeft > 0)
            deadline = Math.Min(deadline, m_solicitAt);
        foreach (var pending in m_pending6.Values)
            deadline = Math.Min(deadline, pending.NextAttempt);
        return deadline;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The interface's link-local address; Any until duplicate address detection passed it.
    /// </summary>
    public Ipv6Address Ipv6LinkLocal => m_linkLocalReady ? m_linkLocal : Ipv6Address.Any;

    /// <summary>
    /// The interface's global address from the router's prefix; Any until a router advertised one and detection passed it.
    /// </summary>
    public Ipv6Address Ipv6Global => m_globalReady ? m_global : Ipv6Address.Any;

    /// <summary>
    /// The default router's link-local address; Any for none.
    /// </summary>
    public Ipv6Address Ipv6Gateway { get; private set; }

    /// <summary>
    /// The on-link prefix the router advertised, and its length; zero for none.
    /// </summary>
    public Ipv6Address Ipv6Prefix => m_prefix;

    /// <inheritdoc cref="Ipv6Prefix" />
    public int Ipv6PrefixLength => m_prefixLength;

    /// <summary>
    /// The DNS servers the router advertised.
    /// </summary>
    public IReadOnlyList<Ipv6Address> Ipv6DnsServers => m_dns6;

    #endregion
}
