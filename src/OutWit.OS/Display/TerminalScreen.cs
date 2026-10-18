using System;
using System.Text;

namespace OutWit.OS.Display;

/// <summary>
/// A small terminal emulator (plan step C1.2, shared with the display service since C2.2): the screen the bytes paint
/// and the cursor they move, so that the runner's side of the guest's terminal and the display's console answer the
/// queries a terminal answers — the cursor position report .NET's Console and PSReadLine wait for — and the screen,
/// not the byte stream, is what the display draws and the host judges. It knows printable text, CR, LF, BS and TAB;
/// the CSI sequences CUU/CUD/CUF/CUB (A–D), CNL/CPL (E, F), CHA (G), CUP/HVP (H, f), ED (J), EL (K), IL/DL (L, M),
/// ICH/DCH/ECH (@, P, X), SU/SD (S, T), VPA (d), DSR (n), SCP/RCP (s, u), DA1 (c) and the size report (18 t); SGR (m)
/// with the 16 colours, their bright forms, the 256 and the 24-bit colours reduced to the palette, bold, underline and
/// inverse (C2.6); the private modes of the cursor's visibility (?25), autowrap (?7) and the alternate screen (?47,
/// ?1047, ?1049); ESC 7/8 (save and restore the cursor), ESC D/E/M (index, next line, reverse index), ESC c (reset),
/// the charset designations (one character skipped), OSC strings (the default colours' queries 10 and 11 answered) and
/// the DCS, APC, PM and SOS strings, swallowed to their terminator. Anything else is ignored. Bytes are UTF-8, a
/// sequence or a character may be split between calls, and an East Asian wide character takes two cells.
/// </summary>
public sealed class TerminalScreen
{
    #region Constants

    /// <summary>
    /// The second cell of a wide character, which holds no character of its own.
    /// </summary>
    public const char WIDE_TAIL = '￿';

    private const int TAB = 8;

    private const int MAX_PARAMETERS = 16;

    private const int MAX_OSC = 64;

    // The answer to DA1: a VT220 with ANSI colour, what xterm's answer begins with.
    private const string DEVICE_ATTRIBUTES = "\x1B[?62;22c";

    // The code points a cell of two columns shows (Unicode's East Asian Wide and Fullwidth, and the emoji presented
    // wide), as ranges sorted by their start.
    private static readonly (int Start, int End)[] WIDE =
    [
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3),
        (0x25FD, 0x25FE), (0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1),
        (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA),
        (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B),
        (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797),
        (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x2E80, 0x303E),
        (0x3041, 0x33FF), (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xA000, 0xA4CF), (0xA960, 0xA97F), (0xAC00, 0xD7A3),
        (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE6F), (0xFF00, 0xFF60), (0xFFE0, 0xFFE6), (0x16FE0, 0x16FE4),
        (0x17000, 0x18AFF), (0x1B000, 0x1B2FF), (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E),
        (0x1F191, 0x1F19A), (0x1F200, 0x1F251), (0x1F300, 0x1F64F), (0x1F680, 0x1F6FF), (0x1F7E0, 0x1F7EB),
        (0x1F90C, 0x1F9FF), (0x1FA70, 0x1FAFF), (0x20000, 0x2FFFD), (0x30000, 0x3FFFD)
    ];

    // The code points that take no cell: combining marks, zero-width characters and the variation selectors.
    private static readonly (int Start, int End)[] ZERO_WIDTH =
    [
        (0x0300, 0x036F), (0x0483, 0x0489), (0x1AB0, 0x1AFF), (0x1DC0, 0x1DFF), (0x200B, 0x200F), (0x2060, 0x2064),
        (0x20D0, 0x20FF), (0xFE00, 0xFE0F), (0xFE20, 0xFE2F), (0xE0100, 0xE01EF)
    ];

    #endregion

    #region Fields

    private readonly char[][] m_mainCells;
    private readonly TerminalStyle[][] m_mainStyles;
    private readonly char[][] m_alternateCells;
    private readonly TerminalStyle[][] m_alternateStyles;
    private readonly Decoder m_decoder = Encoding.UTF8.GetDecoder();
    private readonly int[] m_parameters = new int[MAX_PARAMETERS];
    private readonly StringBuilder m_replies = new();
    private readonly StringBuilder m_osc = new();
    private char[][] m_cells;
    private TerminalStyle[][] m_styles;
    private TerminalStyle m_style = TerminalStyle.Default;
    private State m_state;
    private int m_parameterCount;
    private bool m_parameterDigits;
    private char m_marker;
    private bool m_intermediate;
    private bool m_stringEscape;
    private bool m_wrapPending;
    private char m_highSurrogate;
    private int m_savedRow, m_savedColumn;
    private TerminalStyle m_savedStyle = TerminalStyle.Default;
    private int m_alternateSavedRow, m_alternateSavedColumn;

    #endregion

    #region Constructors

    /// <summary>
    /// A blank screen with the cursor at its top left.
    /// </summary>
    /// <param name="columns">Columns; 80, the terminal service's default, unless the device reports a size.</param>
    /// <param name="rows">Rows; 24 by the same default.</param>
    public TerminalScreen(int columns = 80, int rows = 24)
    {
        Columns = columns;
        Rows = rows;
        (m_mainCells, m_mainStyles) = Allocate(columns, rows);
        (m_alternateCells, m_alternateStyles) = Allocate(columns, rows);
        m_cells = m_mainCells;
        m_styles = m_mainStyles;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Paints what the guest wrote and answers its queries.
    /// </summary>
    /// <param name="bytes">The guest's bytes, UTF-8, in any pieces.</param>
    /// <returns>What a terminal sends back for them: the cursor position, status, device attributes, size and default
    /// colours' reports, or nothing.</returns>
    public string Feed(ReadOnlySpan<byte> bytes)
    {
        m_replies.Clear();
        var chars = new char[bytes.Length + 2];
        var count = m_decoder.GetChars(bytes, chars, flush: false);
        for (var i = 0; i < count; i++)
            Process(chars[i]);
        return m_replies.ToString();
    }

    /// <summary>
    /// One row of the screen, its trailing spaces trimmed; a wide character once.
    /// </summary>
    /// <param name="row">The row, from 0.</param>
    /// <returns>The row's text.</returns>
    public string Line(int row)
    {
        var text = new StringBuilder(Columns);
        foreach (var c in m_cells[row])
        {
            if (c != WIDE_TAIL)
                text.Append(c);
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The character of a cell; <see cref="WIDE_TAIL"/> in the second cell of a wide character.
    /// </summary>
    /// <param name="row">The row, from 0.</param>
    /// <param name="column">The column, from 0.</param>
    public char this[int row, int column] => m_cells[row][column];

    /// <summary>
    /// The look of a cell.
    /// </summary>
    /// <param name="row">The row, from 0.</param>
    /// <param name="column">The column, from 0.</param>
    /// <returns>Its colours and attributes.</returns>
    public TerminalStyle Style(int row, int column) => m_styles[row][column];

    private static (char[][] Cells, TerminalStyle[][] Styles) Allocate(int columns, int rows)
    {
        var cells = new char[rows][];
        var styles = new TerminalStyle[rows][];
        for (var row = 0; row < rows; row++)
        {
            cells[row] = new char[columns];
            styles[row] = new TerminalStyle[columns];
            Array.Fill(cells[row], ' ');
            Array.Fill(styles[row], TerminalStyle.Default);
        }
        return (cells, styles);
    }

    private void Process(char c)
    {
        switch (m_state)
        {
            case State.Ground:
                Ground(c);
                break;
            case State.Escape:
                Escape(c);
                break;
            case State.Csi:
                Csi(c);
                break;
            case State.Osc:
                Osc(c);
                break;
            case State.String:
                SkipString(c);
                break;
            case State.Charset:
                m_state = State.Ground; // the designated set is ignored
                break;
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\x1B':
                m_state = State.Escape;
                break;
            case '\r':
                Column = 0;
                m_wrapPending = false;
                break;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                break;
            case '\b':
                if (Column > 0)
                    Column--;
                m_wrapPending = false;
                break;
            case '\t':
                Column = Math.Min(Columns - 1, (Column / TAB + 1) * TAB);
                m_wrapPending = false;
                break;
            default:
                if (c < ' ' || c is >= '\x7F' and <= '\x9F')
                    break; // the bell, the shift characters, DEL and the C1 controls paint nothing
                if (char.IsHighSurrogate(c))
                {
                    m_highSurrogate = c;
                    break;
                }
                if (char.IsLowSurrogate(c))
                {
                    if (m_highSurrogate != '\0')
                        Print(char.ConvertToUtf32(m_highSurrogate, c));
                    m_highSurrogate = '\0';
                    break;
                }
                m_highSurrogate = '\0';
                Print(c);
                break;
        }
    }

    private void Escape(char c)
    {
        m_state = State.Ground;
        switch (c)
        {
            case '[':
                m_state = State.Csi;
                m_parameterCount = 0;
                m_parameterDigits = false;
                m_marker = '\0';
                m_intermediate = false;
                Array.Clear(m_parameters);
                break;
            case ']':
                m_state = State.Osc;
                m_osc.Clear();
                m_stringEscape = false;
                break;
            case 'P':
            case '_':
            case '^':
            case 'X':
                m_state = State.String; // DCS, APC, PM and SOS paint nothing, whatever they carry
                m_stringEscape = false;
                break;
            case '7':
                m_savedRow = Row;
                m_savedColumn = Column;
                m_savedStyle = m_style;
                break;
            case '8':
                Row = m_savedRow;
                Column = m_savedColumn;
                m_style = m_savedStyle;
                m_wrapPending = false;
                break;
            case 'M':
                if (Row == 0)
                    ScrollDown(1);
                else
                    Row--;
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                Column = 0;
                LineFeed();
                break;
            case 'c':
                Reset();
                break;
            case '(':
            case ')':
            case '*':
            case '+':
                m_state = State.Charset;
                break;
            default:
                break; // ESC =, ESC >, ESC \ and anything else change nothing here
        }
    }

    private void Csi(char c)
    {
        if (c >= '0' && c <= '9')
        {
            if (m_parameterCount == 0)
                m_parameterCount = 1;
            if (m_parameterCount <= MAX_PARAMETERS)
                m_parameters[m_parameterCount - 1] = Math.Min(9999, m_parameters[m_parameterCount - 1] * 10 + (c - '0'));
            m_parameterDigits = true;
            return;
        }
        if (c == ';' || c == ':')
        {
            if (m_parameterCount == 0)
                m_parameterCount = 1;
            m_parameterCount++;
            return;
        }
        if (c is '?' or '>' or '=' or '!' or '<')
        {
            m_marker = c;
            return;
        }
        if (c < '@' || c > '~')
        {
            if (c is >= ' ' and <= '/')
                m_intermediate = true;
            return; // an intermediate or a stray byte inside the sequence
        }
        m_state = State.Ground;
        if (m_intermediate)
            return; // DECSCUSR, DECRQM and the other sequences with intermediates paint nothing
        var count = Math.Min(m_parameterCount, MAX_PARAMETERS);
        if (m_marker == '?')
        {
            if (c is 'h' or 'l')
                PrivateMode(c == 'h', count);
            return;
        }
        if (m_marker != '\0')
            return; // the secondary attributes, the keyboard protocols and the other marked sequences
        var first = count > 0 && m_parameterDigits ? m_parameters[0] : 0;
        var second = count > 1 ? m_parameters[1] : 0;
        switch (c)
        {
            case 'A':
                Row = Math.Max(0, Row - Math.Max(1, first));
                break;
            case 'B':
                Row = Math.Min(Rows - 1, Row + Math.Max(1, first));
                break;
            case 'C':
                Column = Math.Min(Columns - 1, Column + Math.Max(1, first));
                break;
            case 'D':
                Column = Math.Max(0, Column - Math.Max(1, first));
                break;
            case 'E':
                Row = Math.Min(Rows - 1, Row + Math.Max(1, first));
                Column = 0;
                break;
            case 'F':
                Row = Math.Max(0, Row - Math.Max(1, first));
                Column = 0;
                break;
            case 'G':
            case '`':
                Column = Math.Clamp(Math.Max(1, first) - 1, 0, Columns - 1);
                break;
            case 'H':
            case 'f':
                Row = Math.Clamp(Math.Max(1, first) - 1, 0, Rows - 1);
                Column = Math.Clamp(Math.Max(1, second) - 1, 0, Columns - 1);
                break;
            case 'd':
                Row = Math.Clamp(Math.Max(1, first) - 1, 0, Rows - 1);
                break;
            case 'J':
                EraseDisplay(first);
                break;
            case 'K':
                EraseLine(first);
                break;
            case 'L':
                InsertLines(Math.Max(1, first));
                break;
            case 'M':
                DeleteLines(Math.Max(1, first));
                break;
            case '@':
                InsertCharacters(Math.Max(1, first));
                break;
            case 'P':
                DeleteCharacters(Math.Max(1, first));
                break;
            case 'X':
                Erase(Row, Column, Math.Min(Columns - Column, Math.Max(1, first)));
                break;
            case 'S':
                ScrollUp(Math.Max(1, first));
                break;
            case 'T':
                ScrollDown(Math.Max(1, first));
                break;
            case 'n':
                if (first == 6)
                    m_replies.Append("\x1B[").Append(Row + 1).Append(';').Append(Column + 1).Append('R');
                else if (first == 5)
                    m_replies.Append("\x1B[0n");
                break;
            case 'c':
                if (first == 0)
                    m_replies.Append(DEVICE_ATTRIBUTES);
                break;
            case 't':
                if (first == 18)
                    m_replies.Append("\x1B[8;").Append(Rows).Append(';').Append(Columns).Append('t');
                break;
            case 's':
                m_savedRow = Row;
                m_savedColumn = Column;
                break;
            case 'u':
                Row = m_savedRow;
                Column = m_savedColumn;
                break;
            case 'm':
                SelectGraphicRendition(count);
                break;
            default:
                break; // what this emulator does not know paints nothing
        }
        m_wrapPending = false;
    }

    // SGR: each parameter in turn, 38 and 48 taking the colour after them (5 and an index of 256, or 2 and red, green
    // and blue), reduced to the palette.
    private void SelectGraphicRendition(int count)
    {
        if (count == 0)
        {
            m_style = TerminalStyle.Default;
            return;
        }
        for (var i = 0; i < count; i++)
        {
            var p = m_parameters[i];
            switch (p)
            {
                case 0:
                    m_style = TerminalStyle.Default;
                    break;
                case 1:
                    m_style = m_style.WithBold(true);
                    break;
                case 4:
                    m_style = m_style.WithUnderline(true);
                    break;
                case 7:
                    m_style = m_style.WithInverse(true);
                    break;
                case 22:
                    m_style = m_style.WithBold(false);
                    break;
                case 24:
                    m_style = m_style.WithUnderline(false);
                    break;
                case 27:
                    m_style = m_style.WithInverse(false);
                    break;
                case >= 30 and <= 37:
                    m_style = m_style.WithForeground(p - 30);
                    break;
                case 39:
                    m_style = m_style.WithForeground(-1);
                    break;
                case >= 40 and <= 47:
                    m_style = m_style.WithBackground(p - 40);
                    break;
                case 49:
                    m_style = m_style.WithBackground(-1);
                    break;
                case >= 90 and <= 97:
                    m_style = m_style.WithForeground(p - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    m_style = m_style.WithBackground(p - 100 + 8);
                    break;
                case 38:
                case 48:
                    var color = ExtendedColor(count, ref i);
                    if (color >= 0)
                        m_style = p == 38 ? m_style.WithForeground(color) : m_style.WithBackground(color);
                    break;
                default:
                    break; // faint, italic, blink, hidden, crossed out and the rest paint as plain text
            }
        }
    }

    private int ExtendedColor(int count, ref int i)
    {
        if (i + 1 >= count)
            return -1;
        switch (m_parameters[i + 1])
        {
            case 5 when i + 2 < count:
                i += 2;
                return TerminalPalette.From256(Math.Min(255, m_parameters[i]));
            case 2 when i + 4 < count:
                i += 4;
                return TerminalPalette.Nearest(Channel(m_parameters[i - 2]), Channel(m_parameters[i - 1]), Channel(m_parameters[i]));
            default:
                i = count;
                return -1;
        }
    }

    private static byte Channel(int value) => (byte)Math.Min(255, value);

    // DECSET and DECRST: the cursor's visibility, autowrap and the alternate screen; the rest is ignored.
    private void PrivateMode(bool set, int count)
    {
        for (var i = 0; i < count; i++)
        {
            switch (m_parameters[i])
            {
                case 7:
                    AutoWrap = set;
                    if (!set)
                        m_wrapPending = false;
                    break;
                case 25:
                    CursorVisible = set;
                    break;
                case 47:
                case 1047:
                    UseAlternateScreen(set, clear: set);
                    break;
                case 1049:
                    if (set)
                    {
                        m_alternateSavedRow = Row;
                        m_alternateSavedColumn = Column;
                        UseAlternateScreen(true, clear: true);
                    }
                    else
                    {
                        UseAlternateScreen(false, clear: false);
                        Row = m_alternateSavedRow;
                        Column = m_alternateSavedColumn;
                    }
                    break;
            }
        }
        m_wrapPending = false;
    }

    private void UseAlternateScreen(bool alternate, bool clear)
    {
        AlternateScreen = alternate;
        m_cells = alternate ? m_alternateCells : m_mainCells;
        m_styles = alternate ? m_alternateStyles : m_mainStyles;
        if (alternate && clear)
        {
            for (var row = 0; row < Rows; row++)
                Erase(row, 0, Columns);
        }
    }

    // OSC: the text to BEL or ST, kept short; the queries of the default foreground (10) and background (11) are
    // answered with the colours a renderer draws by default, ended as the query was.
    private void Osc(char c)
    {
        var bell = c == '\x07';
        if (bell || (m_stringEscape && c == '\\'))
        {
            m_state = State.Ground;
            m_stringEscape = false;
            var end = bell ? "\x07" : "\x1B\\";
            var text = m_osc.ToString();
            if (text == "10;?")
                m_replies.Append("\x1B]10;rgb:ffff/ffff/ffff").Append(end);
            else if (text == "11;?")
                m_replies.Append("\x1B]11;rgb:0000/0000/0000").Append(end);
            return;
        }
        m_stringEscape = c == '\x1B';
        if (!m_stringEscape && m_osc.Length < MAX_OSC)
            m_osc.Append(c);
    }

    private void SkipString(char c)
    {
        if (c == '\x07' || (m_stringEscape && c == '\\'))
        {
            m_state = State.Ground;
            m_stringEscape = false;
            return;
        }
        m_stringEscape = c == '\x1B';
    }

    private void Print(int codePoint)
    {
        var width = Width(codePoint);
        if (width == 0)
            return; // a combining mark joins the character before it, which the cell already shows
        var c = codePoint <= 0xFFFF ? (char)codePoint : '�';
        if (m_wrapPending)
        {
            Column = 0;
            LineFeed();
        }
        if (width == 2 && Column == Columns - 1)
        {
            if (AutoWrap)
            {
                Put(Row, Column, ' ');
                Column = 0;
                LineFeed();
            }
            else
                width = 1; // no room and no wrap: the character takes the last cell alone
        }
        Put(Row, Column, c);
        if (width == 2)
            Put(Row, Column + 1, WIDE_TAIL);
        if (Column + width < Columns)
            Column += width;
        else
        {
            Column = Columns - 1;
            m_wrapPending = AutoWrap; // the cursor stays on the last column until the next character, as VT100 wraps
        }
    }

    // A cell written: a wide character it cuts in half loses its other half.
    private void Put(int row, int column, char c)
    {
        var cells = m_cells[row];
        if (cells[column] == WIDE_TAIL && column > 0 && c != WIDE_TAIL)
            cells[column - 1] = ' ';
        if (column + 1 < Columns && cells[column + 1] == WIDE_TAIL)
            cells[column + 1] = ' ';
        cells[column] = c;
        m_styles[row][column] = m_style;
    }

    private static int Width(int codePoint)
    {
        if (codePoint < 0x0300)
            return 1;
        if (In(ZERO_WIDTH, codePoint))
            return 0;
        return In(WIDE, codePoint) ? 2 : 1;
    }

    private static bool In((int Start, int End)[] ranges, int codePoint)
    {
        int low = 0, high = ranges.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (codePoint < ranges[middle].Start)
                high = middle - 1;
            else if (codePoint > ranges[middle].End)
                low = middle + 1;
            else
                return true;
        }
        return false;
    }

    private void LineFeed()
    {
        m_wrapPending = false;
        if (Row + 1 < Rows)
            Row++;
        else
            ScrollUp(1);
    }

    private void ScrollUp(int lines)
    {
        lines = Math.Min(lines, Rows);
        Scrolls += lines;
        MoveRows(lines, 0, Rows - lines);
        for (var row = Math.Max(0, Rows - lines); row < Rows; row++)
            Erase(row, 0, Columns);
    }

    private void ScrollDown(int lines)
    {
        lines = Math.Min(lines, Rows);
        Scrolls += lines;
        MoveRows(0, lines, Rows - lines);
        for (var row = 0; row < lines; row++)
            Erase(row, 0, Columns);
    }

    private void InsertLines(int lines)
    {
        lines = Math.Min(lines, Rows - Row);
        MoveRows(Row, Row + lines, Rows - Row - lines);
        for (var row = Row; row < Row + lines; row++)
            Erase(row, 0, Columns);
        Column = 0;
    }

    private void DeleteLines(int lines)
    {
        lines = Math.Min(lines, Rows - Row);
        MoveRows(Row + lines, Row, Rows - Row - lines);
        for (var row = Rows - lines; row < Rows; row++)
            Erase(row, 0, Columns);
        Column = 0;
    }

    // Copies count rows from one place to another, in the order that keeps the source whole.
    private void MoveRows(int from, int to, int count)
    {
        if (count <= 0 || from == to)
            return;
        if (to < from)
        {
            for (var i = 0; i < count; i++)
                CopyRow(from + i, to + i);
        }
        else
        {
            for (var i = count - 1; i >= 0; i--)
                CopyRow(from + i, to + i);
        }
    }

    private void CopyRow(int from, int to)
    {
        Array.Copy(m_cells[from], m_cells[to], Columns);
        Array.Copy(m_styles[from], m_styles[to], Columns);
    }

    private void InsertCharacters(int count)
    {
        count = Math.Min(count, Columns - Column);
        Array.Copy(m_cells[Row], Column, m_cells[Row], Column + count, Columns - Column - count);
        Array.Copy(m_styles[Row], Column, m_styles[Row], Column + count, Columns - Column - count);
        Erase(Row, Column, count);
    }

    private void DeleteCharacters(int count)
    {
        count = Math.Min(count, Columns - Column);
        Array.Copy(m_cells[Row], Column + count, m_cells[Row], Column, Columns - Column - count);
        Array.Copy(m_styles[Row], Column + count, m_styles[Row], Column, Columns - Column - count);
        Erase(Row, Columns - count, count);
    }

    // Blank cells in the current background, as xterm erases.
    private void Erase(int row, int column, int count)
    {
        Array.Fill(m_cells[row], ' ', column, count);
        Array.Fill(m_styles[row], m_style.Blank, column, count);
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var row = Row + 1; row < Rows; row++)
                    Erase(row, 0, Columns);
                break;
            case 1:
                EraseLine(1);
                for (var row = 0; row < Row; row++)
                    Erase(row, 0, Columns);
                break;
            default:
                for (var row = 0; row < Rows; row++)
                    Erase(row, 0, Columns);
                break;
        }
    }

    private void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0:
                Erase(Row, Column, Columns - Column);
                break;
            case 1:
                Erase(Row, 0, Column + 1);
                break;
            default:
                Erase(Row, 0, Columns);
                break;
        }
    }

    private void Reset()
    {
        m_style = TerminalStyle.Default;
        UseAlternateScreen(false, clear: false);
        for (var row = 0; row < Rows; row++)
        {
            Array.Fill(m_alternateCells[row], ' ');
            Array.Fill(m_alternateStyles[row], TerminalStyle.Default);
            Array.Fill(m_mainCells[row], ' ');
            Array.Fill(m_mainStyles[row], TerminalStyle.Default);
        }
        Row = 0;
        Column = 0;
        CursorVisible = true;
        AutoWrap = true;
        m_wrapPending = false;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The lines the screen scrolled so far, up or down (plan step C2.3): what a renderer that moved rows would have moved.
    /// </summary>
    public long Scrolls { get; private set; }

    /// <summary>
    /// Columns of the screen.
    /// </summary>
    public int Columns { get; }

    /// <summary>
    /// Rows of the screen.
    /// </summary>
    public int Rows { get; }

    /// <summary>
    /// The cursor's row, from 0.
    /// </summary>
    public int Row { get; private set; }

    /// <summary>
    /// The cursor's column, from 0.
    /// </summary>
    public int Column { get; private set; }

    /// <summary>
    /// Whether the cursor is shown (DECTCEM, ?25), as it is until a program hides it.
    /// </summary>
    public bool CursorVisible { get; private set; } = true;

    /// <summary>
    /// Whether a character written in the last column wraps the next one to a new line (DECAWM, ?7).
    /// </summary>
    public bool AutoWrap { get; private set; } = true;

    /// <summary>
    /// Whether the alternate screen is shown (?47, ?1047, ?1049); the main screen keeps its text meanwhile.
    /// </summary>
    public bool AlternateScreen { get; private set; }

    /// <summary>
    /// The screen's text: every row, trailing spaces trimmed, joined with '\n', the blank rows at the bottom left out.
    /// </summary>
    public string Text
    {
        get
        {
            var last = Rows - 1;
            while (last >= 0 && Line(last).Length == 0)
                last--;
            var text = new StringBuilder();
            for (var row = 0; row <= last; row++)
            {
                if (row > 0)
                    text.Append('\n');
                text.Append(Line(row));
            }
            return text.ToString();
        }
    }

    #endregion

    #region Nested Types

    private enum State
    {
        Ground,
        Escape,
        Csi,
        Osc,
        String,
        Charset
    }

    #endregion
}
