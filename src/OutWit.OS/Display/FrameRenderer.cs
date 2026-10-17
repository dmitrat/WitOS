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
/// Draws a terminal screen into a framebuffer as cells of a bitmap font (plan step C2.2): white on black, the cursor an
/// inverted cell, each cell redrawn only when its character or the cursor's presence changed since the last frame, since
/// a cell costs the font's pixels in 32-bit stores through device memory. The same renderer draws the display in the
/// service and the expected frame on the host.
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
    private readonly char[] m_shadow;
    private int m_cursorRow = -1;
    private int m_cursorColumn = -1;

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
        m_shadow = new char[Rows * Columns];
        Array.Fill(m_shadow, UNKNOWN);
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
    /// Paints the whole framebuffer black, the margins beyond the cells too, and forgets what was drawn.
    /// </summary>
    public void Clear()
    {
        var words = (long)m_geometry.Stride * m_geometry.Height;
        for (var i = 0L; i < words; i++)
            m_sink.Write32(i * FramebufferGeometry.PIXEL_BYTES, m_background);
        Array.Fill(m_shadow, ' ');
        m_cursorRow = -1;
        m_cursorColumn = -1;
    }

    /// <summary>
    /// Draws the screen: every cell whose character changed, and the cells the cursor left and entered.
    /// </summary>
    /// <param name="screen">The screen, of the renderer's columns and rows.</param>
    /// <exception cref="ArgumentException">The screen has another size.</exception>
    public void Render(TerminalScreen screen)
    {
        if (screen.Columns != Columns || screen.Rows != Rows)
            throw new ArgumentException($"A screen of {screen.Columns} by {screen.Rows} on a display of {Columns} by {Rows}.", nameof(screen));
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var c = screen[row, column];
                var cursor = row == screen.Row && column == screen.Column;
                var hadCursor = row == m_cursorRow && column == m_cursorColumn;
                if (c == m_shadow[row * Columns + column] && cursor == hadCursor)
                    continue;
                Draw(row, column, c, cursor);
                m_shadow[row * Columns + column] = c;
            }
        }
        m_cursorRow = screen.Row;
        m_cursorColumn = screen.Column;
    }

    // One cell: the glyph's pixels in the text's colour on the background, or inverted under the cursor.
    private void Draw(int row, int column, char c, bool inverted)
    {
        var glyph = m_font.Glyph(c);
        var on = inverted ? m_background : m_foreground;
        var off = inverted ? m_foreground : m_background;
        for (var y = 0; y < m_font.Height; y++)
        {
            var line = ((long)(row * m_font.Height + y) * m_geometry.Stride + (long)column * m_font.Width) * FramebufferGeometry.PIXEL_BYTES;
            for (var x = 0; x < m_font.Width; x++)
                m_sink.Write32(line + x * FramebufferGeometry.PIXEL_BYTES, m_font.Pixel(glyph, x, y) ? on : off);
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

    #endregion
}
