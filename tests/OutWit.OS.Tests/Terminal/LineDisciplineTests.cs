using System.Text;
using OutWit.OS.Terminal.Server;
using OutWit.OS.Terminals;

namespace OutWit.OS.Tests.Terminal;

/// <summary>
/// The line discipline (plan step N4.3): canonical editing with echo, the signal characters, raw reads, output
/// processing and the attribute changes, as Linux's N_TTY behaves.
/// </summary>
[TestFixture]
public sealed class LineDisciplineTests
{
    #region Tests

    [Test]
    public void CanonicalLineIsDeliveredWholeAndEchoedTest()
    {
        var discipline = new LineDiscipline();
        var (echo, signals) = discipline.Receive("hel"u8);
        Assert.That(Encoding.ASCII.GetString(echo), Is.EqualTo("hel"));
        Assert.That(signals, Is.Empty);
        Assert.That(discipline.InputReady, Is.False);
        Assert.That(discipline.TryRead(100, out _), Is.False);
        (echo, _) = discipline.Receive("lo\r"u8); // ICRNL: the return ends the line as a newline
        Assert.That(Encoding.ASCII.GetString(echo), Is.EqualTo("lo\n"));
        Assert.That(discipline.InputReady, Is.True);
        Assert.That(discipline.TryRead(100, out var line), Is.True);
        Assert.That(Encoding.ASCII.GetString(line), Is.EqualTo("hello\n"));
        Assert.That(discipline.InputReady, Is.False);
    }

    [Test]
    public void EraseAndKillEditTheLineTest()
    {
        var discipline = new LineDiscipline();
        var (echo, _) = discipline.Receive("abc\x7f"u8); // DEL erases c
        Assert.That(Encoding.ASCII.GetString(echo), Is.EqualTo("abc\b \b"));
        (echo, _) = discipline.Receive("d\n"u8);
        Assert.That(discipline.TryRead(100, out var line), Is.True);
        Assert.That(Encoding.ASCII.GetString(line), Is.EqualTo("abd\n"));
        (echo, _) = discipline.Receive("xyz\x15"u8); // ^U kills the line, erasing it visibly (ECHOKE)
        Assert.That(Encoding.ASCII.GetString(echo), Is.EqualTo("xyz\b \b\b \b\b \b"));
        discipline.Receive("q\n"u8);
        Assert.That(discipline.TryRead(100, out line), Is.True);
        Assert.That(Encoding.ASCII.GetString(line), Is.EqualTo("q\n"));
    }

    [Test]
    public void EndOfFileDeliversThePartialLineThenNothingTest()
    {
        var discipline = new LineDiscipline();
        discipline.Receive("ab\x04"u8); // ^D after text: the line so far
        Assert.That(discipline.TryRead(100, out var line), Is.True);
        Assert.That(Encoding.ASCII.GetString(line), Is.EqualTo("ab"));
        discipline.Receive("\x04"u8); // ^D on an empty line: the end of the file
        Assert.That(discipline.InputReady, Is.True);
        Assert.That(discipline.TryRead(100, out line), Is.True);
        Assert.That(line, Is.Empty);
    }

    [Test]
    public void LongerLineThanTheReadIsReadInPiecesTest()
    {
        var discipline = new LineDiscipline();
        discipline.Receive("0123456789\n"u8);
        Assert.That(discipline.TryRead(4, out var piece), Is.True);
        Assert.That(Encoding.ASCII.GetString(piece), Is.EqualTo("0123"));
        Assert.That(discipline.TryRead(100, out piece), Is.True);
        Assert.That(Encoding.ASCII.GetString(piece), Is.EqualTo("456789\n"));
    }

    [Test]
    public void InterruptCharacterRaisesTheSignalAndFlushesTest()
    {
        var discipline = new LineDiscipline();
        discipline.Receive("typed"u8);
        var (echo, signals) = discipline.Receive("\x03"u8);
        Assert.That(Encoding.ASCII.GetString(echo), Is.EqualTo("^C"));
        Assert.That(signals, Is.EqualTo(new[] { TerminalMessages.EVENT_INTERRUPT }));
        discipline.Receive("\n"u8);
        Assert.That(discipline.TryRead(100, out var line), Is.True);
        Assert.That(Encoding.ASCII.GetString(line), Is.EqualTo("\n"), "the typed text was flushed");
        var raw = Raw(discipline.Attributes) with { Local = discipline.Attributes.Local & ~Termios.ISIG & ~Termios.ICANON };
        discipline.SetAttributes(raw, flushInput: false);
        (_, signals) = discipline.Receive("\x03"u8);
        Assert.That(signals, Is.Empty, "without ISIG the character is a byte");
        Assert.That(discipline.TryRead(100, out line), Is.True);
        Assert.That(line, Is.EqualTo(new byte[] { 3 }));
    }

    [Test]
    public void RawModeReadsBytesAsTheyComeWithoutEchoTest()
    {
        var discipline = new LineDiscipline();
        discipline.SetAttributes(Raw(discipline.Attributes), flushInput: false);
        Assert.That(discipline.TryRead(10, out _), Is.False, "VMIN 1 waits for a byte");
        var (echo, _) = discipline.Receive("k\r"u8);
        Assert.That(echo, Is.Empty);
        Assert.That(discipline.InputReady, Is.True);
        Assert.That(discipline.TryRead(1, out var key), Is.True);
        Assert.That(key, Is.EqualTo("k"u8.ToArray()));
        Assert.That(discipline.TryRead(10, out key), Is.True);
        Assert.That(key, Is.EqualTo("\r"u8.ToArray()), "ICRNL is off in the raw attributes");
    }

    [Test]
    public void OutputProcessingMapsNewlineToReturnNewlineTest()
    {
        var discipline = new LineDiscipline();
        Assert.That(Encoding.ASCII.GetString(discipline.ProcessOutput("a\nb"u8)), Is.EqualTo("a\r\nb"));
        var attributes = discipline.Attributes with { Output = discipline.Attributes.Output & ~Termios.OPOST };
        discipline.SetAttributes(attributes, flushInput: false);
        Assert.That(Encoding.ASCII.GetString(discipline.ProcessOutput("a\nb"u8)), Is.EqualTo("a\nb"));
    }

    [Test]
    public void LeavingCanonicalModeKeepsTheLineBeingEditedTest()
    {
        var discipline = new LineDiscipline();
        discipline.Receive("par"u8);
        discipline.SetAttributes(Raw(discipline.Attributes), flushInput: false);
        Assert.That(discipline.TryRead(10, out var bytes), Is.True);
        Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo("par"));
        discipline.SetAttributes(LineDiscipline.Defaults(), flushInput: true);
        Assert.That(discipline.InputReady, Is.False);
    }

    [Test]
    public void DefaultsAreLinuxStandardTest()
    {
        var attributes = LineDiscipline.Defaults();
        Assert.That(attributes.Input, Is.EqualTo(Termios.ICRNL | Termios.IXON));
        Assert.That(attributes.Output, Is.EqualTo(Termios.OPOST | Termios.ONLCR));
        Assert.That(attributes.Local & (Termios.ICANON | Termios.ECHO | Termios.ISIG), Is.EqualTo(Termios.ICANON | Termios.ECHO | Termios.ISIG));
        Assert.That(attributes.Characters[Termios.VINTR], Is.EqualTo(3));
        Assert.That(attributes.Characters[Termios.VEOF], Is.EqualTo(4));
        Assert.That(attributes.Characters[Termios.VMIN], Is.EqualTo(1));
    }

    #endregion

    #region Tools

    // What .NET's console sets before a read: no canonical mode, no echo, no signals' translations of the input.
    private static TerminalAttributes Raw(TerminalAttributes attributes) => attributes with
    {
        Input = attributes.Input & ~(Termios.IXON | Termios.IXOFF | Termios.ICRNL | Termios.INLCR | Termios.IGNCR),
        Local = attributes.Local & ~(Termios.ECHO | Termios.ICANON | Termios.IEXTEN)
    };

    #endregion
}
