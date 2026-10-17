using System;
using System.Text;

namespace OutWit.OS.Display;

/// <summary>
/// A small terminal emulator (plan step C1.2, shared with the display service since C2.2): the screen the bytes paint
/// and the cursor they move, so that the runner's side of the guest's terminal and the display's console answer the
/// queries a terminal answers — the cursor position report .NET's Console and PSReadLine wait for — and the screen,
/// not the byte stream, is what the display draws and the host judges. It knows
/// printable text, CR, LF, BS and TAB; the CSI sequences CUU/CUD/CUF/CUB (A–D), CNL/CPL (E, F), CHA (G), CUP/HVP (H, f),
/// ED (J), EL (K), SU/SD (S, T), VPA (d), DSR (n), SCP/RCP (s, u) and SGR (m, ignored) with the private modes (?h, ?l,
/// ignored); ESC 7/8 (save and restore the cursor), ESC M (reverse index), ESC c (reset), the charset designations
/// (one character skipped) and OSC strings (ignored to BEL or ST). Anything else is ignored. Bytes are UTF-8, and a
/// sequence or a character may be split between calls.
/// </summary>
public sealed class TerminalScreen
{
    #region Constants

    private const int TAB = 8;

    private const int MAX_PARAMETERS = 16;

    #endregion

    #region Fields

    private readonly char[][] m_cells;
    private readonly Decoder m_decoder = Encoding.UTF8.GetDecoder();
    private readonly int[] m_parameters = new int[MAX_PARAMETERS];
    private readonly StringBuilder m_replies = new();
    private State m_state;
    private int m_parameterCount;
    private bool m_parameterDigits;
    private bool m_privateMode;
    private bool m_oscEscape;
    private bool m_wrapPending;
    private int m_savedRow, m_savedColumn;

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
        m_cells = new char[rows][];
        for (var row = 0; row < rows; row++)
        {
            m_cells[row] = new char[columns];
            Array.Fill(m_cells[row], ' ');
        }
    }

    #endregion

    #region Functions

    /// <summary>
    /// Paints what the guest wrote and answers its queries.
    /// </summary>
    /// <param name="bytes">The guest's bytes, UTF-8, in any pieces.</param>
    /// <returns>What a terminal sends back for them: the cursor position report and the status report, or nothing.</returns>
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
    /// One row of the screen, its trailing spaces trimmed.
    /// </summary>
    /// <param name="row">The row, from 0.</param>
    /// <returns>The row's text.</returns>
    public string Line(int row) => new string(m_cells[row]).TrimEnd();

    /// <summary>
    /// The character of a cell.
    /// </summary>
    /// <param name="row">The row, from 0.</param>
    /// <param name="column">The column, from 0.</param>
    public char this[int row, int column] => m_cells[row][column];

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
            case '\x07':
            case '\x00':
            case '\x0E':
            case '\x0F':
                break; // the bell and the shift characters paint nothing
            default:
                if (c >= ' ' && !char.IsSurrogate(c))
                    Print(c);
                break;
        }
    }

    private void Escape(char c)
    {
        switch (c)
        {
            case '[':
                m_state = State.Csi;
                m_parameterCount = 0;
                m_parameterDigits = false;
                m_privateMode = false;
                Array.Clear(m_parameters);
                break;
            case ']':
                m_state = State.Osc;
                m_oscEscape = false;
                break;
            case '7':
                m_savedRow = Row;
                m_savedColumn = Column;
                m_state = State.Ground;
                break;
            case '8':
                Row = m_savedRow;
                Column = m_savedColumn;
                m_wrapPending = false;
                m_state = State.Ground;
                break;
            case 'M':
                if (Row == 0)
                    ScrollDown(1);
                else
                    Row--;
                m_state = State.Ground;
                break;
            case 'c':
                Reset();
                m_state = State.Ground;
                break;
            case '(':
            case ')':
            case '*':
            case '+':
                m_state = State.Charset;
                break;
            default:
                m_state = State.Ground; // ESC =, ESC >, ESC D, ESC E and anything else change nothing here
                if (c == 'D')
                    LineFeed();
                else if (c == 'E')
                {
                    Column = 0;
                    LineFeed();
                }
                break;
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
        if (c == ';')
        {
            if (m_parameterCount == 0)
                m_parameterCount = 1;
            m_parameterCount++;
            return;
        }
        if (c == '?' || c == '>' || c == '=' || c == '!' || c == '<')
        {
            m_privateMode = true;
            return;
        }
        if (c < '@' || c > '~')
            return; // an intermediate or a stray byte inside the sequence
        m_state = State.Ground;
        if (m_privateMode)
            return; // DECSET, DECRST and the other private sequences paint nothing
        var count = Math.Min(m_parameterCount, MAX_PARAMETERS);
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
            case 's':
                m_savedRow = Row;
                m_savedColumn = Column;
                break;
            case 'u':
                Row = m_savedRow;
                Column = m_savedColumn;
                break;
            case 'm':
            default:
                break; // colors and attributes, and what this emulator does not know, paint nothing
        }
        m_wrapPending = false;
    }

    private void Osc(char c)
    {
        if (c == '\x07' || (m_oscEscape && c == '\\'))
        {
            m_state = State.Ground;
            m_oscEscape = false;
        }
        else
            m_oscEscape = c == '\x1B';
    }

    private void Print(char c)
    {
        if (m_wrapPending)
        {
            Column = 0;
            LineFeed();
        }
        m_cells[Row][Column] = c;
        if (Column + 1 < Columns)
            Column++;
        else
            m_wrapPending = true; // the cursor stays on the last column until the next character, as VT100 wraps
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
        for (var row = 0; row + lines < Rows; row++)
            Array.Copy(m_cells[row + lines], m_cells[row], Columns);
        for (var row = Math.Max(0, Rows - lines); row < Rows; row++)
            Array.Fill(m_cells[row], ' ');
    }

    private void ScrollDown(int lines)
    {
        lines = Math.Min(lines, Rows);
        Scrolls += lines;
        for (var row = Rows - 1; row - lines >= 0; row--)
            Array.Copy(m_cells[row - lines], m_cells[row], Columns);
        for (var row = 0; row < Math.Min(lines, Rows); row++)
            Array.Fill(m_cells[row], ' ');
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var row = Row + 1; row < Rows; row++)
                    Array.Fill(m_cells[row], ' ');
                break;
            case 1:
                EraseLine(1);
                for (var row = 0; row < Row; row++)
                    Array.Fill(m_cells[row], ' ');
                break;
            default:
                foreach (var row in m_cells)
                    Array.Fill(row, ' ');
                break;
        }
    }

    private void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0:
                Array.Fill(m_cells[Row], ' ', Column, Columns - Column);
                break;
            case 1:
                Array.Fill(m_cells[Row], ' ', 0, Column + 1);
                break;
            default:
                Array.Fill(m_cells[Row], ' ');
                break;
        }
    }

    private void Reset()
    {
        foreach (var row in m_cells)
            Array.Fill(row, ' ');
        Row = 0;
        Column = 0;
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
        Charset
    }

    #endregion
}
