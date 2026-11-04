using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Dwc2;
using OutWit.OS.Hardware.Usb;
using OutWit.OS.Tests.Hardware.Sdhci;
using OutWit.OS.Tests.Hardware.Usb;

namespace OutWit.OS.Tests.Hardware.Dwc2;

/// <summary>
/// The DWC2 host (plan step H8.3) against a model of QEMU's dwc2-usb with the USB core's test devices: the core started in
/// host mode with its FIFOs, the USB core over it taking a keyboard on the root port and keys from its reports, a NAKed
/// report asked again after the endpoint's interval, a hub with a keyboard below it and one that comes later, the root
/// port's device going and coming, a stall on the control endpoint and on an interrupt endpoint taken back, bulk transfers
/// in pieces through the Raspberry Pi's DMA offset with their toggles, the devices the host refuses and a transfer no
/// device answers.
/// </summary>
[TestFixture]
public sealed class Dwc2HostTests
{
    #region Constants

    /// <summary>
    /// The keyboard's and the hub's interrupt endpoint 81: index 3.
    /// </summary>
    private const int ENDPOINT = 3;

    #endregion

    #region Tests

    [Test]
    public void CoreStartsInHostModeTest()
    {
        var (host, model, _, clock) = Host();
        Assert.That(host.Version, Is.EqualTo("2.94a"));
        Assert.That((host.Channels, host.MaxTransfer, host.MaxPackets, host.FifoWords), Is.EqualTo((8, 32767, 255, 4096)),
            "QEMU's counters of 15 and 8 bits");
        Assert.That(model.CoreResets, Is.EqualTo(1));
        Assert.That(model.Register(0x00C) & 3u << 29, Is.EqualTo(1u << 29), "host mode forced, device mode not");
        Assert.That((model.Register(0x024), model.Register(0x028), model.Register(0x100)),
            Is.EqualTo((774u, 256u << 16 | 774, 512u << 16 | 1030)), "Linux's FIFOs of the BCM2835 family");
        Assert.That(model.Register(0x008), Is.EqualTo(1u << 5 | 3u << 1 | 1), "DMA with INCR4 bursts, interrupts on");
        Assert.That(model.Register(0x018), Is.EqualTo(1u << 24 | 1u << 25 | 1u << 29), "the port, the channels and a disconnection");
        Assert.That(model.Register(0x418), Is.EqualTo(0xFFu), "every channel's interrupt");
        Assert.That(model.Port & 1u << 12, Is.Not.Zero, "the port powered");
        Assert.That(host.RootPorts.Single().Revision, Is.EqualTo("2.0"));
        Assert.That(clock.Slept, Is.EqualTo(TimeSpan.Zero), "QEMU's port is powered at its reset");
    }

    [Test]
    public void KeyboardOnTheRootPortTest()
    {
        var (host, model, _, clock) = Host();
        var keyboard = Keyboard(XhciContexts.HIGH_SPEED);
        model.Plug(keyboard);
        var log = new List<string>();
        var bus = new UsbBus(host, log.Add);
        bus.Start();
        Assert.That(model.PortResets, Is.EqualTo(1));
        Assert.That(keyboard.Address, Is.EqualTo(1), "SET_ADDRESS to the lowest free address");
        Assert.That(model.Requests.Take(2).Select(item => (item.Address, item.Setup.Request)),
            Is.EqualTo(new[] { (0, UsbSetup.GET_DESCRIPTOR), (0, UsbSetup.SET_ADDRESS) }), "eight bytes of the descriptor, then the address");
        Assert.That(log, Has.Some.Contains("port 1: device 0627:0001, USB 2.0, high speed, slot 1, packets of 64 bytes on its control endpoint"));
        Assert.That(log, Has.Some.Contains("port 1: a keyboard of the report protocol on interface 0, endpoint 81 of 8 bytes at interval 7"));
        Assert.That(model.ToggleErrors, Is.Zero, "every control stage's data PID");

        // The keyboard has nothing yet: its NAK halts the channel, and the host asks again after the interval (2^6 x 125 µs).
        host.Service();
        Assert.That(model.Interrupt, Is.False, "the halt served");
        Assert.That(host.TryTakeTransfer(out _), Is.False);
        Assert.That(host.NextPoll, Is.EqualTo(TimeSpan.FromMilliseconds(8)));
        keyboard.Report(1, [0, 0, 0x0B, 0, 0, 0, 0, 0]);
        clock.Advance(TimeSpan.FromMilliseconds(4));
        host.Service();
        Assert.That(host.TryTakeTransfer(out _), Is.False, "not before the interval");
        clock.Advance(TimeSpan.FromMilliseconds(4));
        host.Service();
        Assert.That(model.Interrupt, Is.True, "the report's channel halted");
        host.Service();
        Assert.That(host.TryTakeTransfer(out var transfer), Is.True);
        Assert.That(transfer, Is.EqualTo((1, ENDPOINT, XhciTrb.SUCCESS, 0)));
        // The next report waits in the keyboard, so that the core's next ask takes it at once, at DATA1.
        keyboard.Report(1, [0, 0, 0, 0, 0, 0, 0, 0]);
        bus.Completed(transfer.Slot, transfer.Index, transfer.Code, transfer.Residual);
        Assert.That(bus.TakeEvents(), Is.EqualTo(new[] { ((ushort)1, (ushort)35, 1), ((ushort)0, (ushort)0, 0) }), "h goes down");
        Assert.That(log, Has.Some.EqualTo("key 35 down (port 1)"));
        host.Service();
        Assert.That(host.TryTakeTransfer(out transfer), Is.True);
        bus.Completed(transfer.Slot, transfer.Index, transfer.Code, transfer.Residual);
        Assert.That(log, Has.Some.EqualTo("key 35 up (port 1)"));
        Assert.That(model.Transfers.Where(item => item.Type == 3 && item.Endpoint == 1).Select(item => item.Pid).Distinct(), Is.EquivalentTo(new[] { 0, 2 }));
        Assert.That(model.ToggleErrors, Is.Zero);
        Assert.That(model.Transfers.Where(item => item.Type == 3).All(item => item.Channel != 0), "interrupt transfers on the periodic channels");
    }

    [Test]
    public void HubWithKeyboardsBelowItTest()
    {
        var (host, model, _, clock) = Host();
        var ports = new FakeHub(8, UsbBusTests.HUB_DESCRIPTOR);
        var hub = new FakeDevice(XhciContexts.FULL_SPEED, UsbBusTests.HUB_DEVICE, UsbBusTests.HUB_CONFIGURATION, hub: ports);
        var first = Keyboard(XhciContexts.FULL_SPEED);
        ports.Plug(3, first);
        model.Plug(hub);
        var log = new List<string>();
        var bus = new UsbBus(host, log.Add);
        bus.Start();
        Assert.That((hub.Address, first.Address), Is.EqualTo((1, 2)));
        Assert.That(log, Has.Some.Contains("port 1: a hub of 8 ports, powered for 100 ms, endpoint 81 at interval 255"));
        Assert.That(log, Has.Some.Contains("port 1.3: device 0627:0001, USB 2.0, full speed, slot 2"));
        Assert.That((bus.Devices, bus.Hubs, bus.Keyboards), Is.EqualTo((2, 1, 1)));
        Assert.That(model.ToggleErrors, Is.Zero);
        Serve(host, bus, model, clock, TimeSpan.Zero);

        // A keyboard plugged into port 5: the hub's status change report brings it.
        var second = Keyboard(XhciContexts.FULL_SPEED);
        ports.Plug(5, second);
        Serve(host, bus, model, clock, TimeSpan.FromMilliseconds(255));
        Assert.That(second.Address, Is.EqualTo(3));
        Assert.That(log, Has.Some.Contains("port 1.5: a keyboard of the report protocol"));
        second.Report(1, [2, 0, 0x04, 0, 0, 0, 0, 0]);
        Serve(host, bus, model, clock, TimeSpan.FromMilliseconds(7));
        Assert.That(log, Has.Some.EqualTo("key 30 down (port 1.5)"), "Shift-a from the keyboard below the hub");
        Assert.That(log, Has.Some.EqualTo("key 42 down (port 1.5)"));
        Assert.That(model.ToggleErrors, Is.Zero);
    }

    [Test]
    public void RootPortDeviceGoesAndComesTest()
    {
        var (host, model, _, clock) = Host();
        var log = new List<string>();
        var bus = new UsbBus(host, log.Add);
        bus.Start();
        Assert.That(bus.Devices, Is.Zero);
        var keyboard = Keyboard(XhciContexts.FULL_SPEED);
        model.Plug(keyboard);
        Assert.That(model.Interrupt, Is.True, "the connection's change");
        Serve(host, bus, model, clock, TimeSpan.Zero);
        Assert.That(bus.Keyboards, Is.EqualTo(1));
        keyboard.Report(1, [0, 0, 0x04, 0, 0, 0, 0, 0]);
        Serve(host, bus, model, clock, TimeSpan.FromMilliseconds(7));
        Assert.That(log, Has.Some.EqualTo("key 30 down (port 1)"));

        model.Unplug();
        Serve(host, bus, model, clock, TimeSpan.Zero);
        Assert.That(log, Has.Some.EqualTo("key 30 up (port 1)"), "the key it held released");
        Assert.That(log, Has.Some.EqualTo("port 1: detached, slot 1 disabled"));
        Assert.That(bus.Devices, Is.Zero);
        Assert.That(host.NextPoll, Is.Null, "its endpoint let go");
        Assert.That(model.Interrupt, Is.False);

        var again = Keyboard(XhciContexts.LOW_SPEED);
        model.Plug(again);
        Serve(host, bus, model, clock, TimeSpan.Zero);
        Assert.That(again.Address, Is.EqualTo(1), "the address free again");
        Assert.That(log, Has.Some.Contains("low speed, slot 1"));
        Assert.That(model.ToggleErrors, Is.Zero);
    }

    [Test]
    public void StallsAreTakenBackTest()
    {
        var (host, model, _, clock) = Host();
        var keyboard = Keyboard(XhciContexts.HIGH_SPEED);
        keyboard.Stalls.Add((0x21, UsbSetup.SET_IDLE));
        model.Plug(keyboard);
        var log = new List<string>();
        var bus = new UsbBus(host, log.Add);
        bus.Start();
        Assert.That(log, Has.Some.Contains("SET_IDLE refused (The request 0a at address 1 stalled in its status stage.)"));
        Assert.That(bus.Keyboards, Is.EqualTo(1), "the control endpoint takes the next request");

        Serve(host, bus, model, clock, TimeSpan.Zero);
        keyboard.Report(1, [0, 0, 0x0B, 0, 0, 0, 0, 0]);
        Serve(host, bus, model, clock, TimeSpan.FromMilliseconds(8));
        Assert.That(log, Has.Some.EqualTo("key 35 down (port 1)"));
        keyboard.Halted.Add(0x81);
        Serve(host, bus, model, clock, TimeSpan.FromMilliseconds(8));
        Assert.That(log, Has.Some.EqualTo("port 1: endpoint 81 completed with code 6 (stall): reset, its halt cleared on the device, asked again"));
        Assert.That(keyboard.Halted, Is.Empty);
        keyboard.Report(1, [0, 0, 0, 0, 0, 0, 0, 0]);
        Serve(host, bus, model, clock, TimeSpan.FromMilliseconds(8));
        Assert.That(log, Has.Some.EqualTo("key 35 up (port 1)"));
        Assert.That(model.ToggleErrors, Is.Zero, "both sides back at DATA0 after the halt");
    }

    [Test]
    public void BulkThroughTheDmaOffsetTest()
    {
        var (host, model, memory, _) = Host(dmaOffset: 0xC000_0000);
        var drive = new FakeDevice(XhciContexts.HIGH_SPEED, UsbBusTests.KEYBOARD_DEVICE, UsbBusTests.KEYBOARD_CONFIGURATION);
        model.Plug(drive);
        host.ResetRootPort(1);
        var slot = host.Address(UsbLocation.Root(1, XhciContexts.HIGH_SPEED));
        var (input, output) = host.ConfigureBulk(slot, new UsbEndpoint(0x82, 2, 512, 0), new UsbEndpoint(0x02, 2, 512, 0));
        Assert.That((input, output), Is.EqualTo((5, 4)));

        var first = memory.Buffer(40 * 1024);
        var second = memory.Buffer(8 * 1024);
        var pattern = Enumerable.Range(0, 48 * 1024).Select(i => (byte)(i * 7 + 3)).ToArray();
        pattern.AsSpan(0, 40 * 1024).CopyTo(first.Bytes);
        pattern.AsSpan(40 * 1024).CopyTo(second.Bytes);
        var sent = host.Bulk(slot, output, [(first.Physical, 40 * 1024), (second.Physical, 8 * 1024)], out var transferred);
        Assert.That((sent, transferred), Is.EqualTo((BulkResult.Done, 48 * 1024)));
        Assert.That(drive.Bulk, Is.EqualTo(pattern));
        Assert.That(model.Transfers.Where(item => item.Type == 2).Select(item => item.Bytes), Is.EqualTo(new[] { 32256, 8704, 8192 }),
            "40 KiB in pieces of 63 packets, then the second segment");

        var back = memory.Buffer(64 * 1024);
        var received = host.Bulk(slot, input, [(back.Physical, 64 * 1024)], out transferred);
        Assert.That((received, transferred), Is.EqualTo((BulkResult.Done, 48 * 1024)), "a short packet ends it");
        Assert.That(back.Bytes[..(48 * 1024)].ToArray(), Is.EqualTo(pattern));

        // The endpoint's own page takes a short answer rounded up to its packets.
        drive.Bulk.AddRange(new byte[13]);
        var page = host.BulkBuffer(slot, input, out var physical);
        Assert.That(host.Bulk(slot, input, [(physical, 13)], out transferred), Is.EqualTo(BulkResult.Done));
        Assert.That(transferred, Is.EqualTo(13));
        Assert.That(page.Length, Is.EqualTo(4096));
        Assert.Throws<ArgumentException>(() => host.Bulk(slot, input, [(back.Physical, 13)], out _), "a short IN segment of the client's");
        Assert.Throws<ArgumentException>(() => host.Bulk(slot, output, [(first.Physical, 100), (second.Physical, 512)], out _), "a short segment first");
        Assert.Throws<ArgumentException>(() => host.Bulk(slot, output, [(0x1_0000_0000UL, 512)], out _), "beyond the core's view");

        drive.Halted.Add(0x02);
        Assert.That(host.Bulk(slot, output, [(first.Physical, 512)], out _), Is.EqualTo(BulkResult.Stalled));
        host.RecoverEndpoint(slot, output);
        host.Control(slot, UsbSetup.ClearEndpointHalt(0x02));
        model.BulkErrors = 1;
        Assert.That(host.Bulk(slot, output, [(first.Physical, 512)], out _), Is.EqualTo(BulkResult.Failed));
        Assert.That(host.Bulk(slot, output, [(first.Physical, 512)], out _), Is.EqualTo(BulkResult.Done));
        Assert.That(model.ToggleErrors, Is.Zero, "the toggles through pieces, short packets and a cleared halt");
        Assert.That(model.Writes.Where(item => item.Offset is >= 0x514 and < 0x600 && (item.Offset & 0x1F) == 0x14).Select(item => item.Value),
            Has.All.GreaterThanOrEqualTo(0xC000_0000u), "every DMA address through the offset");
    }

    [Test]
    public void SplitTransactionsAreRefusedTest()
    {
        var (host, model, _, _) = Host();
        var ports = new FakeHub(4, UsbBusTests.HIGH_SPEED_HUB_DESCRIPTOR);
        var hub = new FakeDevice(XhciContexts.HIGH_SPEED, UsbBusTests.HIGH_SPEED_HUB_DEVICE, UsbBusTests.HUB_CONFIGURATION, hub: ports);
        ports.Plug(2, Keyboard(XhciContexts.FULL_SPEED));
        model.Plug(hub);
        var log = new List<string>();
        var bus = new UsbBus(host, log.Add);
        bus.Start();
        Assert.That(log, Has.Some.EqualTo("port 1.2: A full speed device behind the high-speed hub of slot 1 needs split transactions, which this host does not serve."));
        Assert.That((bus.Devices, bus.Hubs, bus.Keyboards), Is.EqualTo((1, 1, 0)));
    }

    [Test]
    public void UnansweredTransferTimesOutTest()
    {
        var (host, model, _, clock) = Host();
        model.Plug(Keyboard(XhciContexts.HIGH_SPEED));
        var failure = Assert.Throws<IOException>(() => host.Address(UsbLocation.Root(1, XhciContexts.HIGH_SPEED)), "the port was never reset")!;
        Assert.That(failure.Message, Does.Contain("was not answered in its SETUP stage within 5 s"));
        Assert.That(model.Unanswered, Is.EqualTo(1));
        Assert.That(model.Disables, Is.EqualTo(1), "the channel halted");
        Assert.That(clock.Waited, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(5)));
        host.ResetRootPort(1);
        Assert.That(host.Address(UsbLocation.Root(1, XhciContexts.HIGH_SPEED)), Is.EqualTo(1), "the channel takes the next transfer");
    }

    [Test]
    public void ControllersRefusedTest()
    {
        var memory = new FakeMemory();
        var clock = new FakeClock();
        Assert.Throws<IOException>(() => new Dwc2Host(new FakeDwc2(memory, snpsid: 0x1234_5678), clock, memory.Buffer, null, clock, clock.Sleep).Start(),
            "no DWC2 core");
        Assert.Throws<IOException>(() => new Dwc2Host(new FakeDwc2(memory, hwcfg2: FakeDwc2.HWCFG2 & ~(3u << 3)), clock, memory.Buffer, null, clock,
            clock.Sleep).Start(), "a core without internal DMA");
        Assert.Throws<IOException>(() => new Dwc2Host(new FakeDwc2(memory), clock, memory.Buffer, new Dwc2Options(DmaOffset: 0xFFFF_0000), clock,
            clock.Sleep).Start(), "its buffers beyond the core's view");
    }

    #endregion

    #region Tools

    private static (Dwc2Host Host, FakeDwc2 Model, FakeMemory Memory, FakeClock Clock) Host(long dmaOffset = 0)
    {
        var memory = new FakeMemory();
        var clock = new FakeClock();
        var model = new FakeDwc2(memory, dmaOffset);
        var host = new Dwc2Host(model, clock, memory.Buffer, new Dwc2Options(DmaOffset: dmaOffset), clock, clock.Sleep);
        host.Start();
        return (host, model, memory, clock);
    }

    // What the driver does: it serves the host whenever the controller interrupts, then sleeps until a time passed.
    private static void Serve(Dwc2Host host, UsbBus bus, FakeDwc2 model, FakeClock clock, TimeSpan passed)
    {
        Drain(host, bus, model);
        clock.Advance(passed);
        Drain(host, bus, model);
    }

    // The host served, its port changes and completions given to the core, until the controller has nothing pending.
    private static void Drain(Dwc2Host host, UsbBus bus, FakeDwc2 model)
    {
        for (var round = 0; round < 32; round++)
        {
            host.Service();
            var served = false;
            while (host.TryTakePortChange(out var port))
            {
                bus.RootPortChanged(port);
                served = true;
            }
            while (host.TryTakeTransfer(out var transfer))
            {
                bus.Completed(transfer.Slot, transfer.Index, transfer.Code, transfer.Residual);
                served = true;
            }
            if (!served && !model.Interrupt)
                return;
        }
        Assert.Fail("The controller kept interrupting.");
    }

    private static FakeDevice Keyboard(int speed) =>
        new(speed, UsbBusTests.KEYBOARD_DEVICE, UsbBusTests.KEYBOARD_CONFIGURATION, HidReportDescriptorTests.QEMU_KEYBOARD);

    #endregion
}
