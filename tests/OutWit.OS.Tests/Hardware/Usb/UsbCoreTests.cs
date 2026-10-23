using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Usb;

namespace OutWit.OS.Tests.Hardware.Usb;

/// <summary>
/// The USB core of plan step H2.1 on the host: the descriptors QEMU's usb-kbd answers with, the setup packets of
/// enumeration and the HID boot protocol, a boot keyboard's reports turned into evdev's keys, the xHCI rings' cycle
/// bits across their Link TRB, and the contexts the controller is given.
/// </summary>
[TestFixture]
public sealed class UsbCoreTests
{
    #region Constants

    // QEMU's usb-kbd as a USB 2.0 device (hw/usb/dev-hid.c, desc_device_keyboard2): the device descriptor, then the
    // configuration with its interface, HID and endpoint descriptors.
    private static readonly byte[] DEVICE =
        [0x12, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x40, 0x27, 0x06, 0x01, 0x00, 0x00, 0x00, 0x01, 0x04, 0x0B, 0x01];

    private static readonly byte[] CONFIGURATION =
    [
        0x09, 0x02, 0x22, 0x00, 0x01, 0x01, 0x07, 0xA0, 0x32,
        0x09, 0x04, 0x00, 0x00, 0x01, 0x03, 0x01, 0x01, 0x00,
        0x09, 0x21, 0x11, 0x01, 0x00, 0x01, 0x22, 0x3F, 0x00,
        0x07, 0x05, 0x81, 0x03, 0x08, 0x00, 0x07,
    ];

    #endregion

    #region Tests

    [Test]
    public void KeyboardDescriptorsAreReadTest()
    {
        var device = UsbDescriptors.ReadDevice(DEVICE);
        Assert.That((device.Usb, device.MaxPacketSize0, device.Vendor, device.Product, device.Configurations),
            Is.EqualTo(((ushort)0x0200, (byte)64, (ushort)0x0627, (ushort)0x0001, (byte)1)));
        var first = UsbDescriptors.ReadDevice(DEVICE.AsSpan(0, 8));
        Assert.That((first.MaxPacketSize0, first.Vendor), Is.EqualTo(((byte)64, (ushort)0)), "eight bytes give bMaxPacketSize0 alone");
        Assert.That(UsbDescriptors.TotalLength(CONFIGURATION.AsSpan(0, 9)), Is.EqualTo(34));
        var configuration = UsbDescriptors.ReadConfiguration(CONFIGURATION);
        Assert.That((configuration.Value, configuration.InterfaceCount, configuration.Interfaces.Count), Is.EqualTo(((byte)1, (byte)1, 1)));
        var keyboard = configuration.Interfaces[0];
        Assert.That(keyboard.IsBootKeyboard, Is.True);
        Assert.That(keyboard.ReportLength, Is.EqualTo(0x3F), "the HID descriptor's report descriptor length");
        var endpoint = keyboard.Endpoints[0];
        Assert.That((endpoint.Number, endpoint.IsIn, endpoint.Type, endpoint.PacketBytes, endpoint.Interval),
            Is.EqualTo((1, true, UsbEndpoint.INTERRUPT, 8, (byte)7)));
        Assert.That(XhciContexts.Index(endpoint), Is.EqualTo(3), "endpoint 1 IN is Device Context Index 3");
    }

    [Test]
    public void MalformedDescriptorsAreRefusedTest()
    {
        Assert.Throws<FormatException>(() => UsbDescriptors.ReadDevice(CONFIGURATION));
        Assert.Throws<FormatException>(() => UsbDescriptors.ReadDevice(DEVICE.AsSpan(0, 4)));
        Assert.Throws<FormatException>(() => UsbDescriptors.ReadConfiguration(CONFIGURATION.AsSpan(0, 20)), "fewer bytes than wTotalLength");
        var overrun = (byte[])CONFIGURATION.Clone();
        overrun[27] = 0x20; // the endpoint descriptor claims 32 bytes
        Assert.Throws<FormatException>(() => UsbDescriptors.ReadConfiguration(overrun));
        var endpointFirst = (byte[])CONFIGURATION.Clone();
        endpointFirst.AsSpan(27, 7).CopyTo(endpointFirst.AsSpan(9));
        endpointFirst.AsSpan(9, 7).Clear();
        endpointFirst[2] = 16;
        Assert.Throws<FormatException>(() => UsbDescriptors.ReadConfiguration(endpointFirst.AsSpan(0, 16)), "an endpoint before any interface");
    }

    [Test]
    public void SetupPacketsAreEncodedTest()
    {
        Assert.That(Bytes(UsbSetup.GetDescriptor(UsbDescriptors.DEVICE, 0, 18)), Is.EqualTo(new byte[] { 0x80, 0x06, 0x00, 0x01, 0x00, 0x00, 0x12, 0x00 }));
        Assert.That(Bytes(UsbSetup.GetDescriptor(UsbDescriptors.CONFIGURATION, 0, 34)), Is.EqualTo(new byte[] { 0x80, 0x06, 0x00, 0x02, 0x00, 0x00, 0x22, 0x00 }));
        Assert.That(Bytes(UsbSetup.SetConfiguration(1)), Is.EqualTo(new byte[] { 0x00, 0x09, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00 }));
        Assert.That(Bytes(UsbSetup.SetProtocol(2, UsbSetup.BOOT_PROTOCOL)), Is.EqualTo(new byte[] { 0x21, 0x0B, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00 }));
        Assert.That(Bytes(UsbSetup.SetIdle(0, 0)), Is.EqualTo(new byte[] { 0x21, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
        Assert.That(UsbSetup.GetDescriptor(UsbDescriptors.DEVICE, 0, 8).IsIn, Is.True);
        Assert.That(UsbSetup.SetConfiguration(1).IsIn, Is.False);
    }

    [Test]
    public void BootReportsBecomeKeysTest()
    {
        var keyboard = new HidBootKeyboard();
        var events = new List<(ushort Type, ushort Code, int Value)>();
        Assert.That(keyboard.Update([0, 0, 0x04, 0, 0, 0, 0, 0], events), Is.True);
        Assert.That(events, Is.EqualTo(new[] { Key(30, 1), Syn() }), "a goes down");
        events.Clear();
        Assert.That(keyboard.Update([0, 0, 0x04, 0, 0, 0, 0, 0], events), Is.False, "the same state again changes nothing");
        Assert.That(events, Is.Empty);
        Assert.That(keyboard.Update([0x02, 0, 0x04, 0x16, 0, 0, 0, 0], events), Is.True);
        Assert.That(events, Is.EqualTo(new[] { Key(42, 1), Key(31, 1), Syn() }), "left Shift, then s");
        events.Clear();
        Assert.That(keyboard.Update([0x02, 0, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01], events), Is.False, "ErrorRollOver changes nothing");
        Assert.That(keyboard.Update([0, 0, 0, 0, 0, 0, 0, 0], events), Is.True);
        Assert.That(events, Is.EqualTo(new[] { Key(42, 0), Key(30, 0), Key(31, 0), Syn() }), "every key released, the modifiers first");
    }

    [Test]
    public void UsagesMapToEvdevCodesTest()
    {
        Assert.That(HidBootKeyboard.KeyCode(0x04), Is.EqualTo(30), "a");
        Assert.That(HidBootKeyboard.KeyCode(0x1D), Is.EqualTo(44), "z");
        Assert.That(HidBootKeyboard.KeyCode(0x1E), Is.EqualTo(2), "1");
        Assert.That(HidBootKeyboard.KeyCode(0x27), Is.EqualTo(11), "0");
        Assert.That(HidBootKeyboard.KeyCode(0x28), Is.EqualTo(28), "Enter");
        Assert.That(HidBootKeyboard.KeyCode(0x2C), Is.EqualTo(57), "Space");
        Assert.That(HidBootKeyboard.KeyCode(0x45), Is.EqualTo(88), "F12");
        Assert.That(HidBootKeyboard.KeyCode(0x4F), Is.EqualTo(106), "Right");
        Assert.That(HidBootKeyboard.KeyCode(0x52), Is.EqualTo(103), "Up");
        Assert.That(HidBootKeyboard.KeyCode(0x65), Is.EqualTo(127), "Application");
        Assert.That(HidBootKeyboard.KeyCode(0xE5), Is.EqualTo(54), "right Shift");
        Assert.That(HidBootKeyboard.KeyCode(0x66), Is.Zero, "beyond the table");
    }

    [Test]
    public void ProducerRingTogglesItsCycleAtTheLinkTest()
    {
        var segment = new ArrayBuffer(4 * XhciTrb.BYTES, 0x10000);
        var ring = new XhciRing(segment);
        Assert.That(ring.Capacity, Is.EqualTo(3));
        var first = ring.Enqueue(XhciTrb.Of(XhciTrb.NORMAL, 0xAA, 8));
        Assert.That(first, Is.EqualTo(0x10000UL));
        Assert.That(XhciTrb.Read(segment.Bytes).Cycle, Is.True, "the first lap's cycle is 1");
        ring.Enqueue(XhciTrb.Of(XhciTrb.NORMAL));
        ring.Enqueue(XhciTrb.Of(XhciTrb.NORMAL));
        var link = XhciTrb.Read(segment.Bytes[(3 * XhciTrb.BYTES)..]);
        Assert.That((link.Type, link.Parameter, link.Cycle, (link.Control & XhciTrb.TOGGLE_CYCLE) != 0),
            Is.EqualTo((XhciTrb.LINK, 0x10000UL, true, true)), "the Link TRB handed over with the lap's cycle");
        var again = ring.Enqueue(XhciTrb.Of(XhciTrb.NORMAL, 0xBB));
        Assert.That(again, Is.EqualTo(0x10000UL), "the second lap starts at the segment");
        var trb = XhciTrb.Read(segment.Bytes);
        Assert.That((trb.Parameter, trb.Cycle), Is.EqualTo((0xBBUL, false)), "the second lap's cycle is 0");
        Assert.That(XhciTrb.Read(segment.Bytes[XhciTrb.BYTES..]).Cycle, Is.True, "an entry of the last lap keeps the old cycle, where the controller stops");
    }

    [Test]
    public void EventRingTakesWhatTheControllerWroteTest()
    {
        var segment = new ArrayBuffer(16 * XhciTrb.BYTES, 0x20000);
        var events = new XhciEventRing(segment);
        Assert.That(events.TryDequeue(out _), Is.False, "a zeroed ring holds no event");
        for (var i = 0; i < 16; i++)
            Write(segment, i, XhciTrb.Of(XhciTrb.TRANSFER_EVENT, (ulong)i, XhciTrb.SUCCESS << 24, XhciTrb.CYCLE | 1U << 24 | 3U << 16));
        for (var i = 0; i < 16; i++)
        {
            Assert.That(events.TryDequeue(out var trb), Is.True);
            Assert.That((trb.Parameter, trb.Type, trb.Slot, trb.Endpoint, trb.CompletionCode), Is.EqualTo(((ulong)i, XhciTrb.TRANSFER_EVENT, 1, 3, XhciTrb.SUCCESS)));
        }
        Assert.That(events.DequeuePointer, Is.EqualTo(0x20000UL), "the dequeue pointer wrapped");
        Assert.That(events.TryDequeue(out _), Is.False, "the last lap's events have the old cycle");
        Write(segment, 0, XhciTrb.Of(XhciTrb.PORT_STATUS_CHANGE, 5UL << 24));
        Assert.That(events.TryDequeue(out var port), Is.True, "the second lap's cycle is 0");
        Assert.That((port.Type, port.Port), Is.EqualTo((XhciTrb.PORT_STATUS_CHANGE, 5)));
        Assert.That(events.DequeuePointer, Is.EqualTo(0x20000UL + XhciTrb.BYTES));
    }

    [TestCase(32)]
    [TestCase(64)]
    public void ContextsAreWrittenAtTheirIndicesTest(int contextBytes)
    {
        var input = new byte[XhciContexts.INPUT_CONTEXTS * contextBytes];
        XhciContexts.WriteControl(input, contextBytes, 0, 0b1001);
        XhciContexts.WriteSlot(input, contextBytes, XhciContexts.HIGH_SPEED, 5, 3);
        XhciContexts.WriteEndpoint(input, contextBytes, 3, XhciContexts.INTERRUPT_IN, 8, 6, 0, 0x30000, 8);
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4)), Is.EqualTo(0b1001U), "the add flags");
        var slot = input.AsSpan(contextBytes);
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(slot), Is.EqualTo(3U << 20 | 3U << 27), "speed and context entries");
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(slot[4..]) >> 16 & 0xFF, Is.EqualTo(5U), "the root hub port");
        var endpoint = input.AsSpan(contextBytes * 4);
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(endpoint) >> 16 & 0xFF, Is.EqualTo(6U), "the interval");
        var info = BinaryPrimitives.ReadUInt32LittleEndian(endpoint[4..]);
        Assert.That((info >> 1 & 3, info >> 3 & 7, info >> 16), Is.EqualTo((3U, (uint)XhciContexts.INTERRUPT_IN, 8U)), "error count, type, packet bytes");
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(endpoint[8..]), Is.EqualTo(0x30001UL), "the ring with the dequeue cycle state");
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(endpoint[16..]), Is.EqualTo(8U | 8U << 16), "average TRB and maximum ESIT payload");
    }

    [Test]
    public void IntervalsFollowTheSpeedTest()
    {
        Assert.That(XhciContexts.Interval(XhciContexts.FULL_SPEED, 10), Is.EqualTo(6), "10 frames: 64 units of 125 µs");
        Assert.That(XhciContexts.Interval(XhciContexts.LOW_SPEED, 255), Is.EqualTo(10), "clamped to 10");
        Assert.That(XhciContexts.Interval(XhciContexts.FULL_SPEED, 1), Is.EqualTo(3));
        Assert.That(XhciContexts.Interval(XhciContexts.HIGH_SPEED, 7), Is.EqualTo(6), "an exponent less one");
        Assert.That(XhciContexts.Interval(XhciContexts.SUPER_SPEED, 0), Is.Zero);
        Assert.That(XhciContexts.DefaultMaxPacket(XhciContexts.FULL_SPEED), Is.EqualTo(8));
        Assert.That(XhciContexts.DefaultMaxPacket(XhciContexts.HIGH_SPEED), Is.EqualTo(64));
        Assert.That(XhciContexts.DefaultMaxPacket(XhciContexts.SUPER_SPEED), Is.EqualTo(512));
    }

    #endregion

    #region Tools

    private static byte[] Bytes(UsbSetup setup)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, setup.Encode());
        return bytes;
    }

    private static (ushort, ushort, int) Key(ushort code, int value) => (1, code, value);

    private static (ushort, ushort, int) Syn() => (0, 0, 0);

    // A TRB as the controller writes it: the body, then the control word with its cycle bit.
    private static void Write(IDmaBuffer segment, int index, XhciTrb trb)
    {
        var bytes = segment.Bytes[(index * XhciTrb.BYTES)..];
        trb.WriteBody(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], trb.Control);
    }

    private sealed class ArrayBuffer(int bytes, ulong physical) : IDmaBuffer
    {
        private readonly byte[] m_bytes = new byte[bytes];

        public Span<byte> Bytes => m_bytes;

        public ulong Physical { get; } = physical;
    }

    #endregion
}
