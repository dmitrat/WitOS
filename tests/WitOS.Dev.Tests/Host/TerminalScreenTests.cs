using System.Text;
using WitOS.Dev.Host;

namespace WitOS.Dev.Tests.Host;

/// <summary>
/// The runner's terminal emulator (plan step C1.2): what the guest's bytes paint, where the cursor goes, and the
/// cursor position report a program waits for.
/// </summary>
[TestFixture]
public sealed class TerminalScreenTests
{
    #region Functions

    [Test]
    public void PrintsLinesTest()
    {
        var screen = new TerminalScreen();
        Assert.That(Feed(screen, "hello\r\nworld"), Is.EqualTo(""), "Plain text wants no reply");
        Assert.That(screen.Line(0), Is.EqualTo("hello"));
        Assert.That(screen.Line(1), Is.EqualTo("world"));
        Assert.That((screen.Row, screen.Column), Is.EqualTo((1, 5)));
        Assert.That(screen.Text, Is.EqualTo("hello\nworld"));
    }

    [Test]
    public void ReportsTheCursorPositionTest()
    {
        var screen = new TerminalScreen();
        Assert.That(Feed(screen, "PS /data> \x1B[6n"), Is.EqualTo("\x1B[1;11R"), "DSR 6 is answered with the 1-based row and column");
        Assert.That(Feed(screen, "\x1B[5n"), Is.EqualTo("\x1B[0n"), "DSR 5 is answered with OK");
        Assert.That(Feed(screen, "\x1B[3;4H\x1B[6n"), Is.EqualTo("\x1B[3;4R"), "The report follows a cursor move");
    }

    [Test]
    public void SplitSequencesTest()
    {
        var screen = new TerminalScreen();
        Assert.That(Feed(screen, "ab\x1B["), Is.EqualTo(""), "An unfinished sequence waits");
        Assert.That(Feed(screen, "6n"), Is.EqualTo("\x1B[1;3R"), "The sequence completes with the next bytes");
        var cyrillic = Encoding.UTF8.GetBytes("ж");
        Assert.That(screen.Feed(cyrillic.AsSpan(0, 1)), Is.EqualTo(""));
        Assert.That(screen.Feed(cyrillic.AsSpan(1)), Is.EqualTo(""));
        Assert.That(screen.Line(0), Is.EqualTo("abж"), "A character split between calls paints once whole");
    }

    [Test]
    public void MovesTheCursorTest()
    {
        var screen = new TerminalScreen();
        Feed(screen, "\x1B[5;10Hx");
        Assert.That(screen.Line(4), Is.EqualTo("         x"));
        Feed(screen, "\x1B[2A\x1B[3Dy"); // up two, left three
        Assert.That(screen.Line(2), Is.EqualTo("       y"));
        Feed(screen, "\x1B[G\x1B[2Bz"); // the first column, down two
        Assert.That(screen.Line(4), Is.EqualTo("z        x"));
        Feed(screen, "\x1B[s\x1B[1;1Hhome\x1B[u!");
        Assert.That(screen.Line(0), Is.EqualTo("home"));
        Assert.That(screen.Line(4), Is.EqualTo("z!       x"), "The saved cursor is restored");
        Feed(screen, "\x1B[99;99H");
        Assert.That((screen.Row, screen.Column), Is.EqualTo((23, 79)), "Moves are clamped to the screen");
    }

    [Test]
    public void ErasesTest()
    {
        var screen = new TerminalScreen();
        Feed(screen, "first line\r\nsecond line\r\nthird line");
        Feed(screen, "\x1B[2;7H\x1B[K"); // the second line from its seventh column
        Assert.That(screen.Line(1), Is.EqualTo("second"));
        Feed(screen, "\x1B[1K"); // to the cursor
        Assert.That(screen.Line(1), Is.EqualTo(""));
        Feed(screen, "\x1B[J"); // to the end of the screen
        Assert.That(screen.Line(2), Is.EqualTo(""));
        Assert.That(screen.Line(0), Is.EqualTo("first line"));
        Feed(screen, "\x1B[2J");
        Assert.That(screen.Text, Is.EqualTo(""));
    }

    [Test]
    public void ScrollsAndWrapsTest()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        Feed(screen, "0123456789abc");
        Assert.That(screen.Line(0), Is.EqualTo("0123456789"), "The tenth character stays on the first line");
        Assert.That(screen.Line(1), Is.EqualTo("abc"), "The eleventh wraps");
        Feed(screen, "\r\n\r\nlast");
        Assert.That(screen.Line(0), Is.EqualTo("abc"), "The screen scrolled up");
        Assert.That(screen.Line(2), Is.EqualTo("last"));
        Assert.That(screen.Row, Is.EqualTo(2));
    }

    [Test]
    public void IgnoresColorsModesAndTitlesTest()
    {
        var screen = new TerminalScreen();
        Feed(screen, "\x1B[?25l\x1B[1;31mred\x1B[0m\x1B]0;a title\x07\x1B[?1049h\x1B(B\x1B=plain\x1B]2;another\x1B\\!");
        Assert.That(screen.Line(0), Is.EqualTo("redplain!"), "Attributes, private modes, charsets and OSC strings paint nothing");
    }

    private static string Feed(TerminalScreen screen, string text) => screen.Feed(Encoding.UTF8.GetBytes(text));

    #endregion
}
