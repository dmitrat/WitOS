using System;
using System.Text;

namespace OutWit.OS.Display;

/// <summary>
/// The keys of a keyboard as the bytes a terminal sends (plan step C2.2): a US layout, until the input model of M9,
/// over evdev's key codes — the characters with Shift and Caps Lock, the control characters with Ctrl, an ESC prefix
/// with Alt, and xterm's sequences for the keys that are no character (the arrows, Home, End, Insert, Delete, the
/// pages, the function keys). The modifiers' state is the layout's; a release sends nothing.
/// </summary>
public sealed class UsKeyboardLayout
{
    #region Constants

    private const ushort KEY_ESC = 1;
    private const ushort KEY_1 = 2;
    private const ushort KEY_EQUAL = 13;
    private const ushort KEY_BACKSPACE = 14;
    private const ushort KEY_TAB = 15;
    private const ushort KEY_Q = 16;
    private const ushort KEY_RIGHTBRACE = 27;
    private const ushort KEY_ENTER = 28;
    private const ushort KEY_LEFTCTRL = 29;
    private const ushort KEY_A = 30;
    private const ushort KEY_GRAVE = 41;
    private const ushort KEY_LEFTSHIFT = 42;
    private const ushort KEY_BACKSLASH = 43;
    private const ushort KEY_Z = 44;
    private const ushort KEY_SLASH = 53;
    private const ushort KEY_RIGHTSHIFT = 54;
    private const ushort KEY_KPASTERISK = 55;
    private const ushort KEY_LEFTALT = 56;
    private const ushort KEY_SPACE = 57;
    private const ushort KEY_CAPSLOCK = 58;
    private const ushort KEY_F1 = 59;
    private const ushort KEY_F10 = 68;
    private const ushort KEY_KP7 = 71;
    private const ushort KEY_KPMINUS = 74;
    private const ushort KEY_KPPLUS = 78;
    private const ushort KEY_KP0 = 82;
    private const ushort KEY_KPDOT = 83;
    private const ushort KEY_F11 = 87;
    private const ushort KEY_F12 = 88;
    private const ushort KEY_KPENTER = 96;
    private const ushort KEY_RIGHTCTRL = 97;
    private const ushort KEY_KPSLASH = 98;
    private const ushort KEY_RIGHTALT = 100;
    private const ushort KEY_HOME = 102;
    private const ushort KEY_UP = 103;
    private const ushort KEY_PAGEUP = 104;
    private const ushort KEY_LEFT = 105;
    private const ushort KEY_RIGHT = 106;
    private const ushort KEY_END = 107;
    private const ushort KEY_DOWN = 108;
    private const ushort KEY_PAGEDOWN = 109;
    private const ushort KEY_INSERT = 110;
    private const ushort KEY_DELETE = 111;

    private const int RELEASED = 0;

    // The character rows of the US layout by key code, unshifted and shifted: the digits' row from KEY_1, the top row
    // from KEY_Q, the home row from KEY_A, the backslash and the bottom row from KEY_Z.
    private const string DIGITS = "1234567890-=";
    private const string DIGITS_SHIFTED = "!@#$%^&*()_+";
    private const string TOP = "qwertyuiop[]";
    private const string TOP_SHIFTED = "QWERTYUIOP{}";
    private const string HOME = "asdfghjkl;'`";
    private const string HOME_SHIFTED = "ASDFGHJKL:\"~";
    private const string BOTTOM = "zxcvbnm,./";
    private const string BOTTOM_SHIFTED = "ZXCVBNM<>?";
    private const string KEYPAD_HIGH = "789-456+1230."; // KEY_KP7 to KEY_KPDOT, in evdev's order

    private static readonly string[] FUNCTION_KEYS =
        ["\x1BOP", "\x1BOQ", "\x1BOR", "\x1BOS", "\x1B[15~", "\x1B[17~", "\x1B[18~", "\x1B[19~", "\x1B[20~", "\x1B[21~"];

    #endregion

    #region Fields

    private bool m_leftShift, m_rightShift, m_leftCtrl, m_rightCtrl, m_leftAlt, m_rightAlt, m_capsLock;

    #endregion

    #region Functions

    /// <summary>
    /// The bytes of a key event: nothing for a release or a modifier, the modifiers' state updated.
    /// </summary>
    /// <param name="code">The key's evdev code.</param>
    /// <param name="value">1 down, 0 up, 2 repeat.</param>
    /// <returns>The bytes a terminal sends for the key, or none.</returns>
    public byte[] Translate(ushort code, int value)
    {
        var pressed = value != RELEASED;
        switch (code)
        {
            case KEY_LEFTSHIFT:
                m_leftShift = pressed;
                return [];
            case KEY_RIGHTSHIFT:
                m_rightShift = pressed;
                return [];
            case KEY_LEFTCTRL:
                m_leftCtrl = pressed;
                return [];
            case KEY_RIGHTCTRL:
                m_rightCtrl = pressed;
                return [];
            case KEY_LEFTALT:
                m_leftAlt = pressed;
                return [];
            case KEY_RIGHTALT:
                m_rightAlt = pressed;
                return [];
            case KEY_CAPSLOCK:
                if (value == 1)
                    m_capsLock = !m_capsLock;
                return [];
        }
        if (!pressed)
            return [];
        var text = Sequence(code) ?? Character(code);
        if (text is null)
            return [];
        var bytes = Encoding.ASCII.GetBytes(text);
        return Alt ? [0x1B, .. bytes] : bytes;
    }

    // The keys that are no character: xterm's sequences, Shift+Tab's back-tab.
    private string? Sequence(ushort code) => code switch
    {
        KEY_ENTER or KEY_KPENTER => "\r",
        KEY_BACKSPACE => "\x7F",
        KEY_TAB => Shift ? "\x1B[Z" : "\t",
        KEY_ESC => "\x1B",
        KEY_UP => "\x1B[A",
        KEY_DOWN => "\x1B[B",
        KEY_RIGHT => "\x1B[C",
        KEY_LEFT => "\x1B[D",
        KEY_HOME => "\x1B[H",
        KEY_END => "\x1B[F",
        KEY_INSERT => "\x1B[2~",
        KEY_DELETE => "\x1B[3~",
        KEY_PAGEUP => "\x1B[5~",
        KEY_PAGEDOWN => "\x1B[6~",
        >= KEY_F1 and <= KEY_F10 => FUNCTION_KEYS[code - KEY_F1],
        KEY_F11 => "\x1B[23~",
        KEY_F12 => "\x1B[24~",
        _ => null
    };

    // A character key: the US layout's character under Shift and Caps Lock, made a control character under Ctrl.
    private string? Character(ushort code)
    {
        char c;
        if (code == KEY_SPACE)
            c = ' ';
        else if (code >= KEY_1 && code <= KEY_EQUAL)
            c = (Shift ? DIGITS_SHIFTED : DIGITS)[code - KEY_1];
        else if (code >= KEY_Q && code <= KEY_RIGHTBRACE)
            c = Letter(TOP[code - KEY_Q], TOP_SHIFTED[code - KEY_Q]);
        else if (code >= KEY_A && code <= KEY_GRAVE)
            c = Letter(HOME[code - KEY_A], HOME_SHIFTED[code - KEY_A]);
        else if (code == KEY_BACKSLASH)
            c = Shift ? '|' : '\\';
        else if (code >= KEY_Z && code <= KEY_SLASH)
            c = Letter(BOTTOM[code - KEY_Z], BOTTOM_SHIFTED[code - KEY_Z]);
        else if (code >= KEY_KP7 && code <= KEY_KPDOT)
            c = KEYPAD_HIGH[code - KEY_KP7];
        else if (code == KEY_KPASTERISK)
            c = '*';
        else if (code == KEY_KPSLASH)
            c = '/';
        else
            return null;
        if (!Ctrl)
            return c.ToString();
        var control = char.ToUpperInvariant(c) switch
        {
            >= 'A' and <= 'Z' => (char)(char.ToUpperInvariant(c) - 0x40),
            '[' or '{' or '3' => '\x1B',
            '\\' or '|' or '4' => '\x1C',
            ']' or '}' or '5' => '\x1D',
            '^' or '6' or '~' or '`' => '\x1E',
            '_' or '-' or '/' or '?' or '7' => '\x1F',
            ' ' or '@' or '2' => '\0',
            '8' => '\x7F',
            _ => c
        };
        return control.ToString();
    }

    // A letter under Caps Lock and Shift (one of them, not both, gives the capital); another key under Shift alone.
    private char Letter(char unshifted, char shifted)
    {
        if (!char.IsAsciiLetter(unshifted))
            return Shift ? shifted : unshifted;
        return Shift != m_capsLock ? shifted : unshifted;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Whether a Shift key is held.
    /// </summary>
    public bool Shift => m_leftShift || m_rightShift;

    /// <summary>
    /// Whether a Ctrl key is held.
    /// </summary>
    public bool Ctrl => m_leftCtrl || m_rightCtrl;

    /// <summary>
    /// Whether an Alt key is held.
    /// </summary>
    public bool Alt => m_leftAlt || m_rightAlt;

    /// <summary>
    /// Whether Caps Lock is on.
    /// </summary>
    public bool CapsLock => m_capsLock;

    #endregion
}
