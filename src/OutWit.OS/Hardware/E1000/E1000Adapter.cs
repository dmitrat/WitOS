using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware.E1000;

/// <summary>
/// An Intel adapter of the e1000e family the driver holds (Intel 82574 GbE Controller datasheet, plan step H4): the
/// 82574L that QEMU's <c>e1000e</c> and VMware's e1000e present. Its registers are in BAR 0, its rings and buffers in one
/// pinned arena, and its interrupt is bound to an event: an MSI message where the kernel serves messages and the function
/// has the capability, its INTx line otherwise. The driver takes the adapter from whatever drove it (bus mastering
/// stopped, then a reset), reads the address the adapter loaded from its NVM, accepts its own address, broadcast and every
/// multicast frame (IPv6's neighbour discovery reaches its solicited-node groups), and sends and receives with legacy
/// descriptors: RECEIVE_COUNT receive buffers and TRANSMIT_COUNT transmit buffers of BUFFER bytes each, the adapter
/// stripping the receive CRC, padding short frames and adding the transmit CRC. The frames the adapter received wait in
/// the driver's own queue, at most PacketMessages.QUEUE of them, until the client takes them. One driver thread at a time.
/// </summary>
public sealed class E1000Adapter : IPacketDevice, IDisposable
{
    #region Constants

    /// <summary>
    /// The largest payload of a frame: Ethernet's, since the driver does not ask for long packets (RCTL.LPE).
    /// </summary>
    public const int MTU = 1500;

    /// <summary>
    /// The receive descriptors and their buffers.
    /// </summary>
    public const int RECEIVE_COUNT = 32;

    /// <summary>
    /// The transmit descriptors and their buffers; the adapter holds one less at a time, since a full ring's tail would
    /// meet its head.
    /// </summary>
    public const int TRANSMIT_COUNT = 16;

    /// <summary>
    /// The 82574L's device identifier (vendor 0x8086).
    /// </summary>
    public const ushort DEVICE_82574L = 0x10D3;

    /// <summary>
    /// A buffer's bytes: RCTL.BSIZE's 2048, which a frame of the MTU with its header and a VLAN tag fits.
    /// </summary>
    private const int BUFFER = 2048;

    /// <summary>
    /// The arena: a page of both rings, then the buffers, two to a page.
    /// </summary>
    private const int ARENA_PAGES = 1 + (RECEIVE_COUNT + TRANSMIT_COUNT) * BUFFER / MemoryObject.PAGE;

    /// <summary>
    /// The seconds the reset and the end of bus mastering may take.
    /// </summary>
    private const int TIMEOUT_SECONDS = 2;

    /// <summary>
    /// The seconds the link is given to come up after the reset; a link down past them is reported, not refused.
    /// </summary>
    private const int LINK_SECONDS = 5;

    /// <summary>
    /// The smallest frame the driver takes from a receive buffer: an Ethernet header.
    /// </summary>
    private const int HEADER = 14;

    // Registers (82574 datasheet §10.2).
    private const int CTRL = 0x00000;
    private const int STATUS = 0x00008;
    private const int EERD = 0x00014;
    private const int ICR = 0x000C0;
    private const int IMS = 0x000D0;
    private const int IMC = 0x000D8;
    private const int RCTL = 0x00100;
    private const int TCTL = 0x00400;
    private const int TIPG = 0x00410;
    private const int RDBAL = 0x02800;
    private const int RDBAH = 0x02804;
    private const int RDLEN = 0x02808;
    private const int RDH = 0x02810;
    private const int RDT = 0x02818;
    private const int TDBAL = 0x03800;
    private const int TDBAH = 0x03804;
    private const int TDLEN = 0x03808;
    private const int TDH = 0x03810;
    private const int TDT = 0x03818;
    private const int MTA = 0x05200;
    private const int MTA_ENTRIES = 128;
    private const int RAL = 0x05400;
    private const int RAH = 0x05404;

    // CTRL: GIO master disable, set link up, the forced speed and duplex, and the reset.
    private const uint CTRL_MASTER_DISABLE = 1 << 2;
    private const uint CTRL_SET_LINK_UP = 1 << 6;
    private const uint CTRL_FORCE_SPEED = 1 << 11;
    private const uint CTRL_FORCE_DUPLEX = 1 << 12;
    private const uint CTRL_RESET = 1 << 26;

    // STATUS: full duplex, link up, the speed, and the GIO master's requests still pending.
    private const uint STATUS_FULL_DUPLEX = 1;
    private const uint STATUS_LINK_UP = 1 << 1;
    private const uint STATUS_MASTER_ENABLE = 1 << 19;

    // EERD: start, done and the word read, an 82574's address in bits 15–2.
    private const uint EERD_START = 1;
    private const uint EERD_DONE = 1 << 1;

    // RAH: the address is valid (AV).
    private const uint RAH_VALID = 1U << 31;

    // RCTL: enable, multicast promiscuous, broadcast accept and strip the CRC; BSIZE 00 with BSEX clear is 2048 bytes and
    // RDMTS 00 a half of the ring.
    private const uint RCTL_ENABLE = 1 << 1;
    private const uint RCTL_MULTICAST = 1 << 4;
    private const uint RCTL_BROADCAST = 1 << 15;
    private const uint RCTL_STRIP_CRC = 1 << 26;

    // TCTL: enable, pad short packets, the collision threshold (15) and distance (63, full duplex) and retransmit on a late
    // collision, as Intel's drivers set them.
    private const uint TCTL_ENABLE = 1 << 1;
    private const uint TCTL_PAD = 1 << 3;
    private const uint TCTL_COLLISION_THRESHOLD = 0x0F << 4;
    private const uint TCTL_COLLISION_DISTANCE = 0x3F << 12;
    private const uint TCTL_LATE_COLLISION = 1 << 24;

    // TIPG for copper: IPGT 8, IPGR1 8, IPGR2 6.
    private const uint TIPG_COPPER = 8 | 8 << 10 | 6 << 20;

    // ICR and IMS: a transmit descriptor written back, the link's change, the receive ring below its threshold, a receive
    // overrun and the receive timer.
    private const uint INTERRUPT_TRANSMITTED = 1;
    private const uint INTERRUPT_LINK = 1 << 2;
    private const uint INTERRUPT_RECEIVE_LOW = 1 << 4;
    private const uint INTERRUPT_OVERRUN = 1 << 6;
    private const uint INTERRUPT_RECEIVED = 1 << 7;
    private const uint INTERRUPTS = INTERRUPT_TRANSMITTED | INTERRUPT_LINK | INTERRUPT_RECEIVE_LOW | INTERRUPT_OVERRUN | INTERRUPT_RECEIVED;

    #endregion

    #region Fields

    private readonly KernelHandle m_configurationObject;
    private readonly MmioRegion m_configuration;
    private readonly KernelHandle m_barObject;
    private readonly MmioRegion m_bar;
    private readonly KernelHandle m_signaled;
    private readonly Msi? m_msi;
    private readonly InterruptBinding m_binding;
    private readonly DmaArena m_arena;
    private readonly IDmaBuffer m_receiveRing;
    private readonly IDmaBuffer m_transmitRing;
    private readonly IDmaBuffer[] m_receiveBuffers = new IDmaBuffer[RECEIVE_COUNT];
    private readonly IDmaBuffer[] m_transmitBuffers = new IDmaBuffer[TRANSMIT_COUNT];
    private readonly Queue<byte[]> m_received = new();
    private int m_receiveNext;
    private int m_transmitTail;
    private int m_transmitClean;
    private int m_inFlight;
    private bool m_running;

    #endregion

    #region Constructors

    private E1000Adapter(KernelHandle configurationObject, MmioRegion configuration, KernelHandle barObject, MmioRegion bar, KernelHandle signaled,
        Msi? msi, InterruptBinding binding, DmaArena arena, string interruptDescription)
    {
        m_configurationObject = configurationObject;
        m_configuration = configuration;
        m_barObject = barObject;
        m_bar = bar;
        m_signaled = signaled;
        m_msi = msi;
        m_binding = binding;
        m_arena = arena;
        InterruptDescription = interruptDescription;
        m_receiveRing = arena.Allocate(RECEIVE_COUNT * E1000Descriptors.BYTES, E1000Descriptors.RING_ALIGNMENT);
        m_transmitRing = arena.Allocate(TRANSMIT_COUNT * E1000Descriptors.BYTES, E1000Descriptors.RING_ALIGNMENT);
        for (var i = 0; i < RECEIVE_COUNT; i++)
            m_receiveBuffers[i] = arena.Allocate(BUFFER, BUFFER);
        for (var i = 0; i < TRANSMIT_COUNT; i++)
            m_transmitBuffers[i] = arena.Allocate(BUFFER, BUFFER);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Opens the adapter of the function the driver holds, resets it and starts its receiver and transmitter.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="descriptor">Its descriptor: region 0 its configuration space, the others its BARs.</param>
    /// <returns>The running adapter.</returns>
    /// <exception cref="IOException">The adapter did not end its reset or names no address.</exception>
    /// <exception cref="CapabilityException">A region, the arena or the interrupt could not be had.</exception>
    public static E1000Adapter Open(KernelHandle device, DeviceDescriptor descriptor)
    {
        var configurationObject = Device.Memory(device, 0);
        MmioRegion? configuration = null;
        KernelHandle? barObject = null;
        MmioRegion? bar = null;
        KernelHandle? signaled = null;
        Msi? msi = null;
        InterruptBinding? binding = null;
        DmaArena? arena = null;
        E1000Adapter? adapter = null;
        try
        {
            configuration = MmioRegion.Map(configurationObject, MemoryObject.PAGE);
            configuration.Write16(PciConfiguration.COMMAND, (ushort)(configuration.Read16(PciConfiguration.COMMAND) | 6)); // memory decoding, bus mastering
            var region = PciConfiguration.RegionOfBar(configuration, descriptor, 0);
            barObject = Device.Memory(device, region);
            bar = MmioRegion.Map(barObject, (int)Math.Min(descriptor.Regions[region].Size, (ulong)(Device.WINDOW_PAGES * MemoryObject.PAGE)));
            signaled = Event.Create();
            string description;
            if (InterruptBinding.MessagesSupported && Msi.Find(configuration) is { } found)
            {
                binding = InterruptBinding.BindMessage(device, signaled, out var address, out var data);
                found.Enable(address, data);
                msi = found;
                description = $"MSI, message {data:x2} at {address:x}";
            }
            else
            {
                binding = InterruptBinding.Bind(device, 0, signaled);
                description = "its INTx line";
            }
            arena = DmaArena.Create(device, ARENA_PAGES);
            adapter = new E1000Adapter(configurationObject, configuration, barObject, bar, signaled, msi, binding, arena, description);
            adapter.Start();
            return adapter;
        }
        catch
        {
            if (adapter is not null)
            {
                adapter.Dispose(); // its DMA stopped, if it ran, before its arena goes
                throw;
            }
            binding?.Dispose();
            msi?.Disable();
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
    /// The address the receive address registers hold: RAL's four bytes, then RAH's two, valid when RAH's AV is set.
    /// </summary>
    /// <param name="low">RAL.</param>
    /// <param name="high">RAH.</param>
    /// <returns>The address, its first byte the lowest; zero when RAH does not mark it valid.</returns>
    public static ulong AddressOf(uint low, uint high) => (high & RAH_VALID) == 0 ? 0 : low | (ulong)(high & 0xFFFF) << 32;

    /// <summary>
    /// The address of the NVM's first three words, each holding two bytes, the first the low one.
    /// </summary>
    /// <param name="word0">Word 0.</param>
    /// <param name="word1">Word 1.</param>
    /// <param name="word2">Word 2.</param>
    /// <returns>The address, its first byte the lowest.</returns>
    public static ulong AddressOf(ushort word0, ushort word1, ushort word2) => word0 | (ulong)word1 << 16 | (ulong)word2 << 32;

    /// <summary>
    /// Serves the interrupt the event reports: reads (and so clears) its causes, takes what the adapter received into the
    /// frame queue, gives the adapter its buffers back, frees the transmit descriptors it completed and unmasks the INTx
    /// line.
    /// </summary>
    public void Service()
    {
        var causes = m_bar.Read32(ICR);
        if ((causes & INTERRUPT_OVERRUN) != 0)
            Overruns++;
        Collect();
        if (m_msi is null)
            m_binding.Acknowledge();
    }

    /// <summary>
    /// Transmits frames, each in a transmit buffer, waiting for the adapter to free a descriptor when none is left; what
    /// the adapter receives meanwhile waits in the frame queue. Returns once the adapter took every frame.
    /// </summary>
    /// <param name="frames">The frames, each of 14 to 14 + MTU bytes.</param>
    /// <exception cref="IOException">The adapter completed no descriptor in TIMEOUT_SECONDS.</exception>
    public void Transmit(IReadOnlyList<ReadOnlyMemory<byte>> frames)
    {
        var offered = false;
        foreach (var frame in frames)
        {
            while (m_inFlight == TRANSMIT_COUNT - 1)
            {
                if (offered)
                {
                    Offer();
                    offered = false;
                }
                AwaitTransmits();
            }
            var buffer = m_transmitBuffers[m_transmitTail];
            frame.Span.CopyTo(buffer.Bytes);
            E1000Descriptors.WriteTransmit(Descriptor(m_transmitRing, m_transmitTail), buffer.Physical, frame.Length);
            m_transmitTail = (m_transmitTail + 1) % TRANSMIT_COUNT;
            m_inFlight++;
            offered = true;
        }
        if (offered)
            Offer();
        while (m_inFlight > 0)
            AwaitTransmits();
    }

    /// <summary>
    /// Takes the oldest frame the adapter received, if any.
    /// </summary>
    /// <returns>The frame, or null when none waits.</returns>
    public byte[]? Take() => m_received.Count > 0 ? m_received.Dequeue() : null;

    /// <summary>
    /// The length of the oldest frame waiting, or zero.
    /// </summary>
    /// <returns>Its bytes.</returns>
    public int PeekLength() => m_received.Count > 0 ? m_received.Peek().Length : 0;

    // The adapter from whatever drove it: bus mastering stopped (the firmware's driver may have left its rings running),
    // the reset, the address, the filters, both rings and the interrupts; then the link awaited.
    private void Start()
    {
        m_bar.Write32(IMC, uint.MaxValue);
        m_bar.Write32(CTRL, m_bar.Read32(CTRL) | CTRL_MASTER_DISABLE);
        Await(() => (m_bar.Read32(STATUS) & STATUS_MASTER_ENABLE) == 0, "end its bus mastering");
        m_bar.Write32(CTRL, m_bar.Read32(CTRL) | CTRL_RESET);
        Thread.Sleep(10); // the reset reloads the NVM's words; nothing is read during the first milliseconds
        Await(() => (m_bar.Read32(CTRL) & CTRL_RESET) == 0, "end its reset");
        m_running = true;
        m_bar.Write32(IMC, uint.MaxValue);
        _ = m_bar.Read32(ICR);
        var address = AddressOf(m_bar.Read32(RAL), m_bar.Read32(RAH));
        if (address == 0)
            address = AddressOf(ReadNvm(0), ReadNvm(1), ReadNvm(2));
        if (address == 0 || (address & 1) != 0)
            throw new IOException($"The adapter names no unicast address: {address:x12}.");
        Address = address;
        m_bar.Write32(RAL, (uint)address);
        m_bar.Write32(RAH, (uint)(address >> 32) | RAH_VALID);
        for (var i = 0; i < MTA_ENTRIES; i++)
            m_bar.Write32(MTA + 4 * i, 0);
        m_bar.Write32(CTRL, m_bar.Read32(CTRL) & ~(CTRL_FORCE_SPEED | CTRL_FORCE_DUPLEX) | CTRL_SET_LINK_UP);
        for (var i = 0; i < RECEIVE_COUNT; i++)
            E1000Descriptors.WriteReceive(Descriptor(m_receiveRing, i), m_receiveBuffers[i].Physical);
        m_bar.Write32(RDBAL, (uint)m_receiveRing.Physical);
        m_bar.Write32(RDBAH, (uint)(m_receiveRing.Physical >> 32));
        m_bar.Write32(RDLEN, RECEIVE_COUNT * E1000Descriptors.BYTES);
        m_bar.Write32(RDH, 0);
        m_bar.Write32(TDBAL, (uint)m_transmitRing.Physical);
        m_bar.Write32(TDBAH, (uint)(m_transmitRing.Physical >> 32));
        m_bar.Write32(TDLEN, TRANSMIT_COUNT * E1000Descriptors.BYTES);
        m_bar.Write32(TDH, 0);
        m_bar.Write32(TDT, 0);
        m_bar.Write32(TIPG, TIPG_COPPER);
        m_bar.Write32(TCTL, TCTL_ENABLE | TCTL_PAD | TCTL_COLLISION_THRESHOLD | TCTL_COLLISION_DISTANCE | TCTL_LATE_COLLISION);
        m_bar.Write32(RCTL, RCTL_ENABLE | RCTL_MULTICAST | RCTL_BROADCAST | RCTL_STRIP_CRC);
        MmioRegion.Barrier();
        m_bar.Write32(RDT, RECEIVE_COUNT - 1); // every buffer but the one the tail stands on
        m_bar.Write32(IMS, INTERRUPTS);
        var deadline = Clock.MonotonicAfter(LINK_SECONDS);
        while (!LinkUp && Clock.Monotonic() < deadline)
            Thread.Sleep(10);
    }

    // An NVM word through EERD.
    private ushort ReadNvm(int word)
    {
        m_bar.Write32(EERD, (uint)word << 2 | EERD_START);
        uint value = 0;
        Await(() => ((value = m_bar.Read32(EERD)) & EERD_DONE) != 0, $"read its NVM's word {word}");
        return (ushort)(value >> 16);
    }

    // What the adapter completed: received frames into the queue (beyond its room the newest are dropped, as a full ring
    // drops them) and their descriptors given back, the tail moved to the last of them; transmit descriptors freed.
    private void Collect()
    {
        var last = -1;
        for (; ; )
        {
            var descriptor = Descriptor(m_receiveRing, m_receiveNext);
            var received = E1000Descriptors.ReadReceive(descriptor);
            if (!received.Done)
                break;
            if (received.Usable && received.Length is >= HEADER and <= BUFFER)
            {
                if (m_received.Count < PacketMessages.QUEUE)
                    m_received.Enqueue(m_receiveBuffers[m_receiveNext].Bytes[..received.Length].ToArray());
                else
                    Dropped++;
            }
            E1000Descriptors.WriteReceive(descriptor, m_receiveBuffers[m_receiveNext].Physical);
            last = m_receiveNext;
            m_receiveNext = (m_receiveNext + 1) % RECEIVE_COUNT;
        }
        if (last >= 0)
        {
            MmioRegion.Barrier();
            m_bar.Write32(RDT, (uint)last);
        }
        while (m_inFlight > 0 && E1000Descriptors.TransmitDone(Descriptor(m_transmitRing, m_transmitClean)))
        {
            m_transmitClean = (m_transmitClean + 1) % TRANSMIT_COUNT;
            m_inFlight--;
        }
    }

    // The descriptors written so far handed to the adapter.
    private void Offer()
    {
        MmioRegion.Barrier();
        m_bar.Write32(TDT, (uint)m_transmitTail);
    }

    // A transmit descriptor completed: the interrupt awaited in slices, each followed by a look at the descriptors, so
    // that a lost interrupt delays the transmit and no more.
    private void AwaitTransmits()
    {
        var waiting = m_inFlight;
        var deadline = Clock.MonotonicAfter(TIMEOUT_SECONDS);
        for (; ; )
        {
            Service();
            if (m_inFlight < waiting)
                return;
            var now = Clock.Monotonic();
            if (now > deadline)
                throw new IOException($"The adapter completed none of {m_inFlight} transmit descriptors in {TIMEOUT_SECONDS} s: TDH {m_bar.Read32(TDH)}, TDT {m_bar.Read32(TDT)}.");
            try
            {
                Event.Wait(Math.Min(deadline, now + Clock.Frequency() / 100), m_signaled);
            }
            catch (CapabilityException exception) when (exception.Error == 110)
            {
                // The slice passed; the next look reads the descriptors themselves.
            }
        }
    }

    private void Await(Func<bool> condition, string what)
    {
        var deadline = Clock.MonotonicAfter(TIMEOUT_SECONDS);
        while (!condition())
        {
            if (Clock.Monotonic() > deadline)
                throw new IOException($"The adapter did not {what} in {TIMEOUT_SECONDS} s: CTRL {m_bar.Read32(CTRL):x8}, STATUS {m_bar.Read32(STATUS):x8}.");
            Thread.Sleep(1);
        }
    }

    private static Span<byte> Descriptor(IDmaBuffer ring, int index) => ring.Bytes.Slice(index * E1000Descriptors.BYTES, E1000Descriptors.BYTES);

    #endregion

    #region IDisposable

    /// <summary>
    /// Masks the adapter's interrupts and resets it, so that it reads and writes no buffer any more, then gives back the
    /// interrupt, the arena and the regions.
    /// </summary>
    public void Dispose()
    {
        if (m_running)
        {
            m_bar.Write32(IMC, uint.MaxValue);
            m_bar.Write32(RCTL, 0);
            m_bar.Write32(TCTL, 0);
            m_bar.Write32(CTRL, m_bar.Read32(CTRL) | CTRL_MASTER_DISABLE);
            var deadline = Clock.MonotonicAfter(TIMEOUT_SECONDS);
            while ((m_bar.Read32(STATUS) & STATUS_MASTER_ENABLE) != 0 && Clock.Monotonic() < deadline)
                Thread.Sleep(1);
            m_bar.Write32(CTRL, m_bar.Read32(CTRL) | CTRL_RESET);
            Thread.Sleep(10);
            m_running = false;
        }
        m_msi?.Disable();
        m_binding.Dispose();
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
    /// What the interrupt is, for the driver's log.
    /// </summary>
    public string InterruptDescription { get; }

    /// <summary>
    /// The event the adapter's interrupt sets: a wait on it ends when the adapter received a frame, completed a transmit
    /// or saw its link change.
    /// </summary>
    public KernelHandle Interrupt => m_signaled;

    /// <summary>
    /// The adapter's MAC address, its first byte the lowest: what its NVM gave the receive address registers.
    /// </summary>
    public ulong Address { get; private set; }

    /// <summary>
    /// Whether the link is up, as STATUS reports it.
    /// </summary>
    public bool LinkUp => (m_bar.Read32(STATUS) & STATUS_LINK_UP) != 0;

    /// <summary>
    /// Whether the link runs full duplex, as STATUS reports it.
    /// </summary>
    public bool FullDuplex => (m_bar.Read32(STATUS) & STATUS_FULL_DUPLEX) != 0;

    /// <summary>
    /// The link's speed in Mb/s, as STATUS reports it.
    /// </summary>
    public int Speed => (m_bar.Read32(STATUS) >> 6 & 3) switch { 0 => 10, 1 => 100, _ => 1000 };

    /// <inheritdoc />
    public int Mtu => MTU;

    /// <summary>
    /// The frames waiting for the client.
    /// </summary>
    public int Pending => m_received.Count;

    /// <summary>
    /// The frames dropped because the client read slowly.
    /// </summary>
    public long Dropped { get; private set; }

    /// <summary>
    /// The receive overruns the adapter reported: frames it dropped for want of a buffer.
    /// </summary>
    public long Overruns { get; private set; }

    /// <summary>
    /// The pages of DMA memory the adapter's rings and buffers take.
    /// </summary>
    public int ArenaPages => ARENA_PAGES;

    #endregion
}
