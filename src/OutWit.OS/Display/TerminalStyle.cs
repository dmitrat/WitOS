using System;

namespace OutWit.OS.Display;

/// <summary>
/// The look of a terminal cell (plan step C2.6): a foreground and a background of the 16-colour palette or the screen's
/// defaults, and the bold, underline and inverse attributes SGR sets. Colours of 256 or 24 bits are reduced to the
/// nearest of the 16 when SGR sets them, so a cell holds one of 17 values a colour.
/// </summary>
public readonly struct TerminalStyle : IEquatable<TerminalStyle>
{
    #region Constants

    private const int DEFAULT_COLOR = 16;
    private const int COLOR_MASK = 0x1F;
    private const int BACKGROUND_SHIFT = 5;
    private const ushort BOLD = 1 << 10;
    private const ushort UNDERLINE = 1 << 11;
    private const ushort INVERSE = 1 << 12;

    /// <summary>
    /// The default look: the screen's foreground on its background, no attribute.
    /// </summary>
    public static readonly TerminalStyle Default = new(DEFAULT_COLOR | DEFAULT_COLOR << BACKGROUND_SHIFT);

    #endregion

    #region Fields

    private readonly ushort m_value;

    #endregion

    #region Constructors

    private TerminalStyle(int value) => m_value = (ushort)value;

    #endregion

    #region Functions

    /// <summary>
    /// The same look with another foreground.
    /// </summary>
    /// <param name="color">A palette index from 0 to 15, or -1 for the default.</param>
    public TerminalStyle WithForeground(int color) => new(m_value & ~COLOR_MASK | Encode(color));

    /// <summary>
    /// The same look with another background.
    /// </summary>
    /// <param name="color">A palette index from 0 to 15, or -1 for the default.</param>
    public TerminalStyle WithBackground(int color) => new(m_value & ~(COLOR_MASK << BACKGROUND_SHIFT) | Encode(color) << BACKGROUND_SHIFT);

    /// <summary>
    /// The same look with bold set or cleared.
    /// </summary>
    public TerminalStyle WithBold(bool on) => new(on ? m_value | BOLD : m_value & ~BOLD);

    /// <summary>
    /// The same look with underline set or cleared.
    /// </summary>
    public TerminalStyle WithUnderline(bool on) => new(on ? m_value | UNDERLINE : m_value & ~UNDERLINE);

    /// <summary>
    /// The same look with inverse set or cleared.
    /// </summary>
    public TerminalStyle WithInverse(bool on) => new(on ? m_value | INVERSE : m_value & ~INVERSE);

    /// <summary>
    /// What an erase leaves: the background of this look, the default foreground and no attribute, as xterm erases with
    /// the current background.
    /// </summary>
    public TerminalStyle Blank => Default.WithBackground(Background);

    /// <inheritdoc />
    public bool Equals(TerminalStyle other) => m_value == other.m_value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TerminalStyle other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => m_value;

    /// <summary>
    /// Whether two looks are the same.
    /// </summary>
    public static bool operator ==(TerminalStyle left, TerminalStyle right) => left.Equals(right);

    /// <summary>
    /// Whether two looks differ.
    /// </summary>
    public static bool operator !=(TerminalStyle left, TerminalStyle right) => !left.Equals(right);

    private static int Encode(int color) => color is >= 0 and < DEFAULT_COLOR ? color : DEFAULT_COLOR;

    private static int Decode(int value) => value == DEFAULT_COLOR ? -1 : value;

    #endregion

    #region Properties

    /// <summary>
    /// The foreground: a palette index from 0 to 15, or -1 for the screen's default.
    /// </summary>
    public int Foreground => Decode(m_value & COLOR_MASK);

    /// <summary>
    /// The background: a palette index from 0 to 15, or -1 for the screen's default.
    /// </summary>
    public int Background => Decode(m_value >> BACKGROUND_SHIFT & COLOR_MASK);

    /// <summary>
    /// Whether the text is bold, which the renderer shows as the bright colour of a dark one (the font has one weight).
    /// </summary>
    public bool Bold => (m_value & BOLD) != 0;

    /// <summary>
    /// Whether the text is underlined.
    /// </summary>
    public bool Underline => (m_value & UNDERLINE) != 0;

    /// <summary>
    /// Whether the foreground and the background trade places.
    /// </summary>
    public bool Inverse => (m_value & INVERSE) != 0;

    #endregion
}
