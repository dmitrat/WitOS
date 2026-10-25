using System;
using System.Collections.Generic;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// A HID report descriptor (HID 1.11 §6.2.2, plan step H2.2) read into the fields of its input reports, its items walked
/// as Linux's hid-core walks them: the global state with Push and Pop, the local usages a main item takes and clears,
/// Usage Minimum and Maximum expanded into the list, the usage page of the main item joined to every usage of two bytes
/// or less while a usage of four carries its own, each Input item a field at its bit offset within the report its
/// Report ID names. A field's usages are extended usages, the page in the high 16 bits. What a descriptor may hold is
/// bounded, and one that breaks a bound, ends inside an item or leaves a collection open is refused.
/// </summary>
public sealed class HidReportDescriptor
{
    #region Constants

    /// <summary>
    /// The usage page of keyboards and keypads.
    /// </summary>
    public const uint PAGE_KEYBOARD = 7;

    /// <summary>
    /// The usages one main item takes, Usage Minimum and Maximum expanded.
    /// </summary>
    private const int MAX_USAGES = 4096;

    /// <summary>
    /// A field's Report Count.
    /// </summary>
    private const int MAX_ELEMENTS = 4096;

    /// <summary>
    /// A field's Report Size in bits.
    /// </summary>
    private const int MAX_SIZE = 256;

    /// <summary>
    /// The bits of one input report.
    /// </summary>
    private const int MAX_REPORT_BITS = 8 * 4096;

    /// <summary>
    /// The nesting of collections, and of the global state's pushes.
    /// </summary>
    private const int MAX_DEPTH = 32;

    /// <summary>
    /// The prefix of a long item, whose size and tag follow it.
    /// </summary>
    private const byte LONG_ITEM = 0xFE;

    // Item types.
    private const int MAIN = 0;
    private const int GLOBAL = 1;
    private const int LOCAL = 2;

    // Main items.
    private const int INPUT = 0x8;
    private const int COLLECTION = 0xA;
    private const int END_COLLECTION = 0xC;

    // Global items.
    private const int USAGE_PAGE = 0x0;
    private const int LOGICAL_MINIMUM = 0x1;
    private const int LOGICAL_MAXIMUM = 0x2;
    private const int REPORT_SIZE = 0x7;
    private const int REPORT_ID = 0x8;
    private const int REPORT_COUNT = 0x9;
    private const int PUSH = 0xA;
    private const int POP = 0xB;

    // Local items.
    private const int USAGE = 0x0;
    private const int USAGE_MINIMUM = 0x1;
    private const int USAGE_MAXIMUM = 0x2;

    #endregion

    #region Fields

    private readonly Dictionary<int, int> m_inputBits;

    #endregion

    #region Constructors

    private HidReportDescriptor(IReadOnlyList<HidField> inputs, bool usesReportIds, Dictionary<int, int> inputBits)
    {
        Inputs = inputs;
        UsesReportIds = usesReportIds;
        m_inputBits = inputBits;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads a report descriptor.
    /// </summary>
    /// <param name="bytes">The descriptor as the device answered it.</param>
    /// <returns>The descriptor's input fields.</returns>
    /// <exception cref="FormatException">An item runs past the bytes, a bound is broken, or the collections do not balance.</exception>
    public static HidReportDescriptor Parse(ReadOnlySpan<byte> bytes)
    {
        var inputs = new List<HidField>();
        var bits = new Dictionary<int, int>();
        var global = new Global();
        var pushed = new Stack<Global>();
        var usages = new List<(uint Value, bool Extended)>();
        uint? minimum = null;
        var depth = 0;
        var ids = false;
        for (var at = 0; at < bytes.Length;)
        {
            var prefix = bytes[at];
            if (prefix == LONG_ITEM)
            {
                if (at + 2 >= bytes.Length || at + 3 + bytes[at + 1] > bytes.Length)
                    throw new FormatException($"A long item at {at} runs past the descriptor's {bytes.Length} bytes.");
                at += 3 + bytes[at + 1];
                continue;
            }
            var size = (prefix & 3) == 3 ? 4 : prefix & 3;
            if (at + 1 + size > bytes.Length)
                throw new FormatException($"An item at {at} runs past the descriptor's {bytes.Length} bytes.");
            uint value = 0;
            for (var i = 0; i < size; i++)
                value |= (uint)bytes[at + 1 + i] << 8 * i;
            long signed = size switch { 1 => (sbyte)value, 2 => (short)value, 4 => (int)value, _ => 0 };
            var tag = prefix >> 4;
            switch (prefix >> 2 & 3)
            {
                case MAIN:
                    switch (tag)
                    {
                        case INPUT:
                            AddInput(inputs, bits, global, usages, value);
                            break;
                        case COLLECTION:
                            if (++depth > MAX_DEPTH)
                                throw new FormatException($"Collections nested deeper than {MAX_DEPTH} at {at}.");
                            break;
                        case END_COLLECTION:
                            if (--depth < 0)
                                throw new FormatException($"An End Collection at {at} closes no collection.");
                            break;
                    }
                    usages.Clear();
                    minimum = null;
                    break;
                case GLOBAL:
                    switch (tag)
                    {
                        case USAGE_PAGE:
                            global.Page = value & 0xFFFF;
                            break;
                        case LOGICAL_MINIMUM:
                            global.LogicalMinimum = signed;
                            break;
                        case LOGICAL_MAXIMUM:
                            // Unsigned unless the minimum is negative, as Linux reads it: 0x25 0xFF is 255 after a minimum of 0.
                            global.LogicalMaximum = global.LogicalMinimum < 0 ? signed : value;
                            break;
                        case REPORT_SIZE:
                            global.Size = value <= MAX_SIZE ? (int)value : throw new FormatException($"A Report Size of {value} bits at {at}.");
                            break;
                        case REPORT_ID:
                            global.ReportId = value is >= 1 and <= 255 ? (int)value : throw new FormatException($"A Report ID of {value} at {at}.");
                            ids = true;
                            break;
                        case REPORT_COUNT:
                            global.Count = value <= MAX_ELEMENTS ? (int)value : throw new FormatException($"A Report Count of {value} at {at}.");
                            break;
                        case PUSH:
                            if (pushed.Count >= MAX_DEPTH)
                                throw new FormatException($"The global state pushed deeper than {MAX_DEPTH} at {at}.");
                            pushed.Push(global);
                            break;
                        case POP:
                            global = pushed.Count > 0 ? pushed.Pop() : throw new FormatException($"A Pop at {at} with nothing pushed.");
                            break;
                    }
                    break;
                case LOCAL:
                    switch (tag)
                    {
                        case USAGE:
                            if (usages.Count >= MAX_USAGES)
                                throw new FormatException($"More than {MAX_USAGES} usages before the main item at {at}.");
                            usages.Add((value, size == 4));
                            break;
                        case USAGE_MINIMUM:
                            minimum = value;
                            break;
                        case USAGE_MAXIMUM:
                            // From the minimum, or from 0 where none came, to the maximum, each usage as wide as the
                            // maximum's item, as Linux adds them; a maximum below the minimum adds none.
                            var low = minimum ?? 0;
                            if (value >= low && usages.Count + (value - low) >= MAX_USAGES)
                                throw new FormatException($"A usage range of {low:x}–{value:x} at {at}.");
                            for (long usage = low; usage <= value; usage++)
                                usages.Add(((uint)usage, size == 4));
                            minimum = null;
                            break;
                    }
                    break;
            }
            at += 1 + size;
        }
        if (depth != 0)
            throw new FormatException($"{depth} collections left open at the descriptor's end.");
        return new HidReportDescriptor(inputs, ids, bits);
    }

    /// <summary>
    /// The bytes of an input report with its Report ID byte, where the descriptor uses them.
    /// </summary>
    /// <param name="reportId">The report, zero where the descriptor names none.</param>
    /// <returns>The bytes, zero for a report the descriptor does not describe.</returns>
    public int InputBytes(int reportId) =>
        m_inputBits.TryGetValue(reportId, out var bits) ? (bits + 7) / 8 + (UsesReportIds ? 1 : 0) : 0;

    private static void AddInput(List<HidField> inputs, Dictionary<int, int> bits, Global global, List<(uint Value, bool Extended)> usages, uint flags)
    {
        if (global.Size == 0 || global.Count == 0)
            return;
        bits.TryGetValue(global.ReportId, out var offset);
        var end = offset + global.Size * global.Count;
        if (end > MAX_REPORT_BITS)
            throw new FormatException($"Input report {global.ReportId} of more than {MAX_REPORT_BITS} bits.");
        var resolved = new uint[usages.Count];
        for (var i = 0; i < usages.Count; i++)
            resolved[i] = usages[i].Extended ? usages[i].Value : global.Page << 16 | usages[i].Value & 0xFFFF;
        inputs.Add(new HidField(global.ReportId, offset, global.Size, global.Count, flags, global.LogicalMinimum, global.LogicalMaximum, resolved));
        bits[global.ReportId] = end;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The fields of every input report, in the descriptor's order, constant ones among them.
    /// </summary>
    public IReadOnlyList<HidField> Inputs { get; }

    /// <summary>
    /// Whether the descriptor names Report IDs, so that each report starts with its ID's byte.
    /// </summary>
    public bool UsesReportIds { get; }

    #endregion

    #region Nested Types

    private struct Global
    {
        public uint Page;
        public long LogicalMinimum;
        public long LogicalMaximum;
        public int Size;
        public int Count;
        public int ReportId;
    }

    #endregion
}

/// <summary>
/// A field of an input report: its elements of <see cref="Size"/> bits from <see cref="BitOffset"/>, after the Report ID
/// byte where there is one.
/// </summary>
/// <param name="ReportId">The report, zero where the descriptor names none.</param>
/// <param name="BitOffset">The field's first bit within the report's data.</param>
/// <param name="Size">The bits of an element.</param>
/// <param name="Count">The elements.</param>
/// <param name="Flags">The Input item's data: Constant in bit 0, Variable in bit 1 (an array otherwise).</param>
/// <param name="LogicalMinimum">The least value an element takes.</param>
/// <param name="LogicalMaximum">The greatest.</param>
/// <param name="Usages">The extended usages: element n's for a variable field (the last for the elements beyond them), the
/// usage a value names from the minimum on for an array.</param>
public sealed record HidField(int ReportId, int BitOffset, int Size, int Count, uint Flags, long LogicalMinimum, long LogicalMaximum,
    IReadOnlyList<uint> Usages)
{
    #region Functions

    /// <summary>
    /// The usage of a variable field's element.
    /// </summary>
    /// <param name="index">The element.</param>
    /// <returns>Its usage, zero for none.</returns>
    public uint UsageAt(int index) => Usages.Count == 0 ? 0 : Usages[Math.Min(index, Usages.Count - 1)];

    /// <summary>
    /// Reads an element from a report's data, the bits beyond the data read as zeros, signed where the logical minimum
    /// is negative.
    /// </summary>
    /// <param name="data">The report's data, after its Report ID byte.</param>
    /// <param name="index">The element.</param>
    /// <returns>Its value.</returns>
    public long Read(ReadOnlySpan<byte> data, int index)
    {
        var start = BitOffset + index * Size;
        var bits = Math.Min(Size, 64);
        ulong raw = 0;
        for (var bit = 0; bit < bits; bit++)
        {
            var at = start + bit;
            if (at / 8 < data.Length && (data[at / 8] >> at % 8 & 1) != 0)
                raw |= 1UL << bit;
        }
        if (LogicalMinimum < 0 && bits < 64 && (raw >> bits - 1 & 1) != 0)
            raw |= ulong.MaxValue << bits;
        return (long)raw;
    }

    #endregion

    #region Properties

    /// <summary>
    /// A constant field: padding, never data.
    /// </summary>
    public bool IsConstant => (Flags & 1) != 0;

    /// <summary>
    /// A variable field, whose element n reports usage n; an array's elements name the usages held otherwise.
    /// </summary>
    public bool IsVariable => (Flags & 2) != 0;

    #endregion
}
