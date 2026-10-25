using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OutWit.OS.Hardware.Usb;

namespace OutWit.OS.Tests.Hardware.Usb;

/// <summary>
/// A host controller for the USB core's tests: root ports with devices on them, hubs with ports QEMU's usb-hub answers
/// as, keyboards that answer their descriptors, a device found by its route string as QEMU's xHCI finds it, and every
/// request the core sends recorded. A test completes the transfers the core asked for.
/// </summary>
internal sealed class FakeUsbHost : IUsbHost
{
    #region Fields

    private readonly List<UsbRootPort> m_rootPorts = [];
    private readonly Dictionary<int, FakePort> m_root = [];
    private readonly FakeDevice?[] m_slots = new FakeDevice?[17];
    private readonly Dictionary<(int Slot, int Index), byte[]> m_buffers = [];
    private readonly Dictionary<(int Slot, int Index), int> m_pending = [];

    #endregion

    #region Constructors

    /// <summary>
    /// A controller with USB 3 ports 1–4 and USB 2 ports 5–8, as QEMU's qemu-xhci has.
    /// </summary>
    public FakeUsbHost()
    {
        for (var port = 1; port <= 8; port++)
        {
            m_rootPorts.Add(new UsbRootPort(port, port <= 4 ? 3 : 2, 0));
            m_root[port] = new FakePort();
        }
    }

    #endregion

    #region Functions

    /// <summary>
    /// Connects a device to a root port.
    /// </summary>
    public void Plug(int port, FakeDevice device)
    {
        m_root[port].Device = device;
        m_root[port].Changed = true;
    }

    /// <summary>
    /// Disconnects a root port's device.
    /// </summary>
    public void Unplug(int port)
    {
        m_root[port].Device = null;
        m_root[port].Enabled = false;
        m_root[port].Changed = true;
    }

    /// <summary>
    /// Completes the transfer the core asked for on a device's endpoint with the bytes the device sent.
    /// </summary>
    /// <returns>The completion, as the core takes it.</returns>
    public (int Slot, int Index, uint Code, int Residual) Complete(FakeDevice device, int index, byte[] bytes, uint code = XhciTrb.SUCCESS)
    {
        Assert.That(m_pending.Remove((device.Slot, index), out var length), Is.True, $"no transfer asked for on endpoint {index} of slot {device.Slot}");
        bytes.CopyTo(m_buffers[(device.Slot, index)], 0);
        return (device.Slot, index, code, length - bytes.Length);
    }

    /// <summary>
    /// Whether the core asked for a transfer on a device's endpoint.
    /// </summary>
    public bool Asked(FakeDevice device, int index) => device.Slot > 0 && m_pending.ContainsKey((device.Slot, index));

    #endregion

    #region IUsbHost

    public IReadOnlyList<UsbRootPort> RootPorts => m_rootPorts;

    public UsbRootPortState ReadRootPort(int port)
    {
        var state = m_root[port];
        return new UsbRootPortState(state.Device is not null, state.Enabled, state.Device?.Speed ?? 0, state.Changed, false);
    }

    public void AcknowledgeRootPort(int port) => m_root[port].Changed = false;

    public UsbRootPortState ResetRootPort(int port)
    {
        Requests.Add($"reset root port {port}");
        m_root[port].Enabled = m_root[port].Device is not null;
        return ReadRootPort(port);
    }

    public int Address(UsbLocation location)
    {
        var port = m_root[location.RootPort];
        var device = port.Enabled ? port.Device : null;
        for (var tier = 0; tier < location.Depth && device is not null; tier++)
        {
            var hubPort = device.Hub!.Ports[(int)(location.Route >> 4 * tier & 0xF)];
            device = hubPort.Enabled ? hubPort.Device : null;
        }
        if (device is null)
            throw new IOException($"No device answers at {location.Path}.");
        Assert.That(location.Speed, Is.EqualTo(device.Speed), $"the speed the core took for {location.Path}");
        var slot = Array.FindIndex(m_slots, 1, item => item is null);
        m_slots[slot] = device;
        device.Slot = slot;
        device.Location = location;
        Requests.Add($"address {location.Path} as slot {slot}");
        return slot;
    }

    public int MaxPacket0(int slot) => Device(slot).DeviceDescriptor[7];

    public byte[] Control(int slot, UsbSetup setup)
    {
        var device = Device(slot);
        Requests.Add($"slot {slot}: {setup.RequestType:x2} {setup.Request:x2} {setup.Value:x4} {setup.Index:x4}");
        if (device.Stalls.Contains((setup.RequestType, setup.Request)))
            throw new IOException($"The request {setup.Request:x2} of slot {slot} completed with code 6.");
        var data = new byte[setup.Length];
        byte[] answer = (setup.RequestType, setup.Request, setup.Value >> 8) switch
        {
            (0x80, UsbSetup.GET_DESCRIPTOR, UsbDescriptors.DEVICE) => device.DeviceDescriptor,
            (0x80, UsbSetup.GET_DESCRIPTOR, UsbDescriptors.CONFIGURATION) => device.Configuration,
            (0x81, UsbSetup.GET_DESCRIPTOR, UsbDescriptors.REPORT) => device.ReportDescriptor ?? throw new IOException("No report descriptor."),
            (0xA0, UsbSetup.GET_DESCRIPTOR, UsbHub.DESCRIPTOR) => device.Hub!.Descriptor,
            (0xA0, UsbSetup.GET_STATUS, _) => new byte[4],
            (0xA3, UsbSetup.GET_STATUS, _) => device.Hub!.Ports[setup.Index].Status(),
            (0x23, UsbSetup.SET_FEATURE, _) => device.Hub!.Ports[setup.Index].Set(setup.Value),
            (0x23, UsbSetup.CLEAR_FEATURE, _) => device.Hub!.Ports[setup.Index].Clear(setup.Value),
            _ => [],
        };
        answer.AsSpan(0, Math.Min(answer.Length, data.Length)).CopyTo(data);
        return setup.IsIn ? data : [];
    }

    public int ConfigureInterruptIn(int slot, UsbEndpoint endpoint)
    {
        var index = XhciContexts.Index(endpoint);
        m_buffers[(slot, index)] = new byte[UsbBus.REPORT_BUFFER_BYTES];
        Requests.Add($"configure endpoint {endpoint.Address:x2} of slot {slot}");
        return index;
    }

    public void ConfigureHub(int slot, UsbHubDescriptor hub) => Requests.Add($"hub slot {slot}: {hub.Ports} ports");

    public void Receive(int slot, int index, int length)
    {
        Assert.That(m_pending.ContainsKey((slot, index)), Is.False, $"a second transfer on endpoint {index} of slot {slot}");
        m_pending[(slot, index)] = length;
    }

    public ReadOnlySpan<byte> Received(int slot, int index) => m_buffers[(slot, index)];

    public bool RecoverEndpoint(int slot, int index)
    {
        Requests.Add($"recover endpoint {index} of slot {slot}");
        return Recovers;
    }

    public void Disable(int slot)
    {
        Requests.Add($"disable slot {slot}");
        m_slots[slot]!.Slot = 0;
        m_slots[slot] = null;
        foreach (var key in m_pending.Keys.Where(key => key.Slot == slot).ToList())
            m_pending.Remove(key);
    }

    public void Delay(int milliseconds) => Delayed += milliseconds;

    private FakeDevice Device(int slot) => m_slots[slot] ?? throw new IOException($"Slot {slot} holds no device.");

    #endregion

    #region Properties

    /// <summary>
    /// Every request in order.
    /// </summary>
    public List<string> Requests { get; } = [];

    /// <summary>
    /// The milliseconds the core waited.
    /// </summary>
    public int Delayed { get; private set; }

    /// <summary>
    /// What RecoverEndpoint answers.
    /// </summary>
    public bool Recovers { get; set; } = true;

    #endregion

    #region Nested Types

    private sealed class FakePort
    {
        public FakeDevice? Device { get; set; }

        public bool Enabled { get; set; }

        public bool Changed { get; set; }
    }

    #endregion
}

/// <summary>
/// A device of the fake host: its descriptors, the requests it stalls, a hub's ports where it is one.
/// </summary>
internal sealed class FakeDevice(int speed, byte[] deviceDescriptor, byte[] configuration, byte[]? reportDescriptor = null, FakeHub? hub = null)
{
    public int Speed { get; } = speed;

    public byte[] DeviceDescriptor { get; } = deviceDescriptor;

    public byte[] Configuration { get; } = configuration;

    public byte[]? ReportDescriptor { get; } = reportDescriptor;

    public FakeHub? Hub { get; } = hub;

    public HashSet<(byte RequestType, byte Request)> Stalls { get; } = [];

    public int Slot { get; set; }

    public UsbLocation? Location { get; set; }
}

/// <summary>
/// A hub's ports as QEMU's usb-hub keeps them (hw/usb/dev-hub.c): a reset enables a connected port and sets the reset's
/// and the enable's changes; a connection sets the connection's change.
/// </summary>
internal sealed class FakeHub
{
    public FakeHub(int ports, byte[] descriptor)
    {
        Descriptor = descriptor;
        Ports = Enumerable.Range(0, ports + 1).Select(_ => new FakeHubPort()).ToArray();
    }

    public byte[] Descriptor { get; }

    public FakeHubPort[] Ports { get; }

    public void Plug(int port, FakeDevice device)
    {
        Ports[port].Device = device;
        Ports[port].StatusBits = (ushort)(Ports[port].StatusBits & ~0x0602 | 0x0001 | (device.Speed == XhciContexts.LOW_SPEED ? 0x0200 : 0) |
            (device.Speed == XhciContexts.HIGH_SPEED ? 0x0400 : 0));
        Ports[port].ChangeBits |= 0x0001;
    }

    public void Unplug(int port)
    {
        Ports[port].Device = null;
        Ports[port].StatusBits &= unchecked((ushort)~0x0603);
        Ports[port].ChangeBits |= 0x0001;
    }

    /// <summary>
    /// The status change report the hub sends: bit n for each port with a change.
    /// </summary>
    public byte[] Report()
    {
        var bitmap = new byte[(Ports.Length - 1 + 8) / 8];
        for (var port = 1; port < Ports.Length; port++)
        {
            if (Ports[port].ChangeBits != 0)
                bitmap[port / 8] |= (byte)(1 << port % 8);
        }
        return bitmap;
    }
}

internal sealed class FakeHubPort
{
    public FakeDevice? Device { get; set; }

    public ushort StatusBits { get; set; } = 0x0100;

    public ushort ChangeBits { get; set; }

    public bool Enabled => (StatusBits & 0x0002) != 0;

    public byte[] Status()
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, StatusBits);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), ChangeBits);
        return bytes;
    }

    public byte[] Set(ushort feature)
    {
        if (feature == UsbHub.PORT_RESET && Device is not null)
        {
            StatusBits |= 0x0002;
            ChangeBits |= 0x0012;
        }
        return [];
    }

    public byte[] Clear(ushort feature)
    {
        if (feature >= UsbHub.C_PORT_CONNECTION && feature <= UsbHub.C_PORT_RESET)
            ChangeBits &= (ushort)~(1 << feature - UsbHub.C_PORT_CONNECTION);
        return [];
    }
}
