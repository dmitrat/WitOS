using System;
using System.Buffers.Binary;

namespace OutWit.OS.Networking;

/// <summary>
/// An ARP packet for IPv4 over Ethernet (RFC 826, plan step W1.2a).
/// </summary>
/// <param name="Operation">REQUEST or REPLY.</param>
/// <param name="SenderMac">The sender's hardware address.</param>
/// <param name="SenderIp">The sender's protocol address.</param>
/// <param name="TargetMac">The target's hardware address; zero in a request.</param>
/// <param name="TargetIp">The target's protocol address.</param>
public readonly record struct ArpPacket(ushort Operation, MacAddress SenderMac, Ipv4Address SenderIp, MacAddress TargetMac, Ipv4Address TargetIp)
{
    #region Constants

    /// <summary>
    /// The packet's bytes.
    /// </summary>
    public const int SIZE = 28;

    /// <summary>
    /// A request.
    /// </summary>
    public const ushort REQUEST = 1;

    /// <summary>
    /// A reply.
    /// </summary>
    public const ushort REPLY = 2;

    #endregion

    #region Functions

    /// <summary>
    /// Reads a packet: Ethernet hardware, IPv4 protocol, their lengths and a known operation, or nothing.
    /// </summary>
    /// <param name="bytes">The payload of a frame.</param>
    /// <param name="packet">The packet, when the bytes hold one.</param>
    /// <returns>Whether they do.</returns>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out ArpPacket packet)
    {
        packet = default;
        if (bytes.Length < SIZE || BinaryPrimitives.ReadUInt16BigEndian(bytes) != 1 ||
            BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]) != EthernetHeader.IPV4 || bytes[4] != 6 || bytes[5] != 4)
            return false;
        var operation = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        if (operation is not (REQUEST or REPLY))
            return false;
        packet = new ArpPacket(operation, MacAddress.Read(bytes[8..]), Ipv4Address.Read(bytes[14..]), MacAddress.Read(bytes[18..]),
            Ipv4Address.Read(bytes[24..]));
        return true;
    }

    /// <summary>
    /// Writes the packet.
    /// </summary>
    /// <param name="bytes">At least SIZE bytes.</param>
    public void Write(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt16BigEndian(bytes, 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[2..], EthernetHeader.IPV4);
        bytes[4] = 6;
        bytes[5] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(bytes[6..], Operation);
        SenderMac.Write(bytes[8..]);
        SenderIp.Write(bytes[14..]);
        TargetMac.Write(bytes[18..]);
        TargetIp.Write(bytes[24..]);
    }

    #endregion
}
