using System;
using System.Collections.Generic;
using System.Globalization;

namespace OutWit.OS.Display;

/// <summary>
/// A monospaced bitmap font read from its BDF (plan step C2.2): every glyph placed into a cell of the font's bounding
/// box by its own box and the font's ascent, one bit a pixel, the rows packed as the BDF packs them. The display draws
/// cells with it; the host renders the same frame to judge the display. A code point without a glyph draws the font's
/// default character, or nothing when the font names none.
/// </summary>
public sealed class BitmapFont
{
    #region Constants

    private const int MAX_WIDTH = 32;

    private const int MAX_HEIGHT = 64;

    #endregion

    #region Fields

    private readonly Dictionary<int, byte[]> m_glyphs;
    private readonly byte[] m_blank;
    private readonly int m_default;

    #endregion

    #region Constructors

    private BitmapFont(int width, int height, int ascent, Dictionary<int, byte[]> glyphs, int defaultCharacter)
    {
        Width = width;
        Height = height;
        Ascent = ascent;
        m_glyphs = glyphs;
        m_default = defaultCharacter;
        m_blank = new byte[height * BytesPerRow];
    }

    #endregion

    #region Functions

    /// <summary>
    /// Parses a BDF (version 2.1) of a monospaced font: FONTBOUNDINGBOX gives the cell, FONT_ASCENT the baseline (the
    /// box's own when the property is absent), DEFAULT_CHAR the fallback, each STARTCHAR with an ENCODING its glyph.
    /// </summary>
    /// <param name="text">The BDF's text.</param>
    /// <returns>The font.</returns>
    /// <exception cref="FormatException">The text is not a BDF this reader serves.</exception>
    public static BitmapFont ParseBdf(string text)
    {
        var lines = text.Split('\n');
        var width = 0;
        var height = 0;
        var boxY = 0;
        int? ascent = null;
        var defaultCharacter = -1;
        var glyphs = new Dictionary<int, byte[]>();
        var i = 0;
        if (lines.Length == 0 || !lines[0].TrimEnd('\r').StartsWith("STARTFONT ", StringComparison.Ordinal))
            throw new FormatException("Not a BDF: no STARTFONT.");
        for (; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("FONTBOUNDINGBOX ", StringComparison.Ordinal))
            {
                var box = Numbers(line, 4);
                (width, height, boxY) = (box[0], box[1], box[3]);
            }
            else if (line.StartsWith("FONT_ASCENT ", StringComparison.Ordinal))
                ascent = Numbers(line, 1)[0];
            else if (line.StartsWith("DEFAULT_CHAR ", StringComparison.Ordinal))
                defaultCharacter = Numbers(line, 1)[0];
            else if (line.StartsWith("CHARS ", StringComparison.Ordinal))
                break;
        }
        if (width is < 1 or > MAX_WIDTH || height is < 1 or > MAX_HEIGHT)
            throw new FormatException($"The BDF's bounding box {width} by {height} is not a cell this reader draws.");
        var baseline = ascent ?? height + boxY;
        var bytesPerRow = (width + 7) / 8;
        for (i++; i < lines.Length; i++)
        {
            if (!lines[i].TrimEnd('\r').StartsWith("STARTCHAR ", StringComparison.Ordinal))
                continue;
            var encoding = -1;
            var box = new int[4];
            var rows = new List<string>();
            for (i++; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line == "ENDCHAR")
                    break;
                if (line.StartsWith("ENCODING ", StringComparison.Ordinal))
                    encoding = Numbers(line, 1)[0];
                else if (line.StartsWith("BBX ", StringComparison.Ordinal))
                    box = Numbers(line, 4);
                else if (line == "BITMAP")
                {
                    for (i++; i < lines.Length && lines[i].TrimEnd('\r') != "ENDCHAR"; i++)
                        rows.Add(lines[i].TrimEnd('\r').Trim());
                    break;
                }
            }
            if (encoding < 0)
                continue;
            var (glyphWidth, glyphHeight, offsetX, offsetY) = (box[0], box[1], box[2], box[3]);
            if (glyphWidth < 0 || glyphHeight < 0 || rows.Count != glyphHeight)
                throw new FormatException($"Glyph {encoding} has {rows.Count} rows for a box of {glyphWidth} by {glyphHeight}.");
            var glyph = new byte[height * bytesPerRow];
            var top = baseline - (offsetY + glyphHeight); // the glyph's first row within the cell
            var glyphBytes = (glyphWidth + 7) / 8;
            for (var row = 0; row < glyphHeight; row++)
            {
                if (rows[row].Length < glyphBytes * 2)
                    throw new FormatException($"Glyph {encoding} has a short bitmap row.");
                var y = top + row;
                if (y < 0 || y >= height)
                    continue;
                for (var column = 0; column < glyphWidth; column++)
                {
                    var nibble = int.Parse(rows[row].AsSpan(column / 4, 1), NumberStyles.HexNumber);
                    if ((nibble & (8 >> (column % 4))) == 0)
                        continue;
                    var x = offsetX + column;
                    if (x >= 0 && x < width)
                        glyph[y * bytesPerRow + x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }
            glyphs[encoding] = glyph;
        }
        if (glyphs.Count == 0)
            throw new FormatException("The BDF holds no encoded glyph.");
        return new BitmapFont(width, height, baseline, glyphs, defaultCharacter);
    }

    /// <summary>
    /// The glyph of a code point: Height rows of BytesPerRow bytes, the leftmost pixel the high bit; the default
    /// character's when the font has none for it, blank when it has no default either.
    /// </summary>
    /// <param name="codePoint">The character.</param>
    /// <returns>The rows.</returns>
    public ReadOnlySpan<byte> Glyph(int codePoint) =>
        m_glyphs.TryGetValue(codePoint, out var glyph) ? glyph : m_glyphs.TryGetValue(m_default, out var fallback) ? fallback : m_blank;

    /// <summary>
    /// Whether the font has a glyph of its own for the code point.
    /// </summary>
    /// <param name="codePoint">The character.</param>
    public bool Has(int codePoint) => m_glyphs.ContainsKey(codePoint);

    /// <summary>
    /// Whether a pixel of a glyph is set.
    /// </summary>
    /// <param name="glyph">The glyph, as <see cref="Glyph"/> gives it.</param>
    /// <param name="x">The column within the cell.</param>
    /// <param name="y">The row within the cell.</param>
    public bool Pixel(ReadOnlySpan<byte> glyph, int x, int y) => (glyph[y * BytesPerRow + x / 8] & (0x80 >> (x % 8))) != 0;

    private static int[] Numbers(string line, int count)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < count + 1)
            throw new FormatException($"A BDF line lacks its numbers: {line}");
        var numbers = new int[count];
        for (var i = 0; i < count; i++)
            if (!int.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
                throw new FormatException($"A BDF line has a number that is none: {line}");
        return numbers;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The cell's width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// The cell's height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// The baseline's row from the cell's top.
    /// </summary>
    public int Ascent { get; }

    /// <summary>
    /// Bytes of one row of a glyph.
    /// </summary>
    public int BytesPerRow => (Width + 7) / 8;

    /// <summary>
    /// The glyphs the font holds.
    /// </summary>
    public int Count => m_glyphs.Count;

    #endregion
}
