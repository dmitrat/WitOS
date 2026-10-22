using System;
using System.Buffers.Binary;

namespace OutWit.OS.Networking;

/// <summary>
/// The IPv6 header (RFC 8200 section 3; plan step W4.3): forty bytes, no checksum, a payload length, the next header
/// and a hop limit. The stack reads the headers it serves after the fixed one — none of the extension headers, which it
/// drops — and writes a fixed header alone.
/// </summary>
/// <param name="Source">The source.</param>
/// <param name="Destination">The destination.</param>
/// <param name="NextHeader">The protocol of the payload.</param>
/// <param name="HopLimit">The hop limit.</param>
/// <param name="PayloadLength">The payload's bytes after the header.</param>
public readonly record struct Ipv6Header(Ipv6Address Source, Ipv6Address Destination, byte NextHeader, byte HopLimit, int PayloadLength)
{
    #region Constants

    /// <summary>
    /// The header's bytes.
    /// </summary>
    public const int SIZE = 40;

    /// <summary>
    /// ICMPv6's next header.
    /// </summary>
    public const byte ICMPV6 = 58;

    /// <summary>
    /// The hop limit the stack sends with.
    /// </summary>
    public const byte DEFAULT_HOP_LIMIT = 64;

    /// <summary>
    /// The hop limit of neighbour discovery's messages, which a router never forwards (RFC 4861 section 4).
    /// </summary>
    public const byte DISCOVERY_HOP_LIMIT = 255;

    #endregion

    #region Functions

    /// <summary>
    /// Reads a header whose payload the bytes hold whole.
    /// </summary>
    /// <param name="bytes">The packet.</param>
    /// <param name="header">The header read.</param>
    /// <returns>Whether the bytes hold a complete IPv6 packet.</returns>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out Ipv6Header header)
    {
        header = default;
        if (bytes.Length < SIZE || bytes[0] >> 4 != 6)
            return false;
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        if (SIZE + payloadLength > bytes.Length)
            return false;
        header = new Ipv6Header(Ipv6Address.Read(bytes[8..]), Ipv6Address.Read(bytes[24..]), bytes[6], bytes[7], payloadLength);
        return true;
    }

    /// <summary>
    /// Writes a header before a payload.
    /// </summary>
    /// <param name="bytes">The packet, the header first.</param>
    /// <param name="source">The source.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="nextHeader">The payload's protocol.</param>
    /// <param name="payloadLength">The payload's bytes.</param>
    /// <param name="hopLimit">The hop limit.</param>
    public static void Write(Span<byte> bytes, Ipv6Address source, Ipv6Address destination, byte nextHeader, int payloadLength,
        byte hopLimit = DEFAULT_HOP_LIMIT)
    {
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 6U << 28);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[4..], (ushort)payloadLength);
        bytes[6] = nextHeader;
        bytes[7] = hopLimit;
        source.Write(bytes[8..]);
        destination.Write(bytes[24..]);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The packet's bytes, the header and its payload.
    /// </summary>
    public int TotalLength => SIZE + PayloadLength;

    #endregion
}
