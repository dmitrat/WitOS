using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// The USB core of plan step H2.2 over a host controller (<see cref="IUsbHost"/>): the tree of devices below the root
/// hub's ports, kept as their ports change. A device on a port is reset and addressed where it sits and its descriptors
/// read. A hub (USB 2.0 §11) has its ports powered and watched through its status change endpoint; a port that gains a
/// device is reset and the device below it taken the same way, to the route string's five tiers. A HID interface whose
/// report descriptor names keys becomes a keyboard whose reports are always asked for (HID 1.11, the report protocol a
/// reset leaves), and a boot keyboard whose descriptor cannot be read speaks the boot protocol. A device that goes takes
/// the devices below it: its slot is disabled and every key it held released. An interrupt endpoint whose transfer
/// fails is reset, its halt cleared on the device and its transfer asked for again, until it fails more than three
/// times in a row. What the core does goes to its log, each key with the port of its keyboard.
/// </summary>
public sealed class UsbBus
{
    #region Constants

    /// <summary>
    /// The bytes of an interrupt endpoint's buffer, the most a transfer asks for.
    /// </summary>
    public const int REPORT_BUFFER_BYTES = 1024;

    /// <summary>
    /// The least time a hub's ports are given to power, whatever its descriptor says: Linux's 100 ms below the root.
    /// </summary>
    private const int HUB_POWER_MILLISECONDS = 100;

    /// <summary>
    /// The time a new connection settles before its port is reset (USB 2.0 §7.1.7.3, TATTDB).
    /// </summary>
    private const int DEBOUNCE_MILLISECONDS = 100;

    /// <summary>
    /// The time between looks at a hub's port in reset.
    /// </summary>
    private const int RESET_STEP_MILLISECONDS = 10;

    /// <summary>
    /// The most time a hub's port reset may take.
    /// </summary>
    private const int RESET_MILLISECONDS = 500;

    /// <summary>
    /// The reset's recovery time before the device answers (USB 2.0 §7.1.7.3, TRSTRCY).
    /// </summary>
    private const int RECOVERY_MILLISECONDS = 10;

    /// <summary>
    /// The failed transfers in a row an endpoint is taken back from; the next leaves it.
    /// </summary>
    private const int FAILURES = 3;

    /// <summary>
    /// The bytes of the largest configuration or report descriptor read: a device's control buffer.
    /// </summary>
    private const int DESCRIPTOR_LIMIT = 1024;

    /// <summary>
    /// evdev's EV_KEY.
    /// </summary>
    private const ushort EVENT_KEY = 1;

    #endregion

    #region Fields

    private readonly IUsbHost m_host;
    private readonly Action<string> m_log;
    private readonly Dictionary<int, Node> m_roots = [];
    private readonly Dictionary<int, Node> m_slots = [];
    private readonly List<(ushort Type, ushort Code, int Value)> m_events = [];

    #endregion

    #region Constructors

    /// <summary>
    /// Makes the core of a host controller.
    /// </summary>
    /// <param name="host">The controller.</param>
    /// <param name="log">Where the core's lines go.</param>
    public UsbBus(IUsbHost host, Action<string> log)
    {
        m_host = host;
        m_log = log;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Takes the device on every root port that has one, and the devices below each hub among them.
    /// </summary>
    public void Start()
    {
        foreach (var port in m_host.RootPorts)
        {
            var state = m_host.ReadRootPort(port.Number);
            m_host.AcknowledgeRootPort(port.Number);
            if (state.Connected)
                AttachRoot(port);
        }
    }

    /// <summary>
    /// A root port's change: the device that went detached with the devices below it, a device that came taken.
    /// </summary>
    /// <param name="number">The port, from 1.</param>
    public void RootPortChanged(int number)
    {
        var port = m_host.RootPorts.FirstOrDefault(item => item.Number == number);
        if (port is null)
            return;
        var state = m_host.ReadRootPort(number);
        m_host.AcknowledgeRootPort(number);
        var detached = false;
        if (m_roots.TryGetValue(number, out var node) && (state.ConnectionChanged || !state.Connected || state.EnableChanged && !state.Enabled))
        {
            Detach(node);
            detached = true;
        }
        if (state.Connected && !m_roots.ContainsKey(number) && (state.ConnectionChanged || detached))
            AttachRoot(port);
    }

    /// <summary>
    /// A completed transfer of an interrupt endpoint: a keyboard's report turned into key events, a hub's changed
    /// ports served, the transfer asked for again; a failed one taken back.
    /// </summary>
    /// <param name="slot">The device.</param>
    /// <param name="index">The endpoint's Device Context Index.</param>
    /// <param name="code">The completion code, as xHCI names it (<see cref="XhciTrb.SUCCESS"/>, <see cref="XhciTrb.SHORT_PACKET"/>, <see cref="XhciTrb.STALL"/>, …).</param>
    /// <param name="residual">The bytes asked for that did not come.</param>
    public void Completed(int slot, int index, uint code, int residual)
    {
        if (!m_slots.TryGetValue(slot, out var node))
            return; // a device already let go
        var endpoint = node.Endpoints.FirstOrDefault(item => item.Index == index);
        if (endpoint is null || endpoint.Left)
            return;
        if (code is not (XhciTrb.SUCCESS or XhciTrb.SHORT_PACKET))
        {
            Failed(node, endpoint, code);
            return;
        }
        endpoint.Failures = 0;
        var data = m_host.Received(slot, index)[..Math.Clamp(endpoint.Length - residual, 0, endpoint.Length)].ToArray();
        if (endpoint.Keyboard is { } keyboard)
        {
            var start = m_events.Count;
            keyboard.Update(data, m_events);
            LogKeys(node, start);
        }
        else if (node.Hub is { } hub)
        {
            HubChanged(node, hub, data);
        }
        m_host.Receive(slot, index, endpoint.Length);
    }

    /// <summary>
    /// The key events of every keyboard since the last call, in order: what the input protocol carries.
    /// </summary>
    /// <returns>The events.</returns>
    public List<(ushort Type, ushort Code, int Value)> TakeEvents()
    {
        var events = new List<(ushort Type, ushort Code, int Value)>(m_events);
        m_events.Clear();
        return events;
    }

    // The device on a root port: the port readied, then the device taken where it sits.
    private void AttachRoot(UsbRootPort port)
    {
        var name = $"port {port.Number} (USB {port.Revision})";
        UsbRootPortState state;
        try
        {
            state = m_host.ResetRootPort(port.Number);
        }
        catch (IOException exception)
        {
            m_log($"{name}: {exception.Message}");
            return;
        }
        if (!state.Connected || !state.Enabled)
        {
            m_log($"{name}: the port is not enabled after its reset");
            return;
        }
        Attach(UsbLocation.Root(port.Number, state.Speed), null, port.Number);
    }

    // A device on a hub's port: the connection settled, the port reset, then the device taken where it sits.
    private void AttachBelow(Node node, Hub hub, int port)
    {
        var name = $"port {node.Location.Path}.{port}";
        if (node.Location.Depth >= UsbHub.TIERS || port > UsbHub.ROUTABLE_PORTS)
        {
            m_log($"{name}: a device beyond the route string's {UsbHub.TIERS} tiers of {UsbHub.ROUTABLE_PORTS} ports, left");
            return;
        }
        UsbPortStatus status;
        try
        {
            m_host.Delay(DEBOUNCE_MILLISECONDS);
            m_host.Control(node.Slot, UsbSetup.SetPortFeature(port, UsbHub.PORT_RESET));
            for (var waited = 0; ; waited += RESET_STEP_MILLISECONDS)
            {
                m_host.Delay(RESET_STEP_MILLISECONDS);
                status = UsbHub.ReadPortStatus(m_host.Control(node.Slot, UsbSetup.GetPortStatus(port)));
                if (status.ResetChanged && !status.Resetting || !status.Connected)
                    break;
                if (waited >= RESET_MILLISECONDS)
                    throw new IOException($"The port did not end its reset in {RESET_MILLISECONDS} ms: status {status.Status:x4}, change {status.Change:x4}.");
            }
            // The reset's changes acknowledged; a connection change stays, for the port's next report.
            if (status.ResetChanged)
                m_host.Control(node.Slot, UsbSetup.ClearPortFeature(port, UsbHub.C_PORT_RESET));
            if (status.EnableChanged)
                m_host.Control(node.Slot, UsbSetup.ClearPortFeature(port, UsbHub.C_PORT_ENABLE));
            m_host.Delay(RECOVERY_MILLISECONDS);
        }
        catch (Exception exception) when (exception is IOException or FormatException)
        {
            m_log($"{name}: {exception.Message}");
            return;
        }
        if (!status.Connected || !status.Enabled)
        {
            m_log($"{name}: the port is not enabled after its reset: status {status.Status:x4}, change {status.Change:x4}");
            return;
        }
        Attach(UsbLocation.Below(node.Location, node.Slot, port, status.Speed), node, port);
    }

    // The device at a location: addressed, kept in the tree, configured as what it is. A device that fails after its
    // address stays in the tree, so that its port's change lets its slot go.
    private void Attach(UsbLocation location, Node? parent, int port)
    {
        int slot;
        try
        {
            slot = m_host.Address(location);
        }
        catch (IOException exception)
        {
            m_log($"port {location.Path}: {exception.Message}");
            return;
        }
        var node = new Node(location, slot, parent, port);
        m_slots[slot] = node;
        if (parent?.Hub is { } hub)
            hub.Children[port] = node;
        else
            m_roots[port] = node;
        try
        {
            Configure(node);
        }
        catch (Exception exception) when (exception is IOException or FormatException)
        {
            m_log($"{node.Name}: {exception.Message}");
        }
    }

    // The device's descriptors read, then a hub or its keyboards configured; anything else is left addressed.
    private void Configure(Node node)
    {
        var device = UsbDescriptors.ReadDevice(m_host.Control(node.Slot, UsbSetup.GetDescriptor(UsbDescriptors.DEVICE, 0, UsbDescriptors.DEVICE_BYTES)));
        var head = m_host.Control(node.Slot, UsbSetup.GetDescriptor(UsbDescriptors.CONFIGURATION, 0, UsbDescriptors.CONFIGURATION_BYTES));
        var total = Math.Min(UsbDescriptors.TotalLength(head), DESCRIPTOR_LIMIT);
        var configuration = UsbDescriptors.ReadConfiguration(m_host.Control(node.Slot, UsbSetup.GetDescriptor(UsbDescriptors.CONFIGURATION, 0, (ushort)total)));
        m_log($"{node.Name}: device {device.Vendor:x4}:{device.Product:x4}, USB {device.Usb >> 8}.{device.Usb >> 4 & 0xF}, {SpeedName(node.Location.Speed)}, " +
            $"slot {node.Slot}, packets of {m_host.MaxPacket0(node.Slot)} bytes on its control endpoint, configuration {configuration.Value} with " +
            string.Join(", ", configuration.Interfaces.Select(item => $"interface {item.Number} class {item.Class:x2}/{item.SubClass:x2}/{item.Protocol:x2}")));
        var hub = configuration.Interfaces.FirstOrDefault(item => item.Class == UsbDescriptors.CLASS_HUB && item.AlternateSetting == 0 && item.Endpoints.Any(IsInterruptIn));
        if (device.Class == UsbDescriptors.CLASS_HUB || hub is not null)
        {
            ConfigureHub(node, device, configuration, hub ?? throw new FormatException("A hub without an interrupt IN endpoint."));
            return;
        }
        var interfaces = configuration.Interfaces.Where(item => item.Class == UsbDescriptors.CLASS_HID && item.AlternateSetting == 0 && item.Endpoints.Any(IsInterruptIn)).ToList();
        if (interfaces.Count == 0)
        {
            m_log($"{node.Name}: neither a hub nor a HID interface, left unconfigured");
            return;
        }
        ConfigureKeyboards(node, configuration, interfaces);
    }

    // A hub: its status change endpoint configured, the controller told it is a hub, its ports powered and each port
    // with a device served, then its changes asked for.
    private void ConfigureHub(Node node, UsbDeviceDescriptor device, UsbConfiguration configuration, UsbInterface item)
    {
        var endpoint = item.Endpoints.First(IsInterruptIn);
        var index = m_host.ConfigureInterruptIn(node.Slot, endpoint);
        m_host.Control(node.Slot, UsbSetup.SetConfiguration(configuration.Value));
        var descriptor = UsbHub.ReadDescriptor(m_host.Control(node.Slot, UsbSetup.GetHubDescriptor(UsbHub.DESCRIPTOR_BYTES)));
        m_host.ConfigureHub(node.Slot, descriptor);
        for (var port = 1; port <= descriptor.Ports; port++)
            m_host.Control(node.Slot, UsbSetup.SetPortFeature(port, UsbHub.PORT_POWER));
        var power = Math.Max(descriptor.PowerOnToGoodMilliseconds, HUB_POWER_MILLISECONDS);
        m_host.Delay(power);
        var hub = new Hub(descriptor);
        node.Hub = hub;
        var length = (descriptor.Ports + 8) / 8; // a bit a port from bit 1, bit 0 the hub's own
        node.Endpoints.Add(new Endpoint(index, endpoint.Address, length, null));
        var translators = node.Location.Speed != XhciContexts.HIGH_SPEED ? ""
            : device.Protocol == UsbHub.PROTOCOL_MULTIPLE_TT ? ", a Transaction Translator per port, one used" : ", one Transaction Translator";
        m_log($"{node.Name}: a hub of {descriptor.Ports} ports{translators}, powered for {power} ms, endpoint {endpoint.Address:x2} at interval {endpoint.Interval}: watching its ports");
        for (var port = 1; port <= descriptor.Ports; port++)
            HubPort(node, hub, port, true);
        m_host.Receive(node.Slot, index, length);
    }

    // A device's HID interfaces: their endpoints configured before the configuration is set, as xHCI asks; then each
    // interface whose report descriptor names keys a keyboard whose reports are asked for.
    private void ConfigureKeyboards(Node node, UsbConfiguration configuration, List<UsbInterface> interfaces)
    {
        var endpoints = interfaces.Select(item => item.Endpoints.First(IsInterruptIn)).ToList();
        var indices = endpoints.Select(endpoint => m_host.ConfigureInterruptIn(node.Slot, endpoint)).ToList();
        m_host.Control(node.Slot, UsbSetup.SetConfiguration(configuration.Value));
        for (var i = 0; i < interfaces.Count; i++)
        {
            var item = interfaces[i];
            var endpoint = endpoints[i];
            var (keyboard, protocol) = Keyboard(node, item);
            if (keyboard is null)
            {
                m_log($"{node.Name}: interface {item.Number} names no key ({protocol}), left");
                continue;
            }
            try
            {
                m_host.Control(node.Slot, UsbSetup.SetIdle(item.Number, 0));
            }
            catch (IOException exception)
            {
                m_log($"{node.Name}: SET_IDLE refused ({exception.Message}); the keyboard reports as it will");
            }
            var length = Math.Clamp(Math.Max(keyboard.ReportBytes, endpoint.PacketBytes), 1, REPORT_BUFFER_BYTES);
            node.Endpoints.Add(new Endpoint(indices[i], endpoint.Address, length, keyboard));
            m_host.Receive(node.Slot, indices[i], length);
            var reports = keyboard.UsesReportIds ? $" with keys in report {string.Join(", ", keyboard.ReportIds.Order())}" : "";
            m_log($"{node.Name}: a keyboard of the {protocol} on interface {item.Number}, endpoint {endpoint.Address:x2} of {endpoint.PacketBytes} bytes " +
                $"at interval {endpoint.Interval}, reports of {keyboard.ReportBytes} bytes{reports}: reporting keys");
        }
    }

    // The keyboard an interface describes: by its report descriptor, or by the boot protocol where a boot keyboard's
    // descriptor cannot be read. Null with the reason for an interface that names no key.
    private (HidKeyboard? Keyboard, string Protocol) Keyboard(Node node, UsbInterface item)
    {
        if (item.ReportLength > 0)
        {
            try
            {
                var bytes = m_host.Control(node.Slot, UsbSetup.GetReportDescriptor(item.Number, (ushort)Math.Min(item.ReportLength, DESCRIPTOR_LIMIT)));
                return (HidKeyboard.From(HidReportDescriptor.Parse(bytes)), "report protocol");
            }
            catch (Exception exception) when (exception is IOException or FormatException)
            {
                if (!item.IsBootKeyboard)
                    return (null, $"its report descriptor: {exception.Message}");
                m_log($"{node.Name}: interface {item.Number}'s report descriptor could not be read ({exception.Message}); the boot protocol instead");
            }
        }
        if (!item.IsBootKeyboard)
            return (null, "no report descriptor");
        m_host.Control(node.Slot, UsbSetup.SetProtocol(item.Number, UsbSetup.BOOT_PROTOCOL));
        return (HidKeyboard.Boot(), "boot protocol");
    }

    // A hub's status change report: its own change acknowledged, each changed port served.
    private void HubChanged(Node node, Hub hub, ReadOnlySpan<byte> bitmap)
    {
        if (bitmap.Length > 0 && (bitmap[0] & 1) != 0)
            HubStatus(node);
        foreach (var port in UsbHub.ChangedPorts(bitmap, hub.Descriptor.Ports))
            HubPort(node, hub, port, false);
    }

    // A hub's own change, its local power or over-current, acknowledged.
    private void HubStatus(Node node)
    {
        try
        {
            var status = UsbHub.ReadPortStatus(m_host.Control(node.Slot, UsbSetup.GetHubStatus()));
            for (var bit = 0; bit < 2; bit++)
            {
                if ((status.Change & 1 << bit) != 0)
                    m_host.Control(node.Slot, UsbSetup.ClearHubFeature((ushort)(UsbHub.C_HUB_LOCAL_POWER + bit)));
            }
            m_log($"{node.Name}: hub status {status.Status:x4}, its change {status.Change:x4} acknowledged");
        }
        catch (Exception exception) when (exception is IOException or FormatException)
        {
            m_log($"{node.Name}: hub status: {exception.Message}");
        }
    }

    // A hub's port: its changes acknowledged, the device that went or lost its port detached, a device that came — or
    // was there when the hub started — taken.
    private void HubPort(Node node, Hub hub, int port, bool starting)
    {
        UsbPortStatus status;
        try
        {
            status = UsbHub.ReadPortStatus(m_host.Control(node.Slot, UsbSetup.GetPortStatus(port)));
            foreach (var feature in status.ChangeFeatures())
                m_host.Control(node.Slot, UsbSetup.ClearPortFeature(port, feature));
        }
        catch (Exception exception) when (exception is IOException or FormatException)
        {
            m_log($"port {node.Location.Path}.{port}: {exception.Message}");
            return;
        }
        var detached = false;
        if (hub.Children[port] is { } child && (status.ConnectionChanged || !status.Connected || status.EnableChanged && !status.Enabled))
        {
            Detach(child);
            detached = true;
        }
        if (status.Connected && hub.Children[port] is null && (starting || status.ConnectionChanged || detached))
            AttachBelow(node, hub, port);
    }

    // A device that went: the devices below it first, every key it held released, its slot let go, its place emptied.
    private void Detach(Node node)
    {
        if (node.Hub is { } hub)
        {
            foreach (var child in hub.Children.Where(item => item is not null).ToList())
                Detach(child!);
        }
        var start = m_events.Count;
        foreach (var endpoint in node.Endpoints)
            endpoint.Keyboard?.Release(m_events);
        LogKeys(node, start);
        string result;
        try
        {
            m_host.Disable(node.Slot);
            result = $"slot {node.Slot} disabled";
        }
        catch (IOException exception)
        {
            result = $"slot {node.Slot} dropped ({exception.Message})";
        }
        m_slots.Remove(node.Slot);
        if (node.Parent?.Hub is { } parent)
            parent.Children[node.Port] = null;
        else
            m_roots.Remove(node.Port);
        m_log($"{node.Name}: detached, {result}");
    }

    // A failed transfer: the endpoint taken back — reset, its ring moved past the transfer, the device's halt cleared —
    // and asked again, unless it failed too often in a row or cannot be reset.
    private void Failed(Node node, Endpoint endpoint, uint code)
    {
        var what = $"{node.Name}: endpoint {endpoint.Address:x2} completed with code {code} ({CodeName(code)})";
        if (++endpoint.Failures > FAILURES)
        {
            endpoint.Left = true;
            m_log($"{what} after {FAILURES} failures in a row: left until its port changes");
            return;
        }
        if (!m_host.RecoverEndpoint(node.Slot, endpoint.Index))
        {
            endpoint.Left = true;
            m_log($"{what}: the endpoint could not be reset, left until its port changes");
            return;
        }
        string halt;
        try
        {
            m_host.Control(node.Slot, UsbSetup.ClearEndpointHalt(endpoint.Address));
            halt = "its halt cleared on the device";
        }
        catch (IOException exception)
        {
            halt = $"the device refused to clear its halt ({exception.Message})";
        }
        m_log($"{what}: reset, {halt}, asked again");
        m_host.Receive(node.Slot, endpoint.Index, endpoint.Length);
    }

    private void LogKeys(Node node, int start)
    {
        for (var i = start; i < m_events.Count; i++)
        {
            var (type, code, value) = m_events[i];
            if (type == EVENT_KEY)
                m_log($"key {code} {(value == 0 ? "up" : value == 1 ? "down" : "repeat")} ({node.Name})");
        }
    }

    private static bool IsInterruptIn(UsbEndpoint endpoint) => endpoint.IsIn && endpoint.Type == UsbEndpoint.INTERRUPT;

    /// <summary>
    /// A speed's name.
    /// </summary>
    /// <param name="speed">The speed, as PORTSC names it.</param>
    /// <returns>The name.</returns>
    public static string SpeedName(int speed) => speed switch
    {
        XhciContexts.FULL_SPEED => "full speed",
        XhciContexts.LOW_SPEED => "low speed",
        XhciContexts.HIGH_SPEED => "high speed",
        XhciContexts.SUPER_SPEED => "SuperSpeed",
        _ => $"speed {speed}",
    };

    private static string CodeName(uint code) => code switch
    {
        XhciTrb.BABBLE => "babble",
        XhciTrb.TRANSACTION_ERROR => "transaction error",
        XhciTrb.STALL => "stall",
        _ => "an error",
    };

    #endregion

    #region Properties

    /// <summary>
    /// The devices addressed.
    /// </summary>
    public int Devices => m_slots.Count;

    /// <summary>
    /// The hubs among them.
    /// </summary>
    public int Hubs => m_slots.Values.Count(node => node.Hub is not null);

    /// <summary>
    /// The keyboards they hold.
    /// </summary>
    public int Keyboards => m_slots.Values.Sum(node => node.Endpoints.Count(endpoint => endpoint.Keyboard is not null));

    #endregion

    #region Nested Types

    private sealed class Node(UsbLocation location, int slot, Node? parent, int port)
    {
        public UsbLocation Location { get; } = location;

        public int Slot { get; } = slot;

        public Node? Parent { get; } = parent;

        public int Port { get; } = port;

        public Hub? Hub { get; set; }

        public List<Endpoint> Endpoints { get; } = [];

        public string Name => $"port {Location.Path}";
    }

    private sealed class Hub(UsbHubDescriptor descriptor)
    {
        public UsbHubDescriptor Descriptor { get; } = descriptor;

        public Node?[] Children { get; } = new Node?[descriptor.Ports + 1];
    }

    private sealed class Endpoint(int index, byte address, int length, HidKeyboard? keyboard)
    {
        public int Index { get; } = index;

        public byte Address { get; } = address;

        public int Length { get; } = length;

        public HidKeyboard? Keyboard { get; } = keyboard;

        public int Failures { get; set; }

        public bool Left { get; set; }
    }

    #endregion
}
