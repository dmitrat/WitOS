using System;
using OutWit.OS.Display;

namespace OutWit.OS.Tests.Display;

/// <summary>
/// The BDF reader (plan step C2.2): glyphs placed into the font's cell by their boxes and the ascent, the default
/// character for what the font lacks, and the refusals.
/// </summary>
[TestFixture]
public sealed class BitmapFontTests
{
    #region Constants

    /// <summary>
    /// A font of two glyphs in Spleen's 8 by 16 cell: 'A' over the whole cell, '?' in a 4 by 4 box offset by 2 on the baseline.
    /// </summary>
    public const string SAMPLE =
        "STARTFONT 2.1\nFONT -test-sample-medium-r-normal--16-160-72-72-C-80-ISO10646-1\nSIZE 16 72 72\nFONTBOUNDINGBOX 8 16 0 -4\n" +
        "STARTPROPERTIES 2\nFONT_ASCENT 12\nDEFAULT_CHAR 63\nENDPROPERTIES\nCHARS 2\n" +
        "STARTCHAR A\nENCODING 65\nSWIDTH 500 0\nDWIDTH 8 0\nBBX 8 16 0 -4\nBITMAP\n00\n18\n24\n42\n42\n7E\n42\n42\n42\n00\n00\n00\n00\n00\n00\n00\nENDCHAR\n" +
        "STARTCHAR question\nENCODING 63\nSWIDTH 500 0\nDWIDTH 8 0\nBBX 4 4 2 0\nBITMAP\nF0\n90\n90\nF0\nENDCHAR\nENDFONT\n";

    #endregion

    #region Tests

    [Test]
    public void CellAndGlyphsAreReadTest()
    {
        var font = BitmapFont.ParseBdf(SAMPLE);
        Assert.That((font.Width, font.Height, font.Ascent, font.Count, font.BytesPerRow), Is.EqualTo((8, 16, 12, 2, 1)));
        var a = font.Glyph('A');
        Assert.That(a.Length, Is.EqualTo(16));
        Assert.That(a[1], Is.EqualTo(0x18));
        Assert.That(a[5], Is.EqualTo(0x7E));
        Assert.That(font.Pixel(a, 3, 1), Is.True);
        Assert.That(font.Pixel(a, 0, 1), Is.False);
        Assert.That(font.Has('A') && font.Has('?') && !font.Has('Z'), Is.True);
    }

    [Test]
    public void SmallBoxIsPlacedByOffsetAndAscentTest()
    {
        var font = BitmapFont.ParseBdf(SAMPLE);
        var question = font.Glyph('?');
        // Rows 8 to 11 of the cell (the ascent 12 less the box's height 4 and its y offset 0), columns 2 to 5.
        Assert.That(question[7], Is.EqualTo(0));
        Assert.That(question[8], Is.EqualTo(0b0011_1100));
        Assert.That(question[9], Is.EqualTo(0b0010_0100));
        Assert.That(question[11], Is.EqualTo(0b0011_1100));
        Assert.That(question[12], Is.EqualTo(0));
    }

    [Test]
    public void MissingGlyphDrawsTheDefaultCharacterTest()
    {
        var font = BitmapFont.ParseBdf(SAMPLE);
        Assert.That(font.Glyph('Z').ToArray(), Is.EqualTo(font.Glyph('?').ToArray()));
        var noDefault = BitmapFont.ParseBdf(SAMPLE.Replace("DEFAULT_CHAR 63\n", ""));
        Assert.That(noDefault.Glyph('Z').ToArray(), Is.All.EqualTo(0));
    }

    [Test]
    public void MalformedFontsAreRefusedTest()
    {
        Assert.Throws<FormatException>(() => BitmapFont.ParseBdf("not a font"));
        Assert.Throws<FormatException>(() => BitmapFont.ParseBdf(SAMPLE.Replace("FONTBOUNDINGBOX 8 16 0 -4", "FONTBOUNDINGBOX 0 16 0 -4")));
        Assert.Throws<FormatException>(() => BitmapFont.ParseBdf(SAMPLE.Replace("BBX 4 4 2 0", "BBX 4 5 2 0")));
        Assert.Throws<FormatException>(() => BitmapFont.ParseBdf(SAMPLE.Replace("ENCODING 65", "ENCODING -1").Replace("ENCODING 63", "ENCODING -1")),
            "a font without an encoded glyph");
    }

    #endregion
}
