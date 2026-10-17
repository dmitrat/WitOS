using System;
using OutWit.OS.Hardware;

namespace OutWit.OS.Tests.Hardware;

/// <summary>
/// The input protocol's codec (witos/input.h, plan step C2.2): requests, replies and events round-trip, and a request is
/// checked against the window.
/// </summary>
[TestFixture]
public sealed class InputMessagesTests
{
    #region Tests

    [Test]
    public void RequestsAndRepliesRoundTripTest()
    {
        var request = new InputRequest(InputMessages.READ, InputMessages.NONBLOCK, 512, 64);
        var bytes = InputMessages.Encode(request);
        Assert.That(bytes, Has.Length.EqualTo(InputMessages.SIZE));
        Assert.That(InputMessages.DecodeRequest(bytes), Is.EqualTo(request));
        var reply = new InputReply(InputMessages.READ, 0, 16);
        Assert.That(InputMessages.DecodeReply(InputMessages.Encode(reply)), Is.EqualTo(reply));
        Assert.Throws<FormatException>(() => InputMessages.DecodeRequest(new byte[16]));
        Assert.Throws<FormatException>(() => InputMessages.DecodeReply(new byte[InputMessages.SIZE]));
    }

    [Test]
    public void EventsRoundTripTest()
    {
        var bytes = new byte[InputMessages.EVENT_BYTES];
        var input = new InputEvent(InputMessages.KEY, 30, 1);
        InputMessages.WriteEvent(bytes, input);
        Assert.That(InputMessages.ReadEvent(bytes), Is.EqualTo(input));
    }

    [Test]
    public void RequestsAreCheckedAgainstTheWindowTest()
    {
        Assert.That(InputMessages.Check(new InputRequest(InputMessages.ATTACH, 0, InputMessages.WINDOW_BYTES), 0), Is.Zero);
        Assert.That(InputMessages.Check(new InputRequest(InputMessages.ATTACH, 0, 8192), 0), Is.EqualTo(InputMessages.INVALID));
        Assert.That(InputMessages.Check(new InputRequest(InputMessages.READ, 0, 4096), 4096), Is.Zero);
        Assert.That(InputMessages.Check(new InputRequest(InputMessages.READ, 0, 64, 4096 - 56), 4096), Is.EqualTo(InputMessages.INVALID),
            "events beyond the window");
        Assert.That(InputMessages.Check(new InputRequest(InputMessages.READ, 0, 64, 4), 4096), Is.EqualTo(InputMessages.INVALID),
            "an offset that is no event boundary");
        Assert.That(InputMessages.Check(new InputRequest(InputMessages.READ, 2, 64), 4096), Is.EqualTo(InputMessages.INVALID), "an unknown flag");
        Assert.That(InputMessages.Check(new InputRequest(7, 0, 64), 4096), Is.EqualTo(InputMessages.UNKNOWN_OPERATION));
    }

    #endregion
}
