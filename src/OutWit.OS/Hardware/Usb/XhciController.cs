using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// An xHCI host controller (xHCI 1.2, plan steps H2.1 and H2.2) the driver holds: its registers through the BAR the PCI
/// function names, its structures in one pinned arena, its interrupter 0 bound to an event — an MSI-X message where the
/// kernel and the function have one, its INTx line otherwise. It takes the controller from the firmware (USB Legacy
/// Support), resets and starts it with a command ring and one event ring segment, and serves the USB core above it
/// (<see cref="IUsbHost"/>): the root hub's ports with their protocols, a device addressed where it sits — on a root port
/// or below hubs, by its route string — control transfers on its default endpoint, interrupt IN endpoints configured and
/// their transfers queued, a halted endpoint reset and its ring moved past what it held, a slot disabled with its
/// structures kept for the next device, and the events the controller reports.
/// </summary>
public sealed class XhciController : IUsbHost, IDisposable
{
    #region Constants

    /// <summary>
    /// The pages of the arena: the controller's structures, then a device's contexts, control ring and buffer, and a ring
    /// and a buffer for each of its interrupt endpoints, kept for the next device once it goes.
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
    /// The bytes of a block the arena's pools keep: a transfer ring of 64 TRBs, a device's control buffer, an interrupt
    /// endpoint's buffer.
    /// </summary>
    private const int BLOCK_BYTES = UsbBus.REPORT_BUFFER_BYTES;

    /// <summary>
    /// The bytes of a device's control buffer, the largest descriptor the driver reads.
    /// </summary>
    private const int CONTROL_BYTES = BLOCK_BYTES;

    /// <summary>
    /// The seconds a command, a control transfer or the controller's own change may take.
    /// </summary>
    private const int TIMEOUT_SECONDS = 5;

    // The longest a bulk transfer waits (E3): a drive's write of a MiB to a slow medium.
    private const int BULK_TIMEOUT_SECONDS = 30;

    // A Normal TRB's data never crosses a 64 KiB boundary (xHCI 1.2 §4.11.7.1).
    private const ulong TRB_BOUNDARY = 1UL << 16;

    // A bulk endpoint's average TRB, as xHCI 1.2 §4.14.1.1 suggests for bulk.
    private const int BULK_AVERAGE_TRB = 3072;

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
    private const uint CONNECT_CHANGE = 1 << 17;
    private const uint ENABLE_CHANGE = 1 << 18;
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
    private readonly List<UsbRootPort> m_rootPorts = [];
    private readonly XhciDevice?[] m_devices = new XhciDevice?[SLOTS + 1];
    private readonly Stack<XhciSlotMemory> m_slotMemory = [];
    private readonly Stack<IDmaBuffer> m_blocks = [];
    private XhciRing? m_commands;
    private XhciEventRing? m_events;
    private IDmaBuffer? m_contexts;
    private readonly Dictionary<ulong, XhciTrb> m_completed = [];
    private readonly Dictionary<int, List<XhciTrb>> m_controlEvents = [];
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

    /// <inheritdoc />
    public UsbRootPortState ReadRootPort(int port)
    {
        var portsc = ReadPort(port);
        return new UsbRootPortState((portsc & CONNECTED) != 0, (portsc & ENABLED) != 0, (int)(portsc >> 10 & 0xF),
            (portsc & CONNECT_CHANGE) != 0, (portsc & ENABLE_CHANGE) != 0);
    }

    /// <inheritdoc />
    public void AcknowledgeRootPort(int port)
    {
        var portsc = ReadPort(port);
        if ((portsc & CHANGES) != 0)
            WritePort(port, portsc & PRESERVED | portsc & CHANGES); // written 1 to clear, the port's enable left alone
    }

    /// <inheritdoc />
    public UsbRootPortState ResetRootPort(int port)
    {
        var info = RootPort(port);
        var deadline = Clock.MonotonicAfter(TIMEOUT_SECONDS);
        var portsc = ReadPort(port);
        if (info.Major == 2)
        {
            WritePort(port, portsc & PRESERVED | PORT_RESET);
            while (((portsc = ReadPort(port)) & RESET_CHANGE) == 0)
            {
                if (Clock.Monotonic() > deadline)
                    throw new IOException($"Port {port} did not end its reset: PORTSC {portsc:x8}.");
                Thread.Sleep(1);
            }
            WritePort(port, portsc & PRESERVED | RESET_CHANGE);
            Thread.Sleep(10); // USB 2.0 §7.1.7.3: the reset's recovery time before the device answers
        }
        else
        {
            // A USB 3 port enables itself once its link trained.
            while (((portsc = ReadPort(port)) & (CONNECTED | ENABLED)) == CONNECTED && Clock.Monotonic() <= deadline)
                Thread.Sleep(1);
        }
        return ReadRootPort(port);
    }

    /// <inheritdoc />
    public int Address(UsbLocation location)
    {
        var port = RootPort(location.RootPort);
        var slot = Command(XhciTrb.Of(XhciTrb.ENABLE_SLOT, flags: (uint)port.SlotType << 16)).Slot;
        if (slot is <= 0 || slot > SLOTS)
            throw new IOException($"The controller enabled slot {slot}.");
        var device = new XhciDevice(slot, location, TakeSlotMemory(), XhciContexts.DefaultMaxPacket(location.Speed));
        m_devices[slot] = device;
        BinaryPrimitives.WriteUInt64LittleEndian(m_contexts!.Bytes[(slot * 8)..], device.Memory.Output.Physical);
        try
        {
            var input = device.Memory.Input.Bytes;
            XhciContexts.WriteControl(input, m_contextBytes, 0, 0b11);
            XhciContexts.WriteSlot(input, m_contextBytes, location, 1);
            XhciContexts.WriteEndpoint(input, m_contextBytes, 1, XhciContexts.CONTROL, device.MaxPacket0, 0, 0, device.Control.Base, 8);
            Command(XhciTrb.Of(XhciTrb.ADDRESS_DEVICE, device.Memory.Input.Physical, flags: (uint)slot << 24));
            var first = UsbDescriptors.ReadDevice(Control(slot, UsbSetup.GetDescriptor(UsbDescriptors.DEVICE, 0, 8)));
            var maxPacket0 = location.Speed >= XhciContexts.SUPER_SPEED ? 1 << first.MaxPacketSize0 : first.MaxPacketSize0;
            if (maxPacket0 != device.MaxPacket0 && maxPacket0 is 8 or 16 or 32 or 64 or 512)
            {
                device.MaxPacket0 = maxPacket0;
                XhciContexts.WriteControl(input, m_contextBytes, 0, 0b10);
                XhciContexts.WriteEndpoint(input, m_contextBytes, 1, XhciContexts.CONTROL, maxPacket0, 0, 0, device.Control.Base, 8);
                Command(XhciTrb.Of(XhciTrb.EVALUATE_CONTEXT, device.Memory.Input.Physical, flags: (uint)slot << 24));
            }
            return slot;
        }
        catch (Exception exception) when (exception is IOException or FormatException)
        {
            try
            {
                Disable(slot);
            }
            catch (IOException)
            {
                // The slot is dropped all the same; its memory stays out of the pool.
            }
            throw exception as IOException ?? new IOException(exception.Message, exception);
        }
    }

    /// <inheritdoc />
    public int MaxPacket0(int slot) => DeviceAt(slot).MaxPacket0;

    /// <inheritdoc />
    public byte[] Control(int slot, UsbSetup setup)
    {
        var device = DeviceAt(slot);
        if (setup.Length > CONTROL_BYTES)
            throw new ArgumentOutOfRangeException(nameof(setup), $"A data stage of {setup.Length} bytes.");
        var buffer = device.Memory.Buffer.Bytes;
        buffer[..setup.Length].Clear();
        var transferType = setup.Length == 0 ? 0U : setup.IsIn ? 3U : 2U;
        var stages = new List<ulong>(3)
        {
            device.Control.Enqueue(XhciTrb.Of(XhciTrb.SETUP, setup.Encode(), 8, XhciTrb.IMMEDIATE_DATA | transferType << 16))
        };
        if (setup.Length > 0)
            stages.Add(device.Control.Enqueue(XhciTrb.Of(XhciTrb.DATA, device.Memory.Buffer.Physical, setup.Length, setup.IsIn ? XhciTrb.DIRECTION_IN : 0)));
        var status = device.Control.Enqueue(XhciTrb.Of(XhciTrb.STATUS,
            flags: XhciTrb.INTERRUPT_ON_COMPLETION | (setup.Length > 0 && setup.IsIn ? 0 : XhciTrb.DIRECTION_IN)));
        stages.Add(status);
        m_controlEvents.Remove(slot);
        Ring(slot, 1);
        var deadline = Clock.MonotonicAfter(TIMEOUT_SECONDS);
        var what = $"request {setup.Request:x2} of slot {slot}";
        for (; ; )
        {
            if (TakeControlEvent(slot, stages, out var trbEvent))
            {
                if (trbEvent.CompletionCode is not (XhciTrb.SUCCESS or XhciTrb.SHORT_PACKET))
                {
                    // A STALL, a babble or a transaction error halts the endpoint: it takes requests again before this one fails.
                    var recovered = RecoverEndpoint(slot, 1);
                    throw new IOException($"The {what} completed with code {trbEvent.CompletionCode}" +
                        (recovered ? "" : "; its control endpoint could not be reset") + ".");
                }
                if (trbEvent.Parameter == status)
                    return setup.IsIn ? buffer[..setup.Length].ToArray() : [];
                continue; // an earlier stage's event; the status stage's follows
            }
            try
            {
                WaitForEvents(deadline, what);
            }
            catch (IOException)
            {
                RecoverEndpoint(slot, 1); // the request's stages skipped, so that the next one starts on its own
                throw;
            }
        }
    }

    /// <inheritdoc />
    public int ConfigureInterruptIn(int slot, UsbEndpoint endpoint)
    {
        var device = DeviceAt(slot);
        var index = XhciContexts.Index(endpoint);
        var segment = TakeBlock();
        var buffer = TakeBlock();
        var ring = new XhciRing(segment, MmioRegion.Barrier);
        var input = device.Memory.Input.Bytes;
        var entries = Math.Max(index, device.ContextEntries);
        XhciContexts.WriteControl(input, m_contextBytes, 0, 1U | 1U << index);
        XhciContexts.WriteSlot(input, m_contextBytes, device.Location, entries, device.Hub);
        var burst = device.Location.Speed == XhciContexts.HIGH_SPEED ? endpoint.MaxPacketSize >> 11 & 3 : 0;
        XhciContexts.WriteEndpoint(input, m_contextBytes, index, XhciContexts.INTERRUPT_IN, endpoint.PacketBytes,
            XhciContexts.Interval(device.Location.Speed, endpoint.Interval), burst, ring.Base, endpoint.PacketBytes);
        try
        {
            Command(XhciTrb.Of(XhciTrb.CONFIGURE_ENDPOINT, device.Memory.Input.Physical, flags: (uint)slot << 24));
        }
        catch (IOException)
        {
            m_blocks.Push(segment);
            m_blocks.Push(buffer);
            throw;
        }
        device.ContextEntries = entries;
        device.Rings[index] = ring;
        device.Segments[index] = segment;
        device.Buffers[index] = buffer;
        return index;
    }

    /// <inheritdoc />
    public (int In, int Out) ConfigureBulk(int slot, UsbEndpoint input, UsbEndpoint output)
    {
        var device = DeviceAt(slot);
        var (inIndex, outIndex) = (XhciContexts.Index(input), XhciContexts.Index(output));
        IDmaBuffer[] blocks = [TakeBlock(), TakeBlock(), TakeBlock(), TakeBlock()]; // two rings and two buffers
        var inRing = new XhciRing(blocks[0], MmioRegion.Barrier);
        var outRing = new XhciRing(blocks[1], MmioRegion.Barrier);
        var context = device.Memory.Input.Bytes;
        var entries = Math.Max(Math.Max(inIndex, outIndex), device.ContextEntries);
        XhciContexts.WriteControl(context, m_contextBytes, 0, 1U | 1U << inIndex | 1U << outIndex);
        XhciContexts.WriteSlot(context, m_contextBytes, device.Location, entries, device.Hub);
        XhciContexts.WriteEndpoint(context, m_contextBytes, inIndex, XhciContexts.BULK_IN, input.PacketBytes, 0, 0, inRing.Base, BULK_AVERAGE_TRB);
        XhciContexts.WriteEndpoint(context, m_contextBytes, outIndex, XhciContexts.BULK_OUT, output.PacketBytes, 0, 0, outRing.Base, BULK_AVERAGE_TRB);
        try
        {
            Command(XhciTrb.Of(XhciTrb.CONFIGURE_ENDPOINT, device.Memory.Input.Physical, flags: (uint)slot << 24));
        }
        catch (IOException)
        {
            foreach (var block in blocks)
                m_blocks.Push(block);
            throw;
        }
        device.ContextEntries = entries;
        (device.Rings[inIndex], device.Segments[inIndex], device.Buffers[inIndex], device.PacketBytes[inIndex]) =
            (inRing, blocks[0], blocks[2], input.PacketBytes);
        (device.Rings[outIndex], device.Segments[outIndex], device.Buffers[outIndex], device.PacketBytes[outIndex]) =
            (outRing, blocks[1], blocks[3], output.PacketBytes);
        return (inIndex, outIndex);
    }

    /// <inheritdoc />
    public BulkResult Bulk(int slot, int index, ReadOnlySpan<(ulong Address, int Length)> segments, out int transferred)
    {
        var device = DeviceAt(slot);
        var ring = device.Rings[index] ?? throw new InvalidOperationException($"Endpoint {index} of slot {slot} is not configured.");
        // One Transfer Descriptor: the segments cut at 64 KiB boundaries, chained, each interrupting a short packet, the
        // last on its completion; TD Size counts the packets left after each TRB (xHCI 1.2 §4.11.2.4).
        var pieces = new List<(ulong Address, int Length)>();
        long total = 0;
        foreach (var (address, length) in segments)
        {
            for (var at = address; at < address + (ulong)length;)
            {
                var end = Math.Min(address + (ulong)length, (at & ~(TRB_BOUNDARY - 1)) + TRB_BOUNDARY);
                pieces.Add((at, (int)(end - at)));
                at = end;
            }
            total += length;
        }
        transferred = 0;
        if (pieces.Count == 0)
            return BulkResult.Done;
        if (pieces.Count >= ring.Capacity)
            throw new ArgumentException($"A bulk transfer of {pieces.Count} pieces; the ring holds {ring.Capacity - 1}.", nameof(segments));
        var packet = Math.Max(1, device.PacketBytes[index]);
        var trbs = new ulong[pieces.Count];
        var before = new int[pieces.Count];
        var left = total;
        for (var i = 0; i < pieces.Count; i++)
        {
            before[i] = (int)(total - left);
            left -= pieces[i].Length;
            var tdSize = (uint)Math.Min(31, (left + packet - 1) / packet);
            var flags = XhciTrb.INTERRUPT_SHORT | (i < pieces.Count - 1 ? XhciTrb.CHAIN : XhciTrb.INTERRUPT_ON_COMPLETION);
            trbs[i] = ring.Enqueue(XhciTrb.Of(XhciTrb.NORMAL, pieces[i].Address, (uint)pieces[i].Length | tdSize << 17, flags));
        }
        Ring(slot, index);
        var deadline = Clock.MonotonicAfter(BULK_TIMEOUT_SECONDS);
        for (; ; )
        {
            if (TakeTransfer(slot, index, out var trb))
            {
                var at = Array.IndexOf(trbs, trb.Parameter);
                if (at < 0)
                    continue; // an earlier transfer's event for its last TRB, after its short packet ended it
                transferred = before[at] + pieces[at].Length - Math.Min(trb.Residual, pieces[at].Length);
                if (trb.CompletionCode == XhciTrb.SUCCESS && at == trbs.Length - 1 || trb.CompletionCode == XhciTrb.SHORT_PACKET)
                    return BulkResult.Done; // a short packet ends the descriptor, whose TRBs after it the controller skips
                if (trb.CompletionCode == XhciTrb.SUCCESS)
                    continue;
                var stalled = trb.CompletionCode == XhciTrb.STALL;
                RecoverEndpoint(slot, index); // halted on the controller's side: reset, the descriptor's rest skipped
                return stalled ? BulkResult.Stalled : BulkResult.Failed;
            }
            try
            {
                WaitForEvents(deadline, $"a bulk transfer on endpoint {index} of slot {slot}");
            }
            catch (IOException)
            {
                RecoverEndpoint(slot, index); // the descriptor stopped and skipped, so that the next one starts on its own
                return BulkResult.Failed;
            }
        }
    }

    /// <inheritdoc />
    public Span<byte> BulkBuffer(int slot, int index, out ulong physical)
    {
        var buffer = DeviceAt(slot).Buffers[index] ?? throw new InvalidOperationException($"Endpoint {index} of slot {slot} is not configured.");
        physical = buffer.Physical;
        return buffer.Bytes;
    }

    /// <inheritdoc />
    public void ConfigureHub(int slot, UsbHubDescriptor hub)
    {
        var device = DeviceAt(slot);
        var input = device.Memory.Input.Bytes;
        XhciContexts.WriteControl(input, m_contextBytes, 0, 1);
        XhciContexts.WriteSlot(input, m_contextBytes, device.Location, device.ContextEntries, hub);
        Command(XhciTrb.Of(XhciTrb.CONFIGURE_ENDPOINT, device.Memory.Input.Physical, flags: (uint)slot << 24));
        device.Hub = hub;
    }

    /// <inheritdoc />
    public void Receive(int slot, int index, int length)
    {
        var device = DeviceAt(slot);
        var ring = device.Rings[index] ?? throw new InvalidOperationException($"Endpoint {index} of slot {slot} is not configured.");
        if (length is <= 0 or > BLOCK_BYTES)
            throw new ArgumentOutOfRangeException(nameof(length), $"A transfer of {length} bytes.");
        ring.Enqueue(XhciTrb.Of(XhciTrb.NORMAL, device.Buffers[index]!.Physical, (uint)length,
            XhciTrb.INTERRUPT_ON_COMPLETION | XhciTrb.INTERRUPT_SHORT));
        Ring(slot, index);
    }

    /// <inheritdoc />
    public ReadOnlySpan<byte> Received(int slot, int index) =>
        (DeviceAt(slot).Buffers[index] ?? throw new InvalidOperationException($"Endpoint {index} of slot {slot} is not configured.")).Bytes;

    /// <inheritdoc />
    public bool RecoverEndpoint(int slot, int index)
    {
        if (slot is <= 0 || slot > SLOTS || m_devices[slot] is not { } device || device.Rings[index] is not { } ring)
            return false;
        try
        {
            switch (XhciContexts.EndpointState(device.Memory.Output.Bytes, m_contextBytes, index))
            {
                case XhciContexts.HALTED:
                    Command(XhciTrb.OfEndpoint(XhciTrb.RESET_ENDPOINT, slot, index));
                    break;
                case XhciContexts.RUNNING:
                    Command(XhciTrb.OfEndpoint(XhciTrb.STOP_ENDPOINT, slot, index));
                    break;
            }
            Command(XhciTrb.OfEndpoint(XhciTrb.SET_TR_DEQUEUE, slot, index, ring.Next));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Disable(int slot)
    {
        var device = DeviceAt(slot);
        m_devices[slot] = null;
        Forget(slot);
        Command(XhciTrb.Of(XhciTrb.DISABLE_SLOT, flags: (uint)slot << 24)); // a failure keeps the device's memory out of the pools
        Forget(slot); // the events the controller wrote before the command completed
        BinaryPrimitives.WriteUInt64LittleEndian(m_contexts!.Bytes[(slot * 8)..], 0);
        m_slotMemory.Push(device.Memory);
        for (var index = 0; index < device.Segments.Length; index++)
        {
            if (device.Segments[index] is { } segment)
            {
                m_blocks.Push(segment);
                m_blocks.Push(device.Buffers[index]!);
            }
        }
    }

    /// <inheritdoc />
    public void Delay(int milliseconds) => Thread.Sleep(milliseconds);

    /// <summary>
    /// Serves the interrupter once its event was set: the pending interrupt cleared, every event taken and sorted, the
    /// INTx line unmasked. The transfers and the port changes then wait for <see cref="TryTakeTransfer"/> and
    /// <see cref="TryTakePortChange"/>.
    /// </summary>
    public void Service() => Drain();

    /// <summary>
    /// The next Transfer Event of an endpoint other than a default one, in the controller's order; a device let go
    /// leaves none behind.
    /// </summary>
    /// <param name="transfer">The event.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryTakeTransfer(out XhciTrb transfer)
    {
        if (m_transfers.Count == 0)
        {
            transfer = default;
            return false;
        }
        transfer = m_transfers[0];
        m_transfers.RemoveAt(0);
        return true;
    }

    /// <summary>
    /// The next root port a Port Status Change Event named.
    /// </summary>
    /// <param name="port">The port, from 1.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryTakePortChange(out int port)
    {
        if (m_portChanges.Count == 0)
        {
            port = 0;
            return false;
        }
        port = m_portChanges[0];
        m_portChanges.RemoveAt(0);
        return true;
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
        m_rootPorts.AddRange(m_ports.Select(port => new UsbRootPort(port.Number, port.Major, port.Minor)));
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
    // endpoints' Transfer Events by their slot, the other endpoints' and the port changes in order for the core. The
    // Transfer Events a Stop Endpoint leaves are the driver's own doing and dropped. Whether there was any.
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
                case XhciTrb.TRANSFER_EVENT when trb.CompletionCode is XhciTrb.STOPPED or XhciTrb.STOPPED_LENGTH_INVALID:
                    break;
                case XhciTrb.TRANSFER_EVENT when trb.Endpoint == 1:
                    if (!m_controlEvents.TryGetValue(trb.Slot, out var list))
                        m_controlEvents[trb.Slot] = list = [];
                    list.Add(trb);
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

    // The first event of a slot's control endpoint that names one of a request's stages; the others, a request's that
    // ended before, dropped.
    private bool TakeControlEvent(int slot, List<ulong> stages, out XhciTrb trb)
    {
        trb = default;
        if (!m_controlEvents.TryGetValue(slot, out var list))
            return false;
        while (list.Count > 0)
        {
            var first = list[0];
            list.RemoveAt(0);
            if (stages.Contains(first.Parameter))
            {
                trb = first;
                return true;
            }
        }
        return false;
    }

    // The first Transfer Event of an endpoint the core does not serve (a bulk one, E3), taken out of the order the core reads.
    private bool TakeTransfer(int slot, int index, out XhciTrb trb)
    {
        var at = m_transfers.FindIndex(item => item.Slot == slot && item.Endpoint == index);
        if (at < 0)
        {
            trb = default;
            return false;
        }
        trb = m_transfers[at];
        m_transfers.RemoveAt(at);
        return true;
    }

    // A device that goes leaves no event behind for its slot's next device.
    private void Forget(int slot)
    {
        m_transfers.RemoveAll(trb => trb.Slot == slot);
        m_controlEvents.Remove(slot);
    }

    private XhciDevice DeviceAt(int slot) => slot is > 0 and <= SLOTS && m_devices[slot] is { } device ? device
        : throw new IOException($"Slot {slot} holds no device.");

    private XhciPort RootPort(int number) => m_ports.FirstOrDefault(port => port.Number == number)
        ?? throw new IOException($"The controller has no root port {number}.");

    // A device's contexts, control ring and buffer: a device's that went, zeroed, or new from the arena.
    private XhciSlotMemory TakeSlotMemory()
    {
        if (m_slotMemory.TryPop(out var memory))
        {
            memory.Input.Bytes.Clear();
            memory.Output.Bytes.Clear();
            memory.ControlRing.Bytes.Clear();
            memory.Buffer.Bytes.Clear();
            return memory;
        }
        return new XhciSlotMemory(m_arena.Allocate(XhciContexts.INPUT_CONTEXTS * m_contextBytes),
            m_arena.Allocate(XhciContexts.DEVICE_CONTEXTS * m_contextBytes), m_arena.Allocate(BLOCK_BYTES), m_arena.Allocate(CONTROL_BYTES));
    }

    // A block for an interrupt endpoint's ring or buffer: a device's that went, zeroed, or new from the arena.
    private IDmaBuffer TakeBlock()
    {
        if (m_blocks.TryPop(out var block))
        {
            block.Bytes.Clear();
            return block;
        }
        return m_arena.Allocate(BLOCK_BYTES);
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

    /// <inheritdoc />
    public IReadOnlyList<UsbRootPort> RootPorts => m_rootPorts;

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
/// A device's contexts, control ring and buffer, which the next device takes once it goes.
/// </summary>
/// <param name="Input">Its Input Context.</param>
/// <param name="Output">Its Output Context, which the Device Context Base Address Array names.</param>
/// <param name="ControlRing">Its default control endpoint's ring.</param>
/// <param name="Buffer">Its control transfers' data.</param>
internal sealed record XhciSlotMemory(IDmaBuffer Input, IDmaBuffer Output, IDmaBuffer ControlRing, IDmaBuffer Buffer);

/// <summary>
/// A device the controller addressed: its slot and where it sits, its contexts and its rings.
/// </summary>
internal sealed class XhciDevice
{
    #region Constructors

    internal XhciDevice(int slot, UsbLocation location, XhciSlotMemory memory, int maxPacket0)
    {
        Slot = slot;
        Location = location;
        Memory = memory;
        Control = new XhciRing(memory.ControlRing, MmioRegion.Barrier);
        MaxPacket0 = maxPacket0;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The device's slot.
    /// </summary>
    public int Slot { get; }

    /// <summary>
    /// Where it sits, its speed among it.
    /// </summary>
    public UsbLocation Location { get; }

    /// <summary>
    /// Its contexts, control ring and buffer.
    /// </summary>
    public XhciSlotMemory Memory { get; }

    /// <summary>
    /// Its default control endpoint's ring.
    /// </summary>
    public XhciRing Control { get; }

    /// <summary>
    /// The default control endpoint's packet bytes.
    /// </summary>
    public int MaxPacket0 { get; set; }

    /// <summary>
    /// The index of the last valid Endpoint Context.
    /// </summary>
    public int ContextEntries { get; set; } = 1;

    /// <summary>
    /// Its hub descriptor once the controller was told it is a hub.
    /// </summary>
    public UsbHubDescriptor? Hub { get; set; }

    /// <summary>
    /// Its interrupt endpoints' rings by Device Context Index.
    /// </summary>
    public XhciRing?[] Rings { get; } = new XhciRing?[XhciContexts.DEVICE_CONTEXTS];

    /// <summary>
    /// The blocks those rings live in.
    /// </summary>
    public IDmaBuffer?[] Segments { get; } = new IDmaBuffer?[XhciContexts.DEVICE_CONTEXTS];

    /// <summary>
    /// Their transfers' buffers.
    /// </summary>
    public IDmaBuffer?[] Buffers { get; } = new IDmaBuffer?[XhciContexts.DEVICE_CONTEXTS];

    /// <summary>
    /// The packet bytes of each bulk endpoint, by its Device Context Index (E3).
    /// </summary>
    public int[] PacketBytes { get; } = new int[XhciContexts.DEVICE_CONTEXTS];

    #endregion
}
