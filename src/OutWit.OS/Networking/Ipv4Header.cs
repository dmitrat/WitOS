using System;
using System.Buffers.Binary;

namespace OutWit.OS.Networking;

/// <summary>
/// An IPv4 header (RFC 791, plan step W1.2a). The stack writes headers of 20 bytes with don't-fragment set and reads any
/// with options, which it skips; a fragment is recognized and left to the caller, which drops it.
/// </summary>
/// <param name="Source">The source address.</param>
/// <param name="Destination">The destination address.</param>
/// <param name="Protocol">The payload's protocol: ICMP, TCP or UDP.</param>
/// <param name="TimeToLive">The hops left.</param>
/// <param name="Identification">The datagram's identification.</param>
/// <param name="HeaderLength">The header's bytes, options included.</param>
/// <param name="TotalLength">The datagram's bytes, header included.</param>
/// <param name="IsFragment">Whether the datagram is a fragment: more fragments follow or its offset is not zero.</param>
public readonly record struct Ipv4Header(Ipv4Address Source, Ipv4Address Destination, byte Protocol, byte TimeToLive, ushort Identification,
    int HeaderLength, int TotalLength, bool IsFragment)
{
    #region Constants

    /// <summary>
    /// The bytes of a header without options.
    /// </summary>
    public const int SIZE = 20;

    /// <summary>
    /// ICMP.
    /// </summary>
    public const byte ICMP = 1;

    /// <summary>
    /// TCP.
    /// </summary>
    public const byte TCP = 6;

    /// <summary>
    /// UDP.
    /// </summary>
    public const byte UDP = 17;

    /// <summary>
    /// The time to live of the stack's datagrams.
    /// </summary>
    public const byte DEFAULT_TIME_TO_LIVE = 64;

    private const ushort DONT_FRAGMENT = 0x4000;
    private const ushort MORE_FRAGMENTS = 0x2000;
    private const ushort OFFSET_MASK = 0x1FFF;

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks a header: version 4, a header length of at least 20 bytes within the datagram, a total length
    /// within the bytes (a frame may pad them), and the header's checksum.
    /// </summary>
    /// <param name="bytes">The payload of a frame.</param>
    /// <param name="header">The header, when the bytes hold a valid one.</param>
    /// <returns>Whether they do.</returns>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out Ipv4Header header)
    {
        header = default;
        if (bytes.Length < SIZE || bytes[0] >> 4 != 4)
            return false;
        var headerLength = (bytes[0] & 0xF) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        if (headerLength < SIZE || totalLength < headerLength || totalLength > bytes.Length ||
            InternetChecksum.Compute(bytes[..headerLength]) != 0)
            return false;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        header = new Ipv4Header(Ipv4Address.Read(bytes[12..]), Ipv4Address.Read(bytes[16..]), bytes[9], bytes[8],
            BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]), headerLength, totalLength,
            (flags & MORE_FRAGMENTS) != 0 || (flags & OFFSET_MASK) != 0);
        return true;
    }

    /// <summary>
    /// Reads the header an ICMP error quotes: version 4, a header within the bytes and its checksum, while the datagram
    /// itself is cut after eight bytes of its payload.
    /// </summary>
    /// <param name="bytes">The quoted header and what follows it.</param>
    /// <param name="header">The header, when the bytes hold a valid one.</param>
    /// <returns>Whether they do.</returns>
    public static bool TryReadPrefix(ReadOnlySpan<byte> bytes, out Ipv4Header header)
    {
        header = default;
        if (bytes.Length < SIZE || bytes[0] >> 4 != 4)
            return false;
        var headerLength = (bytes[0] & 0xF) * 4;
        if (headerLength < SIZE || headerLength > bytes.Length || InternetChecksum.Compute(bytes[..headerLength]) != 0)
            return false;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        header = new Ipv4Header(Ipv4Address.Read(bytes[12..]), Ipv4Address.Read(bytes[16..]), bytes[9], bytes[8],
            BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]), headerLength, BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]),
            (flags & MORE_FRAGMENTS) != 0 || (flags & OFFSET_MASK) != 0);
        return true;
    }

    /// <summary>
    /// Writes a header of 20 bytes with don't-fragment set and its checksum.
    /// </summary>
    /// <param name="bytes">At least SIZE bytes.</param>
    /// <param name="source">The source address.</param>
    /// <param name="destination">The destination address.</param>
    /// <param name="protocol">The payload's protocol.</param>
    /// <param name="identification">The datagram's identification.</param>
    /// <param name="payloadLength">The payload's bytes.</param>
    /// <param name="timeToLive">The hops it may take.</param>
    public static void Write(Span<byte> bytes, Ipv4Address source, Ipv4Address destination, byte protocol, ushort identification,
        int payloadLength, byte timeToLive = DEFAULT_TIME_TO_LIVE)
    {
        bytes[0] = 0x45;
        bytes[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes[2..], (ushort)(SIZE + payloadLength));
        BinaryPrimitives.WriteUInt16BigEndian(bytes[4..], identification);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[6..], DONT_FRAGMENT);
        bytes[8] = timeToLive;
        bytes[9] = protocol;
        BinaryPrimitives.WriteUInt16BigEndian(bytes[10..], 0);
        source.Write(bytes[12..]);
        destination.Write(bytes[16..]);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[10..], InternetChecksum.Compute(bytes[..SIZE]));
    }

    #endregion

    #region Properties

    /// <summary>
    /// The payload's bytes.
    /// </summary>
    public int PayloadLength => TotalLength - HeaderLength;

    #endregion
}
