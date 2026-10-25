using System;
using System.Collections.Generic;
using System.Linq;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// A HID keyboard (HID 1.11, plan steps H2.1 and H2.2): the input fields of the keyboard page its report descriptor
/// names — the modifiers' bits, an array of the keys held or a bitmap of every key, in reports of their own Report IDs or
/// beside a mouse's and a consumer control's — or the boot protocol's layout (appendix B.1: the modifiers' byte, a
/// reserved byte, six keys). Each report gives its fields' whole state, so a key goes down when it enters a report and up
/// when it leaves one. The keys become Linux's evdev codes, which the input protocol carries (witos/input.h), by the
/// table of Linux's hid-input (hid_keyboard), with a SYN after each report that changed something; a report of
/// ErrorRollOver, where the keyboard holds more keys than it reports, changes nothing.
/// </summary>
public sealed class HidKeyboard
{
    #region Constants

    /// <summary>
    /// Bytes of a boot protocol report.
    /// </summary>
    public const int BOOT_REPORT_BYTES = 8;

    /// <summary>
    /// The usage a keyboard reports in every key's place when it holds too many.
    /// </summary>
    private const int ERROR_ROLL_OVER = 0x01;

    /// <summary>
    /// The first usage of a key: those below report no key or an error.
    /// </summary>
    private const int FIRST_KEY = 0x04;

    /// <summary>
    /// The usage of left Ctrl, the first modifier; right GUI is seven above it.
    /// </summary>
    private const int FIRST_MODIFIER = 0xE0;

    /// <summary>
    /// The keys one report holds at once that are kept; a bitmap of every key may name more.
    /// </summary>
    private const int MAX_KEYS = 32;

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

    private readonly Dictionary<int, Report> m_reports;

    #endregion

    #region Constructors

    private HidKeyboard(Dictionary<int, Report> reports, bool usesReportIds, int reportBytes)
    {
        m_reports = reports;
        UsesReportIds = usesReportIds;
        ReportBytes = reportBytes;
    }

    #endregion

    #region Functions

    /// <summary>
    /// A keyboard of the boot protocol.
    /// </summary>
    /// <returns>The keyboard.</returns>
    public static HidKeyboard Boot()
    {
        var modifiers = Enumerable.Range(FIRST_MODIFIER, 8).Select(usage => HidReportDescriptor.PAGE_KEYBOARD << 16 | (uint)usage).ToArray();
        var keys = Enumerable.Range(0, 256).Select(usage => HidReportDescriptor.PAGE_KEYBOARD << 16 | (uint)usage).ToArray();
        var fields = new[]
        {
            new HidField(0, 0, 1, 8, 2, 0, 1, modifiers),
            new HidField(0, 16, 8, 6, 0, 0, 255, keys),
        };
        return new HidKeyboard(new Dictionary<int, Report> { [0] = new Report(fields) }, false, BOOT_REPORT_BYTES);
    }

    /// <summary>
    /// The keyboard a report descriptor describes: every report that holds a field of keys.
    /// </summary>
    /// <param name="descriptor">The descriptor.</param>
    /// <returns>The keyboard, or null where no input field names a key.</returns>
    public static HidKeyboard? From(HidReportDescriptor descriptor)
    {
        var reports = descriptor.Inputs.Where(field => !field.IsConstant && field.Size <= 32 && field.Usages.Any(IsKey))
            .GroupBy(field => field.ReportId)
            .ToDictionary(group => group.Key, group => new Report(group.ToArray()));
        if (reports.Count == 0)
            return null;
        return new HidKeyboard(reports, descriptor.UsesReportIds, reports.Keys.Max(descriptor.InputBytes));
    }

    /// <summary>
    /// The evdev code of a key usage.
    /// </summary>
    /// <param name="usage">The usage of the keyboard page.</param>
    /// <returns>The code, zero for a usage Linux maps to none.</returns>
    public static ushort KeyCode(byte usage) =>
        usage < KEYS.Length ? KEYS[usage] : usage is >= FIRST_MODIFIER and < FIRST_MODIFIER + 8 ? MODIFIERS[usage - FIRST_MODIFIER] : (ushort)0;

    /// <summary>
    /// Takes a report: the keys and modifiers released, then those pressed, each an evdev event, and a SYN once
    /// anything changed. A report of another Report ID — a mouse's, a consumer control's — changes nothing.
    /// </summary>
    /// <param name="report">The report's bytes, its Report ID first where the keyboard uses them; bits beyond the bytes
    /// read as zeros, so the keys beyond them are unheld.</param>
    /// <param name="events">Where the events go.</param>
    /// <returns>Whether the report changed anything.</returns>
    public bool Update(ReadOnlySpan<byte> report, List<(ushort Type, ushort Code, int Value)> events)
    {
        if (report.Length == 0)
            return false;
        var data = UsesReportIds ? report[1..] : report;
        if (!m_reports.TryGetValue(UsesReportIds ? report[0] : 0, out var state))
            return false;
        byte modifiers = 0;
        var keys = new List<byte>();
        foreach (var field in state.Fields)
        {
            for (var i = 0; i < field.Count; i++)
            {
                uint usage;
                if (field.IsVariable)
                {
                    if (field.Read(data, i) == 0)
                        continue;
                    usage = field.UsageAt(i);
                }
                else
                {
                    var value = field.Read(data, i);
                    if (value < field.LogicalMinimum || value > field.LogicalMaximum || value - field.LogicalMinimum >= field.Usages.Count)
                        continue;
                    usage = field.Usages[(int)(value - field.LogicalMinimum)];
                    if (usage == (HidReportDescriptor.PAGE_KEYBOARD << 16 | ERROR_ROLL_OVER))
                        return false;
                }
                if (usage >> 16 != HidReportDescriptor.PAGE_KEYBOARD)
                    continue;
                var id = (int)(usage & 0xFFFF);
                if (id is >= FIRST_MODIFIER and < FIRST_MODIFIER + 8)
                    modifiers |= (byte)(1 << id - FIRST_MODIFIER);
                else if (id is >= FIRST_KEY and <= 0xFF && !keys.Contains((byte)id) && keys.Count < MAX_KEYS)
                    keys.Add((byte)id);
            }
        }
        return Change(state, modifiers, keys, events);
    }

    /// <summary>
    /// Releases every key held, as when the keyboard goes: what a client saw pressed does not stay down.
    /// </summary>
    /// <param name="events">Where the events go.</param>
    /// <returns>Whether any key was held.</returns>
    public bool Release(List<(ushort Type, ushort Code, int Value)> events)
    {
        var any = false;
        foreach (var state in m_reports.Values)
            any |= Change(state, 0, [], events);
        return any;
    }

    // The modifiers released, the keys released in the order they came, then the modifiers and the keys pressed.
    private static bool Change(Report state, byte modifiers, List<byte> keys, List<(ushort Type, ushort Code, int Value)> events)
    {
        var start = events.Count;
        for (var bit = 0; bit < 8; bit++)
        {
            if ((state.Modifiers & 1 << bit) != 0 && (modifiers & 1 << bit) == 0)
                events.Add((EVENT_KEY, MODIFIERS[bit], 0));
        }
        foreach (var key in state.Keys)
        {
            if (!keys.Contains(key) && KeyCode(key) is var code and not 0)
                events.Add((EVENT_KEY, code, 0));
        }
        for (var bit = 0; bit < 8; bit++)
        {
            if ((state.Modifiers & 1 << bit) == 0 && (modifiers & 1 << bit) != 0)
                events.Add((EVENT_KEY, MODIFIERS[bit], 1));
        }
        foreach (var key in keys)
        {
            if (!state.Keys.Contains(key) && KeyCode(key) is var code and not 0)
                events.Add((EVENT_KEY, code, 1));
        }
        state.Modifiers = modifiers;
        state.Keys = keys;
        if (events.Count == start)
            return false;
        events.Add((EVENT_SYN, 0, 0));
        return true;
    }

    private static bool IsKey(uint usage) =>
        usage >> 16 == HidReportDescriptor.PAGE_KEYBOARD && (usage & 0xFFFF) is >= FIRST_KEY and < FIRST_MODIFIER + 8;

    #endregion

    #region Properties

    /// <summary>
    /// The bytes of the keyboard's largest report, its Report ID among them: what a transfer asks for at least.
    /// </summary>
    public int ReportBytes { get; }

    /// <summary>
    /// Whether each report starts with its Report ID.
    /// </summary>
    public bool UsesReportIds { get; }

    /// <summary>
    /// The reports that hold keys: zero alone where the keyboard names no Report ID.
    /// </summary>
    public IReadOnlyCollection<int> ReportIds => m_reports.Keys;

    #endregion

    #region Nested Types

    private sealed class Report(IReadOnlyList<HidField> fields)
    {
        public IReadOnlyList<HidField> Fields { get; } = fields;

        public byte Modifiers { get; set; }

        public List<byte> Keys { get; set; } = [];
    }

    #endregion
}
