using System;
using OutWit.OS.Hardware;
using OutWit.OS.Terminals;

namespace OutWit.OS.Tests.Terminal;

/// <summary>
/// The terminal and serial protocols' messages (witos/terminal.h, witos/serial.h): round trips and the checks.
/// </summary>
[TestFixture]
public sealed class TerminalMessagesTests
{
    #region Tests

    [Test]
    public void TerminalRequestAndReplyRoundTripTest()
    {
        var request = new TerminalRequest(TerminalMessages.READ, TerminalMessages.NONBLOCK, 100, 4096);
        var bytes = TerminalMessages.Encode(request);
        Assert.That(bytes, Has.Length.EqualTo(TerminalMessages.SIZE));
        Assert.That(TerminalMessages.DecodeRequest(bytes), Is.EqualTo(request));
        var reply = new TerminalReply(TerminalMessages.GET_SIZE, 0, 0, TerminalMessages.INPUT_READY, 80, 24);
        Assert.That(TerminalMessages.DecodeReply(TerminalMessages.Encode(reply)), Is.EqualTo(reply));
        Assert.That(() => TerminalMessages.DecodeRequest(new byte[16]), Throws.TypeOf<FormatException>());
    }

    [Test]
    public void AttributesRoundTripTest()
    {
        var attributes = new TerminalAttributes(1, 2, 3, 4, new byte[TerminalMessages.CONTROLS]);
        attributes.Characters[Termios.VINTR] = 3;
        var window = new byte[TerminalMessages.ATTRIBUTES_SIZE];
        TerminalMessages.Encode(attributes, window);
        Assert.That(TerminalMessages.DecodeAttributes(window), Is.EqualTo(attributes));
        Assert.That(attributes with { Local = 5 }, Is.Not.EqualTo(attributes));
    }

    [Test]
    public void EventRoundTripTest()
    {
        var bytes = TerminalMessages.EncodeEvent(TerminalMessages.EVENT_INTERRUPT);
        Assert.That(bytes, Has.Length.EqualTo(TerminalMessages.EVENT_BYTES));
        Assert.That(TerminalMessages.DecodeEvent(bytes), Is.EqualTo(TerminalMessages.EVENT_INTERRUPT));
        Assert.That(TerminalMessages.DecodeRequest(TerminalMessages.EncodeDevice()).Operation, Is.EqualTo(TerminalMessages.DEVICE));
    }

    [Test]
    public void SerialRequestAndReplyRoundTripTest()
    {
        var request = new SerialRequest(SerialMessages.WRITE, 0, 12, 32768);
        Assert.That(SerialMessages.DecodeRequest(SerialMessages.Encode(request)), Is.EqualTo(request));
        var reply = new SerialReply(SerialMessages.SIZE_QUERY, 0, 0, 80, 24);
        Assert.That(SerialMessages.DecodeReply(SerialMessages.Encode(reply)), Is.EqualTo(reply));
    }

    [Test]
    public void SerialChecksTheWindowTest()
    {
        Assert.That(SerialMessages.Check(new SerialRequest(SerialMessages.ATTACH, 0, SerialMessages.WINDOW_BYTES), 0), Is.Zero);
        Assert.That(SerialMessages.Check(new SerialRequest(SerialMessages.ATTACH, 0, 100), 0), Is.EqualTo(SerialMessages.INVALID));
        Assert.That(SerialMessages.Check(new SerialRequest(SerialMessages.READ, SerialMessages.NONBLOCK, 10, 4096), 8192), Is.Zero);
        Assert.That(SerialMessages.Check(new SerialRequest(SerialMessages.READ, 0, 10, 8190), 8192), Is.EqualTo(SerialMessages.INVALID));
        Assert.That(SerialMessages.Check(new SerialRequest(SerialMessages.WRITE, SerialMessages.NONBLOCK, 10, 0), 8192), Is.EqualTo(SerialMessages.INVALID));
        Assert.That(SerialMessages.Check(new SerialRequest(9, 0, 0), 8192), Is.EqualTo(SerialMessages.UNKNOWN_OPERATION));
    }

    #endregion
}
