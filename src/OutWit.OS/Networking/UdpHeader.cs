using System;
using System.Buffers.Binary;

namespace OutWit.OS.Networking;

/// <summary>
/// A UDP header (RFC 768, plan step W1.2a). A received checksum of zero means the sender computed none, which IPv4
/// allows and IPv6 does not (RFC 8200 section 8.1); the stack always computes one. The addresses are IPv6 ones, an IPv4
/// address in its mapped form, which sums as the IPv4 pseudo header (W4.3).
/// </summary>
/// <param name="SourcePort">The source port.</param>
/// <param name="DestinationPort">The destination port.</param>
/// <param name="Length">The datagram's bytes, header included.</param>
public readonly record struct UdpHeader(ushort SourcePort, ushort DestinationPort, int Length)
{
    #region Constants

    /// <summary>
    /// The header's bytes.
    /// </summary>
    public const int SIZE = 8;

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks a datagram: its length within the segment and its checksum over the pseudo header.
    /// </summary>
    /// <param name="segment">The IP payload.</param>
    /// <param name="source">The datagram's source address.</param>
    /// <param name="destination">The datagram's destination address.</param>
    /// <param name="header">The header, when the datagram is valid.</param>
    /// <returns>Whether it is.</returns>
    public static bool TryRead(ReadOnlySpan<byte> segment, Ipv4Address source, Ipv4Address destination, out UdpHeader header) =>
        TryRead(segment, Ipv6Address.Map(source), Ipv6Address.Map(destination), out header);

    /// <inheritdoc cref="TryRead(ReadOnlySpan{byte}, Ipv4Address, Ipv4Address, out UdpHeader)" />
    public static bool TryRead(ReadOnlySpan<byte> segment, Ipv6Address source, Ipv6Address destination, out UdpHeader header)
    {
        header = default;
        if (segment.Length < SIZE)
            return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(segment[4..]);
        if (length < SIZE || length > segment.Length)
            return false;
        var checksum = BinaryPrimitives.ReadUInt16BigEndian(segment[6..]);
        if (checksum == 0 && !destination.IsMapped)
            return false;
        if (checksum != 0 &&
            InternetChecksum.Finish(InternetChecksum.Add(segment[..length], InternetChecksum.PseudoHeader(source, destination, Ipv4Header.UDP, length))) != 0)
            return false;
        header = new UdpHeader(BinaryPrimitives.ReadUInt16BigEndian(segment), BinaryPrimitives.ReadUInt16BigEndian(segment[2..]), length);
        return true;
    }

    /// <summary>
    /// Writes a datagram's header before its payload, which the target already holds after SIZE bytes, and the checksum.
    /// </summary>
    /// <param name="datagram">The header's SIZE bytes followed by the payload.</param>
    /// <param name="source">The source address.</param>
    /// <param name="destination">The destination address.</param>
    /// <param name="sourcePort">The source port.</param>
    /// <param name="destinationPort">The destination port.</param>
    public static void Write(Span<byte> datagram, Ipv4Address source, Ipv4Address destination, ushort sourcePort, ushort destinationPort) =>
        Write(datagram, Ipv6Address.Map(source), Ipv6Address.Map(destination), sourcePort, destinationPort);

    /// <inheritdoc cref="Write(Span{byte}, Ipv4Address, Ipv4Address, ushort, ushort)" />
    public static void Write(Span<byte> datagram, Ipv6Address source, Ipv6Address destination, ushort sourcePort, ushort destinationPort)
    {
        BinaryPrimitives.WriteUInt16BigEndian(datagram, sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram[2..], destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram[4..], (ushort)datagram.Length);
        BinaryPrimitives.WriteUInt16BigEndian(datagram[6..], 0);
        var checksum = InternetChecksum.Finish(InternetChecksum.Add(datagram,
            InternetChecksum.PseudoHeader(source, destination, Ipv4Header.UDP, datagram.Length)));
        BinaryPrimitives.WriteUInt16BigEndian(datagram[6..], checksum == 0 ? (ushort)0xFFFF : checksum);
    }

    #endregion
}
