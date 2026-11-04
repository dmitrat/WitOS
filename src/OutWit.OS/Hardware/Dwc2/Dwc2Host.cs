using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OutWit.OS.Hardware.Usb;

namespace OutWit.OS.Hardware.Dwc2;

/// <summary>
/// What a board says of its DWC2 controller (plan step H8.3), from its device tree node: the offset of the device's view of
/// memory (the node's dma-ranges: 0xC0000000 on the Raspberry Pi 4's legacy bus) and the AHB burst its bus takes.
/// </summary>
/// <param name="DmaOffset">What the device's addresses are less the physical ones.</param>
/// <param name="AhbBurst">GAHBCFG's HBstLen, the burst of the core's DMA: <see cref="Dwc2Host.BURST_INCR4"/> by default.</param>
public sealed record Dwc2Options(long DmaOffset = 0, int AhbBurst = Dwc2Host.BURST_INCR4);

/// <summary>
/// A Synopsys DesignWare USB 2.0 Hi-Speed On-The-Go controller (DWC2) as a USB host (plan step H8.3): the Raspberry Pi 4's
/// USB-C port, the PocketTerm35's keyboard behind it, and QEMU's dwc2-usb of the raspi boards.
/// <list type="bullet">
/// <item>The core is identified (GSNPSID), must have internal DMA, is reset, forced into host mode and given its FIFOs as
/// Linux gives the BCM2835 family's (774 words to receive, 256 and 512 to transmit), its one root port powered.</item>
/// <item>Transfers are buffer DMA through the host channels: a channel is programmed with the device, the endpoint, the
/// size, the packets and the data PID and enabled, and its halt reports how it ended. Channel 0 carries the synchronous
/// transfers one at a time: the three stages of a control transfer and a bulk transfer in pieces within the core's
/// counters. The other channels carry the interrupt IN endpoints' transfers.</item>
/// <item>An interrupt IN transfer the device answers with NAK is asked again by software after the endpoint's interval,
/// never at the next frame, so that an idle keyboard costs an interrupt an interval rather than one a frame; a
/// completed, stalled or failed one is a completion the USB core takes as xHCI's (<see cref="TryTakeTransfer"/>).</item>
/// <item>Data toggles are the host's: each successful packet flips the endpoint's, SET_CONFIGURATION and a cleared halt
/// return it to DATA0, since QEMU's model leaves HCTSIZ's PID as it was written.</item>
/// <item>A device is addressed by SET_ADDRESS to the lowest free address, which is its slot.</item>
/// <item>A full- or low-speed device behind a high-speed hub would need split transactions, which this host does not
/// serve: such a device is refused when it is addressed.</item>
/// </list>
/// Every register is a 32-bit word, and a buffer the core reaches lies below 4 GiB of the device's view.
/// </summary>
public sealed class Dwc2Host : IUsbHost
{
    #region Constants

    /// <summary>
    /// GAHBCFG's HBstLen for INCR4 bursts, Linux's default.
    /// </summary>
    public const int BURST_INCR4 = 3;

    /// <summary>
    /// The bytes of the control buffer, the largest descriptor the USB core reads.
    /// </summary>
    private const int CONTROL_BYTES = 1024;

    /// <summary>
    /// An interrupt endpoint's buffer: a report of up to <see cref="UsbBus.REPORT_BUFFER_BYTES"/> rounded up to its packets.
    /// </summary>
    private const int INTERRUPT_BYTES = 2048;

    /// <summary>
    /// A bulk endpoint's page, for its transport's small transfers.
    /// </summary>
    private const int BULK_BYTES = 4096;

    /// <summary>
    /// The channel of the synchronous transfers.
    /// </summary>
    private const int SYNCHRONOUS = 0;

    /// <summary>
    /// The most channels a core has.
    /// </summary>
    private const int MAX_CHANNELS = 16;

    /// <summary>
    /// The most bytes and packets a channel moves at once, whatever the core's counters hold: Linux's for the BCM2835
    /// family.
    /// </summary>
    private const int TRANSFER_LIMIT = 65535;

    private const int PACKET_LIMIT = 511;

    // The FIFOs in 32-bit words, Linux's for the BCM2835 family: receive, non-periodic transmit, periodic transmit.
    private const int RECEIVE_FIFO = 774;
    private const int NONPERIODIC_FIFO = 256;
    private const int PERIODIC_FIFO = 512;

    /// <summary>
    /// A root port's reset (USB 2.0 §7.1.7.5, TDRSTR).
    /// </summary>
    private const int RESET_MILLISECONDS = 50;

    /// <summary>
    /// The reset's recovery before the device answers (USB 2.0 §7.1.7.3, TRSTRCY).
    /// </summary>
    private const int RECOVERY_MILLISECONDS = 10;

    /// <summary>
    /// SET_ADDRESS's recovery before the device answers at its address (USB 2.0 §9.2.6.3).
    /// </summary>
    private const int ADDRESS_MILLISECONDS = 2;

    /// <summary>
    /// The time a newly powered port is given before its connection is looked at.
    /// </summary>
    private const int POWER_MILLISECONDS = 100;

    /// <summary>
    /// The longest a port takes to enable after its reset.
    /// </summary>
    private const int ENABLE_MILLISECONDS = 500;

    private static readonly TimeSpan CONTROL_TIME = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BULK_TIME = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CORE_TIME = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HALT_TIME = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The least time between two asks of an interrupt endpoint.
    /// </summary>
    private static readonly TimeSpan LEAST_INTERVAL = TimeSpan.FromMilliseconds(1);

    // The core's global registers.
    private const int GAHBCFG = 0x008;
    private const int GUSBCFG = 0x00C;
    private const int GRSTCTL = 0x010;
    private const int GINTSTS = 0x014;
    private const int GINTMSK = 0x018;
    private const int GRXFSIZ = 0x024;
    private const int GNPTXFSIZ = 0x028;
    private const int GSNPSID = 0x040;
    private const int GHWCFG2 = 0x048;
    private const int GHWCFG3 = 0x04C;
    private const int HPTXFSIZ = 0x100;
    private const int PCGCTL = 0xE00;

    // The host's registers, then each channel's eight words from 0x500.
    private const int HCFG = 0x400;
    private const int HFNUM = 0x408;
    private const int HAINT = 0x414;
    private const int HAINTMSK = 0x418;
    private const int HPRT0 = 0x440;
    private const int CHANNELS = 0x500;
    private const int CHANNEL_BYTES = 0x20;
    private const int HCCHAR = 0x00;
    private const int HCSPLT = 0x04;
    private const int HCINT = 0x08;
    private const int HCINTMSK = 0x0C;
    private const int HCTSIZ = 0x10;
    private const int HCDMA = 0x14;

    // GAHBCFG, GUSBCFG, GRSTCTL.
    private const uint GLOBAL_INTERRUPTS = 1;
    private const uint DMA_ENABLE = 1 << 5;
    private const uint FORCE_HOST = 1 << 29;
    private const uint FORCE_DEVICE = 1 << 30;
    private const uint CORE_RESET = 1;
    private const uint RECEIVE_FLUSH = 1 << 4;
    private const uint TRANSMIT_FLUSH = 1 << 5;
    private const uint ALL_TRANSMIT_FIFOS = 0x10 << 6;
    private const uint AHB_IDLE = 1U << 31;

    // GINTSTS and GINTMSK.
    private const uint HOST_MODE = 1;
    private const uint PORT_INTERRUPT = 1 << 24;
    private const uint CHANNEL_INTERRUPT = 1 << 25;
    private const uint DISCONNECTED = 1 << 29;

    // GSNPSID: "OT2" and "OT3", the cores Linux's dwc2 serves.
    private const uint CORE_ID_MASK = 0xFFFF_F000;
    private const uint CORE_OT2 = 0x4F54_2000;
    private const uint CORE_OT3 = 0x4F54_3000;
    private const uint INTERNAL_DMA = 2;

    // HCFG's FS/LS PHY clock: 30/60 MHz, for a UTMI+ high-speed PHY.
    private const uint PHY_CLOCK_MASK = 3;

    // HPRT0: its changes and its enable are written 1 to clear, so a write of the port carries none of them but those it clears.
    private const uint PORT_CONNECTED = 1;
    private const uint PORT_CONNECT_CHANGE = 1 << 1;
    private const uint PORT_ENABLED = 1 << 2;
    private const uint PORT_ENABLE_CHANGE = 1 << 3;
    private const uint PORT_OVERCURRENT_CHANGE = 1 << 5;
    private const uint PORT_RESET = 1 << 8;
    private const uint PORT_POWER = 1 << 12;
    private const int PORT_SPEED_SHIFT = 17;
    private const uint PORT_CHANGES = PORT_CONNECT_CHANGE | PORT_ENABLE_CHANGE | PORT_OVERCURRENT_CHANGE;
    private const uint PORT_WRITE_CLEARS = PORT_CHANGES | PORT_ENABLED;

    // HCCHAR.
    private const int EPNUM_SHIFT = 11;
    private const uint DIRECTION_IN = 1 << 15;
    private const uint LOW_SPEED_DEVICE = 1 << 17;
    private const int TYPE_SHIFT = 18;
    private const uint ONE_TRANSACTION = 1 << 20;
    private const int ADDRESS_SHIFT = 22;
    private const uint ODD_FRAME = 1 << 29;
    private const uint CHANNEL_DISABLE = 1 << 30;
    private const uint CHANNEL_ENABLE = 1U << 31;

    // HCTSIZ.
    private const uint SIZE_MASK = 0x7FFFF;
    private const int PACKETS_SHIFT = 19;
    private const int PID_SHIFT = 29;
    private const int PID_DATA0 = 0;
    private const int PID_DATA1 = 2;
    private const int PID_SETUP = 3;

    // HCINT and HCINTMSK.
    private const uint TRANSFER_COMPLETE = 1;
    private const uint HALTED = 1 << 1;
    private const uint AHB_ERROR = 1 << 2;
    private const uint STALLED = 1 << 3;
    private const uint NAKED = 1 << 4;
    private const uint BABBLE = 1 << 8;

    // The endpoint types HCCHAR names.
    private const int CONTROL = 0;
    private const int BULK = 2;
    private const int INTERRUPT = 3;

    #endregion

    #region Fields

    private readonly IDeviceRegisters m_registers;
    private readonly IInterruptWait m_wait;
    private readonly Func<int, IDmaBuffer> m_allocate;
    private readonly Dwc2Options m_options;
    private readonly TimeProvider m_time;
    private readonly Action<TimeSpan> m_sleep;
    private readonly UsbRootPort[] m_rootPorts = [new UsbRootPort(1, 2, 0)];
    private readonly Dwc2Device?[] m_devices = new Dwc2Device?[128];
    private readonly List<Dwc2Endpoint> m_periodic = [];
    private readonly Dwc2Endpoint?[] m_channels = new Dwc2Endpoint?[MAX_CHANNELS];
    private readonly List<(int Slot, int Index, uint Code, int Residual)> m_transfers = [];
    private readonly Stack<IDmaBuffer> m_interruptBuffers = [];
    private readonly Stack<IDmaBuffer> m_bulkBuffers = [];
    private IDmaBuffer? m_setup;
    private IDmaBuffer? m_control;
    private bool m_portChanged;
    private bool m_synchronousHalted;
    private uint m_synchronousInterrupt;

    #endregion

    #region Constructors

    /// <summary>
    /// Makes the host of a controller; <see cref="Start"/> starts it.
    /// </summary>
    /// <param name="registers">The controller's registers.</param>
    /// <param name="wait">How the host waits for the controller.</param>
    /// <param name="allocate">Memory the core reaches, of a size asked, aligned to 64 bytes.</param>
    /// <param name="options">What the board says of the controller.</param>
    /// <param name="time">The clock of the timeouts and the endpoints' intervals; the system's by default.</param>
    /// <param name="sleep">How the host waits a time out; <see cref="Thread.Sleep(TimeSpan)"/> by default.</param>
    public Dwc2Host(IDeviceRegisters registers, IInterruptWait wait, Func<int, IDmaBuffer> allocate, Dwc2Options? options = null,
        TimeProvider? time = null, Action<TimeSpan>? sleep = null)
    {
        m_registers = registers;
        m_wait = wait;
        m_allocate = allocate;
        m_options = options ?? new Dwc2Options();
        m_time = time ?? TimeProvider.System;
        m_sleep = sleep ?? Thread.Sleep;
        if (m_options.AhbBurst is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(options), $"An AHB burst of {m_options.AhbBurst}.");
    }

    #endregion

    #region Functions

    /// <summary>
    /// Identifies the core, resets it into host mode, gives it its FIFOs and interrupts and powers its root port.
    /// </summary>
    /// <exception cref="IOException">The core is no DWC2 this host serves, or it did not reset.</exception>
    public void Start()
    {
        var id = m_registers.Read32(GSNPSID);
        if ((id & CORE_ID_MASK) is not (CORE_OT2 or CORE_OT3))
            throw new IOException($"GSNPSID reads {id:x8}, which names no DWC2 core.");
        Version = $"{id >> 12 & 0xF}.{id >> 8 & 0xF}{id >> 4 & 0xF}{(char)('a' + Math.Max(0, (int)(id & 0xF) - 0xA))}";
        var hardware2 = m_registers.Read32(GHWCFG2);
        if ((hardware2 >> 3 & 3) != INTERNAL_DMA)
            throw new IOException($"GHWCFG2 reads {hardware2:x8}: the core has no internal DMA.");
        Channels = Math.Min((int)(hardware2 >> 14 & 0xF) + 1, MAX_CHANNELS);
        if (Channels < 2)
            throw new IOException("The core has one host channel.");
        var hardware3 = m_registers.Read32(GHWCFG3);
        MaxTransfer = Math.Min((1 << (int)(hardware3 & 0xF) + 11) - 1, TRANSFER_LIMIT);
        MaxPackets = Math.Min((1 << (int)(hardware3 >> 4 & 7) + 4) - 1, PACKET_LIMIT);
        FifoWords = (int)(hardware3 >> 16);

        m_registers.Write32(GAHBCFG, 0);
        ResetCore();
        m_registers.Write32(GUSBCFG, m_registers.Read32(GUSBCFG) & ~FORCE_DEVICE | FORCE_HOST);
        Until(() => (m_registers.Read32(GINTSTS) & HOST_MODE) != 0, "the core to enter host mode");
        m_registers.Write32(PCGCTL, 0);
        m_registers.Write32(HCFG, m_registers.Read32(HCFG) & ~PHY_CLOCK_MASK);
        if (FifoWords >= RECEIVE_FIFO + NONPERIODIC_FIFO + PERIODIC_FIFO)
        {
            m_registers.Write32(GRXFSIZ, RECEIVE_FIFO);
            m_registers.Write32(GNPTXFSIZ, NONPERIODIC_FIFO << 16 | RECEIVE_FIFO);
            m_registers.Write32(HPTXFSIZ, PERIODIC_FIFO << 16 | RECEIVE_FIFO + NONPERIODIC_FIFO);
        }
        m_registers.Write32(GRSTCTL, TRANSMIT_FLUSH | ALL_TRANSMIT_FIFOS);
        Until(() => (m_registers.Read32(GRSTCTL) & TRANSMIT_FLUSH) == 0, "the transmit FIFOs' flush");
        m_registers.Write32(GRSTCTL, RECEIVE_FLUSH);
        Until(() => (m_registers.Read32(GRSTCTL) & RECEIVE_FLUSH) == 0, "the receive FIFO's flush");
        for (var channel = 0; channel < Channels; channel++)
        {
            Halt(channel);
            m_registers.Write32(Channel(channel, HCINTMSK), HALTED | AHB_ERROR);
            m_registers.Write32(Channel(channel, HCINT), uint.MaxValue);
        }
        m_registers.Write32(HAINTMSK, (1U << Channels) - 1);
        m_registers.Write32(GINTSTS, uint.MaxValue);
        m_registers.Write32(GINTMSK, PORT_INTERRUPT | CHANNEL_INTERRUPT | DISCONNECTED);
        m_registers.Write32(GAHBCFG, DMA_ENABLE | (uint)m_options.AhbBurst << 1 | GLOBAL_INTERRUPTS);

        m_setup = Allocate(64);
        m_control = Allocate(CONTROL_BYTES);
        var port = m_registers.Read32(HPRT0);
        if ((port & PORT_POWER) == 0)
        {
            m_registers.Write32(HPRT0, port & ~PORT_WRITE_CLEARS | PORT_POWER);
            Delay(POWER_MILLISECONDS);
        }
    }

    /// <summary>
    /// Stops the controller: its interrupts off, every channel halted, its port's power off, the core reset.
    /// </summary>
    public void Stop()
    {
        m_registers.Write32(GAHBCFG, 0);
        for (var channel = 0; channel < Channels; channel++)
        {
            Halt(channel);
            m_channels[channel] = null;
        }
        m_registers.Write32(HPRT0, m_registers.Read32(HPRT0) & ~(PORT_WRITE_CLEARS | PORT_POWER));
        try
        {
            ResetCore();
        }
        catch (IOException)
        {
            // Stopped all the same: nothing runs.
        }
    }

    /// <summary>
    /// Serves the controller once it may have interrupted, or an endpoint's interval passed: a root port's change noted
    /// for <see cref="TryTakePortChange"/>; each halted channel's transfer ended — the synchronous one's for its waiter,
    /// an interrupt endpoint's a completion for <see cref="TryTakeTransfer"/> or, after a NAK, asked again after its
    /// interval — and every interrupt endpoint whose ask is due given a free channel.
    /// </summary>
    public void Service()
    {
        var status = m_registers.Read32(GINTSTS);
        if ((status & DISCONNECTED) != 0)
        {
            m_registers.Write32(GINTSTS, DISCONNECTED);
            m_portChanged = true;
        }
        if ((status & PORT_INTERRUPT) != 0 && (m_registers.Read32(HPRT0) & PORT_CHANGES) != 0)
            m_portChanged = true;
        if ((status & CHANNEL_INTERRUPT) != 0)
        {
            var channels = m_registers.Read32(HAINT);
            for (var channel = 0; channel < Channels; channel++)
            {
                if ((channels & 1U << channel) != 0)
                    Halted(channel);
            }
        }
        StartDue();
    }

    /// <summary>
    /// The next completion of an interrupt endpoint's transfer, in the order the channels ended; a device let go leaves
    /// none behind.
    /// </summary>
    /// <param name="transfer">The slot, the endpoint's index, the completion code as xHCI names it and the bytes asked that did not come.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryTakeTransfer(out (int Slot, int Index, uint Code, int Residual) transfer)
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
    /// Whether the root port changed since the last call.
    /// </summary>
    /// <param name="port">The port, 1.</param>
    /// <returns>Whether it did.</returns>
    public bool TryTakePortChange(out int port)
    {
        port = m_portChanged ? 1 : 0;
        var changed = m_portChanged;
        m_portChanged = false;
        return changed;
    }

    /// <inheritdoc />
    public UsbRootPortState ReadRootPort(int port)
    {
        CheckPort(port);
        var value = m_registers.Read32(HPRT0);
        return new UsbRootPortState((value & PORT_CONNECTED) != 0, (value & PORT_ENABLED) != 0, Speed(value), (value & PORT_CONNECT_CHANGE) != 0,
            (value & PORT_ENABLE_CHANGE) != 0);
    }

    /// <inheritdoc />
    public void AcknowledgeRootPort(int port)
    {
        CheckPort(port);
        var value = m_registers.Read32(HPRT0);
        if ((value & PORT_CHANGES) != 0)
            m_registers.Write32(HPRT0, value & ~PORT_WRITE_CLEARS | value & PORT_CHANGES);
    }

    /// <inheritdoc />
    public UsbRootPortState ResetRootPort(int port)
    {
        CheckPort(port);
        var value = m_registers.Read32(HPRT0) & ~PORT_WRITE_CLEARS;
        m_registers.Write32(HPRT0, value | PORT_RESET);
        Delay(RESET_MILLISECONDS);
        m_registers.Write32(HPRT0, m_registers.Read32(HPRT0) & ~(PORT_WRITE_CLEARS | PORT_RESET));
        for (var waited = 0; (m_registers.Read32(HPRT0) & (PORT_CONNECTED | PORT_ENABLED)) == PORT_CONNECTED; waited++)
        {
            if (waited >= ENABLE_MILLISECONDS)
                throw new IOException($"The root port did not enable after its reset: HPRT0 {m_registers.Read32(HPRT0):x8}.");
            Delay(1);
        }
        value = m_registers.Read32(HPRT0);
        if ((value & PORT_ENABLE_CHANGE) != 0)
            m_registers.Write32(HPRT0, value & ~PORT_WRITE_CLEARS | PORT_ENABLE_CHANGE); // the reset's own change
        Delay(RECOVERY_MILLISECONDS);
        return ReadRootPort(port);
    }

    /// <inheritdoc />
    public int Address(UsbLocation location)
    {
        if (location.TtSlot != 0)
            throw new IOException($"A {UsbBus.SpeedName(location.Speed)} device behind the high-speed hub of slot {location.TtSlot} needs split " +
                "transactions, which this host does not serve.");
        var address = Array.FindIndex(m_devices, 1, item => item is null);
        if (address < 0)
            throw new IOException("Every address of the bus is taken.");
        // The device answers at the default address until SET_ADDRESS: its first eight bytes name its control packets.
        var device = new Dwc2Device(0, location, location.Speed == XhciContexts.HIGH_SPEED ? 64 : 8);
        var first = Control(device, UsbSetup.GetDescriptor(UsbDescriptors.DEVICE, 0, 8));
        var maxPacket0 = first[7];
        if (maxPacket0 is not (8 or 16 or 32 or 64))
            throw new IOException($"The device names packets of {maxPacket0} bytes on its control endpoint.");
        device.MaxPacket0 = maxPacket0;
        Control(device, UsbSetup.SetAddress(address));
        Delay(ADDRESS_MILLISECONDS);
        device.Address = address;
        m_devices[address] = device;
        return address;
    }

    /// <inheritdoc />
    public int MaxPacket0(int slot) => DeviceAt(slot).MaxPacket0;

    /// <inheritdoc />
    public byte[] Control(int slot, UsbSetup setup)
    {
        var device = DeviceAt(slot);
        var data = Control(device, setup);
        if (setup.Request == UsbSetup.SET_CONFIGURATION && setup.RequestType == 0)
        {
            foreach (var endpoint in device.Endpoints.Values)
                endpoint.Data1 = false; // USB 2.0 §9.4.5: a configuration's endpoints start at DATA0
        }
        else if (setup.Request == UsbSetup.CLEAR_FEATURE && setup.RequestType == 2 && setup.Value == UsbSetup.ENDPOINT_HALT &&
            device.Endpoints.Values.FirstOrDefault(item => item.Endpoint.Address == setup.Index) is { } halted)
        {
            halted.Data1 = false;
        }
        return data;
    }

    /// <inheritdoc />
    public int ConfigureInterruptIn(int slot, UsbEndpoint endpoint)
    {
        var device = DeviceAt(slot);
        if (!endpoint.IsIn || endpoint.Type != UsbEndpoint.INTERRUPT)
            throw new ArgumentException($"Endpoint {endpoint.Address:x2} is no interrupt IN endpoint.", nameof(endpoint));
        var index = XhciContexts.Index(endpoint);
        Release(device, index);
        var interval = device.Location.Speed == XhciContexts.HIGH_SPEED
            ? TimeSpan.FromMicroseconds(125 << Math.Clamp((int)endpoint.Interval, 1, 16) - 1)
            : TimeSpan.FromMilliseconds(Math.Max(1, (int)endpoint.Interval));
        var buffer = m_interruptBuffers.Count > 0 ? m_interruptBuffers.Pop() : Allocate(INTERRUPT_BYTES);
        var configured = new Dwc2Endpoint(device, index, endpoint, buffer, interval < LEAST_INTERVAL ? LEAST_INTERVAL : interval);
        device.Endpoints[index] = configured;
        m_periodic.Add(configured);
        return index;
    }

    /// <inheritdoc />
    public void ConfigureHub(int slot, UsbHubDescriptor hub) => DeviceAt(slot).Hub = hub;

    /// <inheritdoc />
    public (int In, int Out) ConfigureBulk(int slot, UsbEndpoint input, UsbEndpoint output)
    {
        var device = DeviceAt(slot);
        if (input.Type != UsbEndpoint.BULK || !input.IsIn || output.Type != UsbEndpoint.BULK || output.IsIn)
            throw new ArgumentException($"Endpoints {input.Address:x2} and {output.Address:x2} are no bulk IN and OUT pair.");
        var indexes = (In: XhciContexts.Index(input), Out: XhciContexts.Index(output));
        foreach (var (index, endpoint) in new[] { (indexes.In, input), (indexes.Out, output) })
        {
            Release(device, index);
            var buffer = m_bulkBuffers.Count > 0 ? m_bulkBuffers.Pop() : Allocate(BULK_BYTES);
            device.Endpoints[index] = new Dwc2Endpoint(device, index, endpoint, buffer, TimeSpan.Zero);
        }
        return indexes;
    }

    /// <inheritdoc />
    public BulkResult Bulk(int slot, int index, ReadOnlySpan<(ulong Address, int Length)> segments, out int transferred)
    {
        var device = DeviceAt(slot);
        var endpoint = EndpointAt(device, index);
        if (endpoint.Endpoint.Type != UsbEndpoint.BULK)
            throw new InvalidOperationException($"Endpoint {index} of slot {slot} is no bulk endpoint.");
        var packet = Math.Max(1, endpoint.Endpoint.PacketBytes);
        for (var i = 0; i < segments.Length; i++)
        {
            var (address, length) = segments[i];
            if (length <= 0 || (address & 3) != 0 || DeviceAddress(address, length) is null)
                throw new ArgumentException($"A segment of {length} bytes at 0x{address:X}, which the core cannot reach.", nameof(segments));
            if (i < segments.Length - 1 && length % packet != 0)
                throw new ArgumentException($"A segment of {length} bytes before the last, not a multiple of the endpoint's packets of {packet}.", nameof(segments));
            // The core fills an IN transfer in whole packets: a segment ends on one, unless the endpoint's own page holds the rest.
            if (endpoint.Endpoint.IsIn && length % packet != 0 && !Within(endpoint.Buffer, address, (length + packet - 1) / packet * packet))
                throw new ArgumentException($"An IN segment of {length} bytes, which the core fills in whole packets of {packet}.", nameof(segments));
        }
        transferred = 0;
        var piece = Math.Min(MaxTransfer, MaxPackets * packet) / packet * packet;
        foreach (var (address, length) in segments)
        {
            for (var at = 0; at < length;)
            {
                var bytes = Math.Min(length - at, piece);
                var pid = endpoint.Data1 ? PID_DATA1 : PID_DATA0;
                if (!TryTransfer(device, endpoint.Endpoint.Number, endpoint.Endpoint.IsIn, BULK, packet, pid, address + (ulong)at, bytes, BULK_TIME,
                    out var interrupt, out var moved))
                {
                    return BulkResult.Failed;
                }
                if ((interrupt & TRANSFER_COMPLETE) == 0)
                {
                    if ((interrupt & STALLED) == 0)
                        return BulkResult.Failed;
                    endpoint.Data1 = false; // the caller clears the halt at the device, which starts it again at DATA0
                    return BulkResult.Stalled;
                }
                Flip(endpoint, moved, packet);
                transferred += Math.Min(moved, bytes);
                if (moved < bytes)
                    return BulkResult.Done; // a short packet ends the transfer
                at += bytes;
            }
        }
        return BulkResult.Done;
    }

    /// <inheritdoc />
    public Span<byte> BulkBuffer(int slot, int index, out ulong physical)
    {
        var endpoint = EndpointAt(DeviceAt(slot), index);
        physical = endpoint.Buffer.Physical;
        return endpoint.Buffer.Bytes[..BULK_BYTES];
    }

    /// <inheritdoc />
    public void Receive(int slot, int index, int length)
    {
        var endpoint = EndpointAt(DeviceAt(slot), index);
        if (endpoint.Endpoint.Type != UsbEndpoint.INTERRUPT)
            throw new InvalidOperationException($"Endpoint {index} of slot {slot} is no interrupt endpoint.");
        if (length is <= 0 or > UsbBus.REPORT_BUFFER_BYTES)
            throw new ArgumentOutOfRangeException(nameof(length), $"A transfer of {length} bytes.");
        if (endpoint.Asked)
            throw new InvalidOperationException($"A second transfer on endpoint {index} of slot {slot}.");
        endpoint.Asked = true;
        endpoint.Length = length;
        endpoint.Due = m_time.GetTimestamp();
        StartDue();
    }

    /// <inheritdoc />
    public ReadOnlySpan<byte> Received(int slot, int index) => EndpointAt(DeviceAt(slot), index).Buffer.Bytes;

    /// <inheritdoc />
    public bool RecoverEndpoint(int slot, int index)
    {
        if (slot is <= 0 or >= 128 || m_devices[slot] is not { } device || !device.Endpoints.TryGetValue(index, out var endpoint))
            return false;
        Drop(endpoint);
        endpoint.Data1 = false;
        return true;
    }

    /// <inheritdoc />
    public void Disable(int slot)
    {
        var device = DeviceAt(slot);
        foreach (var index in device.Endpoints.Keys.ToList())
            Release(device, index);
        m_transfers.RemoveAll(item => item.Slot == slot);
        m_devices[slot] = null;
    }

    /// <inheritdoc />
    public void Delay(int milliseconds) => m_sleep(TimeSpan.FromMilliseconds(milliseconds));

    // The transfer of a control request at a device's address: its SETUP, its data stage and its status stage in the
    // other direction, each through the synchronous channel.
    private byte[] Control(Dwc2Device device, UsbSetup setup)
    {
        if (!setup.IsIn && setup.Length > 0)
            throw new ArgumentException($"A request {setup.Request:x2} with {setup.Length} bytes to send, which this host does not serve.", nameof(setup));
        if (setup.Length > CONTROL_BYTES)
            throw new ArgumentException($"A request {setup.Request:x2} for {setup.Length} bytes; the control buffer holds {CONTROL_BYTES}.", nameof(setup));
        BinaryPrimitives.WriteUInt64LittleEndian(m_setup!.Bytes, setup.Encode());
        Stage(device, setup, "SETUP", false, PID_SETUP, m_setup.Physical, 8, out _);
        var data = new byte[setup.IsIn ? setup.Length : 0];
        if (setup.Length > 0)
        {
            Stage(device, setup, "data", true, PID_DATA1, m_control!.Physical, setup.Length, out var moved);
            m_control.Bytes[..Math.Min(moved, setup.Length)].CopyTo(data);
        }
        Stage(device, setup, "status", setup.Length == 0, PID_DATA1, m_control!.Physical, 0, out _);
        return data;
    }

    private void Stage(Dwc2Device device, UsbSetup setup, string stage, bool isIn, int pid, ulong address, int bytes, out int moved)
    {
        if (!TryTransfer(device, 0, isIn, CONTROL, device.MaxPacket0, pid, address, bytes, CONTROL_TIME, out var interrupt, out moved))
            throw new IOException($"The request {setup.Request:x2} at address {device.Address} was not answered in its {stage} stage within {CONTROL_TIME.TotalSeconds} s.");
        if ((interrupt & TRANSFER_COMPLETE) != 0)
            return;
        throw new IOException($"The request {setup.Request:x2} at address {device.Address} {Failure(interrupt)} in its {stage} stage.");
    }

    // One transfer through the synchronous channel, waited for: whether the channel halted in time, how (HCINT) and the
    // bytes it moved. An IN transfer asks for its bytes rounded up to its packets, as the core asks.
    private bool TryTransfer(Dwc2Device device, int number, bool isIn, int type, int packet, int pid, ulong address, int bytes, TimeSpan timeout,
        out uint interrupt, out int moved)
    {
        var asked = isIn ? (bytes + packet - 1) / packet * packet : bytes;
        var packets = Math.Max(1, (asked + packet - 1) / packet);
        m_synchronousHalted = false;
        Program(SYNCHRONOUS, device, number, isIn, type, packet, pid, address, asked, packets, 0);
        var deadline = m_time.GetTimestamp() + (long)(timeout.TotalSeconds * m_time.TimestampFrequency);
        while (true)
        {
            Service();
            if (m_synchronousHalted)
                break;
            var left = m_time.GetElapsedTime(m_time.GetTimestamp(), deadline);
            if (left <= TimeSpan.Zero)
            {
                Halt(SYNCHRONOUS);
                interrupt = 0;
                moved = 0;
                return false;
            }
            m_wait.Wait(left);
        }
        interrupt = m_synchronousInterrupt;
        moved = asked - (int)(m_registers.Read32(Channel(SYNCHRONOUS, HCTSIZ)) & SIZE_MASK);
        return true;
    }

    // A channel programmed and enabled: the transfer's size, packets and PID, its buffer, then its characteristics with
    // the enable, after a barrier that puts the buffer's bytes before the core's first look.
    private void Program(int channel, Dwc2Device device, int number, bool isIn, int type, int packet, int pid, ulong address, int bytes, int packets,
        uint oddFrame)
    {
        var dma = DeviceAddress(address, Math.Max(1, bytes)) ?? throw new ArgumentException($"A buffer at 0x{address:X}, which the core cannot reach.");
        m_registers.Write32(Channel(channel, HCINT), uint.MaxValue);
        m_registers.Write32(Channel(channel, HCSPLT), 0);
        m_registers.Write32(Channel(channel, HCTSIZ), (uint)bytes | (uint)packets << PACKETS_SHIFT | (uint)pid << PID_SHIFT);
        m_registers.Write32(Channel(channel, HCDMA), dma);
        m_registers.Fence();
        var characteristics = (uint)packet | (uint)number << EPNUM_SHIFT | (isIn ? DIRECTION_IN : 0) |
            (device.Location.Speed == XhciContexts.LOW_SPEED ? LOW_SPEED_DEVICE : 0) | (uint)type << TYPE_SHIFT | ONE_TRANSACTION |
            (uint)device.Address << ADDRESS_SHIFT | oddFrame;
        m_registers.Write32(Channel(channel, HCCHAR), characteristics | CHANNEL_ENABLE);
    }

    // A halted channel: the synchronous one's end kept for its waiter; an interrupt endpoint's transfer completed, asked
    // again after its interval when the device had nothing (NAK), or ended with the code xHCI would give it.
    private void Halted(int channel)
    {
        var interrupt = m_registers.Read32(Channel(channel, HCINT));
        m_registers.Write32(Channel(channel, HCINT), interrupt);
        if ((interrupt & HALTED) == 0)
            return;
        if (channel == SYNCHRONOUS)
        {
            m_synchronousInterrupt = interrupt;
            m_synchronousHalted = true;
            return;
        }
        var endpoint = m_channels[channel];
        m_channels[channel] = null;
        if (endpoint is null || endpoint.Channel != channel)
            return; // a transfer dropped while its channel halted
        endpoint.Channel = -1;
        if ((interrupt & TRANSFER_COMPLETE) != 0)
        {
            var asked = Rounded(endpoint);
            var moved = asked - (int)(m_registers.Read32(Channel(channel, HCTSIZ)) & SIZE_MASK);
            Flip(endpoint, moved, Math.Max(1, endpoint.Endpoint.PacketBytes));
            var received = Math.Clamp(moved, 0, endpoint.Length);
            endpoint.Asked = false;
            m_transfers.Add((endpoint.Device.Address, endpoint.Index, received < endpoint.Length ? XhciTrb.SHORT_PACKET : XhciTrb.SUCCESS,
                endpoint.Length - received));
            return;
        }
        if ((interrupt & NAKED) != 0)
        {
            endpoint.Due = m_time.GetTimestamp() + (long)(endpoint.Interval.TotalSeconds * m_time.TimestampFrequency);
            return;
        }
        endpoint.Asked = false;
        var code = (interrupt & STALLED) != 0 ? XhciTrb.STALL : (interrupt & BABBLE) != 0 ? XhciTrb.BABBLE : XhciTrb.TRANSACTION_ERROR;
        m_transfers.Add((endpoint.Device.Address, endpoint.Index, code, endpoint.Length));
    }

    // Every interrupt endpoint whose ask is due, in the order they were configured, on a free channel while one is left.
    private void StartDue()
    {
        var now = m_time.GetTimestamp();
        foreach (var endpoint in m_periodic)
        {
            if (!endpoint.Asked || endpoint.Channel >= 0 || endpoint.Due > now)
                continue;
            var channel = Array.FindIndex(m_channels, 1, Channels - 1, item => item is null);
            if (channel < 0)
                return;
            m_channels[channel] = endpoint;
            endpoint.Channel = channel;
            var packet = Math.Max(1, endpoint.Endpoint.PacketBytes);
            var asked = Rounded(endpoint);
            // The next (micro)frame's parity, for the core's periodic schedule.
            var oddFrame = (m_registers.Read32(HFNUM) & 1) == 0 ? ODD_FRAME : 0;
            Program(channel, endpoint.Device, endpoint.Endpoint.Number, true, INTERRUPT, packet, endpoint.Data1 ? PID_DATA1 : PID_DATA0,
                endpoint.Buffer.Physical, asked, asked / packet, oddFrame);
        }
    }

    // An endpoint's channel halted and its transfer dropped, as a halt the core reports leaves it.
    private void Drop(Dwc2Endpoint endpoint)
    {
        if (endpoint.Channel >= 0)
        {
            var channel = endpoint.Channel;
            m_channels[channel] = null;
            endpoint.Channel = -1;
            Halt(channel);
        }
        endpoint.Asked = false;
        m_transfers.RemoveAll(item => item.Slot == endpoint.Device.Address && item.Index == endpoint.Index);
    }

    // An endpoint let go: its transfer dropped, its buffer back to its pool.
    private void Release(Dwc2Device device, int index)
    {
        if (!device.Endpoints.Remove(index, out var endpoint))
            return;
        Drop(endpoint);
        m_periodic.Remove(endpoint);
        (endpoint.Endpoint.Type == UsbEndpoint.BULK ? m_bulkBuffers : m_interruptBuffers).Push(endpoint.Buffer);
    }

    // A channel stopped: disabled while enabled, its halt awaited and cleared (DWC2's buffer DMA mode: CHDIS with CHENA).
    private void Halt(int channel)
    {
        var characteristics = m_registers.Read32(Channel(channel, HCCHAR));
        if ((characteristics & CHANNEL_ENABLE) == 0)
            return;
        m_registers.Write32(Channel(channel, HCCHAR), characteristics | CHANNEL_DISABLE | CHANNEL_ENABLE);
        var started = m_time.GetTimestamp();
        while ((m_registers.Read32(Channel(channel, HCINT)) & HALTED) == 0 && m_time.GetElapsedTime(started) < HALT_TIME)
            m_wait.Wait(HALT_TIME - m_time.GetElapsedTime(started));
        m_registers.Write32(Channel(channel, HCINT), uint.MaxValue);
    }

    // The core's soft reset: the AHB master idle, the reset's bit set until the core clears it, the master idle again.
    private void ResetCore()
    {
        Until(() => (m_registers.Read32(GRSTCTL) & AHB_IDLE) != 0, "the core's AHB master to idle");
        m_registers.Write32(GRSTCTL, CORE_RESET);
        Until(() => (m_registers.Read32(GRSTCTL) & CORE_RESET) == 0, "the core's soft reset");
        Until(() => (m_registers.Read32(GRSTCTL) & AHB_IDLE) != 0, "the core's AHB master to idle after its reset");
    }

    private void Until(Func<bool> done, string what)
    {
        var started = m_time.GetTimestamp();
        while (!done())
        {
            if (m_time.GetElapsedTime(started) > CORE_TIME)
                throw new IOException($"Waited {CORE_TIME.TotalSeconds} s for {what}.");
            Delay(1);
        }
    }

    // Each packet moved flips the endpoint's toggle; a transfer of no bytes is one packet.
    private static void Flip(Dwc2Endpoint endpoint, int moved, int packet)
    {
        var packets = moved <= 0 ? 1 : (moved + packet - 1) / packet;
        if ((packets & 1) != 0)
            endpoint.Data1 = !endpoint.Data1;
    }

    private static int Rounded(Dwc2Endpoint endpoint)
    {
        var packet = Math.Max(1, endpoint.Endpoint.PacketBytes);
        return (endpoint.Length + packet - 1) / packet * packet;
    }

    private static string Failure(uint interrupt) => (interrupt & STALLED) != 0 ? "stalled"
        : (interrupt & BABBLE) != 0 ? "babbled" : (interrupt & AHB_ERROR) != 0 ? "met an AHB error" : $"failed (HCINT {interrupt:x8})";

    // The device's address of a range, or null for one beyond its 32-bit view.
    private uint? DeviceAddress(ulong physical, int length)
    {
        var address = (long)physical + m_options.DmaOffset;
        return address < 0 || address + length > 1L << 32 ? null : (uint)address;
    }

    private static bool Within(IDmaBuffer buffer, ulong address, int length) =>
        address >= buffer.Physical && address + (ulong)length <= buffer.Physical + (ulong)buffer.Bytes.Length;

    private IDmaBuffer Allocate(int bytes)
    {
        var buffer = m_allocate(bytes);
        if ((buffer.Physical & 3) != 0 || DeviceAddress(buffer.Physical, bytes) is null || buffer.Bytes.Length < bytes)
            throw new IOException($"Memory at 0x{buffer.Physical:X}, which the core cannot reach.");
        return buffer;
    }

    private static int Speed(uint port) => (port >> PORT_SPEED_SHIFT & 3) switch
    {
        0 => XhciContexts.HIGH_SPEED,
        1 => XhciContexts.FULL_SPEED,
        2 => XhciContexts.LOW_SPEED,
        _ => 0,
    };

    private static int Channel(int channel, int register) => CHANNELS + channel * CHANNEL_BYTES + register;

    private static void CheckPort(int port)
    {
        if (port != 1)
            throw new ArgumentOutOfRangeException(nameof(port), $"Root port {port}; the controller has one.");
    }

    private Dwc2Device DeviceAt(int slot) =>
        slot is > 0 and < 128 && m_devices[slot] is { } device ? device : throw new InvalidOperationException($"Slot {slot} holds no device.");

    private static Dwc2Endpoint EndpointAt(Dwc2Device device, int index) =>
        device.Endpoints.TryGetValue(index, out var endpoint) ? endpoint
            : throw new InvalidOperationException($"Endpoint {index} of slot {device.Address} is not configured.");

    #endregion

    #region Properties

    /// <inheritdoc />
    public IReadOnlyList<UsbRootPort> RootPorts => m_rootPorts;

    /// <summary>
    /// The core's release, as GSNPSID names it: 2.80a on the Raspberry Pi 4, 2.94a in QEMU.
    /// </summary>
    public string Version { get; private set; } = "";

    /// <summary>
    /// The host channels used.
    /// </summary>
    public int Channels { get; private set; }

    /// <summary>
    /// The most bytes a channel moves at once.
    /// </summary>
    public int MaxTransfer { get; private set; }

    /// <summary>
    /// The most packets a channel moves at once.
    /// </summary>
    public int MaxPackets { get; private set; }

    /// <summary>
    /// The core's FIFO RAM in 32-bit words.
    /// </summary>
    public int FifoWords { get; private set; }

    /// <summary>
    /// The time until an interrupt endpoint's next ask is due while no channel carries it, null when none waits: the
    /// longest the driver waits for the controller's interrupt before it serves the host again.
    /// </summary>
    public TimeSpan? NextPoll
    {
        get
        {
            long? due = null;
            foreach (var endpoint in m_periodic)
            {
                if (endpoint.Asked && endpoint.Channel < 0 && (due is null || endpoint.Due < due))
                    due = endpoint.Due;
            }
            if (due is not { } value)
                return null;
            var left = m_time.GetElapsedTime(m_time.GetTimestamp(), value);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    #endregion

    #region Nested Types

    private sealed class Dwc2Device(int address, UsbLocation location, int maxPacket0)
    {
        public int Address { get; set; } = address;

        public UsbLocation Location { get; } = location;

        public int MaxPacket0 { get; set; } = maxPacket0;

        public UsbHubDescriptor? Hub { get; set; }

        public Dictionary<int, Dwc2Endpoint> Endpoints { get; } = [];
    }

    private sealed class Dwc2Endpoint(Dwc2Device device, int index, UsbEndpoint endpoint, IDmaBuffer buffer, TimeSpan interval)
    {
        public Dwc2Device Device { get; } = device;

        public int Index { get; } = index;

        public UsbEndpoint Endpoint { get; } = endpoint;

        public IDmaBuffer Buffer { get; } = buffer;

        public TimeSpan Interval { get; } = interval;

        public bool Data1 { get; set; }

        public bool Asked { get; set; }

        public int Length { get; set; }

        public long Due { get; set; }

        public int Channel { get; set; } = -1;
    }

    #endregion
}
