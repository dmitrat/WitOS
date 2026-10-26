using System.Collections.Generic;

namespace OutWit.OS.Hardware.I8042;

/// <summary>
/// The PC keyboard's scan codes of set 1, as an i8042 that translates gives them (plan step H5.2, ADR 0031), turned into
/// evdev key events: a code below 0x80 is a key's press and the same code with bit 7 its release; 0xE0 introduces the
/// keys of the enhanced keyboard, whose own table this class holds, and 0xE1 the Pause key, whose six bytes the keyboard
/// sends at its press alone and which this class reports as a press at the first and a release at the last. The
/// main block's codes are evdev's own key codes, which Linux took from set 1. A press of a key already down is the
/// keyboard's typematic repeat. The two shifts a keyboard fakes around the enhanced keys (0xE0 0x2A and 0xE0 0x36, with
/// their releases) and the controller's answers (0xFA, 0xFE, 0xEE, 0x00, 0xFF) give nothing.
/// </summary>
public sealed class ScanCodeSet1
{
    #region Constants

    /// <summary>
    /// evdev's key event type.
    /// </summary>
    public const ushort EV_KEY = 1;

    /// <summary>
    /// evdev's synchronization event type, which ends each report.
    /// </summary>
    public const ushort EV_SYN = 0;

    /// <summary>
    /// A key's release, as evdev's value.
    /// </summary>
    public const int RELEASED = 0;

    /// <summary>
    /// A key's press, as evdev's value.
    /// </summary>
    public const int PRESSED = 1;

    /// <summary>
    /// A key's typematic repeat, as evdev's value.
    /// </summary>
    public const int REPEATED = 2;

    private const byte EXTENDED = 0xE0;
    private const byte PAUSE_PREFIX = 0xE1;
    private const byte RELEASE = 0x80;
    private const byte LAST_MAIN_CODE = 0x53; // the keypad's Del, evdev's KEY_KPDOT
    private const ushort KEY_102ND = 86;
    private const ushort KEY_F11 = 87;
    private const ushort KEY_F12 = 88;
    private const ushort KEY_PAUSE = 119;
    private const int PAUSE_BYTES = 5; // 0xE1 0x1D 0x45 0xE1 0x9D 0xC5: the first byte, then five

    private static readonly Dictionary<byte, ushort> EXTENDED_KEYS = new()
    {
        [0x1C] = 96, // KEY_KPENTER
        [0x1D] = 97, // KEY_RIGHTCTRL
        [0x35] = 98, // KEY_KPSLASH
        [0x37] = 99, // KEY_SYSRQ, Print Screen
        [0x38] = 100, // KEY_RIGHTALT
        [0x47] = 102, // KEY_HOME
        [0x48] = 103, // KEY_UP
        [0x49] = 104, // KEY_PAGEUP
        [0x4B] = 105, // KEY_LEFT
        [0x4D] = 106, // KEY_RIGHT
        [0x4F] = 107, // KEY_END
        [0x50] = 108, // KEY_DOWN
        [0x51] = 109, // KEY_PAGEDOWN
        [0x52] = 110, // KEY_INSERT
        [0x53] = 111, // KEY_DELETE
        [0x5B] = 125, // KEY_LEFTMETA
        [0x5C] = 126, // KEY_RIGHTMETA
        [0x5D] = 127, // KEY_COMPOSE, the menu key
    };

    #endregion

    #region Fields

    private readonly HashSet<ushort> m_down = [];

    private bool m_extended;

    private int m_pauseLeft;

    #endregion

    #region Functions

    /// <summary>
    /// Takes the next byte the controller delivered.
    /// </summary>
    /// <param name="code">The byte.</param>
    /// <returns>The key event it completes, or null.</returns>
    public (ushort Code, int Value)? Take(byte code)
    {
        if (m_pauseLeft > 0)
        {
            // The Pause key's sequence: its press reported at its first byte, its release at its last.
            --m_pauseLeft;
            return m_pauseLeft == 0 ? (KEY_PAUSE, RELEASED) : null;
        }
        if (code == PAUSE_PREFIX)
        {
            m_extended = false;
            m_pauseLeft = PAUSE_BYTES;
            return (KEY_PAUSE, PRESSED);
        }
        if (code == EXTENDED)
        {
            m_extended = true;
            return null;
        }
        var extended = m_extended;
        m_extended = false;
        var released = (code & RELEASE) != 0;
        var make = (byte)(code & ~RELEASE);
        ushort key;
        if (extended)
        {
            if (!EXTENDED_KEYS.TryGetValue(make, out key))
                return null; // a fake shift, or an enhanced key the console has no use for
        }
        else if (make >= 1 && make <= LAST_MAIN_CODE)
        {
            key = make;
        }
        else
        {
            key = make switch
            {
                0x56 => KEY_102ND,
                0x57 => KEY_F11,
                0x58 => KEY_F12,
                _ => 0
            };
            if (key == 0)
                return null; // the controller's answers, and codes no key of a PC keyboard gives
        }
        if (released)
            return m_down.Remove(key) ? (key, RELEASED) : null;
        return (key, m_down.Add(key) ? PRESSED : REPEATED);
    }

    /// <summary>
    /// Releases every key held down, as when the keyboard goes away.
    /// </summary>
    /// <returns>The releases.</returns>
    public IReadOnlyList<(ushort Code, int Value)> ReleaseAll()
    {
        var released = new List<(ushort Code, int Value)>();
        foreach (var key in m_down)
            released.Add((key, RELEASED));
        m_down.Clear();
        return released;
    }

    #endregion
}
