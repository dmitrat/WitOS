using System;
using System.Collections.Generic;
using System.Linq;
using OutWit.OS.Hardware.Usb;

namespace OutWit.OS.Tests.Hardware.Usb;

/// <summary>
/// The USB core of plan step H2.2 on the host, over a fake controller: a keyboard on a root port by its report
/// descriptor, a hub with a keyboard below it, keyboards that come and go on a hub's ports and on root ports, the keys a
/// keyboard held released when it goes, a stalled endpoint taken back and left after too many failures, a boot keyboard
/// whose report descriptor fails, the Transaction Translator of a high-speed hub, and the route string's last tier.
/// </summary>
[TestFixture]
public sealed class UsbBusTests
{
    #region Constants

    // QEMU's usb-kbd (hw/usb/dev-hid.c) as UsbCoreTests reads it.
    private static readonly byte[] KEYBOARD_DEVICE =
        [0x12, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x40, 0x27, 0x06, 0x01, 0x00, 0x00, 0x00, 0x01, 0x04, 0x0B, 0x01];

    private static readonly byte[] KEYBOARD_CONFIGURATION =
    [
        0x09, 0x02, 0x22, 0x00, 0x01, 0x01, 0x07, 0xA0, 0x32,
        0x09, 0x04, 0x00, 0x00, 0x01, 0x03, 0x01, 0x01, 0x00,
        0x09, 0x21, 0x11, 0x01, 0x00, 0x01, 0x22, 0x3F, 0x00,
        0x07, 0x05, 0x81, 0x03, 0x08, 0x00, 0x07,
    ];

    // QEMU's usb-hub (hw/usb/dev-hub.c): a USB 1.1 hub of eight ports, its status change endpoint of two bytes.
    private static readonly byte[] HUB_DEVICE =
        [0x12, 0x01, 0x10, 0x01, 0x09, 0x00, 0x00, 0x08, 0x09, 0x04, 0xAA, 0x55, 0x01, 0x01, 0x01, 0x02, 0x03, 0x01];

    private static readonly byte[] HUB_CONFIGURATION =
    [
        0x09, 0x02, 0x19, 0x00, 0x01, 0x01, 0x00, 0xE0, 0x00,
        0x09, 0x04, 0x00, 0x00, 0x01, 0x09, 0x00, 0x00, 0x00,
        0x07, 0x05, 0x81, 0x03, 0x02, 0x00, 0xFF,
    ];

    private static readonly byte[] HUB_DESCRIPTOR = [0x0A, 0x29, 0x08, 0x0A, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF];

    // A high-speed hub of four ports with one Transaction Translator, its TT Think Time 1 (bits 6–5 of the characteristics).
    private static readonly byte[] HIGH_SPEED_HUB_DEVICE =
        [0x12, 0x01, 0x00, 0x02, 0x09, 0x00, 0x01, 0x40, 0x09, 0x04, 0x5A, 0x5A, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01];

    private static readonly byte[] HIGH_SPEED_HUB_DESCRIPTOR = [0x09, 0x29, 0x04, 0x29, 0x00, 0x32, 0x64, 0x00, 0xFF];

    /// <summary>
    /// The keyboard's and the hub's interrupt endpoint 81: Device Context Index 3.
    /// </summary>
    private const int ENDPOINT = 3;

    #endregion

    #region Tests

    [Test]
    public void RootKeyboardSpeaksTheReportProtocolTest()
    {
        var (host, bus, log) = Bus();
        var keyboard = Keyboard(XhciContexts.HIGH_SPEED);
        host.Plug(5, keyboard);
        bus.Start();
        Assert.That(keyboard.Location, Is.EqualTo(UsbLocation.Root(5, XhciContexts.HIGH_SPEED)));
        Assert.That(log, Has.Some.Contains("port 5: a keyboard of the report protocol on interface 0, endpoint 81 of 8 bytes at interval 7, reports of 8 bytes: reporting keys"));
        Assert.That(host.Requests, Has.Some.EqualTo("slot 1: 81 06 2200 0000"), "the report descriptor asked for");
        Assert.That(host.Requests, Has.None.Contains(" 21 0b "), "no SET_PROTOCOL: a reset leaves the report protocol");
        Assert.That(host.Requests.IndexOf("configure endpoint 81 of slot 1"), Is.LessThan(host.Requests.IndexOf("slot 1: 00 09 0001 0000")),
            "the endpoint configured before the configuration is set, as xHCI asks");
        Assert.That((bus.Devices, bus.Hubs, bus.Keyboards), Is.EqualTo((1, 0, 1)));
        bus.Completed(host.Complete(keyboard, ENDPOINT, [0, 0, 0x0B, 0, 0, 0, 0, 0]));
        Assert.That(bus.TakeEvents(), Is.EqualTo(new[] { Key(35, 1), Syn() }), "h goes down");
        Assert.That(log, Has.Some.EqualTo("key 35 down (port 5)"));
        Assert.That(host.Asked(keyboard, ENDPOINT), Is.True, "the next report asked for");
    }

    [Test]
    public void HubTakesTheKeyboardBelowItTest()
    {
        var (host, bus, log) = Bus();
        var (hub, ports) = Hub();
        var keyboard = Keyboard(XhciContexts.FULL_SPEED);
        ports.Plug(1, keyboard);
        host.Plug(5, hub);
        bus.Start();
        Assert.That(log, Has.Some.EqualTo("port 5: a hub of 8 ports, powered for 100 ms, endpoint 81 at interval 255: watching its ports"));
        Assert.That(host.Requests, Has.Some.EqualTo("hub slot 1: 8 ports"), "the controller told the device is a hub");
        Assert.That(Enumerable.Range(1, 8).All(port => host.Requests.Contains($"slot 1: 23 03 0008 {port:x4}")), Is.True, "every port powered");
        Assert.That(keyboard.Location, Is.EqualTo(new UsbLocation(5, 1, 1, XhciContexts.FULL_SPEED, 0, 0)), "route 1 below root port 5, no TT behind a full-speed hub");
        Assert.That(log, Has.Some.StartsWith("port 5.1: a keyboard of the report protocol"));
        Assert.That(ports.Ports[1].ChangeBits, Is.Zero, "the connection's, the reset's and the enable's changes acknowledged");
        Assert.That(host.Asked(hub, ENDPOINT), Is.True, "the hub's changes asked for");
        Assert.That((bus.Devices, bus.Hubs, bus.Keyboards), Is.EqualTo((2, 1, 1)));
        bus.Completed(host.Complete(keyboard, ENDPOINT, [0, 0, 0x16, 0, 0, 0, 0, 0]));
        Assert.That(log, Has.Some.EqualTo("key 31 down (port 5.1)"));
    }

    [Test]
    public void KeyboardComesAndGoesOnAHubPortTest()
    {
        var (host, bus, log) = Bus();
        var (hub, ports) = Hub();
        host.Plug(5, hub);
        bus.Start();
        Assert.That(bus.Keyboards, Is.Zero);
        var keyboard = Keyboard(XhciContexts.FULL_SPEED);
        ports.Plug(2, keyboard);
        bus.Completed(host.Complete(hub, ENDPOINT, ports.Report()));
        Assert.That(keyboard.Location!.Path, Is.EqualTo("5.2"));
        Assert.That(bus.Keyboards, Is.EqualTo(1));
        Assert.That(host.Asked(hub, ENDPOINT), Is.True, "the hub's next change asked for");
        bus.Completed(host.Complete(keyboard, ENDPOINT, [0x02, 0, 0x04, 0, 0, 0, 0, 0]));
        bus.TakeEvents();
        var slot = keyboard.Slot;
        ports.Unplug(2);
        bus.Completed(host.Complete(hub, ENDPOINT, ports.Report()));
        Assert.That(host.Requests, Has.Some.EqualTo($"disable slot {slot}"));
        Assert.That(log, Has.Some.EqualTo($"port 5.2: detached, slot {slot} disabled"));
        Assert.That(bus.TakeEvents(), Is.EqualTo(new[] { Key(42, 0), Key(30, 0), Syn() }), "what the keyboard held let up");
        Assert.That((bus.Devices, bus.Keyboards), Is.EqualTo((1, 0)));
        ports.Plug(2, keyboard);
        bus.Completed(host.Complete(hub, ENDPOINT, ports.Report()));
        Assert.That(bus.Keyboards, Is.EqualTo(1), "the keyboard taken again");
    }

    [Test]
    public void KeyboardComesAndGoesOnARootPortTest()
    {
        var (host, bus, log) = Bus();
        bus.Start();
        var keyboard = Keyboard(XhciContexts.HIGH_SPEED);
        host.Plug(6, keyboard);
        bus.RootPortChanged(6);
        Assert.That(host.Requests, Has.Some.EqualTo("reset root port 6"));
        Assert.That(log, Has.Some.StartsWith("port 6: a keyboard of the report protocol"));
        bus.RootPortChanged(6);
        Assert.That(bus.Devices, Is.EqualTo(1), "a change without a connection change (the reset's own) takes nothing again");
        host.Unplug(6);
        bus.RootPortChanged(6);
        Assert.That(log, Has.Some.EqualTo("port 6: detached, slot 1 disabled"));
        Assert.That(bus.Devices, Is.Zero);
    }

    [Test]
    public void HubThatGoesTakesItsDevicesTest()
    {
        var (host, bus, log) = Bus();
        var (hub, ports) = Hub();
        var first = Keyboard(XhciContexts.FULL_SPEED);
        var second = Keyboard(XhciContexts.LOW_SPEED);
        ports.Plug(1, first);
        ports.Plug(3, second);
        host.Plug(5, hub);
        bus.Start();
        Assert.That((bus.Devices, bus.Keyboards), Is.EqualTo((3, 2)));
        Assert.That(second.Location!.Speed, Is.EqualTo(XhciContexts.LOW_SPEED), "the speed the hub's port reports");
        host.Unplug(5);
        bus.RootPortChanged(5);
        var disabled = host.Requests.Where(request => request.StartsWith("disable slot", StringComparison.Ordinal)).ToList();
        Assert.That(disabled, Is.EqualTo(new[] { "disable slot 2", "disable slot 3", "disable slot 1" }), "the devices below the hub first");
        Assert.That(log.TakeLast(3), Is.EqualTo(new[] { "port 5.1: detached, slot 2 disabled", "port 5.3: detached, slot 3 disabled", "port 5: detached, slot 1 disabled" }));
        Assert.That(bus.Devices, Is.Zero);
    }

    [Test]
    public void StalledEndpointIsTakenBackTest()
    {
        var (host, bus, log) = Bus();
        var keyboard = Keyboard(XhciContexts.FULL_SPEED);
        host.Plug(5, keyboard);
        bus.Start();
        bus.Completed(host.Complete(keyboard, ENDPOINT, [], XhciTrb.STALL));
        Assert.That(host.Requests.TakeLast(2), Is.EqualTo(new[] { "recover endpoint 3 of slot 1", "slot 1: 02 01 0000 0081" }),
            "the endpoint reset, then CLEAR_FEATURE(ENDPOINT_HALT) of 81");
        Assert.That(log, Has.Some.EqualTo("port 5: endpoint 81 completed with code 6 (stall): reset, its halt cleared on the device, asked again"));
        Assert.That(host.Asked(keyboard, ENDPOINT), Is.True);
        bus.Completed(host.Complete(keyboard, ENDPOINT, [0, 0, 0x04, 0, 0, 0, 0, 0]));
        Assert.That(bus.TakeEvents(), Is.EqualTo(new[] { Key(30, 1), Syn() }), "reports come again");
        keyboard.Stalls.Add((0x02, UsbSetup.CLEAR_FEATURE)); // as QEMU's usb-kbd answers it
        for (var failure = 1; failure <= 3; failure++)
            bus.Completed(host.Complete(keyboard, ENDPOINT, [], XhciTrb.TRANSACTION_ERROR));
        Assert.That(log, Has.Some.Contains("the device refused to clear its halt"));
        Assert.That(host.Asked(keyboard, ENDPOINT), Is.True, "three failures in a row are taken back");
        bus.Completed(host.Complete(keyboard, ENDPOINT, [], XhciTrb.TRANSACTION_ERROR));
        Assert.That(log.Last(), Is.EqualTo("port 5: endpoint 81 completed with code 4 (transaction error) after 3 failures in a row: left until its port changes"));
        Assert.That(host.Asked(keyboard, ENDPOINT), Is.False, "the fourth leaves the endpoint");
    }

    [Test]
    public void EndpointThatCannotBeResetIsLeftTest()
    {
        var (host, bus, log) = Bus();
        var keyboard = Keyboard(XhciContexts.FULL_SPEED);
        host.Plug(5, keyboard);
        bus.Start();
        host.Recovers = false;
        bus.Completed(host.Complete(keyboard, ENDPOINT, [], XhciTrb.BABBLE));
        Assert.That(log.Last(), Is.EqualTo("port 5: endpoint 81 completed with code 3 (babble): the endpoint could not be reset, left until its port changes"));
        Assert.That(host.Asked(keyboard, ENDPOINT), Is.False);
    }

    [Test]
    public void BootKeyboardFallsBackToTheBootProtocolTest()
    {
        var (host, bus, log) = Bus();
        var keyboard = Keyboard(XhciContexts.FULL_SPEED);
        keyboard.Stalls.Add((0x81, UsbSetup.GET_DESCRIPTOR));
        host.Plug(5, keyboard);
        bus.Start();
        Assert.That(log, Has.Some.StartsWith("port 5: interface 0's report descriptor could not be read"));
        Assert.That(host.Requests, Has.Some.EqualTo("slot 1: 21 0b 0000 0000"), "SET_PROTOCOL(BOOT)");
        Assert.That(log, Has.Some.StartsWith("port 5: a keyboard of the boot protocol on interface 0"));
    }

    [Test]
    public void KeyboardBehindAHighSpeedHubUsesItsTranslatorTest()
    {
        var (host, bus, log) = Bus();
        var ports = new FakeHub(4, HIGH_SPEED_HUB_DESCRIPTOR);
        var hub = new FakeDevice(XhciContexts.HIGH_SPEED, HIGH_SPEED_HUB_DEVICE, HUB_CONFIGURATION, hub: ports);
        var lowerPorts = new FakeHub(8, HUB_DESCRIPTOR);
        var lower = new FakeDevice(XhciContexts.FULL_SPEED, HUB_DEVICE, HUB_CONFIGURATION, hub: lowerPorts);
        var fast = Keyboard(XhciContexts.HIGH_SPEED);
        var slow = Keyboard(XhciContexts.LOW_SPEED);
        ports.Plug(2, fast);
        ports.Plug(4, lower);
        lowerPorts.Plug(7, slow);
        host.Plug(5, hub);
        bus.Start();
        Assert.That(log, Has.Some.EqualTo("port 5: a hub of 4 ports, one Transaction Translator, powered for 100 ms, endpoint 81 at interval 255: watching its ports"));
        Assert.That(fast.Location, Is.EqualTo(new UsbLocation(5, 2, 1, XhciContexts.HIGH_SPEED, 0, 0)), "a high-speed device needs no translator");
        Assert.That(lower.Location, Is.EqualTo(new UsbLocation(5, 4, 1, XhciContexts.FULL_SPEED, 1, 4)), "the hub's translator, on its port 4");
        Assert.That(slow.Location, Is.EqualTo(new UsbLocation(5, 0x74, 2, XhciContexts.LOW_SPEED, 1, 4)), "the same translator two tiers down");
        Assert.That(slow.Location!.Path, Is.EqualTo("5.4.7"));
        Assert.That(bus.Keyboards, Is.EqualTo(2));
    }

    [Test]
    public void LastTierTakesNoDeviceBelowItTest()
    {
        var (host, bus, log) = Bus();
        var hubs = Enumerable.Range(0, 6).Select(_ => Hub()).ToList();
        for (var i = 0; i < 5; i++)
            hubs[i].Ports.Plug(1, hubs[i + 1].Device);
        hubs[5].Ports.Plug(1, Keyboard(XhciContexts.FULL_SPEED));
        host.Plug(5, hubs[0].Device);
        bus.Start();
        Assert.That(hubs[5].Device.Location!.Path, Is.EqualTo("5.1.1.1.1.1"), "a hub at the fifth tier");
        Assert.That(log, Has.Some.EqualTo("port 5.1.1.1.1.1.1: a device beyond the route string's 5 tiers of 15 ports, left"));
        Assert.That((bus.Hubs, bus.Keyboards), Is.EqualTo((6, 0)));
    }

    [Test]
    public void DeviceThatIsNoKeyboardIsLeftUnconfiguredTest()
    {
        var (host, bus, log) = Bus();
        byte[] vendor =
        [
            0x09, 0x02, 0x20, 0x00, 0x01, 0x01, 0x00, 0x80, 0x32,
            0x09, 0x04, 0x00, 0x00, 0x02, 0xFF, 0x00, 0x00, 0x00,
            0x07, 0x05, 0x81, 0x02, 0x00, 0x02, 0x00,
            0x07, 0x05, 0x02, 0x02, 0x00, 0x02, 0x00,
        ];
        host.Plug(5, new FakeDevice(XhciContexts.HIGH_SPEED, KEYBOARD_DEVICE, vendor));
        bus.Start();
        Assert.That(log, Has.Some.EqualTo("port 5: neither a hub, a drive nor a HID interface, left unconfigured"));
        Assert.That(bus.Devices, Is.EqualTo(1), "addressed, so that its port's change lets it go");
    }

    // A drive (E3): its mass storage interface of SCSI over Bulk-Only Transport configured with its two bulk endpoints,
    // its units asked, and announced when it comes and when it goes.
    [Test]
    public void DriveIsConfiguredAndAnnouncedTest()
    {
        var (host, bus, log) = Bus();
        byte[] storage =
        [
            0x09, 0x02, 0x20, 0x00, 0x01, 0x01, 0x00, 0x80, 0x32,
            0x09, 0x04, 0x00, 0x00, 0x02, 0x08, 0x06, 0x50, 0x00,
            0x07, 0x05, 0x81, 0x02, 0x00, 0x02, 0x00,
            0x07, 0x05, 0x02, 0x02, 0x00, 0x02, 0x00,
        ];
        host.Plug(5, new FakeDevice(XhciContexts.HIGH_SPEED, KEYBOARD_DEVICE, storage));
        bus.Start();
        Assert.That(host.Requests, Has.Some.EqualTo("configure bulk endpoints 81 and 02 of slot 1"));
        Assert.That(host.Requests, Has.Some.EqualTo("slot 1: a1 fe 0000 0000"), "Get Max LUN of interface 0");
        Assert.That(log, Has.Some.EqualTo("port 5: a drive, interface 0, bulk endpoints 81 and 02 of 512 bytes, 1 logical unit"));
        var arrived = bus.TakeStorage();
        Assert.That(arrived.Select(item => (item.Arrived, item.Storage.Slot, item.Storage.InIndex, item.Storage.OutIndex, item.Storage.Name)),
            Is.EqualTo(new[] { (true, 1, 3, 4, "port 5") }));
        Assert.That(bus.TakeStorage(), Is.Empty);

        host.Unplug(5);
        bus.RootPortChanged(5);
        Assert.That(bus.TakeStorage().Select(item => (item.Arrived, item.Storage.Slot)), Is.EqualTo(new[] { (false, 1) }));
    }

    [Test]
    public void SlotContextCarriesTheRouteAndTheHubTest()
    {
        const int contextBytes = 32;
        var input = new byte[XhciContexts.INPUT_CONTEXTS * contextBytes];
        var hub = UsbHub.ReadDescriptor(HIGH_SPEED_HUB_DESCRIPTOR);
        Assert.That((hub.Ports, hub.ThinkTime, hub.PowerOnToGoodMilliseconds), Is.EqualTo((4, 1, 100)));
        XhciContexts.WriteSlot(input, contextBytes, new UsbLocation(5, 0x74, 2, XhciContexts.HIGH_SPEED, 0, 0), 3, hub);
        var slot = input.AsSpan(contextBytes);
        Assert.That(BitConverter.ToUInt32(slot), Is.EqualTo(0x74U | 3U << 20 | 1U << 26 | 3U << 27), "route, speed, Hub, context entries");
        Assert.That(BitConverter.ToUInt32(slot[4..]), Is.EqualTo(5U << 16 | 4U << 24), "root port, ports");
        Assert.That(BitConverter.ToUInt32(slot[8..]), Is.EqualTo(1U << 16), "the TT Think Time of a high-speed hub");
        XhciContexts.WriteSlot(input, contextBytes, new UsbLocation(5, 0x74, 2, XhciContexts.LOW_SPEED, 1, 4), 3);
        Assert.That(BitConverter.ToUInt32(slot), Is.EqualTo(0x74U | 2U << 20 | 3U << 27), "no Hub flag for a keyboard");
        Assert.That(BitConverter.ToUInt32(slot[8..]), Is.EqualTo(1U | 4U << 8), "its translator's hub slot and port");
    }

    [Test]
    public void LocationsFollowTheRouteStringTest()
    {
        var root = UsbLocation.Root(5, XhciContexts.FULL_SPEED);
        Assert.That((root.Path, root.Route, root.Depth), Is.EqualTo(("5", 0U, 0)));
        var below = UsbLocation.Below(root, 1, 15, XhciContexts.FULL_SPEED);
        Assert.That((below.Path, below.Route), Is.EqualTo(("5.15", 0xFU)));
        Assert.Throws<ArgumentOutOfRangeException>(() => UsbLocation.Below(root, 1, 16, XhciContexts.FULL_SPEED), "port 16 has no nibble");
        var deep = root;
        for (var tier = 0; tier < UsbHub.TIERS; tier++)
            deep = UsbLocation.Below(deep, 1, 2, XhciContexts.FULL_SPEED);
        Assert.That((deep.Route, deep.Depth), Is.EqualTo((0x22222U, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => UsbLocation.Below(deep, 1, 2, XhciContexts.FULL_SPEED), "no sixth tier");
        var status = UsbHub.ReadPortStatus([0x03, 0x02, 0x13, 0x00]);
        Assert.That((status.Connected, status.Enabled, status.Speed, status.ConnectionChanged, status.EnableChanged, status.ResetChanged),
            Is.EqualTo((true, true, XhciContexts.LOW_SPEED, true, true, true)));
        Assert.That(status.ChangeFeatures(), Is.EqualTo(new[] { UsbHub.C_PORT_CONNECTION, UsbHub.C_PORT_ENABLE, UsbHub.C_PORT_RESET }));
        Assert.That(UsbHub.ChangedPorts([0b0000_0101, 0b0000_0001], 8), Is.EqualTo(new[] { 2, 8 }), "bit 0 is the hub's own");
    }

    #endregion

    #region Tools

    private static (FakeUsbHost Host, UsbBus Bus, List<string> Log) Bus()
    {
        var host = new FakeUsbHost();
        var log = new List<string>();
        return (host, new UsbBus(host, log.Add), log);
    }

    private static FakeDevice Keyboard(int speed) =>
        new(speed, KEYBOARD_DEVICE, KEYBOARD_CONFIGURATION, HidReportDescriptorTests.QEMU_KEYBOARD);

    private static (FakeDevice Device, FakeHub Ports) Hub()
    {
        var ports = new FakeHub(8, HUB_DESCRIPTOR);
        return (new FakeDevice(XhciContexts.FULL_SPEED, HUB_DEVICE, HUB_CONFIGURATION, hub: ports), ports);
    }

    private static (ushort, ushort, int) Key(ushort code, int value) => (1, code, value);

    private static (ushort, ushort, int) Syn() => (0, 0, 0);

    #endregion
}

/// <summary>
/// The completions a test hands the core as the controller's events would.
/// </summary>
internal static class UsbBusExtensions
{
    public static void Completed(this UsbBus bus, (int Slot, int Index, uint Code, int Residual) completion) =>
        bus.Completed(completion.Slot, completion.Index, completion.Code, completion.Residual);
}
