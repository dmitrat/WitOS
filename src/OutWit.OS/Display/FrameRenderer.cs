using System;
using System.Buffers.Binary;

namespace OutWit.OS.Display;

/// <summary>
/// Where a frame's pixels go: the framebuffer's device windows in the display service, a byte array on the host that
/// renders the frame it expects (plan step C2.2). One aligned 32-bit store a pixel, since device memory forbids wider
/// or unaligned stores on every ISA.
/// </summary>
public interface IFrameSink
{
    /// <summary>
    /// Stores a pixel's word.
    /// </summary>
    /// <param name="offset">Its byte offset in the framebuffer.</param>
    /// <param name="value">The word, in the framebuffer's format.</param>
    void Write32(long offset, uint value);
}

/// <summary>
/// A frame in memory: what the host renders to judge the display, and what tests render.
/// </summary>
public sealed class MemoryFrame : IFrameSink
{
    #region Constructors

    /// <summary>
    /// A black frame of the geometry's bytes.
    /// </summary>
    /// <param name="geometry">The framebuffer's geometry.</param>
    public MemoryFrame(FramebufferGeometry geometry)
    {
        Geometry = geometry;
        Bytes = new byte[geometry.Bytes];
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    public void Write32(long offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan((int)offset), value);

    /// <summary>
    /// The colour of a pixel.
    /// </summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <returns>Red, green and blue.</returns>
    public (byte Red, byte Green, byte Blue) Pixel(int x, int y) =>
        FrameRenderer.Decode(BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan((y * Geometry.Stride + x) * FramebufferGeometry.PIXEL_BYTES)),
            Geometry.Format);

    #endregion

    #region Properties

    /// <summary>
    /// The geometry.
    /// </summary>
    public FramebufferGeometry Geometry { get; }

    /// <summary>
    /// The frame's bytes, in the framebuffer's layout.
    /// </summary>
    public byte[] Bytes { get; }

    #endregion
}

/// <summary>
/// Draws a terminal screen into a framebuffer as cells of a bitmap font (plan step C2.2): white on black by default,
/// the cell's colours of <see cref="TerminalPalette"/> when SGR set them (C2.6; bold brightens a dark colour, inverse
/// trades the colours, underline lights the cell's bottom row), the cursor an inverted cell while it is shown, a
/// character the font lacks a hollow box. A cell is drawn when its character, its look or the cursor's presence changed
/// since the last frame, and of a drawn cell only the pixels that differ from what the framebuffer shows are stored
/// (C2.3): the renderer keeps a word for every pixel of the framebuffer, since every store goes through device memory,
/// so a scroll — every cell of the screen changed — costs the pixels that changed, not the cells. The same renderer
/// draws the display in the service and the expected frame on the host.
/// </summary>
public sealed class FrameRenderer
{
    #region Constants

    private const char UNKNOWN = '\0';

    /// <summary>
    /// The text's colour.
    /// </summary>
    public static readonly (byte Red, byte Green, byte Blue) FOREGROUND = (255, 255, 255);

    /// <summary>
    /// The background's colour.
    /// </summary>
    public static readonly (byte Red, byte Green, byte Blue) BACKGROUND = (0, 0, 0);

    #endregion

    #region Fields

    private readonly FramebufferGeometry m_geometry;
    private readonly BitmapFont m_font;
    private readonly IFrameSink m_sink;
    private readonly uint m_foreground;
    private readonly uint m_background;
    private readonly uint[] m_palette;
    private readonly byte[] m_missing;
    private readonly char[] m_shadow;
    private readonly TerminalStyle[] m_shadowStyles;
    private readonly uint[] m_pixels;
    private int m_cursorRow = -1;
    private int m_cursorColumn = -1;
    private bool m_cleared;
    private long m_scrollsSeen;

    #endregion

    #region Constructors

    /// <summary>
    /// A renderer of a framebuffer with a font: as many whole cells as fit.
    /// </summary>
    /// <param name="geometry">The framebuffer's geometry.</param>
    /// <param name="font">The font.</param>
    /// <param name="sink">Where the pixels go.</param>
    /// <exception cref="ArgumentException">Not one cell fits.</exception>
    public FrameRenderer(FramebufferGeometry geometry, BitmapFont font, IFrameSink sink)
    {
        m_geometry = geometry;
        m_font = font;
        m_sink = sink;
        Columns = geometry.Width / font.Width;
        Rows = geometry.Height / font.Height;
        if (Columns < 1 || Rows < 1)
            throw new ArgumentException($"A {geometry.Width} by {geometry.Height} framebuffer holds no cell of {font.Width} by {font.Height}.", nameof(font));
        m_foreground = TestPattern.Encode(FOREGROUND, geometry.Format);
        m_background = TestPattern.Encode(BACKGROUND, geometry.Format);
        m_palette = new uint[TerminalPalette.COLORS.Length];
        for (var i = 0; i < m_palette.Length; i++)
            m_palette[i] = TestPattern.Encode(TerminalPalette.COLORS[i], geometry.Format);
        m_missing = Box(font);
        m_shadow = new char[Rows * Columns];
        Array.Fill(m_shadow, UNKNOWN);
        m_shadowStyles = new TerminalStyle[Rows * Columns];
        Array.Fill(m_shadowStyles, TerminalStyle.Default);
        m_pixels = new uint[geometry.Stride * geometry.Height];
    }

    #endregion

    #region Functions

    /// <summary>
    /// The colour of a pixel's word.
    /// </summary>
    /// <param name="value">The word.</param>
    /// <param name="format">The framebuffer's format.</param>
    /// <returns>Red, green and blue.</returns>
    public static (byte Red, byte Green, byte Blue) Decode(uint value, uint format) => format == FramebufferGeometry.BGRX
        ? ((byte)(value >> 16), (byte)(value >> 8), (byte)value)
        : ((byte)value, (byte)(value >> 8), (byte)(value >> 16));

    /// <summary>
    /// Paints the whole framebuffer black, the margins beyond the cells too, and forgets what was drawn. Its stores are
    /// not counted among <see cref="PixelsStored"/>.
    /// </summary>
    public void Clear()
    {
        for (var i = 0; i < m_pixels.Length; i++)
            m_sink.Write32((long)i * FramebufferGeometry.PIXEL_BYTES, m_background);
        Array.Fill(m_pixels, m_background);
        Array.Fill(m_shadow, ' ');
        Array.Fill(m_shadowStyles, TerminalStyle.Default);
        m_cursorRow = -1;
        m_cursorColumn = -1;
        m_cleared = true;
    }

    /// <summary>
    /// Draws the screen: every cell whose character or look changed, and the cells the cursor left and entered; of each,
    /// the pixels that differ from the framebuffer. Clears the framebuffer first when nothing cleared it yet.
    /// </summary>
    /// <param name="screen">The screen, of the renderer's columns and rows.</param>
    /// <exception cref="ArgumentException">The screen has another size.</exception>
    public void Render(TerminalScreen screen)
    {
        if (screen.Columns != Columns || screen.Rows != Rows)
            throw new ArgumentException($"A screen of {screen.Columns} by {screen.Rows} on a display of {Columns} by {Rows}.", nameof(screen));
        if (!m_cleared)
            Clear();
        var scrolled = screen.Scrolls - m_scrollsSeen;
        m_scrollsSeen = screen.Scrolls;
        RowMovePixels += scrolled * (Rows - 1) * (long)Columns * CellPixels;
        var cursorRow = screen.CursorVisible ? screen.Row : -1;
        var cursorColumn = screen.CursorVisible ? screen.Column : -1;
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var c = screen[row, column];
                var style = screen.Style(row, column);
                var cursor = row == cursorRow && column == cursorColumn;
                var hadCursor = row == m_cursorRow && column == m_cursorColumn;
                var index = row * Columns + column;
                if (c == m_shadow[index] && style == m_shadowStyles[index] && cursor == hadCursor)
                    continue;
                Draw(row, column, c, style, cursor);
                m_shadow[index] = c;
                m_shadowStyles[index] = style;
            }
        }
        m_cursorRow = cursorRow;
        m_cursorColumn = cursorColumn;
    }

    // A hollow box inside the cell, the glyph of a character the font lacks.
    private static byte[] Box(BitmapFont font)
    {
        var glyph = new byte[font.Height * font.BytesPerRow];
        int left = 1, right = font.Width - 2, top = 2, bottom = font.Height - 3;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                if (y == top || y == bottom || x == left || x == right)
                    glyph[y * font.BytesPerRow + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        return glyph;
    }

    // One cell: the glyph's pixels in the cell's foreground on its background, the two traded by inverse and again by
    // the cursor; a pixel is stored only when the framebuffer shows another value there. A blank cell, and the second
    // cell of a wide character, is the background whatever the font gives a space, since Clear leaves every cell blank
    // without drawing one.
    private void Draw(int row, int column, char c, TerminalStyle style, bool cursor)
    {
        var glyph = c is ' ' or TerminalScreen.WIDE_TAIL ? ReadOnlySpan<byte>.Empty : m_font.Has(c) ? m_font.Glyph(c) : m_missing;
        var foreground = style.Foreground < 0 ? m_foreground : m_palette[style.Bold && style.Foreground < 8 ? style.Foreground + 8 : style.Foreground];
        var background = style.Background < 0 ? m_background : m_palette[style.Background];
        var inverted = style.Inverse != cursor;
        var on = inverted ? background : foreground;
        var off = inverted ? foreground : background;
        var underline = style.Underline ? m_font.Height - 1 : -1;
        CellsDrawn++;
        for (var y = 0; y < m_font.Height; y++)
        {
            var index = (row * m_font.Height + y) * m_geometry.Stride + column * m_font.Width;
            for (var x = 0; x < m_font.Width; x++, index++)
            {
                var value = y == underline || (!glyph.IsEmpty && m_font.Pixel(glyph, x, y)) ? on : off;
                if (m_pixels[index] == value)
                    continue;
                m_pixels[index] = value;
                m_sink.Write32((long)index * FramebufferGeometry.PIXEL_BYTES, value);
                PixelsStored++;
            }
        }
    }

    #endregion

    #region Properties

    /// <summary>
    /// The cells across.
    /// </summary>
    public int Columns { get; }

    /// <summary>
    /// The cells down.
    /// </summary>
    public int Rows { get; }

    /// <summary>
    /// The pixels of one cell: the font's width by its height.
    /// </summary>
    public int CellPixels => m_font.Width * m_font.Height;

    /// <summary>
    /// The cells drawn so far: each whose character, look or cursor changed between two frames.
    /// </summary>
    public long CellsDrawn { get; private set; }

    /// <summary>
    /// The pixels stored into the sink by the cells drawn so far, each a pixel that differed from what the framebuffer
    /// showed; at most <see cref="CellsDrawn"/> times <see cref="CellPixels"/>.
    /// </summary>
    public long PixelsStored { get; private set; }

    /// <summary>
    /// The pixels a renderer that scrolled by moving the framebuffer's rows would have stored so far: for each line the
    /// screen scrolled, every pixel of the rows that moved (C2.3; the cells of the new row are drawn either way). Under
    /// an emulator a load from device memory costs what a store costs, so the move would be twice as many accesses as
    /// stores, and the display service reports both counts for the comparison.
    /// </summary>
    public long RowMovePixels { get; private set; }

    #endregion
}
