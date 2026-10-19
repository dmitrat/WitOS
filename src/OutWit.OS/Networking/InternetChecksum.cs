using System;
using System.Buffers.Binary;

namespace OutWit.OS.Networking;

/// <summary>
/// The Internet checksum (RFC 1071) of IPv4 headers, ICMP, UDP and TCP: the ones' complement of the ones' complement sum
/// of 16-bit words, a trailing odd byte padded with zero; UDP and TCP sum a pseudo header first (RFC 768, RFC 9293).
/// </summary>
public static class InternetChecksum
{
    #region Functions

    /// <summary>
    /// Adds bytes to a running sum, words in network order.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="sum">The sum so far.</param>
    /// <returns>The new sum, not folded.</returns>
    public static uint Add(ReadOnlySpan<byte> bytes, uint sum = 0)
    {
        var i = 0;
        for (; i + 1 < bytes.Length; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(bytes[i..]);
        if (i < bytes.Length)
            sum += (uint)bytes[i] << 8;
        return sum;
    }

    /// <summary>
    /// The sum of the IPv4 pseudo header of a transport segment.
    /// </summary>
    /// <param name="source">The source address.</param>
    /// <param name="destination">The destination address.</param>
    /// <param name="protocol">The transport protocol.</param>
    /// <param name="length">The segment's bytes.</param>
    /// <returns>The sum, not folded.</returns>
    public static uint PseudoHeader(Ipv4Address source, Ipv4Address destination, byte protocol, int length) =>
        (source.Value >> 16) + (source.Value & 0xFFFF) + (destination.Value >> 16) + (destination.Value & 0xFFFF) + protocol + (uint)length;

    /// <summary>
    /// Folds a sum into 16 bits and complements it: the checksum to write.
    /// </summary>
    /// <param name="sum">The sum.</param>
    /// <returns>The checksum.</returns>
    public static ushort Finish(uint sum)
    {
        while (sum >> 16 != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    /// <summary>
    /// The checksum of bytes alone.
    /// </summary>
    /// <param name="bytes">The bytes, their checksum field zero when it is computed, or as received when it is checked.</param>
    /// <returns>The checksum; zero for received bytes whose checksum is right.</returns>
    public static ushort Compute(ReadOnlySpan<byte> bytes) => Finish(Add(bytes));

    #endregion
}
