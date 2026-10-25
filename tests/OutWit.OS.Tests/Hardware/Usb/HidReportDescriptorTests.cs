using System;
using System.Collections.Generic;
using System.Linq;
using OutWit.OS.Hardware.Usb;

namespace OutWit.OS.Tests.Hardware.Usb;

/// <summary>
/// The HID report descriptors of plan step H2.2 on the host: QEMU's usb-kbd describing the boot layout, a TinyUSB
/// composite with Report IDs, a keyboard of every key as a bitmap, a mouse that names no key, and descriptors that break
/// the format — each read into its input fields and, where it names keys, a keyboard whose reports become evdev's keys.
/// </summary>
[TestFixture]
public sealed class HidReportDescriptorTests
{
    #region Constants

    /// <summary>
    /// QEMU's usb-kbd (hw/usb/dev-hid.c, qemu_keyboard_hid_report_descriptor): the boot layout described, 63 bytes.
    /// </summary>
    internal static readonly byte[] QEMU_KEYBOARD =
    [
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x75, 0x01, 0x95, 0x08, 0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7,
        0x15, 0x00, 0x25, 0x01, 0x81, 0x02, 0x95, 0x01, 0x75, 0x08, 0x81, 0x01, 0x95, 0x05, 0x75, 0x01,
        0x05, 0x08, 0x19, 0x01, 0x29, 0x05, 0x91, 0x02, 0x95, 0x01, 0x75, 0x03, 0x91, 0x01, 0x95, 0x06,
        0x75, 0x08, 0x15, 0x00, 0x25, 0xFF, 0x05, 0x07, 0x19, 0x00, 0x29, 0xFF, 0x81, 0x00, 0xC0,
    ];

    /// <summary>
    /// TinyUSB's keyboard and consumer control in one interface with Report IDs 1 and 3 (TUD_HID_REPORT_DESC_KEYBOARD,
    /// TUD_HID_REPORT_DESC_CONSUMER), as an RP2040 keyboard's firmware may send them; PocketTerm35's own is measured on
    /// the device.
    /// </summary>
    internal static readonly byte[] TINYUSB_COMPOSITE =
    [
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, 0x01,
        0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01, 0x95, 0x08, 0x75, 0x01, 0x81, 0x02,
        0x95, 0x01, 0x75, 0x08, 0x81, 0x01,
        0x05, 0x08, 0x19, 0x01, 0x29, 0x05, 0x95, 0x05, 0x75, 0x01, 0x91, 0x02,
        0x95, 0x01, 0x75, 0x03, 0x91, 0x01,
        0x05, 0x07, 0x19, 0x00, 0x2A, 0xFF, 0x00, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x95, 0x06, 0x75, 0x08, 0x81, 0x00,
        0xC0,
        0x05, 0x0C, 0x09, 0x01, 0xA1, 0x01, 0x85, 0x03,
        0x15, 0x00, 0x26, 0xFF, 0x03, 0x19, 0x00, 0x2A, 0xFF, 0x03, 0x95, 0x01, 0x75, 0x10, 0x81, 0x00,
        0xC0,
    ];

    /// <summary>
    /// A keyboard of every key: the modifiers' bits, a bit for each usage from 0x04 to 0x65, six bits of padding.
    /// </summary>
    private static readonly byte[] BITMAP_KEYBOARD =
    [
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01,
        0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02,
        0x19, 0x04, 0x29, 0x65, 0x95, 0x62, 0x81, 0x02,
        0x95, 0x06, 0x81, 0x01,
        0xC0,
    ];

    /// <summary>
    /// A boot mouse: three buttons, padding, X and Y as signed bytes.
    /// </summary>
    private static readonly byte[] MOUSE =
    [
        0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00, 0x05, 0x09, 0x19, 0x01, 0x29, 0x03,
        0x15, 0x00, 0x25, 0x01, 0x95, 0x03, 0x75, 0x01, 0x81, 0x02, 0x95, 0x01, 0x75, 0x05, 0x81, 0x01,
        0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x02, 0x81, 0x06,
        0xC0, 0xC0,
    ];

    #endregion

    #region Tests

    [Test]
    public void QemuKeyboardDescriptorIsReadTest()
    {
        var descriptor = HidReportDescriptor.Parse(QEMU_KEYBOARD);
        Assert.That(descriptor.UsesReportIds, Is.False);
        Assert.That(descriptor.InputBytes(0), Is.EqualTo(8), "the boot report's eight bytes; the LEDs are output");
        Assert.That(descriptor.Inputs.Select(field => (field.BitOffset, field.Size, field.Count, field.IsConstant, field.IsVariable)),
            Is.EqualTo(new[] { (0, 1, 8, false, true), (8, 8, 1, true, false), (16, 8, 6, false, false) }));
        var modifiers = descriptor.Inputs[0];
        Assert.That((modifiers.UsageAt(0), modifiers.UsageAt(7)), Is.EqualTo((0x7_00E0U, 0x7_00E7U)), "left Ctrl to right GUI on the keyboard page");
        var keys = descriptor.Inputs[2];
        Assert.That((keys.LogicalMinimum, keys.LogicalMaximum, keys.Usages.Count), Is.EqualTo((0L, 255L, 256)), "0x25 0xFF is 255 after a minimum of 0");
        var keyboard = HidKeyboard.From(descriptor)!;
        Assert.That((keyboard.ReportBytes, keyboard.UsesReportIds), Is.EqualTo((8, false)));
    }

    [Test]
    public void ReportProtocolKeyboardMatchesTheBootLayoutTest()
    {
        var described = HidKeyboard.From(HidReportDescriptor.Parse(QEMU_KEYBOARD))!;
        var boot = HidKeyboard.Boot();
        byte[][] reports =
        [
            [0, 0, 0x04, 0, 0, 0, 0, 0],
            [0x02, 0, 0x04, 0x16, 0, 0, 0, 0],
            [0x02, 0, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01],
            [0x22, 0, 0x16, 0x28, 0, 0, 0, 0],
            [0, 0, 0, 0, 0, 0, 0, 0],
        ];
        foreach (var report in reports)
        {
            var expected = new List<(ushort, ushort, int)>();
            var actual = new List<(ushort, ushort, int)>();
            Assert.That(described.Update(report, actual), Is.EqualTo(boot.Update(report, expected)));
            Assert.That(actual, Is.EqualTo(expected), $"the report {Convert.ToHexString(report)}");
        }
    }

    [Test]
    public void ReportIdsSeparateTheKeyboardFromItsNeighboursTest()
    {
        var descriptor = HidReportDescriptor.Parse(TINYUSB_COMPOSITE);
        Assert.That(descriptor.UsesReportIds, Is.True);
        Assert.That((descriptor.InputBytes(1), descriptor.InputBytes(3)), Is.EqualTo((9, 3)), "each report with its ID byte");
        var keyboard = HidKeyboard.From(descriptor)!;
        Assert.That((keyboard.UsesReportIds, keyboard.ReportBytes), Is.EqualTo((true, 9)));
        Assert.That(keyboard.ReportIds, Is.EquivalentTo(new[] { 1 }), "the consumer control's report names no key");
        var events = new List<(ushort Type, ushort Code, int Value)>();
        Assert.That(keyboard.Update([3, 0xE9, 0x00], events), Is.False, "Volume Up is the consumer control's");
        Assert.That(keyboard.Update([1, 0x02, 0, 0x04, 0, 0, 0, 0, 0], events), Is.True);
        Assert.That(events, Is.EqualTo(new[] { Key(42, 1), Key(30, 1), Syn() }), "left Shift, then a");
        events.Clear();
        Assert.That(keyboard.Update([1, 0, 0, 0, 0, 0, 0, 0, 0], events), Is.True);
        Assert.That(events, Is.EqualTo(new[] { Key(42, 0), Key(30, 0), Syn() }));
    }

    [Test]
    public void BitmapKeyboardReportsEveryKeyHeldTest()
    {
        var descriptor = HidReportDescriptor.Parse(BITMAP_KEYBOARD);
        Assert.That(descriptor.InputBytes(0), Is.EqualTo(14), "8 + 98 + 6 bits");
        var keyboard = HidKeyboard.From(descriptor)!;
        var report = new byte[14];
        report[0] = 0x01; // left Ctrl
        report[1] = 0xFF; // a to h: usages 0x04–0x0B
        report[4] = 0x02; // z: usage 0x1D, bit 8 + 25
        var events = new List<(ushort Type, ushort Code, int Value)>();
        Assert.That(keyboard.Update(report, events), Is.True);
        Assert.That(events, Is.EqualTo(new[]
        {
            Key(29, 1), Key(30, 1), Key(48, 1), Key(46, 1), Key(32, 1), Key(18, 1), Key(33, 1), Key(34, 1), Key(35, 1), Key(44, 1), Syn(),
        }), "nine keys at once, more than a boot report holds, in the order of their usages");
        events.Clear();
        Assert.That(keyboard.Release(events), Is.True);
        Assert.That(events.Count(item => item.Type == 1 && item.Value == 0), Is.EqualTo(10), "every key let up when the keyboard goes");
        events.Clear();
        Assert.That(keyboard.Release(events), Is.False, "nothing held any more");
    }

    [Test]
    public void MouseNamesNoKeyTest()
    {
        var descriptor = HidReportDescriptor.Parse(MOUSE);
        Assert.That(HidKeyboard.From(descriptor), Is.Null);
        var axes = descriptor.Inputs[2];
        Assert.That((axes.UsageAt(0), axes.UsageAt(1), axes.LogicalMinimum), Is.EqualTo((0x1_0030U, 0x1_0031U, -127L)));
        Assert.That((axes.Read([0, 0xFF, 0x05], 0), axes.Read([0, 0xFF, 0x05], 1)), Is.EqualTo((-1L, 5L)), "signed where the minimum is negative");
    }

    [Test]
    public void GlobalStateIsPushedAndPoppedTest()
    {
        byte[] bytes =
        [
            0x05, 0x07, 0x75, 0x08, 0x95, 0x02, 0xA4, // page 7, 2 × 8 bits, pushed
            0x05, 0x0C, 0x75, 0x10, 0x95, 0x01, 0x09, 0xE9, 0x81, 0x02, // a 16-bit consumer usage
            0xB4, 0x19, 0x04, 0x29, 0x05, 0x15, 0x00, 0x25, 0x01, 0x81, 0x02, // popped: 2 × 8 bits of keys again
        ];
        var descriptor = HidReportDescriptor.Parse(bytes);
        Assert.That(descriptor.Inputs.Select(field => (field.BitOffset, field.Size, field.Count, field.UsageAt(0))),
            Is.EqualTo(new[] { (0, 16, 1, 0xC_00E9U), (16, 8, 2, 0x7_0004U) }));
    }

    [TestCase(new byte[] { 0x05 }, TestName = "TruncatedItemIsRefusedTest")]
    [TestCase(new byte[] { 0xA1, 0x01 }, TestName = "OpenCollectionIsRefusedTest")]
    [TestCase(new byte[] { 0xC0 }, TestName = "StrayEndCollectionIsRefusedTest")]
    [TestCase(new byte[] { 0x85, 0x00 }, TestName = "ReportIdZeroIsRefusedTest")]
    [TestCase(new byte[] { 0x96, 0x01, 0x20 }, TestName = "HugeReportCountIsRefusedTest")]
    [TestCase(new byte[] { 0x76, 0x01, 0x02 }, TestName = "HugeReportSizeIsRefusedTest")]
    [TestCase(new byte[] { 0xB4 }, TestName = "PopWithoutPushIsRefusedTest")]
    [TestCase(new byte[] { 0x19, 0x00, 0x2A, 0xFF, 0xFF }, TestName = "HugeUsageRangeIsRefusedTest")]
    [TestCase(new byte[] { 0xFE, 0x05, 0x00, 0x01 }, TestName = "TruncatedLongItemIsRefusedTest")]
    public void MalformedDescriptorTest(byte[] bytes)
    {
        Assert.Throws<FormatException>(() => HidReportDescriptor.Parse(bytes));
    }

    [Test]
    public void ReportsBeyondTheirBytesReadAsZerosTest()
    {
        var keyboard = HidKeyboard.Boot();
        var events = new List<(ushort Type, ushort Code, int Value)>();
        Assert.That(keyboard.Update([0, 0, 0x04], events), Is.True, "a short report holds a");
        Assert.That(keyboard.Update([], events), Is.False, "an empty report changes nothing");
        Assert.That(keyboard.Update([0, 0], events), Is.True, "a report that stops before its keys holds none");
        Assert.That(events, Is.EqualTo(new[] { Key(30, 1), Syn(), Key(30, 0), Syn() }));
    }

    #endregion

    #region Tools

    private static (ushort, ushort, int) Key(ushort code, int value) => (1, code, value);

    private static (ushort, ushort, int) Syn() => (0, 0, 0);

    #endregion
}
