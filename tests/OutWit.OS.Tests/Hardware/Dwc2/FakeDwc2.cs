using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Usb;
using OutWit.OS.Tests.Hardware.Sdhci;
using OutWit.OS.Tests.Hardware.Usb;

namespace OutWit.OS.Tests.Hardware.Dwc2;

/// <summary>
/// A clock a test keeps (plan step H8.3): a wait for the controller and a sleep are the time passing, since the model
/// answers at once.
/// </summary>
internal sealed class FakeClock : TimeProvider, IInterruptWait
{
    #region Fields

    private long m_ticks = TimeSpan.TicksPerHour;

    #endregion

    #region Functions

    public override long GetTimestamp() => m_ticks;

    public void Advance(TimeSpan time) => m_ticks += Math.Max(1, time.Ticks);

    public void Wait(TimeSpan timeout)
    {
        Waited += timeout;
        Advance(timeout);
    }

    public void Sleep(TimeSpan time)
    {
        Slept += time;
        Advance(time);
    }

    #endregion

    #region Properties

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public TimeSpan Waited { get; private set; }

    public TimeSpan Slept { get; private set; }

    #endregion
}

/// <summary>
/// A model of a DWC2 core in host mode as QEMU's dwc2-usb serves one (hw/usb/hcd-dwc2.c of QEMU 11.1, plan step H8.3): its
/// identity and hardware configuration, its global, host and channel registers as 32-bit words with their read-only,
/// self-clearing and written-1-to-clear bits, its one root port (power, connection, reset, enable), and each channel
/// executed at the write that enables it in buffer DMA mode, whole: the transfer's packets moved through the memory at
/// HCDMA less the board's DMA offset, a control or bulk endpoint's NAK retried, an interrupt endpoint's NAK, a STALL, a
/// babble or a transfer to no device halting the channel as QEMU halts it. The devices are the USB core's test devices,
/// found by address through the enabled ports of the hubs between; unlike QEMU, the model checks each transfer's data PID
/// against the toggle the device keeps, as a device on a real bus does.
/// </summary>
internal sealed class FakeDwc2 : IDeviceRegisters
{
    #region Constants

    public const uint SNPSID = 0x4F54_294A;

    // GHWCFG2: 8 host channels, internal DMA; GHWCFG3: 4096 words of FIFO RAM, counters of 15 and 8 bits (QEMU's reset values).
    public const uint HWCFG2 = 7u << 14 | 2u << 3 | 6;
    public const uint HWCFG3 = 4096u << 16 | 4u << 4 | 4;

    private const int CHANNELS = 8;

    private const uint AHB_IDLE = 1U << 31;
    private const uint SELF_CLEARING = 0x3F; // GRSTCTL's resets and flushes
    private const uint HOST_MODE = 1;
    private const uint PORT_INTERRUPT = 1 << 24;
    private const uint CHANNEL_INTERRUPT = 1 << 25;
    private const uint INTERRUPTS_READ_ONLY = 1u << 26 | CHANNEL_INTERRUPT | PORT_INTERRUPT | 1u << 19 | 1u << 18 | 1u << 7 | 1u << 6 | 1u << 5 | 1u << 4 |
        1u << 2 | HOST_MODE;

    private const uint CONNECTED = 1;
    private const uint CONNECT_CHANGE = 1 << 1;
    private const uint ENABLED = 1 << 2;
    private const uint ENABLE_CHANGE = 1 << 3;
    private const uint OVERCURRENT_CHANGE = 1 << 5;
    private const uint RESUME = 1 << 6;
    private const uint SUSPEND = 1 << 7;
    private const uint RESET = 1 << 8;
    private const uint POWER = 1 << 12;
    private const uint SPEED = 3u << 17;
    private const uint LINE_STATUS = 3u << 10;
    private const uint OVERCURRENT = 1 << 4;
    private const uint PORT_CHANGES = CONNECT_CHANGE | ENABLE_CHANGE | OVERCURRENT_CHANGE;

    private const uint COMPLETE = 1;
    private const uint HALTED = 1 << 1;
    private const uint STALL = 1 << 3;
    private const uint NAK = 1 << 4;
    private const uint TRANSACTION_ERROR = 1 << 7;
    private const uint BABBLE = 1 << 8;
    private const uint DISABLE = 1 << 30;
    private const uint ENABLE = 1U << 31;

    private const int PID_DATA0 = 0;
    private const int PID_DATA1 = 2;
    private const int PID_SETUP = 3;

    #endregion

    #region Fields

    private readonly FakeMemory m_memory;
    private readonly long m_dmaOffset;
    private readonly Dictionary<int, uint> m_registers = [];
    private readonly uint[,] m_channels = new uint[CHANNELS, 8];
    private readonly Dictionary<FakeDevice, ControlState> m_control = [];
    private FakeDevice? m_root;

    #endregion

    #region Constructors

    public FakeDwc2(FakeMemory memory, long dmaOffset = 0, uint snpsid = SNPSID, uint hwcfg2 = HWCFG2)
    {
        m_memory = memory;
        m_dmaOffset = dmaOffset;
        m_registers[0x00C] = 5u << 10; // GUSBCFG
        m_registers[0x010] = AHB_IDLE; // GRSTCTL
        m_registers[0x014] = 1u << 28 | 1u << 26 | 1u << 5 | HOST_MODE; // GINTSTS
        m_registers[0x024] = 1024; // GRXFSIZ
        m_registers[0x028] = 1024u << 16; // GNPTXFSIZ
        m_registers[0x040] = snpsid;
        m_registers[0x048] = hwcfg2;
        m_registers[0x04C] = HWCFG3;
        m_registers[0x100] = 500u << 16; // HPTXFSIZ
        m_registers[0x400] = 2u << 8; // HCFG
        m_registers[0x404] = 60000; // HFIR
        m_registers[0x440] = POWER; // HPRT0, as the reset's exit leaves it
    }

    #endregion

    #region Functions

    /// <summary>
    /// Connects a device to the root port, as QEMU's dwc2_attach does: its speed, the connection and its change.
    /// </summary>
    public void Plug(FakeDevice device)
    {
        m_root = device;
        device.Address = 0;
        var speed = device.Speed switch
        {
            XhciContexts.HIGH_SPEED => 0u,
            XhciContexts.FULL_SPEED => 1u,
            _ => 2u,
        };
        Port = Port & ~SPEED | speed << 17 | CONNECTED | CONNECT_CHANGE;
    }

    /// <summary>
    /// Disconnects the root port's device, as QEMU's dwc2_detach does.
    /// </summary>
    public void Unplug()
    {
        m_root = null;
        Port = Port & ~(SPEED | SUSPEND | ENABLED | CONNECTED) | CONNECT_CHANGE | ENABLE_CHANGE;
    }

    public uint Read32(int offset)
    {
        Reads++;
        if (offset is >= 0x500 and < 0x500 + CHANNELS * 0x20)
            return m_channels[(offset - 0x500) / 0x20, (offset & 0x1F) / 4];
        switch (offset)
        {
            case 0x010:
                {
                    var value = m_registers[0x010];
                    m_registers[0x010] = value & ~SELF_CLEARING; // QEMU clears the self-clearing bits at the read
                    return value & ~SELF_CLEARING;
                }
            case 0x014:
                return Interrupts;
            case 0x408:
                return Frame;
            case 0x414:
                return Pending;
            default:
                return m_registers.GetValueOrDefault(offset);
        }
    }

    public void Write32(int offset, uint value)
    {
        Writes.Add((offset, value));
        if (offset is >= 0x500 and < 0x500 + CHANNELS * 0x20)
        {
            WriteChannel((offset - 0x500) / 0x20, (offset & 0x1F) / 4, value);
            return;
        }
        var old = m_registers.GetValueOrDefault(offset);
        switch (offset)
        {
            case 0x010:
                if ((value & 1) != 0)
                    CoreResets++;
                m_registers[offset] = value | AHB_IDLE;
                return;
            case 0x014:
                m_registers[offset] = old & ~(value & ~INTERRUPTS_READ_ONLY);
                return;
            case 0x408 or 0x414:
                throw new InvalidOperationException($"A write to the read-only register 0x{offset:x3}.");
            case 0x418:
                m_registers[offset] = value & 0xFFFF;
                return;
            case 0x440:
                WritePort(old, value);
                return;
            default:
                m_registers[offset] = value;
                return;
        }
    }

    public void Fence() => Fences++;

    // HPRT0 as QEMU's dwc2_hreg0_write takes it: its read-only bits kept, its enable never set by a write, its changes
    // and its enable written 1 to clear, and the end of a reset enabling the port of a connected device.
    private void WritePort(uint old, uint value)
    {
        value |= old & (SPEED | LINE_STATUS | OVERCURRENT | CONNECTED | SUSPEND | RESUME);
        if ((old & ENABLED) == 0)
            value &= ~ENABLED;
        var clears = value & (PORT_CHANGES | ENABLED);
        value = value & ~(PORT_CHANGES | ENABLED) | old & (PORT_CHANGES | ENABLED) & ~clears;
        if ((value & RESET) == 0 && (old & RESET) != 0 && m_root is not null)
        {
            value = value & ~CONNECT_CHANGE | ENABLED | ENABLE_CHANGE;
            m_root.Address = 0;
            m_control.Clear();
            PortResets++;
        }
        m_registers[0x440] = value;
    }

    private void WriteChannel(int channel, int register, uint value)
    {
        var old = m_channels[channel, register];
        switch (register)
        {
            case 0: // HCCHAR
                if ((value & DISABLE) != 0 && (old & DISABLE) == 0)
                {
                    m_channels[channel, 0] = value & ~(ENABLE | DISABLE);
                    m_channels[channel, 2] |= HALTED;
                    Disables++;
                    return;
                }
                m_channels[channel, 0] = value;
                if ((value & ENABLE) != 0 && (old & ENABLE) == 0)
                    Execute(channel);
                return;
            case 2: // HCINT
                m_channels[channel, 2] = old & ~value;
                return;
            case 7: // HCDMAB
                throw new InvalidOperationException("A write to the read-only HCDMAB.");
            default:
                m_channels[channel, register] = value;
                return;
        }
    }

    // A channel's transfer, whole, as QEMU's dwc2_handle_packet moves it packet by packet.
    private void Execute(int channel)
    {
        var characteristics = m_channels[channel, 0];
        var size = m_channels[channel, 4];
        var dma = m_channels[channel, 5];
        var packet = (int)(characteristics & 0x7FF);
        var number = (int)(characteristics >> 11 & 0xF);
        var isIn = (characteristics & 1 << 15) != 0;
        var type = (int)(characteristics >> 18 & 3);
        var address = (int)(characteristics >> 22 & 0x7F);
        var bytes = (int)(size & 0x7FFFF);
        var packets = (int)(size >> 19 & 0x3FF);
        var pid = (int)(size >> 29 & 3);
        Transfers.Add((channel, address, number, isIn, type, bytes, pid));
        if (packet == 0 || (dma & 3) != 0 || packets == 0 || packets * packet < bytes || (isIn && bytes % packet != 0))
            throw new InvalidOperationException($"Channel {channel}: HCCHAR {characteristics:x8}, HCTSIZ {size:x8}, HCDMA {dma:x8}.");
        if (bytes > 65536)
            return; // QEMU logs the size and leaves the channel enabled
        var device = (Port & ENABLED) != 0 ? Find(m_root, address) : null;
        if (device is null)
        {
            Unanswered++;
            return; // QEMU's dwc2_enable_chan finds no device and leaves the channel enabled
        }
        if ((characteristics & 1 << 17) != 0 != (device.Speed == XhciContexts.LOW_SPEED))
            throw new InvalidOperationException($"Channel {channel}: the low-speed bit for a device of speed {device.Speed}.");
        var memory = (ulong)((long)dma - m_dmaOffset);
        var (status, moved) = type switch
        {
            0 when pid == PID_SETUP => Setup(device, memory, bytes),
            0 => ControlStage(device, isIn, pid, memory, bytes, packet),
            3 when isIn => InterruptIn(device, number, pid, memory, bytes, packet),
            2 => Bulk(device, number, isIn, pid, memory, bytes, packet),
            _ => throw new InvalidOperationException($"Channel {channel}: a transfer of type {type}, which the model does not serve."),
        };
        if (status == NAK && type is 0 or 2)
            throw new InvalidOperationException("A NAK of a control or bulk transfer, which the core retries.");
        if (status == COMPLETE)
        {
            var left = bytes - moved;
            var sent = moved == 0 ? 1 : (moved + packet - 1) / packet;
            m_channels[channel, 4] = size & ~(0x7FFFFu | 0x3FFu << 19) | (uint)left | (uint)Math.Max(0, packets - sent) << 19;
            m_channels[channel, 5] = dma + (uint)moved;
            m_channels[channel, 2] |= COMPLETE | HALTED;
        }
        else
        {
            m_channels[channel, 2] |= status | HALTED;
        }
        m_channels[channel, 0] &= ~ENABLE;
    }

    private (uint Status, int Moved) Setup(FakeDevice device, ulong memory, int bytes)
    {
        if (bytes != 8)
            throw new InvalidOperationException($"A SETUP of {bytes} bytes.");
        var raw = m_memory.At(memory, 8);
        var setup = new UsbSetup(raw[0], raw[1], BinaryPrimitives.ReadUInt16LittleEndian(raw[2..]), BinaryPrimitives.ReadUInt16LittleEndian(raw[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(raw[6..]));
        Requests.Add((device.Address, setup));
        var stalled = device.Stalls.Contains((setup.RequestType, setup.Request));
        m_control[device] = new ControlState(setup, stalled ? null : device.Answer(setup));
        return (COMPLETE, 8);
    }

    // A control transfer's data or status stage: DATA1 first in each, as USB 2.0 §8.5.3 asks; the status stage's end does
    // what the request asked, as QEMU's device does by then.
    private (uint Status, int Moved) ControlStage(FakeDevice device, bool isIn, int pid, ulong memory, int bytes, int packet)
    {
        if (!m_control.TryGetValue(device, out var state))
            throw new InvalidOperationException("A control stage without its SETUP.");
        if (state.Answer is null)
            return (STALL, 0);
        if (pid != PID_DATA1)
            ToggleErrors++;
        var setup = state.Setup;
        if (setup.Length > 0 && !state.DataDone && isIn == setup.IsIn)
        {
            state.DataDone = true;
            if (!isIn)
                throw new InvalidOperationException("A control transfer's OUT data stage, which the host does not send.");
            var moved = Math.Min(Math.Min(state.Answer.Length, setup.Length), bytes);
            state.Answer.AsSpan(0, moved).CopyTo(m_memory.At(memory, moved));
            return (COMPLETE, moved);
        }
        if (bytes != 0 || isIn == (setup.Length > 0 && setup.IsIn))
            throw new InvalidOperationException($"A status stage of {bytes} bytes {(isIn ? "in" : "out")} for request {setup.Request:x2}.");
        m_control.Remove(device);
        switch (setup.RequestType, setup.Request)
        {
            case (0x00, UsbSetup.SET_ADDRESS):
                device.Address = setup.Value;
                break;
            case (0x00, UsbSetup.SET_CONFIGURATION):
                device.Toggles.Clear();
                break;
            case (0x02, UsbSetup.CLEAR_FEATURE) when setup.Value == UsbSetup.ENDPOINT_HALT:
                device.Toggles.Remove(setup.Index);
                device.Halted.Remove(setup.Index);
                break;
        }
        return (COMPLETE, 0);
    }

    // An interrupt IN transfer: a hub's change bitmap while a port has a change, a keyboard's queued report, NAK otherwise.
    private (uint Status, int Moved) InterruptIn(FakeDevice device, int number, int pid, ulong memory, int bytes, int packet)
    {
        var endpoint = 0x80 | number;
        if (device.Halted.Contains(endpoint))
            return (STALL, 0);
        byte[]? report = null;
        if (device.Hub is { } hub && number == 1)
        {
            var bitmap = hub.Report();
            report = bitmap.Any(item => item != 0) ? bitmap : null;
        }
        else if (device.Reports.TryGetValue(number, out var queue) && queue.Count > 0)
        {
            report = queue.Dequeue();
        }
        if (report is null)
            return (NAK, 0);
        if (report.Length > bytes)
            return (BABBLE, 0);
        Toggle(device, endpoint, pid, report.Length, packet);
        report.CopyTo(m_memory.At(memory, report.Length));
        return (COMPLETE, report.Length);
    }

    // A bulk transfer through the device's loopback: OUT appends, IN sends what is there, a short packet when it runs out.
    private (uint Status, int Moved) Bulk(FakeDevice device, int number, bool isIn, int pid, ulong memory, int bytes, int packet)
    {
        var endpoint = (isIn ? 0x80 : 0) | number;
        if (device.Halted.Contains(endpoint))
            return (STALL, 0);
        if (BulkErrors > 0)
        {
            BulkErrors--;
            return (TRANSACTION_ERROR, 0);
        }
        if (isIn)
        {
            var moved = Math.Min(bytes, device.Bulk.Count);
            device.Bulk.GetRange(0, moved).ToArray().CopyTo(m_memory.At(memory, moved));
            device.Bulk.RemoveRange(0, moved);
            Toggle(device, endpoint, pid, moved, packet);
            return (COMPLETE, moved);
        }
        device.Bulk.AddRange(m_memory.At(memory, bytes).ToArray());
        Toggle(device, endpoint, pid, bytes, packet);
        return (COMPLETE, bytes);
    }

    // The device's toggle for the endpoint checked against the transfer's PID, then flipped by each packet it took.
    private void Toggle(FakeDevice device, int endpoint, int pid, int moved, int packet)
    {
        var data1 = device.Toggles.GetValueOrDefault(endpoint);
        if (pid != (data1 ? PID_DATA1 : PID_DATA0))
            ToggleErrors++;
        var packets = moved == 0 ? 1 : (moved + packet - 1) / packet;
        device.Toggles[endpoint] = data1 ^ (packets & 1) != 0;
    }

    // The device at an address: the root port's, or one below a hub through enabled ports, as QEMU's usb_find_device.
    private static FakeDevice? Find(FakeDevice? device, int address)
    {
        if (device is null)
            return null;
        if (device.Address == address)
            return device;
        if (device.Hub is not { } hub)
            return null;
        foreach (var port in hub.Ports.Skip(1).Where(item => item.Enabled))
        {
            if (Find(port.Device, address) is { } found)
                return found;
        }
        return null;
    }

    #endregion

    #region Properties

    public uint Port
    {
        get => m_registers[0x440];
        private set => m_registers[0x440] = value;
    }

    /// <summary>
    /// GINTSTS with its port and channel bits as the port and the channels hold them.
    /// </summary>
    public uint Interrupts
    {
        get
        {
            var value = m_registers[0x014] & ~(PORT_INTERRUPT | CHANNEL_INTERRUPT);
            if ((Port & PORT_CHANGES) != 0)
                value |= PORT_INTERRUPT;
            if ((Pending & m_registers.GetValueOrDefault(0x418)) != 0)
                value |= CHANNEL_INTERRUPT;
            return value;
        }
    }

    /// <summary>
    /// HAINT: each channel whose HCINT holds a bit its HCINTMSK lets through.
    /// </summary>
    public uint Pending
    {
        get
        {
            uint value = 0;
            for (var channel = 0; channel < CHANNELS; channel++)
            {
                if ((m_channels[channel, 2] & m_channels[channel, 3]) != 0)
                    value |= 1u << channel;
            }
            return value;
        }
    }

    /// <summary>
    /// The interrupt line: an unmasked interrupt while the global enable is on.
    /// </summary>
    public bool Interrupt => (Interrupts & m_registers.GetValueOrDefault(0x018)) != 0 && (m_registers.GetValueOrDefault(0x008) & 1) != 0;

    public uint Register(int offset) => m_registers.GetValueOrDefault(offset);

    public uint ChannelRegister(int channel, int register) => m_channels[channel, register];

    public uint Frame { get; set; }

    public int Reads { get; private set; }

    public List<(int Offset, uint Value)> Writes { get; } = [];

    public List<(int Channel, int Address, int Endpoint, bool IsIn, int Type, int Bytes, int Pid)> Transfers { get; } = [];

    public List<(int Address, UsbSetup Setup)> Requests { get; } = [];

    public int ToggleErrors { get; private set; }

    public int Unanswered { get; private set; }

    public int Fences { get; private set; }

    public int CoreResets { get; private set; }

    public int PortResets { get; private set; }

    public int Disables { get; private set; }

    public int BulkErrors { get; set; }

    #endregion

    #region Nested Types

    private sealed class ControlState(UsbSetup setup, byte[]? answer)
    {
        public UsbSetup Setup { get; } = setup;

        public byte[]? Answer { get; } = answer;

        public bool DataDone { get; set; }
    }

    #endregion
}
