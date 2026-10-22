using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace OutWit.OS.Networking;

/// <summary>
/// ICMPv6 (RFC 4443) and neighbour discovery (RFC 4861, RFC 4862; plan step W4.3) as the stack speaks them: the echo, the
/// destination unreachable, router solicitations and advertisements, neighbour solicitations and advertisements, and
/// the options the stack reads — the source and target link-layer addresses, the prefix information, the MTU and the
/// recursive DNS servers (RFC 8106). A message's checksum is over the IPv6 pseudo header.
/// </summary>
public static class Icmpv6
{
    #region Constants

    public const byte DESTINATION_UNREACHABLE = 1;
    public const byte PORT_UNREACHABLE = 4;
    public const byte ECHO_REQUEST = 128;
    public const byte ECHO_REPLY = 129;
    public const byte ROUTER_SOLICITATION = 133;
    public const byte ROUTER_ADVERTISEMENT = 134;
    public const byte NEIGHBOUR_SOLICITATION = 135;
    public const byte NEIGHBOUR_ADVERTISEMENT = 136;

    public const byte OPTION_SOURCE_LINK_LAYER = 1;
    public const byte OPTION_TARGET_LINK_LAYER = 2;
    public const byte OPTION_PREFIX_INFORMATION = 3;
    public const byte OPTION_MTU = 5;
    public const byte OPTION_RECURSIVE_DNS = 25;

    /// <summary>
    /// A neighbour advertisement's flags: router, solicited, override.
    /// </summary>
    public const uint ROUTER_FLAG = 0x8000_0000, SOLICITED_FLAG = 0x4000_0000, OVERRIDE_FLAG = 0x2000_0000;

    /// <summary>
    /// A prefix information option's flags: on-link, autonomous.
    /// </summary>
    public const byte ON_LINK_FLAG = 0x80, AUTONOMOUS_FLAG = 0x40;

    private const int HEADER = 4;

    #endregion

    #region Functions

    /// <summary>
    /// Reads a message's type and code, checking its checksum over the pseudo header.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="source">The packet's source.</param>
    /// <param name="destination">The packet's destination.</param>
    /// <param name="type">The type.</param>
    /// <param name="code">The code.</param>
    /// <returns>Whether the message is whole and sums.</returns>
    public static bool TryRead(ReadOnlySpan<byte> message, Ipv6Address source, Ipv6Address destination, out byte type, out byte code)
    {
        type = code = 0;
        if (message.Length < HEADER ||
            InternetChecksum.Finish(InternetChecksum.Add(message, InternetChecksum.PseudoHeader(source, destination, Ipv6Header.ICMPV6, message.Length))) != 0)
            return false;
        type = message[0];
        code = message[1];
        return true;
    }

    /// <summary>
    /// An echo reply for a request: the request's body with the type changed, summed again.
    /// </summary>
    public static byte[] EchoReply(ReadOnlySpan<byte> request, Ipv6Address source, Ipv6Address destination)
    {
        var reply = request.ToArray();
        reply[0] = ECHO_REPLY;
        Seal(reply, source, destination);
        return reply;
    }

    /// <summary>
    /// A destination unreachable message quoting as much of the packet as fits a minimum MTU (RFC 4443 section 3.1).
    /// </summary>
    public static byte[] Unreachable(byte code, ReadOnlySpan<byte> packet, Ipv6Address source, Ipv6Address destination)
    {
        var quoted = packet[..Math.Min(packet.Length, 1280 - Ipv6Header.SIZE - 8)];
        var message = new byte[8 + quoted.Length];
        message[0] = DESTINATION_UNREACHABLE;
        message[1] = code;
        quoted.CopyTo(message.AsSpan(8));
        Seal(message, source, destination);
        return message;
    }

    /// <summary>
    /// A router solicitation with the source link-layer address option (RFC 4861 section 4.1).
    /// </summary>
    public static byte[] RouterSolicitation(MacAddress mac, Ipv6Address source, Ipv6Address destination)
    {
        var message = new byte[8 + 8];
        message[0] = ROUTER_SOLICITATION;
        LinkLayerOption(message.AsSpan(8), OPTION_SOURCE_LINK_LAYER, mac);
        Seal(message, source, destination);
        return message;
    }

    /// <summary>
    /// A neighbour solicitation for a target, with the source link-layer address option unless the source is
    /// unspecified (duplicate address detection, RFC 4862 section 5.4.2).
    /// </summary>
    public static byte[] NeighbourSolicitation(Ipv6Address target, MacAddress mac, Ipv6Address source, Ipv6Address destination)
    {
        var message = new byte[8 + Ipv6Address.SIZE + (source.IsAny ? 0 : 8)];
        message[0] = NEIGHBOUR_SOLICITATION;
        target.Write(message.AsSpan(8));
        if (!source.IsAny)
            LinkLayerOption(message.AsSpan(8 + Ipv6Address.SIZE), OPTION_SOURCE_LINK_LAYER, mac);
        Seal(message, source, destination);
        return message;
    }

    /// <summary>
    /// A neighbour advertisement for a target with the flags and the target link-layer address option.
    /// </summary>
    public static byte[] NeighbourAdvertisement(Ipv6Address target, uint flags, MacAddress mac, Ipv6Address source, Ipv6Address destination)
    {
        var message = new byte[8 + Ipv6Address.SIZE + 8];
        message[0] = NEIGHBOUR_ADVERTISEMENT;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), flags);
        target.Write(message.AsSpan(8));
        LinkLayerOption(message.AsSpan(8 + Ipv6Address.SIZE), OPTION_TARGET_LINK_LAYER, mac);
        Seal(message, source, destination);
        return message;
    }

    /// <summary>
    /// A router advertisement as a router (the tests' simulated one) sends it: the router lifetime, a prefix with the
    /// on-link and autonomous flags, the source link-layer address, the MTU and the DNS servers.
    /// </summary>
    public static byte[] RouterAdvertisement(ushort routerLifetime, Ipv6Address prefix, byte prefixLength, uint validLifetime,
        uint preferredLifetime, MacAddress mac, uint mtu, IReadOnlyList<Ipv6Address> dns, Ipv6Address source, Ipv6Address destination)
    {
        var message = new byte[16 + 8 + 32 + 8 + (dns.Count > 0 ? 8 + Ipv6Address.SIZE * dns.Count : 0)];
        message[0] = ROUTER_ADVERTISEMENT;
        message[4] = 64; // the hop limit
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6), routerLifetime);
        var at = 16;
        LinkLayerOption(message.AsSpan(at), OPTION_SOURCE_LINK_LAYER, mac);
        at += 8;
        message[at] = OPTION_PREFIX_INFORMATION;
        message[at + 1] = 4;
        message[at + 2] = prefixLength;
        message[at + 3] = ON_LINK_FLAG | AUTONOMOUS_FLAG;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(at + 4), validLifetime);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(at + 8), preferredLifetime);
        prefix.Write(message.AsSpan(at + 16));
        at += 32;
        message[at] = OPTION_MTU;
        message[at + 1] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(at + 4), mtu);
        at += 8;
        if (dns.Count > 0)
        {
            message[at] = OPTION_RECURSIVE_DNS;
            message[at + 1] = (byte)(1 + 2 * dns.Count);
            BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(at + 4), validLifetime);
            for (var i = 0; i < dns.Count; i++)
                dns[i].Write(message.AsSpan(at + 8 + Ipv6Address.SIZE * i));
        }
        Seal(message, source, destination);
        return message;
    }

    /// <summary>
    /// The options after a message's fixed part, each a type, a length in units of eight bytes and its body.
    /// </summary>
    /// <param name="options">The bytes after the fixed part.</param>
    /// <returns>Each option's type and body; the walk stops at a malformed option.</returns>
    public static IEnumerable<(byte Type, byte[] Body)> Options(ReadOnlyMemory<byte> options)
    {
        for (var at = 0; at + 2 <= options.Length;)
        {
            var length = options.Span[at + 1] * 8;
            if (length == 0 || at + length > options.Length)
                yield break;
            yield return (options.Span[at], options.Slice(at + 2, length - 2).ToArray());
            at += length;
        }
    }

    /// <summary>
    /// The hardware address a link-layer option carries.
    /// </summary>
    public static MacAddress LinkLayer(byte[] body) => MacAddress.Read(body);

    /// <summary>
    /// A prefix information option: the prefix, its length, its flags and its lifetimes.
    /// </summary>
    public static bool TryPrefix(byte[] body, out Ipv6Address prefix, out byte length, out byte flags, out uint validLifetime, out uint preferredLifetime)
    {
        prefix = Ipv6Address.Any;
        length = flags = 0;
        validLifetime = preferredLifetime = 0;
        if (body.Length < 30)
            return false;
        length = body[0];
        flags = body[1];
        validLifetime = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(2));
        preferredLifetime = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(6));
        prefix = Ipv6Address.Read(body.AsSpan(14));
        return true;
    }

    /// <summary>
    /// The DNS servers a recursive DNS server option names, with their lifetime.
    /// </summary>
    public static IReadOnlyList<Ipv6Address> DnsServers(byte[] body, out uint lifetime)
    {
        lifetime = body.Length >= 6 ? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(2)) : 0;
        var servers = new List<Ipv6Address>();
        for (var at = 6; at + Ipv6Address.SIZE <= body.Length; at += Ipv6Address.SIZE)
            servers.Add(Ipv6Address.Read(body.AsSpan(at)));
        return servers;
    }

    private static void LinkLayerOption(Span<byte> bytes, byte type, MacAddress mac)
    {
        bytes[0] = type;
        bytes[1] = 1;
        mac.Write(bytes[2..]);
    }

    private static void Seal(byte[] message, Ipv6Address source, Ipv6Address destination)
    {
        message[2] = message[3] = 0;
        var checksum = InternetChecksum.Finish(InternetChecksum.Add(message, InternetChecksum.PseudoHeader(source, destination, Ipv6Header.ICMPV6, message.Length)));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), checksum);
    }

    #endregion
}
