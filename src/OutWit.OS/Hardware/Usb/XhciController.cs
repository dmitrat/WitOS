using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// An xHCI host controller (xHCI 1.2, plan step H2.1) the driver holds: its registers through the BAR the PCI function
/// names, its structures in one pinned arena, its interrupter 0 bound to an event — an MSI-X message where the kernel
/// and the function have one, its INTx line otherwise. It takes the controller from the firmware (USB Legacy Support),
/// resets and starts it with a command ring and one event ring segment, and serves what a USB core above it asks: the
/// root hub's ports with their protocols, a device addressed on a port, control transfers on its default endpoint, an
/// interrupt IN endpoint configured and its transfers queued, and the events the controller reports.
/// </summary>
public sealed class XhciController : IDisposable
{
    #region Constants

    /// <summary>
    /// The pages of the arena: the controller's structures, then five pages' worth for each device.
    /// </summary>
    private const int ARENA_PAGES = 64;

    /// <summary>
    /// The slots the driver enables, of the controller's MaxSlots.
    /// </summary>
    private const int SLOTS = 16;

    /// <summary>
    /// The TRBs of the command ring and of the event ring's segment: a page each.
    /// </summary>
    private const int RING_BYTES = 4096;

    /// <summary>
    /// The TRBs of a device's transfer ring: 64.
    /// </summary>
    private const int TRANSFER_RING_BYTES = 1024;

    /// <summary>
    /// The bytes of a device's control buffer, the largest configuration the driver reads.
    /// </summary>
    private const int CONTROL_BYTES = 1024;

    /// <summary>
    /// The seconds a command, a control transfer or the controller's own change may take.
    /// </summary>
    private const int TIMEOUT_SECONDS = 5;

    /// <summary>
    /// The extended capability of USB Legacy Support.
    /// </summary>
    private const int LEGACY_SUPPORT = 1;

    /// <summary>
    /// The extended capability of a Supported Protocol.
    /// </summary>
    private const int SUPPORTED_PROTOCOL = 2;

    // Operational registers.
    private const int USBCMD = 0x00;
    private const int USBSTS = 0x04;
    private const int CRCR = 0x18;
    private const int DCBAAP = 0x30;
    private const int CONFIG = 0x38;
    private const int PORTS = 0x400;

    // USBCMD and USBSTS bits.
    private const uint RUN = 1;
    private const uint RESET = 1 << 1;
    private const uint INTERRUPTER_ENABLE = 1 << 2;
    private const uint HALTED = 1;
    private const uint EVENT_INTERRUPT = 1 << 3;
    private const uint NOT_READY = 1 << 11;

    // Interrupter 0, from the runtime registers.
    private const int IMAN = 0x20;
    private const int IMOD = 0x24;
    private const int ERSTSZ = 0x28;
    private const int ERSTBA = 0x30;
    private const int ERDP = 0x38;
    private const uint INTERRUPT_PENDING = 1;
    private const uint INTERRUPT_ENABLE = 1 << 1;
    private const uint EVENT_HANDLER_BUSY = 1 << 3;

    // PORTSC.
    private const uint CONNECTED = 1;
    private const uint ENABLED = 1 << 1;
    private const uint PORT_RESET = 1 << 4;
    private const uint PORT_POWER = 1 << 9;
    private const uint CHANGES = 0x7FU << 17; // CSC, PEC, WRC, OCC, PRC, PLC, CEC: written 1 to clear
    private const uint RESET_CHANGE = 1 << 21;
    private const uint PRESERVED = 1 | 1 << 3 | 0xFU << 10 | 1U << 30 | 0xFU << 5 | 1 << 9 | 3 << 14 | 7U << 25;

    #endregion

    #region Fields

    private readonly KernelHandle m_device;
    private readonly KernelHandle m_configurationObject;
    private readonly MmioRegion m_configuration;
    private readonly KernelHandle m_barObject;
    private readonly MmioRegion m_bar;
    private readonly KernelHandle m_signaled;
    private readonly MsiX? m_msiX;
    private readonly InterruptBinding m_binding;
    private readonly DmaArena m_arena;
    private readonly int m_operational;
    private readonly int m_runtime;
    private readonly int m_doorbells;
    private readonly int m_contextBytes;
    private readonly List<XhciPort> m_ports = [];
    private XhciRing? m_commands;
    private XhciEventRing? m_events;
    private IDmaBuffer? m_contexts;
    private readonly Dictionary<ulong, XhciTrb> m_completed = [];
    private readonly Dictionary<int, XhciTrb> m_controlEvents = [];
    private readonly List<XhciTrb> m_transfers = [];
    private readonly List<int> m_portChanges = [];
    private bool m_running;

    #endregion

    #region Constructors

    private XhciController(KernelHandle device, KernelHandle configurationObject, MmioRegion configuration, KernelHandle barObject, MmioRegion bar,
        KernelHandle signaled, MsiX? msiX, InterruptBinding binding, DmaArena arena, string interruptDescription)
    {
        m_device = device;
        m_configurationObject = configurationObject;
        m_configuration = configuration;
        m_barObject = barObject;
        m_bar = bar;
        m_signaled = signaled;
        m_msiX = msiX;
        m_binding = binding;
        m_arena = arena;
        InterruptDescription = interruptDescription;
        var first = bar.Read32(0); // CAPLENGTH and HCIVERSION as one dword: QEMU's xHCI answers a 16-bit read at 2 with zero
        m_operational = (int)(first & 0xFF);
        Version = (ushort)(first >> 16);
        var structural1 = bar.Read32(4);
        MaxSlots = (int)(structural1 & 0xFF);
        MaxPorts = (int)(structural1 >> 24);
        var structural2 = bar.Read32(8);
        Scratchpads = (int)((structural2 >> 21 & 0x1F) << 5 | structural2 >> 27);
        var capability1 = bar.Read32(0x10);
        m_contextBytes = (capability1 & 1 << 2) != 0 ? 64 : 32;
        m_doorbells = (int)(bar.Read32(0x14) & ~3U);
        m_runtime = (int)(bar.Read32(0x18) & ~0x1FU);
        ReadExtendedCapabilities((int)(capability1 >> 16) * 4);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Opens the controller of the function the driver holds and starts it.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="descriptor">Its descriptor: region 0 its configuration space, the others its BARs.</param>
    /// <returns>The running controller.</returns>
    /// <exception cref="IOException">The function is no xHCI controller the driver serves, or the controller did not start.</exception>
    /// <exception cref="CapabilityException">A region, the arena or the interrupt could not be had.</exception>
    public static XhciController Open(KernelHandle device, DeviceDescriptor descriptor)
    {
        var configurationObject = Device.Memory(device, 0);
        MmioRegion? configuration = null;
        KernelHandle? barObject = null;
        MmioRegion? bar = null;
        KernelHandle? signaled = null;
        MsiX? msiX = null;
        InterruptBinding? binding = null;
        DmaArena? arena = null;
        XhciController? controller = null;
        try
        {
            configuration = MmioRegion.Map(configurationObject, MemoryObject.PAGE);
            configuration.Write16(PciConfiguration.COMMAND, (ushort)(configuration.Read16(PciConfiguration.COMMAND) | 6)); // memory decoding, bus mastering
            var region = PciConfiguration.RegionOfBar(configuration, descriptor, 0);
            barObject = Device.Memory(device, region);
            bar = MmioRegion.Map(barObject, (int)Math.Min(descriptor.Regions[region].Size, (ulong)(Device.WINDOW_PAGES * MemoryObject.PAGE)));
            signaled = Event.Create();
            string description;
            if (InterruptBinding.MessagesSupported && MsiX.Find(configuration, device, descriptor) is { } found)
            {
                msiX = found;
                binding = InterruptBinding.BindMessage(device, signaled, out var address, out var data);
                msiX.Program(0, address, data);
                msiX.Enable();
                description = $"MSI-X entry 0, message {data:x2} at {address:x}";
            }
            else
            {
                binding = InterruptBinding.Bind(device, 0, signaled);
                description = "its INTx line";
            }
            arena = DmaArena.Create(device, ARENA_PAGES);
            controller = new XhciController(device, configurationObject, configuration, barObject, bar, signaled, msiX, binding, arena, description);
            controller.Start();
            return controller;
        }
        catch
        {
            if (controller is not null)
            {
                controller.Dispose(); // halted, if it ran, before its arena goes
                throw;
            }
            binding?.Dispose();
            msiX?.Disable();
            msiX?.Dispose();
            signaled?.Dispose();
            arena?.Dispose();
            bar?.Dispose();
            barObject?.Dispose();
            configuration?.Dispose();
            configurationObject.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Addresses the device on a port: the port reset where its protocol needs one, a slot enabled, the default control
    /// endpoint set up from the port's speed and the device given its address, its descriptor's bMaxPacketSize0 taken.
    /// </summary>
    /// <param name="port">A port with a device connected.</param>
    /// <returns>The device.</returns>
    /// <exception cref="IOException">The port did not enable, or a command failed.</exception>
    public XhciDevice Address(XhciPort port)
    {
        var portsc = ReadPort(port.Number);
        if (port.Major == 2)
        {
            WritePort(port.Number, portsc & PRESERVED | PORT_RESET);
            var deadline = Clock.MonotonicAfter(TIMEOUT_SECONDS);
            while (((portsc = ReadPort(port.Number)) & RESET_CHANGE) == 0)
            {
                if (Clock.Monotonic() > deadline)
                    throw new IOException($"Port {port.Number} did not end its reset: PORTSC {portsc:x8}.");
                Thread.Sleep(1);
            }
            WritePort(port.Number, portsc & PRESERVED | RESET_CHANGE);
            Thread.Sleep(10); // USB 2.0 §7.1.7.3: the reset's recovery time before the device answers
            portsc = ReadPort(port.Number);
        }
        if ((portsc & (CONNECTED | ENABLED)) != (CONNECTED | ENABLED))
            throw new IOException($"Port {port.Number} is not enabled: PORTSC {portsc:x8}.");
        var speed = (int)(portsc >> 10 & 0xF);
        var slot = Command(XhciTrb.Of(XhciTrb.ENABLE_SLOT, flags: (uint)port.SlotType << 16)).Slot;
        if (slot is <= 0 || slot > SLOTS)
            throw new IOException($"The controller enabled slot {slot}.");
        var device = new XhciDevice(slot, port.Number, speed, m_arena.Allocate(XhciContexts.INPUT_CONTEXTS * m_contextBytes),
            m_arena.Allocate(XhciContexts.DEVICE_CONTEXTS * m_contextBytes), new XhciRing(m_arena.Allocate(TRANSFER_RING_BYTES), MmioRegion.Barrier),
            m_arena.Allocate(CONTROL_BYTES), XhciContexts.DefaultMaxPacket(speed));
        BinaryPrimitives.WriteUInt64LittleEndian(m_contexts!.Bytes[(slot * 8)..], device.Output.Physical);
        var input = device.Input.Bytes;
        XhciContexts.WriteControl(input, m_contextBytes, 0, 0b11);
        XhciContexts.WriteSlot(input, m_contextBytes, speed, port.Number, 1);
        XhciContexts.WriteEndpoint(input, m_contextBytes, 1, XhciContexts.CONTROL, device.MaxPacket0, 0, 0, device.Control.Base, 8);
        Command(XhciTrb.Of(XhciTrb.ADDRESS_DEVICE, device.Input.Physical, flags: (uint)slot << 24));
        var first = UsbDescriptors.ReadDevice(Control(device, UsbSetup.GetDescriptor(UsbDescriptors.DEVICE, 0, 8)));
        var maxPacket0 = speed >= XhciContexts.SUPER_SPEED ? 1 << first.MaxPacketSize0 : first.MaxPacketSize0;
        if (maxPacket0 != device.MaxPacket0 && maxPacket0 is 8 or 16 or 32 or 64 or 512)
        {
            device.MaxPacket0 = maxPacket0;
            XhciContexts.WriteControl(input, m_contextBytes, 0, 0b10);
            XhciContexts.WriteEndpoint(input, m_contextBytes, 1, XhciContexts.CONTROL, maxPacket0, 0, 0, device.Control.Base, 8);
            Command(XhciTrb.Of(XhciTrb.EVALUATE_CONTEXT, device.Input.Physical, flags: (uint)slot << 24));
        }
        return device;
    }

    /// <summary>
    /// A control transfer on a device's default endpoint: the setup, the data the request names, the status.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="setup">The request; its wLength at most the device's control buffer.</param>
    /// <returns>For an IN request, the bytes of the data stage as the buffer holds them (wLength of them); none otherwise.</returns>
    /// <exception cref="IOException">The transfer failed or timed out.</exception>
    public byte[] Control(XhciDevice device, UsbSetup setup)
    {
        if (setup.Length > CONTROL_BYTES)
            throw new ArgumentOutOfRangeException(nameof(setup), $"A data stage of {setup.Length} bytes.");
        var buffer = device.Buffer.Bytes;
        buffer[..setup.Length].Clear();
        var transferType = setup.Length == 0 ? 0U : setup.IsIn ? 3U : 2U;
        device.Control.Enqueue(XhciTrb.Of(XhciTrb.SETUP, setup.Encode(), 8, XhciTrb.IMMEDIATE_DATA | transferType << 16));
        if (setup.Length > 0)
            device.Control.Enqueue(XhciTrb.Of(XhciTrb.DATA, device.Buffer.Physical, setup.Length, setup.IsIn ? XhciTrb.DIRECTION_IN : 0));
        var status = device.Control.Enqueue(XhciTrb.Of(XhciTrb.STATUS,
            flags: XhciTrb.INTERRUPT_ON_COMPLETION | (setup.Length > 0 && setup.IsIn ? 0 : XhciTrb.DIRECTION_IN)));
        m_controlEvents.Remove(device.Slot);
        Ring(device.Slot, 1);
        var deadline = Clock.MonotonicAfter(TIMEOUT_SECONDS);
        for (; ; )
        {
            if (m_controlEvents.Remove(device.Slot, out var trbEvent))
            {
                if (trbEvent.CompletionCode is not (XhciTrb.SUCCESS or XhciTrb.SHORT_PACKET))
                    throw new IOException($"Request {setup.Request:x2} of slot {device.Slot} completed with code {trbEvent.CompletionCode}.");
                if (trbEvent.Parameter == status)
                    return setup.IsIn ? buffer[..setup.Length].ToArray() : [];
                continue; // an earlier stage's event; the status stage's follows
            }
            WaitForEvents(deadline, $"request {setup.Request:x2} of slot {device.Slot}");
        }
    }

    /// <summary>
    /// Configures an interrupt IN endpoint of an addressed device with a transfer ring of its own.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <returns>Its Device Context Index.</returns>
    /// <exception cref="IOException">The command failed.</exception>
    public int ConfigureInterruptIn(XhciDevice device, UsbEndpoint endpoint)
    {
        var index = XhciContexts.Index(endpoint);
        var ring = new XhciRing(m_arena.Allocate(TRANSFER_RING_BYTES), MmioRegion.Barrier);
        device.Rings[index] = ring;
        var input = device.Input.Bytes;
        XhciContexts.WriteControl(input, m_contextBytes, 0, 1U | 1U << index);
        XhciContexts.WriteSlot(input, m_contextBytes, device.Speed, device.Port, Math.Max(index, device.ContextEntries));
        device.ContextEntries = Math.Max(index, device.ContextEntries);
        var burst = device.Speed == XhciContexts.HIGH_SPEED ? endpoint.MaxPacketSize >> 11 & 3 : 0;
        XhciContexts.WriteEndpoint(input, m_contextBytes, index, XhciContexts.INTERRUPT_IN, endpoint.PacketBytes,
            XhciContexts.Interval(device.Speed, endpoint.Interval), burst, ring.Base, endpoint.PacketBytes);
        Command(XhciTrb.Of(XhciTrb.CONFIGURE_ENDPOINT, device.Input.Physical, flags: (uint)device.Slot << 24));
        return index;
    }

    /// <summary>
    /// Queues a transfer on an IN endpoint the device configured: the controller fills the buffer when the device sends.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="index">The endpoint's Device Context Index.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="length">The bytes asked for.</param>
    public void Receive(XhciDevice device, int index, IDmaBuffer buffer, int length)
    {
        device.Rings[index].Enqueue(XhciTrb.Of(XhciTrb.NORMAL, buffer.Physical, (uint)length,
            XhciTrb.INTERRUPT_ON_COMPLETION | XhciTrb.INTERRUPT_SHORT));
        Ring(device.Slot, index);
    }

    /// <summary>
    /// A buffer from the controller's arena, for a device's transfers.
    /// </summary>
    /// <param name="bytes">Its bytes, at most a page.</param>
    /// <returns>The buffer.</returns>
    public IDmaBuffer Allocate(int bytes) => m_arena.Allocate(bytes);

    /// <summary>
    /// Serves the interrupter once its event was set: the pending interrupt cleared, every event taken, the INTx line
    /// unmasked.
    /// </summary>
    /// <returns>The Transfer Events of the endpoints other than the default ones, in the controller's order.</returns>
    public List<XhciTrb> Service()
    {
        Drain();
        var transfers = new List<XhciTrb>(m_transfers);
        m_transfers.Clear();
        return transfers;
    }

    /// <summary>
    /// The ports whose status changed since the last call, by Port Status Change Events.
    /// </summary>
    /// <returns>Their numbers.</returns>
    public List<int> TakePortChanges()
    {
        var changes = new List<int>(m_portChanges);
        m_portChanges.Clear();
        return changes;
    }

    /// <summary>
    /// A port's PORTSC, for the log.
    /// </summary>
    /// <param name="port">The port, from 1.</param>
    /// <returns>Its value.</returns>
    public uint ReadPort(int port) => m_bar.Read32(m_operational + PORTS + 0x10 * (port - 1));

    // The controller from the firmware, reset, its structures given and running (xHCI 1.2 §4.2).
    private void Start()
    {
        var deadline = Clock.MonotonicAfter(TIMEOUT_SECONDS);
        WaitOperational(USBSTS, NOT_READY, 0, deadline, "be ready");
        if ((ReadOperational(USBSTS) & HALTED) == 0)
        {
            WriteOperational(USBCMD, ReadOperational(USBCMD) & ~RUN);
            WaitOperational(USBSTS, HALTED, HALTED, deadline, "halt");
        }
        WriteOperational(USBCMD, RESET);
        WaitOperational(USBCMD, RESET, 0, deadline, "end its reset");
        WaitOperational(USBSTS, NOT_READY, 0, deadline, "be ready after its reset");
        var slots = Math.Min(MaxSlots, SLOTS);
        WriteOperational(CONFIG, (uint)slots);
        m_contexts = m_arena.Allocate((SLOTS + 1) * 8);
        if (Scratchpads > 0)
        {
            if (Scratchpads > 16)
                throw new IOException($"The controller asks for {Scratchpads} scratchpad pages.");
            var array = m_arena.Allocate(Scratchpads * 8);
            for (var i = 0; i < Scratchpads; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(array.Bytes[(i * 8)..], m_arena.Allocate(MemoryObject.PAGE, MemoryObject.PAGE).Physical);
            BinaryPrimitives.WriteUInt64LittleEndian(m_contexts.Bytes, array.Physical);
        }
        WriteOperational64(DCBAAP, m_contexts.Physical);
        m_commands = new XhciRing(m_arena.Allocate(RING_BYTES), MmioRegion.Barrier);
        WriteOperational64(CRCR, m_commands.Base | 1);
        m_events = new XhciEventRing(m_arena.Allocate(RING_BYTES), MmioRegion.Barrier);
        var table = m_arena.Allocate(16);
        BinaryPrimitives.WriteUInt64LittleEndian(table.Bytes, m_events.Base);
        BinaryPrimitives.WriteUInt32LittleEndian(table.Bytes[8..], (uint)m_events.Count);
        MmioRegion.Barrier();
        m_bar.Write32(m_runtime + ERSTSZ, 1);
        WriteRuntime64(ERDP, m_events.Base);
        WriteRuntime64(ERSTBA, table.Physical);
        m_bar.Write32(m_runtime + IMOD, 0);
        m_bar.Write32(m_runtime + IMAN, INTERRUPT_PENDING | INTERRUPT_ENABLE);
        WriteOperational(USBCMD, RUN | INTERRUPTER_ENABLE);
        WaitOperational(USBSTS, HALTED, 0, deadline, "run");
        m_running = true;
        foreach (var port in m_ports)
        {
            var portsc = ReadPort(port.Number);
            if ((portsc & PORT_POWER) == 0)
                WritePort(port.Number, portsc & PRESERVED | PORT_POWER);
        }
    }

    // Takes the controller from the firmware where it has USB Legacy Support, and reads its Supported Protocols.
    private void ReadExtendedCapabilities(int offset)
    {
        for (var guard = 0; offset != 0 && offset + 16 <= m_bar.Length && guard < 64; guard++)
        {
            var header = m_bar.Read32(offset);
            switch (header & 0xFF)
            {
                case LEGACY_SUPPORT:
                    TakeFromFirmware(offset);
                    break;
                case SUPPORTED_PROTOCOL:
                    var ports = m_bar.Read32(offset + 8);
                    var slotType = (int)(m_bar.Read32(offset + 12) & 0x1F);
                    for (var i = 0; i < (ports >> 8 & 0xFF); i++)
                    {
                        var number = (int)(ports & 0xFF) + i;
                        if (number >= 1 && number <= MaxPorts)
                            m_ports.Add(new XhciPort(number, (int)(header >> 24), (int)(header >> 16 & 0xFF), slotType));
                    }
                    break;
            }
            var next = (int)(header >> 8 & 0xFF);
            offset = next == 0 ? 0 : offset + next * 4;
        }
        m_ports.Sort((left, right) => left.Number.CompareTo(right.Number));
    }

    // USB Legacy Support (xHCI 1.2 §7.1): the OS Owned semaphore set, the BIOS's cleared within a second, the SMIs off.
    private void TakeFromFirmware(int offset)
    {
        var legacy = m_bar.Read32(offset);
        if ((legacy & 1 << 16) == 0)
            return;
        m_bar.Write32(offset, legacy | 1 << 24);
        var deadline = Clock.MonotonicAfter(1);
        while ((m_bar.Read32(offset) & 1 << 16) != 0 && Clock.Monotonic() < deadline)
            Thread.Sleep(1);
        m_bar.Write32(offset + 4, m_bar.Read32(offset + 4) & 0xE0000000); // the SMI enables cleared, the status bits written back
        FirmwareHeld = (m_bar.Read32(offset) & 1 << 16) != 0;
    }

    // A command: put on the command ring, the doorbell rung, its completion awaited.
    private XhciTrb Command(XhciTrb command)
    {
        var physical = m_commands!.Enqueue(command);
        m_bar.Write32(m_doorbells, 0);
        var deadline = Clock.MonotonicAfter(TIMEOUT_SECONDS);
        for (; ; )
        {
            if (m_completed.Remove(physical, out var completion))
            {
                if (completion.CompletionCode != XhciTrb.SUCCESS)
                    throw new IOException($"Command {command.Type} completed with code {completion.CompletionCode}.");
                return completion;
            }
            WaitForEvents(deadline, $"command {command.Type}");
        }
    }

    // The events the controller wrote, the interrupt served; then a wait for the next interrupt, bounded by the deadline
    // and by a slice, so that a lost interrupt delays the driver rather than stops it.
    private void WaitForEvents(ulong deadline, string what)
    {
        if (Drain())
            return;
        var now = Clock.Monotonic();
        if (now > deadline)
            throw new IOException($"The controller did not complete {what}.");
        try
        {
            Event.Wait(Math.Min(deadline, now + Clock.Frequency() / 100), m_signaled);
        }
        catch (CapabilityException exception) when (exception.Error == 110)
        {
            // The slice passed; the next drain looks at the ring itself.
        }
    }

    // Every event the ring holds, sorted for whoever waits for it: command completions by their TRB, the default
    // endpoints' Transfer Events by their slot, the other endpoints' for Service, port changes by their port. Whether
    // there was any.
    private bool Drain()
    {
        m_bar.Write32(m_runtime + IMAN, INTERRUPT_PENDING | INTERRUPT_ENABLE);
        WriteOperational(USBSTS, EVENT_INTERRUPT);
        var any = false;
        while (m_events!.TryDequeue(out var trb))
        {
            any = true;
            switch (trb.Type)
            {
                case XhciTrb.COMMAND_COMPLETION:
                    m_completed[trb.Parameter] = trb;
                    break;
                case XhciTrb.TRANSFER_EVENT when trb.Endpoint == 1:
                    m_controlEvents[trb.Slot] = trb;
                    break;
                case XhciTrb.TRANSFER_EVENT:
                    m_transfers.Add(trb);
                    break;
                case XhciTrb.PORT_STATUS_CHANGE:
                    m_portChanges.Add(trb.Port);
                    break;
            }
        }
        if (any)
            WriteRuntime64(ERDP, m_events.DequeuePointer | EVENT_HANDLER_BUSY);
        if (m_msiX is null)
            m_binding.Acknowledge();
        return any;
    }

    private void Ring(int slot, int target)
    {
        MmioRegion.Barrier();
        m_bar.Write32(m_doorbells + slot * 4, (uint)target);
    }

    private void WaitOperational(int register, uint mask, uint value, ulong deadline, string what)
    {
        while ((ReadOperational(register) & mask) != value)
        {
            if (Clock.Monotonic() > deadline)
                throw new IOException($"The controller did not {what}: USBCMD {ReadOperational(USBCMD):x8}, USBSTS {ReadOperational(USBSTS):x8}.");
            Thread.Sleep(1);
        }
    }

    private uint ReadOperational(int register) => m_bar.Read32(m_operational + register);

    private void WriteOperational(int register, uint value) => m_bar.Write32(m_operational + register, value);

    // A 64-bit register as two 32-bit writes, the low half first: QEMU's xHCI acts on the high half's write.
    private void WriteOperational64(int register, ulong value)
    {
        m_bar.Write32(m_operational + register, (uint)value);
        m_bar.Write32(m_operational + register + 4, (uint)(value >> 32));
    }

    private void WriteRuntime64(int register, ulong value)
    {
        m_bar.Write32(m_runtime + register, (uint)value);
        m_bar.Write32(m_runtime + register + 4, (uint)(value >> 32));
    }

    private void WritePort(int port, uint value) => m_bar.Write32(m_operational + PORTS + 0x10 * (port - 1), value);

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (m_running)
        {
            // Halted before the arena goes, so that the controller writes into no page that is no longer pinned.
            WriteOperational(USBCMD, ReadOperational(USBCMD) & ~(RUN | INTERRUPTER_ENABLE));
            try
            {
                WaitOperational(USBSTS, HALTED, HALTED, Clock.MonotonicAfter(TIMEOUT_SECONDS), "halt");
                WriteOperational(USBCMD, RESET);
            }
            catch (IOException)
            {
                // A controller that does not halt keeps its pages; the arena below stays only as long as the process.
                m_running = false;
                return;
            }
            m_running = false;
        }
        m_msiX?.Disable();
        m_binding.Dispose();
        m_msiX?.Dispose();
        m_signaled.Dispose();
        m_arena.Dispose();
        m_bar.Dispose();
        m_barObject.Dispose();
        m_configuration.Dispose();
        m_configurationObject.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// HCIVERSION.
    /// </summary>
    public ushort Version { get; }

    /// <summary>
    /// HCSPARAMS1's MaxSlots.
    /// </summary>
    public int MaxSlots { get; }

    /// <summary>
    /// HCSPARAMS1's MaxPorts.
    /// </summary>
    public int MaxPorts { get; }

    /// <summary>
    /// The scratchpad pages the controller asked for.
    /// </summary>
    public int Scratchpads { get; }

    /// <summary>
    /// The bytes of a context: 32, or 64.
    /// </summary>
    public int ContextBytes => m_contextBytes;

    /// <summary>
    /// The root hub's ports the Supported Protocols name, by number.
    /// </summary>
    public IReadOnlyList<XhciPort> Ports => m_ports;

    /// <summary>
    /// Whether the firmware kept the controller after the driver asked for it (USB Legacy Support).
    /// </summary>
    public bool FirmwareHeld { get; private set; }

    /// <summary>
    /// The event the kernel sets on each interrupt of interrupter 0.
    /// </summary>
    public KernelHandle Interrupt => m_signaled;

    /// <summary>
    /// What the interrupt is, for the driver's log.
    /// </summary>
    public string InterruptDescription { get; }

    /// <summary>
    /// The bytes of the arena taken.
    /// </summary>
    public int ArenaUsed => m_arena.Used;

    #endregion
}

/// <summary>
/// A root hub port of the controller.
/// </summary>
/// <param name="Number">The port, from 1.</param>
/// <param name="Major">Its protocol's major revision: 2 or 3.</param>
/// <param name="Minor">Its protocol's minor revision.</param>
/// <param name="SlotType">The Protocol Slot Type an Enable Slot command names for its devices.</param>
public sealed record XhciPort(int Number, int Major, int Minor, int SlotType);

/// <summary>
/// A device the controller addressed: its slot, its port and speed, its contexts and its rings.
/// </summary>
public sealed class XhciDevice
{
    #region Constructors

    internal XhciDevice(int slot, int port, int speed, IDmaBuffer input, IDmaBuffer output, XhciRing control, IDmaBuffer buffer, int maxPacket0)
    {
        Slot = slot;
        Port = port;
        Speed = speed;
        Input = input;
        Output = output;
        Control = control;
        Buffer = buffer;
        MaxPacket0 = maxPacket0;
        Rings[1] = control;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The device's slot.
    /// </summary>
    public int Slot { get; }

    /// <summary>
    /// Its root hub port.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Its speed, as PORTSC reported it.
    /// </summary>
    public int Speed { get; }

    /// <summary>
    /// The default control endpoint's packet bytes.
    /// </summary>
    public int MaxPacket0 { get; internal set; }

    /// <summary>
    /// The index of the last valid Endpoint Context.
    /// </summary>
    public int ContextEntries { get; internal set; } = 1;

    internal IDmaBuffer Input { get; }

    internal IDmaBuffer Output { get; }

    internal XhciRing Control { get; }

    internal IDmaBuffer Buffer { get; }

    internal XhciRing[] Rings { get; } = new XhciRing[32];

    #endregion
}
