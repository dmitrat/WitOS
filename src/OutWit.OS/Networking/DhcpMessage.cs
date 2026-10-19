using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace OutWit.OS.Networking;

/// <summary>
/// A DHCP message (RFC 2131, RFC 2132; plan step W1.2a): BOOTP's fixed part with the magic cookie and the options a
/// client needs — the message type, the requested address, the server identifier, the lease time, the subnet mask, the
/// router and the DNS servers.
/// </summary>
/// <param name="Operation">BOOT_REQUEST or BOOT_REPLY.</param>
/// <param name="TransactionId">The client's transaction identifier.</param>
/// <param name="ClientAddress">ciaddr: the client's address while it renews.</param>
/// <param name="YourAddress">yiaddr: the address the server offers or assigns.</param>
/// <param name="ClientMac">chaddr: the client's hardware address.</param>
/// <param name="MessageType">The DHCP message type: DISCOVER, OFFER, REQUEST, ACK, NAK.</param>
/// <param name="ServerId">The server identifier.</param>
/// <param name="RequestedAddress">The address a REQUEST asks for.</param>
/// <param name="LeaseSeconds">The lease's seconds.</param>
/// <param name="SubnetMask">The subnet mask.</param>
/// <param name="Router">The first router.</param>
/// <param name="DnsServers">The DNS servers.</param>
public sealed record DhcpMessage(byte Operation, uint TransactionId, Ipv4Address ClientAddress, Ipv4Address YourAddress, MacAddress ClientMac,
    byte MessageType, Ipv4Address ServerId, Ipv4Address RequestedAddress, uint LeaseSeconds, Ipv4Address SubnetMask, Ipv4Address Router,
    IReadOnlyList<Ipv4Address> DnsServers)
{
    #region Constants

    /// <summary>
    /// The server's port.
    /// </summary>
    public const ushort SERVER_PORT = 67;

    /// <summary>
    /// The client's port.
    /// </summary>
    public const ushort CLIENT_PORT = 68;

    /// <summary>
    /// A request, from a client.
    /// </summary>
    public const byte BOOT_REQUEST = 1;

    /// <summary>
    /// A reply, from a server.
    /// </summary>
    public const byte BOOT_REPLY = 2;

    /// <summary>
    /// DHCPDISCOVER.
    /// </summary>
    public const byte DISCOVER = 1;

    /// <summary>
    /// DHCPOFFER.
    /// </summary>
    public const byte OFFER = 2;

    /// <summary>
    /// DHCPREQUEST.
    /// </summary>
    public const byte REQUEST = 3;

    /// <summary>
    /// DHCPACK.
    /// </summary>
    public const byte ACK = 5;

    /// <summary>
    /// DHCPNAK.
    /// </summary>
    public const byte NAK = 6;

    private const int FIXED = 236;
    private const uint MAGIC_COOKIE = 0x6382_5363;
    private const ushort BROADCAST_FLAG = 0x8000;
    private const byte PAD = 0;
    private const byte SUBNET_MASK = 1;
    private const byte ROUTER = 3;
    private const byte DNS = 6;
    private const byte REQUESTED_ADDRESS = 50;
    private const byte LEASE_TIME = 51;
    private const byte MESSAGE_TYPE = 53;
    private const byte SERVER_ID = 54;
    private const byte PARAMETERS = 55;
    private const byte END = 255;

    #endregion

    #region Functions

    /// <summary>
    /// A DHCPDISCOVER, asking for the mask, the router and the DNS servers, with the broadcast flag.
    /// </summary>
    /// <param name="mac">The client's hardware address.</param>
    /// <param name="transactionId">The transaction identifier.</param>
    /// <returns>The message's bytes.</returns>
    public static byte[] Discover(MacAddress mac, uint transactionId) =>
        Build(mac, transactionId, Ipv4Address.Any, DISCOVER, Ipv4Address.Any, Ipv4Address.Any);

    /// <summary>
    /// A DHCPREQUEST: selecting an offer (the requested address and the server named, ciaddr zero) or renewing a lease
    /// (ciaddr the leased address, no requested address and no server).
    /// </summary>
    /// <param name="mac">The client's hardware address.</param>
    /// <param name="transactionId">The transaction identifier.</param>
    /// <param name="clientAddress">ciaddr: the leased address while renewing, otherwise zero.</param>
    /// <param name="requested">The offered address while selecting, otherwise zero.</param>
    /// <param name="server">The offering server while selecting, otherwise zero.</param>
    /// <returns>The message's bytes.</returns>
    public static byte[] Request(MacAddress mac, uint transactionId, Ipv4Address clientAddress, Ipv4Address requested, Ipv4Address server) =>
        Build(mac, transactionId, clientAddress, REQUEST, requested, server);

    /// <summary>
    /// Reads a message: BOOTP's fixed part, the magic cookie and well-formed options; a message without a type is none.
    /// </summary>
    /// <param name="bytes">The UDP payload.</param>
    /// <param name="message">The message, when the bytes hold one.</param>
    /// <returns>Whether they do.</returns>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out DhcpMessage? message)
    {
        message = null;
        if (bytes.Length < FIXED + 4 || bytes[1] != 1 || bytes[2] != 6 || BinaryPrimitives.ReadUInt32BigEndian(bytes[FIXED..]) != MAGIC_COOKIE)
            return false;
        byte type = 0;
        uint lease = 0;
        Ipv4Address server = Ipv4Address.Any, requested = Ipv4Address.Any, mask = Ipv4Address.Any, router = Ipv4Address.Any;
        var dns = new List<Ipv4Address>();
        for (var at = FIXED + 4; at < bytes.Length;)
        {
            var code = bytes[at++];
            if (code == PAD)
                continue;
            if (code == END)
                break;
            if (at >= bytes.Length || at + 1 + bytes[at] > bytes.Length)
                return false;
            var value = bytes.Slice(at + 1, bytes[at]);
            at += 1 + value.Length;
            switch (code)
            {
                case MESSAGE_TYPE when value.Length == 1:
                    type = value[0];
                    break;
                case LEASE_TIME when value.Length == 4:
                    lease = BinaryPrimitives.ReadUInt32BigEndian(value);
                    break;
                case SERVER_ID when value.Length == 4:
                    server = Ipv4Address.Read(value);
                    break;
                case REQUESTED_ADDRESS when value.Length == 4:
                    requested = Ipv4Address.Read(value);
                    break;
                case SUBNET_MASK when value.Length == 4:
                    mask = Ipv4Address.Read(value);
                    break;
                case ROUTER when value.Length >= 4 && value.Length % 4 == 0:
                    router = Ipv4Address.Read(value);
                    break;
                case DNS when value.Length >= 4 && value.Length % 4 == 0:
                    for (var i = 0; i < value.Length; i += 4)
                        dns.Add(Ipv4Address.Read(value[i..]));
                    break;
            }
        }
        if (type == 0)
            return false;
        message = new DhcpMessage(bytes[0], BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]), Ipv4Address.Read(bytes[12..]),
            Ipv4Address.Read(bytes[16..]), MacAddress.Read(bytes[28..]), type, server, requested, lease, mask, router, dns);
        return true;
    }

    private static byte[] Build(MacAddress mac, uint transactionId, Ipv4Address clientAddress, byte type, Ipv4Address requested,
        Ipv4Address server)
    {
        var options = new List<byte> { MESSAGE_TYPE, 1, type };
        if (requested != Ipv4Address.Any)
            AddAddress(options, REQUESTED_ADDRESS, requested);
        if (server != Ipv4Address.Any)
            AddAddress(options, SERVER_ID, server);
        options.AddRange([PARAMETERS, 3, SUBNET_MASK, ROUTER, DNS, END]);
        var bytes = new byte[Math.Max(FIXED + 4 + options.Count, 300)]; // BOOTP's minimum of 300 bytes for old relays
        bytes[0] = BOOT_REQUEST;
        bytes[1] = 1;
        bytes[2] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), clientAddress == Ipv4Address.Any ? BROADCAST_FLAG : (ushort)0);
        clientAddress.Write(bytes.AsSpan(12));
        mac.Write(bytes.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(FIXED), MAGIC_COOKIE);
        options.CopyTo(bytes, FIXED + 4);
        return bytes;
    }

    private static void AddAddress(List<byte> options, byte code, Ipv4Address address)
    {
        Span<byte> value = stackalloc byte[4];
        address.Write(value);
        options.AddRange([code, 4, value[0], value[1], value[2], value[3]]);
    }

    #endregion
}
