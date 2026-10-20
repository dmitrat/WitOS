using System;
using System.Buffers.Binary;

namespace OutWit.OS.Networking;

/// <summary>
/// A TCP segment's header (RFC 9293 section 3.1; plan step W1.2b), with the options the stack reads and writes: the
/// maximum segment size (RFC 9293 section 3.7.1) and window scaling (RFC 7323 section 2). Every other option is skipped.
/// </summary>
/// <param name="SourcePort">The source port.</param>
/// <param name="DestinationPort">The destination port.</param>
/// <param name="Sequence">The sequence number.</param>
/// <param name="Acknowledgment">The acknowledgment number, meaningful with ACK.</param>
/// <param name="HeaderLength">The header's bytes with its options.</param>
/// <param name="Flags">FIN, SYN, RST, PSH, ACK and URG.</param>
/// <param name="Window">The window, unscaled.</param>
/// <param name="Mss">The maximum segment size option; zero when absent.</param>
/// <param name="WindowScale">The window scale option's shift; -1 when absent.</param>
public readonly record struct TcpHeader(ushort SourcePort, ushort DestinationPort, uint Sequence, uint Acknowledgment, int HeaderLength,
    byte Flags, ushort Window, ushort Mss, int WindowScale)
{
    #region Constants

    /// <summary>
    /// A header without options.
    /// </summary>
    public const int SIZE = 20;

    /// <summary>
    /// The flags.
    /// </summary>
    public const byte FIN = 0x01;

    /// <inheritdoc cref="FIN" />
    public const byte SYN = 0x02;

    /// <inheritdoc cref="FIN" />
    public const byte RST = 0x04;

    /// <inheritdoc cref="FIN" />
    public const byte PSH = 0x08;

    /// <inheritdoc cref="FIN" />
    public const byte ACK = 0x10;

    /// <inheritdoc cref="FIN" />
    public const byte URG = 0x20;

    /// <summary>
    /// The largest window scale shift (RFC 7323 section 2.3).
    /// </summary>
    public const int MAX_SCALE = 14;

    private const byte END = 0;
    private const byte NOP = 1;
    private const byte MSS_OPTION = 2;
    private const byte SCALE_OPTION = 3;

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks a segment: its header and options within the segment and its checksum over the pseudo header.
    /// </summary>
    /// <param name="segment">The IPv4 payload.</param>
    /// <param name="source">The segment's source address.</param>
    /// <param name="destination">The segment's destination address.</param>
    /// <param name="header">The header, when the segment is valid.</param>
    /// <returns>Whether it is.</returns>
    public static bool TryRead(ReadOnlySpan<byte> segment, Ipv4Address source, Ipv4Address destination, out TcpHeader header)
    {
        header = default;
        if (segment.Length < SIZE)
            return false;
        var length = (segment[12] >> 4) * 4;
        if (length < SIZE || length > segment.Length)
            return false;
        if (InternetChecksum.Finish(InternetChecksum.Add(segment, InternetChecksum.PseudoHeader(source, destination, Ipv4Header.TCP, segment.Length))) != 0)
            return false;
        ushort mss = 0;
        var scale = -1;
        var options = segment[SIZE..length];
        for (var i = 0; i < options.Length;)
        {
            var kind = options[i];
            if (kind == END)
                break;
            if (kind == NOP)
            {
                i++;
                continue;
            }
            if (i + 1 >= options.Length || options[i + 1] < 2 || i + options[i + 1] > options.Length)
                return false;
            var size = options[i + 1];
            if (kind == MSS_OPTION && size == 4)
                mss = BinaryPrimitives.ReadUInt16BigEndian(options[(i + 2)..]);
            else if (kind == SCALE_OPTION && size == 3)
                scale = Math.Min((int)options[i + 2], MAX_SCALE);
            i += size;
        }
        header = new TcpHeader(BinaryPrimitives.ReadUInt16BigEndian(segment), BinaryPrimitives.ReadUInt16BigEndian(segment[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(segment[4..]), BinaryPrimitives.ReadUInt32BigEndian(segment[8..]), length,
            (byte)(segment[13] & 0x3F), BinaryPrimitives.ReadUInt16BigEndian(segment[14..]), mss, scale);
        return true;
    }

    /// <summary>
    /// The bytes of the options a SYN carries: the maximum segment size and, when asked, the window scale.
    /// </summary>
    /// <param name="mss">The maximum segment size.</param>
    /// <param name="scale">The window scale shift, or -1 for none.</param>
    /// <returns>The options' bytes, a multiple of four.</returns>
    public static int OptionBytes(ushort mss, int scale) => (mss != 0 ? 4 : 0) + (scale >= 0 ? 4 : 0);

    /// <summary>
    /// Writes a segment's header, with its options, before its payload, which the target already holds after the header,
    /// and the checksum.
    /// </summary>
    /// <param name="segment">The header's bytes followed by the payload.</param>
    /// <param name="source">The source address.</param>
    /// <param name="destination">The destination address.</param>
    /// <param name="sourcePort">The source port.</param>
    /// <param name="destinationPort">The destination port.</param>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="acknowledgment">The acknowledgment number.</param>
    /// <param name="flags">The flags.</param>
    /// <param name="window">The window, already scaled down.</param>
    /// <param name="mss">The maximum segment size option, or zero for none.</param>
    /// <param name="scale">The window scale option, or -1 for none.</param>
    /// <returns>The header's bytes.</returns>
    public static int Write(Span<byte> segment, Ipv4Address source, Ipv4Address destination, ushort sourcePort, ushort destinationPort,
        uint sequence, uint acknowledgment, byte flags, ushort window, ushort mss = 0, int scale = -1)
    {
        var length = SIZE + OptionBytes(mss, scale);
        BinaryPrimitives.WriteUInt16BigEndian(segment, sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(segment[2..], destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(segment[4..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(segment[8..], acknowledgment);
        segment[12] = (byte)(length / 4 << 4);
        segment[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(segment[14..], window);
        BinaryPrimitives.WriteUInt32BigEndian(segment[16..], 0); // the checksum and the urgent pointer
        var at = SIZE;
        if (mss != 0)
        {
            segment[at] = MSS_OPTION;
            segment[at + 1] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(segment[(at + 2)..], mss);
            at += 4;
        }
        if (scale >= 0)
        {
            segment[at] = NOP;
            segment[at + 1] = SCALE_OPTION;
            segment[at + 2] = 3;
            segment[at + 3] = (byte)scale;
        }
        var checksum = InternetChecksum.Finish(InternetChecksum.Add(segment,
            InternetChecksum.PseudoHeader(source, destination, Ipv4Header.TCP, segment.Length)));
        BinaryPrimitives.WriteUInt16BigEndian(segment[16..], checksum);
        return length;
    }

    /// <summary>
    /// Whether a flag is set.
    /// </summary>
    public bool Has(byte flag) => (Flags & flag) != 0;

    #endregion
}
