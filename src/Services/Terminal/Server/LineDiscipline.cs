using System;
using System.Collections.Generic;
using OutWit.OS.Terminals;
using static OutWit.OS.Terminals.Termios;

namespace OutWit.OS.Terminal.Server;

/// <summary>
/// The line discipline (plan step N4.3, RFC 0020 §6): what a terminal does between the bytes a device carries and the
/// bytes a program reads and writes, as Linux's N_TTY does with the attributes a termios holds. Input: the input
/// flags' translations, the signal characters with ISIG, canonical editing (erase, kill, end of file, a line delivered
/// whole) and echo, or raw bytes with VMIN. Output: OPOST's translations. The flags and characters are Linux's generic
/// values, which the libc copies into a termios as they are. Not thread-safe: the hub serializes it.
/// </summary>
internal sealed class LineDiscipline
{
    #region Constants

    /// <summary>
    /// The bytes of input the discipline keeps at most: what is typed beyond them while nobody reads is dropped.
    /// </summary>
    public const int INPUT_LIMIT = 65536;

    /// <summary>
    /// The bytes of one line at most in canonical mode, as Linux's N_TTY_BUF_SIZE bounds a line.
    /// </summary>
    public const int LINE_LIMIT = 4095;

    private const byte NEWLINE = (byte)'\n';
    private const byte RETURN = (byte)'\r';
    private const byte DELETE = 0x7F;

    #endregion

    #region Fields

    private readonly List<byte> m_line = [];
    private readonly List<byte[]> m_lines = [];
    private readonly List<byte> m_raw = [];
    private int m_queued;

    #endregion

    #region Constructors

    /// <summary>
    /// A discipline with Linux's standard attributes: ICRNL and IXON in, OPOST and ONLCR out, 8 bits, canonical with
    /// echo and signals, and the usual control characters.
    /// </summary>
    public LineDiscipline()
    {
        Attributes = Defaults();
    }

    #endregion

    #region Functions

    /// <summary>
    /// Linux's standard attributes (tty_std_termios).
    /// </summary>
    /// <returns>The attributes.</returns>
    public static TerminalAttributes Defaults()
    {
        var characters = new byte[TerminalMessages.CONTROLS];
        characters[VINTR] = 3; // ^C
        characters[VQUIT] = 28; // ^\
        characters[VERASE] = DELETE;
        characters[VKILL] = 21; // ^U
        characters[VEOF] = 4; // ^D
        characters[VTIME] = 0;
        characters[VMIN] = 1;
        characters[VSTART] = 17; // ^Q
        characters[VSTOP] = 19; // ^S
        characters[VSUSP] = 26; // ^Z
        characters[VREPRINT] = 18; // ^R
        characters[VDISCARD] = 15; // ^O
        characters[VWERASE] = 23; // ^W
        characters[VLNEXT] = 22; // ^V
        return new TerminalAttributes(ICRNL | IXON, OPOST | ONLCR, B38400 | CS8 | CREAD | HUPCL,
            ISIG | ICANON | ECHO | ECHOE | ECHOK | ECHOCTL | ECHOKE | IEXTEN, characters);
    }

    /// <summary>
    /// Takes bytes the device received: the translations, the signal characters, the editing, the echo.
    /// </summary>
    /// <param name="raw">The bytes.</param>
    /// <returns>What to echo to the device, before output processing, and the signals the bytes raised (EVENT_INTERRUPT,
    /// EVENT_QUIT), in order.</returns>
    public (byte[] Echo, uint[] Signals) Receive(ReadOnlySpan<byte> raw)
    {
        var echo = new List<byte>();
        var signals = new List<uint>();
        var attributes = Attributes;
        var canonical = (attributes.Local & ICANON) != 0;
        foreach (var received in raw)
        {
            var c = received;
            if ((attributes.Input & ISTRIP) != 0)
                c &= 0x7F;
            if (c == RETURN)
            {
                if ((attributes.Input & IGNCR) != 0)
                    continue;
                if ((attributes.Input & ICRNL) != 0)
                    c = NEWLINE;
            }
            else if (c == NEWLINE && (attributes.Input & INLCR) != 0)
            {
                c = RETURN;
            }
            if ((attributes.Local & ISIG) != 0)
            {
                var signal = Is(c, VINTR) ? TerminalMessages.EVENT_INTERRUPT : Is(c, VQUIT) ? TerminalMessages.EVENT_QUIT : 0;
                if (signal != 0)
                {
                    if ((attributes.Local & NOFLSH) == 0)
                        FlushInput();
                    EchoControl(echo, c);
                    signals.Add(signal);
                    continue;
                }
                if (Is(c, VSUSP))
                    continue; // no job control: the character is taken and nothing happens
            }
            if ((attributes.Input & IXON) != 0 && (Is(c, VSTOP) || Is(c, VSTART)))
                continue; // no flow control: the characters are taken and nothing stops
            if (canonical)
                ReceiveCanonical(c, echo);
            else
                ReceiveRaw(c, echo);
        }
        return ([.. echo], [.. signals]);
    }

    /// <summary>
    /// What a read takes now: a line, or the end of the file, in canonical mode; VMIN's bytes otherwise.
    /// </summary>
    /// <param name="limit">The bytes the reader takes at most.</param>
    /// <param name="bytes">The bytes read.</param>
    /// <returns>Whether the read returns now: with bytes, or with none at the end of the file or with VMIN zero.</returns>
    public bool TryRead(int limit, out byte[] bytes)
    {
        if ((Attributes.Local & ICANON) != 0)
        {
            if (m_lines.Count == 0)
            {
                bytes = [];
                return false;
            }
            var line = m_lines[0];
            if (line.Length <= limit)
            {
                m_lines.RemoveAt(0);
                m_queued -= line.Length;
                bytes = line;
                return true;
            }
            bytes = line[..limit];
            m_lines[0] = line[limit..]; // the rest of the line, read next
            m_queued -= limit;
            return true;
        }
        var minimum = Minimum;
        if (m_raw.Count < minimum)
        {
            bytes = [];
            return minimum == 0;
        }
        var count = Math.Min(limit, m_raw.Count);
        bytes = [.. m_raw.GetRange(0, count)];
        m_raw.RemoveRange(0, count);
        m_queued -= count;
        return true;
    }

    /// <summary>
    /// Translates bytes a program writes for the device: OPOST's ONLCR, OCRNL and ONLRET.
    /// </summary>
    /// <param name="bytes">The program's bytes.</param>
    /// <returns>The device's bytes.</returns>
    public byte[] ProcessOutput(ReadOnlySpan<byte> bytes)
    {
        var attributes = Attributes;
        if ((attributes.Output & OPOST) == 0)
            return bytes.ToArray();
        var output = new List<byte>(bytes.Length + 16);
        foreach (var c in bytes)
        {
            if (c == NEWLINE && (attributes.Output & ONLCR) != 0)
            {
                output.Add(RETURN);
                output.Add(NEWLINE);
            }
            else if (c == RETURN && (attributes.Output & OCRNL) != 0)
            {
                output.Add(NEWLINE);
            }
            else
            {
                output.Add(c);
            }
        }
        return [.. output];
    }

    /// <summary>
    /// Sets the attributes, as tcsetattr does; a change out of canonical mode gives the line being edited to the raw
    /// queue, as Linux does.
    /// </summary>
    /// <param name="attributes">The attributes.</param>
    /// <param name="flushInput">TCSAFLUSH: what was typed and not read is dropped.</param>
    public void SetAttributes(TerminalAttributes attributes, bool flushInput)
    {
        var wasCanonical = (Attributes.Local & ICANON) != 0;
        var canonical = (attributes.Local & ICANON) != 0;
        Attributes = attributes;
        if (flushInput)
        {
            FlushInput();
            return;
        }
        if (wasCanonical && !canonical)
        {
            foreach (var line in m_lines)
                m_raw.AddRange(line);
            m_lines.Clear();
            m_raw.AddRange(m_line);
            m_line.Clear();
        }
        else if (!wasCanonical && canonical)
        {
            m_line.AddRange(m_raw);
            m_raw.Clear();
        }
    }

    /// <summary>
    /// Drops what was typed and not read.
    /// </summary>
    public void FlushInput()
    {
        m_line.Clear();
        m_lines.Clear();
        m_raw.Clear();
        m_queued = 0;
    }

    private void ReceiveCanonical(byte c, List<byte> echo)
    {
        var attributes = Attributes;
        if (Is(c, VERASE))
        {
            if (m_line.Count > 0)
            {
                var erased = m_line[^1];
                m_line.RemoveAt(m_line.Count - 1);
                if ((attributes.Local & ECHO) != 0 && (attributes.Local & ECHOE) != 0)
                    EraseVisibly(echo, erased);
                else if ((attributes.Local & ECHO) != 0)
                    EchoControl(echo, c);
            }
            return;
        }
        if (Is(c, VKILL))
        {
            if ((attributes.Local & ECHO) != 0)
            {
                if ((attributes.Local & ECHOKE) != 0)
                {
                    for (var i = m_line.Count - 1; i >= 0; i--)
                        EraseVisibly(echo, m_line[i]);
                }
                else
                {
                    EchoControl(echo, c);
                    if ((attributes.Local & ECHOK) != 0)
                        echo.Add(NEWLINE);
                }
            }
            m_line.Clear();
            return;
        }
        if (Is(c, VEOF))
        {
            Deliver([.. m_line]); // the line so far, the end of the file when it is empty
            m_line.Clear();
            return;
        }
        var ends = c == NEWLINE || (Is(c, VEOL) && c != 0) || (Is(c, VEOL2) && c != 0);
        if (m_line.Count < LINE_LIMIT || ends)
        {
            m_line.Add(c);
            if (ends)
            {
                if ((attributes.Local & ECHO) != 0 || ((attributes.Local & ECHONL) != 0 && c == NEWLINE))
                    echo.Add(c);
                Deliver([.. m_line]);
                m_line.Clear();
            }
            else if ((attributes.Local & ECHO) != 0)
            {
                EchoPrintable(echo, c);
            }
        }
    }

    private void ReceiveRaw(byte c, List<byte> echo)
    {
        if (m_queued >= INPUT_LIMIT)
            return;
        m_raw.Add(c);
        m_queued++;
        if ((Attributes.Local & ECHO) != 0)
            EchoPrintable(echo, c);
    }

    private void Deliver(byte[] line)
    {
        if (m_queued + line.Length > INPUT_LIMIT)
            return;
        m_lines.Add(line);
        m_queued += line.Length;
    }

    private void EchoPrintable(List<byte> echo, byte c)
    {
        if ((c < 0x20 && c != NEWLINE && c != (byte)'\t') || c == DELETE)
            EchoControl(echo, c);
        else
            echo.Add(c);
    }

    // A control character echoed as ^X when ECHOCTL asks, as Linux shows ^C and ^D.
    private void EchoControl(List<byte> echo, byte c)
    {
        if ((Attributes.Local & ECHO) == 0)
            return;
        if ((Attributes.Local & ECHOCTL) != 0 && (c < 0x20 || c == DELETE) && c != NEWLINE && c != (byte)'\t')
        {
            echo.Add((byte)'^');
            echo.Add((byte)(c == DELETE ? '?' : c + 0x40));
        }
        else
        {
            echo.Add(c);
        }
    }

    // A character erased from the screen: backspace, space, backspace; two of them for a control character shown as ^X.
    private void EraseVisibly(List<byte> echo, byte erased)
    {
        var width = (Attributes.Local & ECHOCTL) != 0 && (erased < 0x20 || erased == DELETE) && erased != (byte)'\t' ? 2 : 1;
        for (var i = 0; i < width; i++)
        {
            echo.Add((byte)'\b');
            echo.Add((byte)' ');
            echo.Add((byte)'\b');
        }
    }

    private bool Is(byte c, int control) => c != 0 && Attributes.Characters[control] == c;

    #endregion

    #region Properties

    /// <summary>
    /// The attributes.
    /// </summary>
    public TerminalAttributes Attributes { get; private set; }

    /// <summary>
    /// Whether a read would return now, as poll reports: a line, or the end of the file, in canonical mode; VMIN's
    /// bytes, at least one, otherwise.
    /// </summary>
    public bool InputReady => (Attributes.Local & ICANON) != 0 ? m_lines.Count > 0 : m_raw.Count >= Math.Max(Minimum, 1);

    // The bytes a raw read waits for: VMIN, or one while VTIME runs a timer the service does not keep.
    private int Minimum => Attributes.Characters[VTIME] != 0 ? Math.Max((int)Attributes.Characters[VMIN], 1) : Attributes.Characters[VMIN];

    #endregion
}
