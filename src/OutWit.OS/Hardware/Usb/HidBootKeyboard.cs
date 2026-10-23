using System;
using System.Collections.Generic;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// A keyboard of the HID boot protocol (HID 1.11 appendix B.1): each report is eight bytes — the modifiers' bits, a
/// reserved byte and the usages of up to six keys held — and the keyboard reports its whole state, so a key goes down
/// when it enters a report and up when it leaves one. The keys become Linux's evdev codes, which the input protocol
/// carries (witos/input.h), by the table of Linux's hid-input (hid_keyboard), with a SYN after each report that changed
/// something; a report of ErrorRollOver, where the keyboard holds more keys than it reports, changes nothing.
/// </summary>
public sealed class HidBootKeyboard
{
    #region Constants

    /// <summary>
    /// Bytes of a boot report.
    /// </summary>
    public const int REPORT_BYTES = 8;

    /// <summary>
    /// The usage a keyboard reports in every key's place when it holds too many.
    /// </summary>
    private const byte ERROR_ROLL_OVER = 0x01;

    /// <summary>
    /// The modifiers' evdev codes by bit: left Ctrl, Shift, Alt, GUI, then the right ones.
    /// </summary>
    private static readonly ushort[] MODIFIERS = [29, 42, 56, 125, 97, 54, 100, 126];

    /// <summary>
    /// Linux's hid_keyboard table from usage 0 to 0x65: the evdev code of each key usage, zero for none.
    /// </summary>
    private static readonly ushort[] KEYS =
    [
        0, 0, 0, 0, 30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38,
        50, 49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44, 2, 3,
        4, 5, 6, 7, 8, 9, 10, 11, 28, 1, 14, 15, 57, 12, 13, 26,
        27, 43, 43, 39, 40, 41, 51, 52, 53, 58, 59, 60, 61, 62, 63, 64,
        65, 66, 67, 68, 87, 88, 99, 70, 119, 110, 102, 104, 111, 107, 109, 106,
        105, 108, 103, 69, 98, 55, 74, 78, 96, 79, 80, 81, 75, 76, 77, 71,
        72, 73, 82, 83, 86, 127,
    ];

    /// <summary>
    /// evdev's EV_KEY.
    /// </summary>
    private const ushort EVENT_KEY = 1;

    /// <summary>
    /// evdev's EV_SYN.
    /// </summary>
    private const ushort EVENT_SYN = 0;

    #endregion

    #region Fields

    private byte m_modifiers;
    private readonly byte[] m_keys = new byte[6];

    #endregion

    #region Functions

    /// <summary>
    /// The evdev code of a key usage.
    /// </summary>
    /// <param name="usage">The usage of the keyboard page.</param>
    /// <returns>The code, zero for a usage Linux maps to none.</returns>
    public static ushort KeyCode(byte usage) =>
        usage < KEYS.Length ? KEYS[usage] : usage is >= 0xE0 and <= 0xE7 ? MODIFIERS[usage - 0xE0] : (ushort)0;

    /// <summary>
    /// Takes a report: the keys and modifiers released, then those pressed, each an evdev event, and a SYN once
    /// anything changed.
    /// </summary>
    /// <param name="report">The report's bytes; fewer than eight leave the keys beyond them unheld.</param>
    /// <param name="events">Where the events go.</param>
    /// <returns>Whether the report changed anything.</returns>
    public bool Update(ReadOnlySpan<byte> report, List<(ushort Type, ushort Code, int Value)> events)
    {
        if (report.Length == 0)
            return false;
        Span<byte> keys = stackalloc byte[6];
        for (var i = 0; i < keys.Length && 2 + i < report.Length; i++)
            keys[i] = report[2 + i];
        if (keys.Contains(ERROR_ROLL_OVER))
            return false;
        var start = events.Count;
        var modifiers = report[0];
        for (var bit = 0; bit < 8; bit++)
        {
            if ((m_modifiers & 1 << bit) != 0 && (modifiers & 1 << bit) == 0)
                events.Add((EVENT_KEY, MODIFIERS[bit], 0));
        }
        foreach (var key in m_keys)
        {
            if (key != 0 && !keys.Contains(key) && KeyCode(key) is var code and not 0)
                events.Add((EVENT_KEY, code, 0));
        }
        for (var bit = 0; bit < 8; bit++)
        {
            if ((m_modifiers & 1 << bit) == 0 && (modifiers & 1 << bit) != 0)
                events.Add((EVENT_KEY, MODIFIERS[bit], 1));
        }
        foreach (var key in keys)
        {
            if (key != 0 && Array.IndexOf(m_keys, key) < 0 && KeyCode(key) is var code and not 0)
                events.Add((EVENT_KEY, code, 1));
        }
        m_modifiers = modifiers;
        keys.CopyTo(m_keys);
        if (events.Count == start)
            return false;
        events.Add((EVENT_SYN, 0, 0));
        return true;
    }

    #endregion
}
