using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace OutWit.OS.Networking;

/// <summary>
/// The socket protocol's messages (witos/socket.h, plan step W1.2a): what the network service serves on a client's
/// session and events channels, and what the device manager sends it on the grant channel.
/// </summary>
public static class SocketMessages
{
    #region Constants

    /// <summary>
    /// WIT_SOCKET_VERSION.
    /// </summary>
    public const uint VERSION = 1;

    /// <summary>
    /// The bytes of a request.
    /// </summary>
    public const int REQUEST_SIZE = 88;

    /// <summary>
    /// The bytes of a reply.
    /// </summary>
    public const int REPLY_SIZE = 64;

    /// <summary>
    /// The bytes of an event.
    /// </summary>
    public const int EVENT_SIZE = 16;

    /// <summary>
    /// The bytes of the configuration.
    /// </summary>
    public const int CONFIGURATION_SIZE = 56;

    /// <summary>
    /// The bytes of the IPv6 configuration (W4.3).
    /// </summary>
    public const int CONFIGURATION6_SIZE = 136;

    /// <summary>
    /// The bytes of an adapter's message.
    /// </summary>
    public const int ADAPTER_SIZE = 64;

    /// <summary>
    /// WIT_SOCKET_CONNECT.
    /// </summary>
    public const uint CONNECT = 1;

    /// <summary>
    /// WIT_SOCKET_OPEN.
    /// </summary>
    public const uint OPEN = 2;

    /// <summary>
    /// WIT_SOCKET_BIND.
    /// </summary>
    public const uint BIND = 3;

    /// <summary>
    /// WIT_SOCKET_CONNECT_TO.
    /// </summary>
    public const uint CONNECT_TO = 4;

    /// <summary>
    /// WIT_SOCKET_LISTEN.
    /// </summary>
    public const uint LISTEN = 5;

    /// <summary>
    /// WIT_SOCKET_ACCEPT.
    /// </summary>
    public const uint ACCEPT = 6;

    /// <summary>
    /// WIT_SOCKET_SEND.
    /// </summary>
    public const uint SEND = 7;

    /// <summary>
    /// WIT_SOCKET_RECEIVE.
    /// </summary>
    public const uint RECEIVE = 8;

    /// <summary>
    /// WIT_SOCKET_SHUTDOWN.
    /// </summary>
    public const uint SHUTDOWN = 9;

    /// <summary>
    /// WIT_SOCKET_CLOSE.
    /// </summary>
    public const uint CLOSE = 10;

    /// <summary>
    /// WIT_SOCKET_NAME.
    /// </summary>
    public const uint NAME = 11;

    /// <summary>
    /// WIT_SOCKET_POLL.
    /// </summary>
    public const uint POLL = 12;

    /// <summary>
    /// WIT_SOCKET_GET_OPTION.
    /// </summary>
    public const uint GET_OPTION = 13;

    /// <summary>
    /// WIT_SOCKET_SET_OPTION.
    /// </summary>
    public const uint SET_OPTION = 14;

    /// <summary>
    /// WIT_SOCKET_CONFIGURATION.
    /// </summary>
    public const uint CONFIGURATION = 15;

    /// <summary>
    /// WIT_SOCKET_CONFIGURATION6 (W4.3).
    /// </summary>
    public const uint CONFIGURATION6 = 16;

    /// <summary>
    /// WIT_SOCKET_CONFIGURATION_EVENT: a WitSocketEvent whose socket is this tells of the configuration, not of a socket
    /// (W4.2).
    /// </summary>
    public const uint CONFIGURATION_EVENT = 0xFFFFFFFF;

    /// <summary>
    /// WIT_SOCKET_IPV6_LINK_LOCAL and WIT_SOCKET_IPV6_GLOBAL: the IPv6 configuration's flags, an address assigned (W4.3).
    /// </summary>
    public const uint IPV6_LINK_LOCAL = 0x1;

    /// <inheritdoc cref="IPV6_LINK_LOCAL" />
    public const uint IPV6_GLOBAL = 0x2;

    /// <summary>
    /// WIT_SOCKET_PEEK.
    /// </summary>
    public const uint PEEK = 0x2;

    /// <summary>
    /// WIT_SOCKET_PEER.
    /// </summary>
    public const uint PEER = 0x1;

    /// <summary>
    /// WIT_SOCKET_ADAPTER.
    /// </summary>
    public const uint ADAPTER = 1;

    /// <summary>
    /// An adapter's message's flag: no handle, no adapter follows.
    /// </summary>
    public const uint ADAPTERS_END = 0x1;

    /// <summary>
    /// WIT_SOCKET_WINDOW_BYTES.
    /// </summary>
    public const int WINDOW_BYTES = 16 * 4096;

    /// <summary>
    /// AF_UNSPEC.
    /// </summary>
    public const ushort UNSPECIFIED = 0;

    /// <summary>
    /// AF_INET.
    /// </summary>
    public const ushort INET = 2;

    /// <summary>
    /// AF_INET6 (W4.3).
    /// </summary>
    public const ushort INET6 = 10;

    /// <summary>
    /// SOCK_STREAM.
    /// </summary>
    public const ulong STREAM = 1;

    /// <summary>
    /// SOCK_DGRAM.
    /// </summary>
    public const ulong DATAGRAM = 2;

    /// <summary>
    /// SOL_SOCKET.
    /// </summary>
    public const ulong SOCKET_LEVEL = 1;

    /// <summary>
    /// SO_REUSEADDR.
    /// </summary>
    public const ulong REUSE_ADDRESS = 2;

    /// <summary>
    /// SO_TYPE.
    /// </summary>
    public const ulong TYPE = 3;

    /// <summary>
    /// SO_ERROR.
    /// </summary>
    public const ulong ERROR = 4;

    /// <summary>
    /// SO_BROADCAST.
    /// </summary>
    public const ulong BROADCAST = 6;

    /// <summary>
    /// SO_SNDBUF.
    /// </summary>
    public const ulong SEND_BUFFER = 7;

    /// <summary>
    /// SO_RCVBUF.
    /// </summary>
    public const ulong RECEIVE_BUFFER = 8;

    /// <summary>
    /// SO_KEEPALIVE.
    /// </summary>
    public const ulong KEEP_ALIVE = 9;

    /// <summary>
    /// SO_REUSEPORT.
    /// </summary>
    public const ulong REUSE_PORT = 15;

    /// <summary>
    /// SO_ACCEPTCONN.
    /// </summary>
    public const ulong ACCEPTS_CONNECTIONS = 30;

    /// <summary>
    /// IPPROTO_TCP, the level of TCP's options.
    /// </summary>
    public const ulong TCP_LEVEL = 6;

    /// <summary>
    /// TCP_NODELAY.
    /// </summary>
    public const ulong NO_DELAY = 1;

    /// <summary>
    /// IPPROTO_IPV6, the level of IPv6's options (W4.3).
    /// </summary>
    public const ulong IPV6_LEVEL = 41;

    /// <summary>
    /// IPV6_V6ONLY.
    /// </summary>
    public const ulong V6_ONLY = 26;

    /// <summary>
    /// SHUTDOWN's directions: SHUT_RD, SHUT_WR and SHUT_RDWR.
    /// </summary>
    public const ulong SHUT_READ = 0;

    /// <inheritdoc cref="SHUT_READ" />
    public const ulong SHUT_WRITE = 1;

    /// <inheritdoc cref="SHUT_READ" />
    public const ulong SHUT_BOTH = 2;

    #endregion

    #region Functions

    /// <summary>
    /// Encodes a request.
    /// </summary>
    public static byte[] Encode(SocketRequest request)
    {
        var bytes = new byte[REQUEST_SIZE];
        Header(bytes, REQUEST_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), request.Operation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), request.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), request.Socket);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), request.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), request.Window);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), request.Argument0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), request.Argument1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), request.Argument2);
        WriteAddress(bytes.AsSpan(64), request.Address);
        return bytes;
    }

    /// <summary>
    /// Decodes a request.
    /// </summary>
    /// <exception cref="FormatException">The bytes are no request of this version, or a reserved word is not zero.</exception>
    public static SocketRequest DecodeRequest(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, REQUEST_SIZE, "request");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[68..]) != 0)
            throw new FormatException("A socket request's reserved word is not zero.");
        return new SocketRequest(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]), ReadAddress(bytes[64..]));
    }

    /// <summary>
    /// Encodes a reply.
    /// </summary>
    public static byte[] Encode(SocketReply reply)
    {
        var bytes = new byte[REPLY_SIZE];
        Header(bytes, REPLY_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), reply.Operation);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), reply.Error);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), reply.Socket);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), reply.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), reply.Value);
        WriteAddress(bytes.AsSpan(40), reply.Address);
        return bytes;
    }

    /// <summary>
    /// Decodes a reply.
    /// </summary>
    /// <exception cref="FormatException">The bytes are no reply of this version.</exception>
    public static SocketReply DecodeReply(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, REPLY_SIZE, "reply");
        return new SocketReply(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]), ReadAddress(bytes[40..]));
    }

    /// <summary>
    /// Encodes a readiness event.
    /// </summary>
    public static byte[] EncodeEvent(uint socket, uint readiness)
    {
        var bytes = new byte[EVENT_SIZE];
        Header(bytes, EVENT_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), socket);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), readiness);
        return bytes;
    }

    /// <summary>
    /// Decodes a readiness event.
    /// </summary>
    /// <exception cref="FormatException">The bytes are no event of this version.</exception>
    public static (uint Socket, uint Readiness) DecodeEvent(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, EVENT_SIZE, "event");
        return (BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
    }

    /// <summary>
    /// Writes the configuration into the window.
    /// </summary>
    public static void WriteConfiguration(Span<byte> bytes, SocketConfiguration configuration)
    {
        bytes[..CONFIGURATION_SIZE].Clear();
        Header(bytes, CONFIGURATION_SIZE);
        configuration.Address.Write(bytes[8..]);
        configuration.Mask.Write(bytes[12..]);
        configuration.Gateway.Write(bytes[16..]);
        var count = Math.Min(configuration.DnsServers.Count, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[20..], (uint)count);
        for (var i = 0; i < count; i++)
            configuration.DnsServers[i].Write(bytes[(24 + 4 * i)..]);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], configuration.Mac.Value);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[48..], configuration.Mtu);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[52..], configuration.Leased ? 1U : 0U);
    }

    /// <summary>
    /// Reads the configuration from the window.
    /// </summary>
    /// <exception cref="FormatException">The bytes are no configuration of this version.</exception>
    public static SocketConfiguration ReadConfiguration(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes[..CONFIGURATION_SIZE], CONFIGURATION_SIZE, "configuration");
        var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]), 4U);
        var dns = new List<Ipv4Address>(count);
        for (var i = 0; i < count; i++)
            dns.Add(Ipv4Address.Read(bytes[(24 + 4 * i)..]));
        return new SocketConfiguration(Ipv4Address.Read(bytes[8..]), Ipv4Address.Read(bytes[12..]), Ipv4Address.Read(bytes[16..]), dns,
            new MacAddress(BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..])), BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[52..]) != 0);
    }

    /// <summary>
    /// Writes the IPv6 configuration into the window (W4.3).
    /// </summary>
    public static void WriteConfiguration6(Span<byte> bytes, SocketConfiguration6 configuration)
    {
        bytes[..CONFIGURATION6_SIZE].Clear();
        Header(bytes, CONFIGURATION6_SIZE);
        configuration.LinkLocal.Write(bytes[8..]);
        configuration.Global.Write(bytes[24..]);
        configuration.Router.Write(bytes[40..]);
        var count = Math.Min(configuration.DnsServers.Count, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[56..], (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[60..], configuration.PrefixLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[64..], configuration.Flags);
        for (var i = 0; i < count; i++)
            configuration.DnsServers[i].Write(bytes[(72 + Ipv6Address.SIZE * i)..]);
    }

    /// <summary>
    /// Reads the IPv6 configuration from the window.
    /// </summary>
    /// <exception cref="FormatException">The bytes are no IPv6 configuration of this version.</exception>
    public static SocketConfiguration6 ReadConfiguration6(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes[..CONFIGURATION6_SIZE], CONFIGURATION6_SIZE, "IPv6 configuration");
        var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes[56..]), 4U);
        var dns = new List<Ipv6Address>(count);
        for (var i = 0; i < count; i++)
            dns.Add(Ipv6Address.Read(bytes[(72 + Ipv6Address.SIZE * i)..]));
        return new SocketConfiguration6(Ipv6Address.Read(bytes[8..]), Ipv6Address.Read(bytes[24..]), Ipv6Address.Read(bytes[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..]), dns, BinaryPrimitives.ReadUInt32LittleEndian(bytes[64..]));
    }

    /// <summary>
    /// Encodes an adapter's message: the device's identity, cut to 47 bytes, and the flags.
    /// </summary>
    public static byte[] EncodeAdapter(string device, uint flags = 0)
    {
        var bytes = new byte[ADAPTER_SIZE];
        Header(bytes, ADAPTER_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), ADAPTER);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), flags);
        var name = Encoding.ASCII.GetBytes(device);
        name.AsSpan(0, Math.Min(name.Length, 47)).CopyTo(bytes.AsSpan(16));
        return bytes;
    }

    /// <summary>
    /// Decodes an adapter's message.
    /// </summary>
    /// <returns>The device's identity and the flags, or null for bytes that are no adapter's message.</returns>
    public static (string Device, uint Flags)? DecodeAdapter(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ADAPTER_SIZE || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VERSION ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != ADAPTER_SIZE || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != ADAPTER)
            return null;
        var name = bytes[16..];
        var end = name.IndexOf((byte)0);
        return (Encoding.ASCII.GetString(end < 0 ? name : name[..end]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
    }

    // An AF_INET address's four bytes, an AF_INET6 one's sixteen, in network order at 8.
    private static void WriteAddress(Span<byte> bytes, SocketAddress address)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, address.Family);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], address.Port);
        bytes[8..24].Clear();
        if (address.Family == INET6)
            address.Address.Write(bytes[8..]);
        else if (address.Family == INET)
            address.Address.MappedIpv4.Write(bytes[8..]);
    }

    private static SocketAddress ReadAddress(ReadOnlySpan<byte> bytes)
    {
        var family = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var port = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        return family switch
        {
            INET6 => new SocketAddress(family, port, Ipv6Address.Read(bytes[8..])),
            INET => new SocketAddress(family, port, Ipv6Address.Map(Ipv4Address.Read(bytes[8..]))),
            _ => new SocketAddress(family, port, Ipv6Address.Any)
        };
    }

    private static void Header(Span<byte> bytes, int size)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], (uint)size);
    }

    private static void CheckHeader(ReadOnlySpan<byte> bytes, int size, string what)
    {
        if (bytes.Length != size || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VERSION ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != size)
            throw new FormatException($"A socket {what} of {bytes.Length} bytes is not of version {VERSION}.");
    }

    #endregion
}

/// <summary>
/// An address of the socket protocol (WitSocketAddress): AF_INET or AF_INET6 with a port, or AF_UNSPEC for none. The
/// address is an IPv6 one whatever the family, an AF_INET address in its mapped form (W4.3).
/// </summary>
/// <param name="Family">AF_INET, AF_INET6 or AF_UNSPEC.</param>
/// <param name="Port">The port, in host order.</param>
/// <param name="Address">The address; an IPv4 one in its mapped form.</param>
public readonly record struct SocketAddress(ushort Family, ushort Port, Ipv6Address Address)
{
    /// <summary>
    /// No address.
    /// </summary>
    public static readonly SocketAddress None = new(SocketMessages.UNSPECIFIED, 0, Ipv6Address.Any);

    /// <summary>
    /// An AF_INET address.
    /// </summary>
    public static SocketAddress Inet(Ipv4Address address, ushort port) => new(SocketMessages.INET, port, Ipv6Address.Map(address));

    /// <summary>
    /// An AF_INET6 address; an IPv4 address in its mapped form names a dual-stack socket's IPv4 peer.
    /// </summary>
    public static SocketAddress Inet6(Ipv6Address address, ushort port) => new(SocketMessages.INET6, port, address);

    /// <summary>
    /// An address in a socket's family: an AF_INET socket's as AF_INET, an AF_INET6 socket's as AF_INET6, mapped for an
    /// IPv4 address, as Linux names a dual-stack socket's peers.
    /// </summary>
    /// <param name="family">The socket's family.</param>
    /// <param name="address">The address; an IPv4 one in its mapped form.</param>
    /// <param name="port">The port.</param>
    /// <returns>The address.</returns>
    public static SocketAddress Of(ushort family, Ipv6Address address, ushort port) => new(family, port, address);

    /// <summary>
    /// The address's text: an AF_INET address's dotted quad, an AF_INET6 address's RFC 5952 text, nothing for none.
    /// </summary>
    public string AddressText => Family switch
    {
        SocketMessages.INET => Address.MappedIpv4.ToString(),
        SocketMessages.INET6 => Address.ToString(),
        _ => ""
    };

    /// <inheritdoc />
    public override string ToString() => Family switch
    {
        SocketMessages.INET => $"{AddressText}:{Port}",
        SocketMessages.INET6 => $"[{AddressText}]:{Port}",
        _ => "none"
    };
}

/// <summary>
/// A socket request (WitSocketRequest).
/// </summary>
public readonly record struct SocketRequest(uint Operation, uint Flags = 0, uint Socket = 0, long Length = 0, long Window = 0,
    ulong Argument0 = 0, ulong Argument1 = 0, ulong Argument2 = 0, SocketAddress Address = default);

/// <summary>
/// A socket reply (WitSocketReply).
/// </summary>
public readonly record struct SocketReply(uint Operation, int Error, uint Socket = 0, long Length = 0, ulong Value = 0,
    SocketAddress Address = default);

/// <summary>
/// The interface and its lease (WitSocketConfiguration).
/// </summary>
public sealed record SocketConfiguration(Ipv4Address Address, Ipv4Address Mask, Ipv4Address Gateway, IReadOnlyList<Ipv4Address> DnsServers,
    MacAddress Mac, int Mtu, bool Leased);

/// <summary>
/// The interface's IPv6 configuration (WitSocketConfiguration6, W4.3): its link-local and global addresses (Any until
/// assigned; the flags say which are), the on-link prefix's length, the default router and the DNS servers the router
/// advertised.
/// </summary>
public sealed record SocketConfiguration6(Ipv6Address LinkLocal, Ipv6Address Global, Ipv6Address Router, uint PrefixLength,
    IReadOnlyList<Ipv6Address> DnsServers, uint Flags);
