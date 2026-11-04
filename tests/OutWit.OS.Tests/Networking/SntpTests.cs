using System;
using System.Buffers.Binary;
using OutWit.OS.Networking;

namespace OutWit.OS.Tests.Networking;

/// <summary>
/// The SNTP client (RFC 4330, plan step H7.4): the request's header and nonce, a server's reply read in era 0 and era 1,
/// the checks that refuse a reply, and the time at the reply's arrival.
/// </summary>
[TestFixture]
public sealed class SntpTests
{
    #region Constants

    private const ulong NANOSECONDS = 1_000_000_000;

    // 2026-01-01T00:00:00Z.
    private const ulong NEW_YEAR_2026 = 1_767_225_600UL * NANOSECONDS;

    #endregion

    #region Tests

    [Test]
    public void RequestTest()
    {
        var request = Sntp.Request(0x0123_4567_89AB_CDEF);
        Assert.That(request, Has.Length.EqualTo(Sntp.PACKET_BYTES));
        Assert.That(request[0], Is.EqualTo(0x23), "no leap indicator, version 4, mode 3 (client)");
        Assert.That(BinaryPrimitives.ReadUInt64BigEndian(request.AsSpan(40)), Is.EqualTo(0x0123_4567_89AB_CDEFUL), "the nonce as the transmit timestamp");
        Assert.That(request.AsSpan(1, 39).ToArray(), Is.All.EqualTo(0), "nothing else: no client time, which a board without a clock has not");
        Assert.Throws<ArgumentOutOfRangeException>(() => Sntp.Request(0));
    }

    [Test]
    public void ReplyIsReadTest()
    {
        const ulong nonce = 42;
        var reply = Reply(nonce, NEW_YEAR_2026, NEW_YEAR_2026 + 500_000_000);
        Assert.That(Sntp.TryRead(reply, nonce, out var read, out var reason), Is.True, reason);
        Assert.That(read, Is.EqualTo(new SntpReply(NEW_YEAR_2026, NEW_YEAR_2026 + 500_000_000, 2)));
        Assert.That(Sntp.TryRead(Reply(nonce, NEW_YEAR_2026, NEW_YEAR_2026, version: 3), nonce, out _, out _), Is.True, "version 3 is read too");
    }

    [Test]
    public void ErasTest()
    {
        Assert.That(Sntp.UnixNanoseconds(Timestamp(NEW_YEAR_2026)), Is.EqualTo(NEW_YEAR_2026));
        Assert.That(Sntp.UnixNanoseconds((2_208_988_800UL + 1_767_225_600UL) << 32 | 0x8000_0000), Is.EqualTo(NEW_YEAR_2026 + 500_000_000),
            "half a second's fraction");
        // 2040-01-01T00:00:00Z lies past era 0's end in 2036: its seconds since 1900 wrap to a small number.
        const ulong year2040 = 2_208_988_800UL;
        var wrapped = (year2040 + 2_208_988_800UL) - (1UL << 32);
        Assert.That(Sntp.UnixNanoseconds(wrapped << 32), Is.EqualTo(year2040 * NANOSECONDS), "era 1");
        Assert.That(Sntp.UnixNanoseconds(Timestamp(year2040 * NANOSECONDS)), Is.EqualTo(year2040 * NANOSECONDS), "the test's own encoding agrees");
    }

    [Test]
    public void RefusedRepliesTest()
    {
        const ulong nonce = 7;
        Assert.That(Sntp.TryRead(Reply(nonce, NEW_YEAR_2026, NEW_YEAR_2026)[..47], nonce, out _, out var reason), Is.False);
        Assert.That(reason, Does.Contain("47 bytes"));
        Assert.That(Sntp.TryRead(Reply(nonce, NEW_YEAR_2026, NEW_YEAR_2026, mode: 3), nonce, out _, out reason), Is.False);
        Assert.That(reason, Does.Contain("mode 3"));
        Assert.That(Sntp.TryRead(Reply(nonce, NEW_YEAR_2026, NEW_YEAR_2026, version: 2), nonce, out _, out reason), Is.False);
        Assert.That(reason, Does.Contain("version 2"));
        var kiss = Reply(nonce, NEW_YEAR_2026, NEW_YEAR_2026, stratum: 0);
        "RATE"u8.CopyTo(kiss.AsSpan(12));
        Assert.That(Sntp.TryRead(kiss, nonce, out _, out reason), Is.False);
        Assert.That(reason, Does.Contain("kiss-o'-death \"RATE\""));
        Assert.That(Sntp.TryRead(Reply(nonce, NEW_YEAR_2026, NEW_YEAR_2026, stratum: 16), nonce, out _, out reason), Is.False);
        Assert.That(reason, Does.Contain("unsynchronized"));
        Assert.That(Sntp.TryRead(Reply(nonce, NEW_YEAR_2026, NEW_YEAR_2026, leap: 3), nonce, out _, out reason), Is.False);
        Assert.That(reason, Does.Contain("unsynchronized"));
        Assert.That(Sntp.TryRead(Reply(nonce + 1, NEW_YEAR_2026, NEW_YEAR_2026), nonce, out _, out reason), Is.False, "another request's answer");
        Assert.That(reason, Does.Contain("origin"));
        Assert.That(Sntp.TryRead(Reply(nonce, 0, NEW_YEAR_2026, rawReceive: 0), nonce, out _, out reason), Is.False);
        Assert.That(reason, Does.Contain("no receive"));
        Assert.That(Sntp.TryRead(Reply(nonce, NEW_YEAR_2026 + NANOSECONDS, NEW_YEAR_2026), nonce, out _, out reason), Is.False);
        Assert.That(reason, Does.Contain("before the receive"));
    }

    [Test]
    public void TimeAtArrivalTest()
    {
        // The server held the request 10 ms; the client saw 110 ms: 50 ms each way.
        var reply = new SntpReply(NEW_YEAR_2026, NEW_YEAR_2026 + 10_000_000, 1);
        Assert.That(Sntp.TimeAtArrival(reply, 110_000_000), Is.EqualTo(NEW_YEAR_2026 + 60_000_000));
        Assert.That(Sntp.TimeAtArrival(reply, 5_000_000), Is.EqualTo(NEW_YEAR_2026 + 10_000_000), "a round trip shorter than the hold is none");
    }

    #endregion

    #region Tools

    // A server's reply, written here from RFC 5905's layout rather than by the code under test.
    private static byte[] Reply(ulong nonce, ulong received, ulong transmitted, int leap = 0, int version = 4, int mode = 4, int stratum = 2,
        ulong? rawReceive = null)
    {
        var packet = new byte[48];
        packet[0] = (byte)(leap << 6 | version << 3 | mode);
        packet[1] = (byte)stratum;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(24), nonce);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(32), rawReceive ?? Timestamp(received));
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(40), Timestamp(transmitted));
        return packet;
    }

    private static ulong Timestamp(ulong unixNanoseconds)
    {
        var seconds = (unixNanoseconds / NANOSECONDS + 2_208_988_800UL) & 0xFFFF_FFFF;
        var fraction = (unixNanoseconds % NANOSECONDS << 32) / NANOSECONDS;
        return seconds << 32 | fraction;
    }

    #endregion
}
