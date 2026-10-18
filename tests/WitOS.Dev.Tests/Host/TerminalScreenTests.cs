using System.Text;
using OutWit.OS.Display;
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
    public void TitlesCharsetsAndStringsPaintNothingTest()
    {
        var screen = new TerminalScreen();
        Feed(screen, "\x1B[1;31mred\x1B[0m\x1B]0;a title\x07\x1B(B\x1B=plain\x1B]2;another\x1B\\!");
        Feed(screen, "\x1B_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\x1B\\\x1BPq#0;2;0;0;0#0~~\x1B\\\x1B^private\x1B\\\x1BXstring\x07.");
        Assert.That(screen.Line(0), Is.EqualTo("redplain!."), "Attributes, charsets, OSC, APC, DCS, PM and SOS strings paint nothing");
    }

    // SGR (C2.6): the 16 colours and their bright forms, bold, underline and inverse, each until reset.
    [Test]
    public void ColorsAndAttributesTest()
    {
        var screen = new TerminalScreen();
        Feed(screen, "\x1B[1;31mR\x1B[0m\x1B[42mG\x1B[7mI\x1B[0m\x1B[4mU\x1B[24;94mB\x1B[39;49mD\x1B[mN");
        var red = screen.Style(0, 0);
        Assert.That((red.Foreground, red.Background, red.Bold), Is.EqualTo((1, -1, true)));
        Assert.That(screen.Style(0, 1).Background, Is.EqualTo(2), "a background of 40–47");
        Assert.That(screen.Style(0, 2).Inverse && screen.Style(0, 2).Background == 2, Is.True, "inverse adds to what is set");
        Assert.That(screen.Style(0, 3).Underline, Is.True);
        var bright = screen.Style(0, 4);
        Assert.That((bright.Foreground, bright.Underline), Is.EqualTo((12, false)), "90–97 are the bright colours; 24 ends underline");
        Assert.That(screen.Style(0, 5).Foreground, Is.EqualTo(-1), "39 and 49 return to the defaults");
        Assert.That(screen.Style(0, 6), Is.EqualTo(TerminalStyle.Default), "SGR without a parameter resets");
        Assert.That(screen.Line(0), Is.EqualTo("RGIUBDN"));
    }

    // 38;5 and 38;2 (and 48): xterm's 256 colours and 24-bit colours reduced to the nearest of the palette.
    [Test]
    public void ExtendedColorsAreReducedToThePaletteTest()
    {
        var screen = new TerminalScreen();
        Feed(screen, "\x1B[38;5;9ma\x1B[38;5;214mb\x1B[48;5;232mc\x1B[38;2;0;0;250md\x1B[38:5:46me\x1B[38;5mf");
        Assert.That(screen.Style(0, 0).Foreground, Is.EqualTo(9), "the first 16 of the 256 are the palette");
        Assert.That(screen.Style(0, 1).Foreground, Is.EqualTo(TerminalPalette.From256(214)));
        Assert.That(TerminalPalette.From256(214), Is.EqualTo(3), "orange (255, 175, 0) is nearest the dark yellow");
        Assert.That(screen.Style(0, 2).Background, Is.EqualTo(0), "the darkest grey is black");
        Assert.That(screen.Style(0, 3).Foreground, Is.EqualTo(4), "(0, 0, 250) is nearest the blue");
        Assert.That(screen.Style(0, 4).Foreground, Is.EqualTo(10), "colons separate sub-parameters as semicolons do");
        Assert.That(screen.Style(0, 5).Foreground, Is.EqualTo(10), "an incomplete colour changes nothing");
    }

    // An erase leaves the current background, as xterm erases (C2.6).
    [Test]
    public void EraseKeepsTheBackgroundTest()
    {
        var screen = new TerminalScreen(columns: 4, rows: 2);
        Feed(screen, "\x1B[1;44mab\x1B[2J");
        Assert.That(screen.Style(1, 3).Background, Is.EqualTo(4));
        Assert.That(screen.Style(1, 3).Bold, Is.False, "an erased cell keeps the background alone");
        Assert.That(screen.Text, Is.EqualTo(""));
    }

    // ?1049: the alternate screen, blank on entry, the main screen and the cursor back on exit.
    [Test]
    public void AlternateScreenTest()
    {
        var screen = new TerminalScreen(columns: 10, rows: 3);
        Feed(screen, "main\r\nline");
        Feed(screen, "\x1B[?1049h");
        Assert.That(screen.AlternateScreen, Is.True);
        Assert.That(screen.Text, Is.EqualTo(""), "the alternate screen starts blank");
        Feed(screen, "\x1B[3;1Hfullscreen");
        Assert.That(screen.Line(2), Is.EqualTo("fullscreen"));
        Feed(screen, "\x1B[?1049l");
        Assert.That(screen.AlternateScreen, Is.False);
        Assert.That(screen.Text, Is.EqualTo("main\nline"), "the main screen is back as it was");
        Assert.That((screen.Row, screen.Column), Is.EqualTo((1, 4)), "and the cursor where it was");
    }

    // ?25 hides and shows the cursor; ?7 turns autowrap off, and the last column takes every further character.
    [Test]
    public void CursorVisibilityAndAutowrapTest()
    {
        var screen = new TerminalScreen(columns: 4, rows: 2);
        Assert.That(screen.CursorVisible, Is.True);
        Feed(screen, "\x1B[?25l");
        Assert.That(screen.CursorVisible, Is.False);
        Feed(screen, "\x1B[?7labcdef");
        Assert.That(screen.Line(0), Is.EqualTo("abcf"), "no wrap: the last column is overwritten");
        Assert.That(screen.Line(1), Is.EqualTo(""));
        Feed(screen, "\x1B[?7h\x1B[?25h\rxyzwv");
        Assert.That((screen.Line(0), screen.Line(1)), Is.EqualTo(("xyzw", "v")), "autowrap again");
        Assert.That(screen.CursorVisible, Is.True);
    }

    // DA1, the size report and the default colours' queries are answered; DA2 and the keyboard protocols' queries are not.
    [Test]
    public void AnswersTheTerminalQueriesTest()
    {
        var screen = new TerminalScreen(columns: 100, rows: 37);
        Assert.That(Feed(screen, "\x1B[c"), Is.EqualTo("\x1B[?62;22c"));
        Assert.That(Feed(screen, "\x1B[0c"), Is.EqualTo("\x1B[?62;22c"));
        Assert.That(Feed(screen, "\x1B[18t"), Is.EqualTo("\x1B[8;37;100t"));
        Assert.That(Feed(screen, "\x1B]11;?\x07"), Is.EqualTo("\x1B]11;rgb:0000/0000/0000\x07"));
        Assert.That(Feed(screen, "\x1B]10;?\x1B\\"), Is.EqualTo("\x1B]10;rgb:ffff/ffff/ffff\x1B\\"));
        Assert.That(Feed(screen, "\x1B[>c\x1B[?u\x1B[>29u\x1B[14t\x1B[16t"), Is.EqualTo(""));
        Assert.That(screen.Text, Is.EqualTo(""));
    }

    // An East Asian wide character takes two cells, a surrogate pair is one character, and a combining mark takes none.
    [Test]
    public void WideAndCombiningCharactersTest()
    {
        var screen = new TerminalScreen(columns: 6, rows: 2);
        Feed(screen, "a中b");
        Assert.That((screen[0, 0], screen[0, 1], screen[0, 2], screen[0, 3]), Is.EqualTo(('a', '中', TerminalScreen.WIDE_TAIL, 'b')));
        Assert.That(screen.Column, Is.EqualTo(4));
        Assert.That(screen.Line(0), Is.EqualTo("a中b"));
        Feed(screen, "\r\ne\u0301\U0001F600");
        Assert.That((screen[1, 0], screen[1, 1], screen[1, 2]), Is.EqualTo(('e', '\uFFFD', TerminalScreen.WIDE_TAIL)),
            "the mark joins the e; an emoji beyond the BMP is a wide replacement character");
        Assert.That(screen.Column, Is.EqualTo(3));
        Feed(screen, "\x1B[1;2Hx");
        Assert.That(screen.Line(0), Is.EqualTo("ax b"), "a wide character cut in half loses its other half");
    }

    // ICH, DCH, ECH, IL and DL.
    [Test]
    public void InsertsAndDeletesTest()
    {
        var screen = new TerminalScreen(columns: 6, rows: 3);
        Feed(screen, "abcdef\x1B[1;3H\x1B[2@");
        Assert.That(screen.Line(0), Is.EqualTo("ab  cd"));
        Feed(screen, "\x1B[3P");
        Assert.That(screen.Line(0), Is.EqualTo("abd"));
        Feed(screen, "\x1B[1;1H\x1B[2X");
        Assert.That(screen.Line(0), Is.EqualTo("  d"));
        Feed(screen, "\x1B[2;1Hrow2\x1B[3;1Hrow3\x1B[2;1H\x1B[L");
        Assert.That(screen.Text, Is.EqualTo("  d\n\nrow2"), "IL pushes the rows below down");
        Feed(screen, "\x1B[1;1H\x1B[2M");
        Assert.That(screen.Text, Is.EqualTo("row2"), "DL pulls the rows below up");
    }

    private static string Feed(TerminalScreen screen, string text) => screen.Feed(Encoding.UTF8.GetBytes(text));

    #endregion
}
