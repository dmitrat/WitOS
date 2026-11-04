using System;
using System.Buffers.Binary;

namespace OutWit.OS.Networking;

/// <summary>
/// The Simple Network Time Protocol's client (RFC 4330, over NTP version 4's packet of RFC 5905; plan step H7.4): the
/// request a client sends, the checks its reply must pass, and the time the reply gives at the moment it arrived. The
/// request carries a random transmit timestamp rather than the client's time, which a board without a clock has not,
/// and the server's reply must carry it back as its origin (RFC 9109's data minimization asks the same of every client).
/// </summary>
public static class Sntp
{
    #region Constants

    /// <summary>
    /// The bytes of a request and of the reply this client reads: the header without extension fields.
    /// </summary>
    public const int PACKET_BYTES = 48;

    /// <summary>
    /// NTP's port.
    /// </summary>
    public const ushort PORT = 123;

    // The seconds from NTP's epoch, 1900, to Unix's, 1970.
    private const ulong UNIX_EPOCH_SECONDS = 2_208_988_800;

    // Era 0 of NTP's 32-bit seconds ends in February 2036; a time before 2020 in a reply is read in era 1, which runs to
    // 2156, so that the client stays right across 2036 (RFC 5905, section 6).
    private const ulong ERA_PIVOT_SECONDS = UNIX_EPOCH_SECONDS + 1_577_836_800;

    private const ulong NANOSECONDS = 1_000_000_000;

    private const byte VERSION = 4;
    private const byte MODE_CLIENT = 3;
    private const byte MODE_SERVER = 4;
    private const byte LEAP_UNSYNCHRONIZED = 3;
    private const int MAXIMUM_STRATUM = 15;

    #endregion

    #region Functions

    /// <summary>
    /// A client's request: no leap indicator, version 4, mode 3, and the nonce as its transmit timestamp.
    /// </summary>
    /// <param name="nonce">A random value the reply must carry back as its origin timestamp; never zero.</param>
    /// <returns>The packet.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The nonce is zero, which a server's reply may never be checked by.</exception>
    public static byte[] Request(ulong nonce)
    {
        if (nonce == 0)
            throw new ArgumentOutOfRangeException(nameof(nonce), "A request's transmit timestamp must not be zero.");
        var packet = new byte[PACKET_BYTES];
        packet[0] = (byte)((VERSION << 3) | MODE_CLIENT);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(40), nonce);
        return packet;
    }

    /// <summary>
    /// Reads a server's reply to the request with the nonce given, as RFC 4330 section 5 checks it: a server's mode, version
    /// 3 or 4, a clock that is synchronized (no alarm, a stratum of 1 to 15: zero is a kiss-o'-death), the origin
    /// timestamp equal to the nonce, and receive and transmit timestamps.
    /// </summary>
    /// <param name="packet">The datagram the server sent.</param>
    /// <param name="nonce">The request's transmit timestamp.</param>
    /// <param name="reply">The server's times, in nanoseconds since 1970.</param>
    /// <param name="reason">Why the reply was refused, or null.</param>
    /// <returns>Whether the reply passed.</returns>
    public static bool TryRead(ReadOnlySpan<byte> packet, ulong nonce, out SntpReply reply, out string? reason)
    {
        reply = default;
        if (packet.Length < PACKET_BYTES)
            return Refuse($"{packet.Length} bytes", out reason);
        var leap = packet[0] >> 6;
        var version = (packet[0] >> 3) & 7;
        var mode = packet[0] & 7;
        var stratum = packet[1];
        if (mode != MODE_SERVER)
            return Refuse($"mode {mode}", out reason);
        if (version is < 3 or > 4)
            return Refuse($"version {version}", out reason);
        if (stratum == 0)
            return Refuse($"a kiss-o'-death \"{KissCode(packet[12..16])}\"", out reason);
        if (stratum > MAXIMUM_STRATUM || leap == LEAP_UNSYNCHRONIZED)
            return Refuse($"an unsynchronized server (stratum {stratum}, leap indicator {leap})", out reason);
        if (BinaryPrimitives.ReadUInt64BigEndian(packet[24..]) != nonce)
            return Refuse("an origin timestamp that is not the request's", out reason);
        var received = BinaryPrimitives.ReadUInt64BigEndian(packet[32..]);
        var transmitted = BinaryPrimitives.ReadUInt64BigEndian(packet[40..]);
        if (received == 0 || transmitted == 0)
            return Refuse("no receive or transmit timestamp", out reason);
        reply = new SntpReply(UnixNanoseconds(received), UnixNanoseconds(transmitted), stratum);
        if (reply.Transmitted < reply.Received)
            return Refuse("a transmit timestamp before the receive timestamp", out reason);
        reason = null;
        return true;
    }

    /// <summary>
    /// The time when the reply arrived (RFC 4330, section 5): the server's transmit time and half the round trip the
    /// network took, which is the client's own measure of the exchange less the time the server held the request.
    /// </summary>
    /// <param name="reply">The server's times.</param>
    /// <param name="roundTripNanoseconds">The client's measure from its send to the reply's arrival, on its own clock.</param>
    /// <returns>UTC at the arrival, in nanoseconds since 1970.</returns>
    public static ulong TimeAtArrival(SntpReply reply, ulong roundTripNanoseconds)
    {
        var held = reply.Transmitted - reply.Received;
        var network = roundTripNanoseconds > held ? roundTripNanoseconds - held : 0;
        return reply.Transmitted + network / 2;
    }

    /// <summary>
    /// A 64-bit NTP timestamp as nanoseconds since 1970, in era 0 or, for a time before 2020, era 1.
    /// </summary>
    /// <param name="timestamp">Seconds since 1900 in the upper half, their fraction in the lower.</param>
    /// <returns>The time.</returns>
    public static ulong UnixNanoseconds(ulong timestamp)
    {
        ulong seconds = timestamp >> 32;
        if (seconds < ERA_PIVOT_SECONDS)
            seconds += 1UL << 32;
        var fraction = ((timestamp & 0xFFFF_FFFF) * NANOSECONDS) >> 32;
        return (seconds - UNIX_EPOCH_SECONDS) * NANOSECONDS + fraction;
    }

    private static bool Refuse(string why, out string? reason)
    {
        reason = why;
        return false;
    }

    private static string KissCode(ReadOnlySpan<byte> code)
    {
        Span<char> text = stackalloc char[4];
        for (var i = 0; i < 4; i++)
            text[i] = code[i] is >= 0x20 and < 0x7F ? (char)code[i] : '?';
        return new string(text);
    }

    #endregion
}

/// <summary>
/// A server's times in an SNTP reply (plan step H7.4).
/// </summary>
/// <param name="Received">When the server received the request, in nanoseconds since 1970.</param>
/// <param name="Transmitted">When it sent the reply, in nanoseconds since 1970.</param>
/// <param name="Stratum">The server's distance from a reference clock, 1 to 15.</param>
public readonly record struct SntpReply(ulong Received, ulong Transmitted, int Stratum);
