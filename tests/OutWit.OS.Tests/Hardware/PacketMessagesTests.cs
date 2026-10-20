using System;
using System.Collections.Generic;
using OutWit.OS.Hardware;

namespace OutWit.OS.Tests.Hardware;

/// <summary>
/// The packet protocol's codec (witos/packet.h, plan step W1.1): requests and replies round-trip, a request is checked
/// against the window, and packed frames are packed and checked whole.
/// </summary>
[TestFixture]
public sealed class PacketMessagesTests
{
    #region Tests

    [Test]
    public void RequestsAndRepliesRoundTripTest()
    {
        var request = new PacketRequest(PacketMessages.SEND, 0, 64, 8, 2);
        var bytes = PacketMessages.Encode(request);
        Assert.That(bytes, Has.Length.EqualTo(PacketMessages.REQUEST_SIZE));
        Assert.That(PacketMessages.DecodeRequest(bytes), Is.EqualTo(request));
        var reply = new PacketReply(PacketMessages.INFO, 0, Link: PacketMessages.LINK_UP, Address: 0x5634_1200_5452UL, Mtu: 1500);
        var replyBytes = PacketMessages.Encode(reply);
        Assert.That(replyBytes, Has.Length.EqualTo(PacketMessages.REPLY_SIZE));
        Assert.That(PacketMessages.DecodeReply(replyBytes), Is.EqualTo(reply));
        Assert.That(replyBytes.AsSpan(32, 6).ToArray(), Is.EqualTo(new byte[] { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 }),
            "the address's first byte first");
        Assert.Throws<FormatException>(() => PacketMessages.DecodeRequest(new byte[32]));
        Assert.Throws<FormatException>(() => PacketMessages.DecodeReply(new byte[PacketMessages.REQUEST_SIZE]));
        bytes[36] = 1;
        Assert.Throws<FormatException>(() => PacketMessages.DecodeRequest(bytes), "a reserved word that is not zero");
    }

    [Test]
    public void RequestsAreCheckedAgainstTheWindowTest()
    {
        const int window = 4 * 4096;
        Assert.That(PacketMessages.Check(new PacketRequest(PacketMessages.ATTACH, 0, window), 0), Is.Zero);
        Assert.That(PacketMessages.Check(new PacketRequest(PacketMessages.ATTACH, 0, 4000), 0), Is.EqualTo(PacketMessages.INVALID),
            "a window of no whole pages");
        Assert.That(PacketMessages.Check(new PacketRequest(PacketMessages.ATTACH, 0, PacketMessages.WINDOW_BYTES + 4096), 0),
            Is.EqualTo(PacketMessages.INVALID), "a window beyond the largest");
        Assert.That(PacketMessages.Check(new PacketRequest(PacketMessages.INFO, 0, 0), 0), Is.Zero);
        Assert.That(PacketMessages.Check(new PacketRequest(PacketMessages.SEND, 0, 64, 0, 1), window), Is.Zero);
        Assert.That(PacketMessages.Check(new PacketRequest(PacketMessages.SEND, 0, 64, 0, 0), window), Is.EqualTo(PacketMessages.INVALID),
            "a SEND of no frame");
        Assert.That(PacketMessages.Check(new PacketRequest(PacketMessages.SEND, 0, 64, 4, 1), window), Is.EqualTo(PacketMessages.INVALID),
            "an offset that is no multiple of 8");
        Assert.That(PacketMessages.Check(new PacketRequest(PacketMessages.RECEIVE, PacketMessages.NONBLOCK, 8192, 8192), window), Is.Zero);
        Assert.That(PacketMessages.Check(new PacketRequest(PacketMessages.RECEIVE, 0, 8192, 8200), window), Is.EqualTo(PacketMessages.INVALID),
            "frames beyond the window");
        Assert.That(PacketMessages.Check(new PacketRequest(PacketMessages.RECEIVE, 2, 64), window), Is.EqualTo(PacketMessages.INVALID),
            "an unknown flag");
        Assert.That(PacketMessages.Check(new PacketRequest(9, 0, 64), window), Is.EqualTo(PacketMessages.UNKNOWN_OPERATION));
    }

    [Test]
    public void FramesArePackedAndCheckedWholeTest()
    {
        var bytes = new byte[256];
        var first = new byte[42];
        var second = new byte[60];
        first[0] = 0xAA;
        second[59] = 0xBB;
        var offset = PacketMessages.Pack(bytes, first);
        Assert.That(offset, Is.EqualTo(56), "a header of 8 and 42 bytes, rounded up to 8");
        offset += PacketMessages.Pack(bytes.AsSpan(offset), second);
        var frames = new List<(int Offset, int Length)>();
        Assert.That(PacketMessages.Unpack(bytes.AsSpan(0, offset), 2, 1500, frames), Is.Zero);
        Assert.That(frames, Is.EqualTo(new[] { (8, 42), (64, 60) }));
        Assert.That(bytes[frames[0].Offset], Is.EqualTo(0xAA));
        Assert.That(bytes[frames[1].Offset + 59], Is.EqualTo(0xBB));
        Assert.That(PacketMessages.Unpack(bytes.AsSpan(0, offset), 3, 1500, frames), Is.EqualTo(PacketMessages.INVALID), "a frame missing");
        Assert.That(PacketMessages.Unpack(bytes.AsSpan(0, offset), 1, 1500, frames), Is.EqualTo(PacketMessages.INVALID), "bytes left over");
        var tiny = new byte[16];
        PacketMessages.Pack(tiny, new byte[8]);
        Assert.That(PacketMessages.Unpack(tiny, 1, 1500, frames), Is.EqualTo(PacketMessages.FRAME_SIZE), "a frame shorter than its header");
        var large = new byte[PacketMessages.PackedBytes(1515)];
        PacketMessages.Pack(large, new byte[1515]);
        Assert.That(PacketMessages.Unpack(large, 1, 1500, frames), Is.EqualTo(PacketMessages.FRAME_SIZE), "a frame beyond the MTU");
    }

    #endregion
}
