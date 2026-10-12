using System;
using System.Buffers.Binary;
using System.IO;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioBlock.Virtio;

/// <summary>
/// The virtio block device's request queue (virtio 1.x, §5.2): queue 0 of eight entries in five pages the driver owns
/// and pins for the device — the descriptors, the available ring, the used ring, the request with its status, and the
/// data — driven by the device's INTx line bound to an event. One request at a time.
/// </summary>
internal sealed class VirtioBlockQueue : IDisposable
{
    #region Constants

    /// <summary>
    /// The bytes of a sector.
    /// </summary>
    public const int SECTOR = 512;

    private const ushort QUEUE_SIZE = 8;
    private const int PAGES = 5;
    private const int PAGE = MemoryObject.PAGE;
    private const int DESCRIPTORS = 0;
    private const int AVAILABLE = PAGE;
    private const int USED = 2 * PAGE;
    private const int REQUEST = 3 * PAGE;
    private const int DATA = 4 * PAGE;
    private const int STATUS = REQUEST + SECTOR;
    private const ushort NEXT = 1;
    private const ushort WRITE = 2;
    private const uint TYPE_IN = 0;

    // The common configuration's registers (virtio 1.x, §4.1.4.3).
    private const int DRIVER_FEATURE_SELECT = 8;
    private const int DRIVER_FEATURE = 12;
    private const int DEVICE_FEATURE_SELECT = 0;
    private const int DEVICE_FEATURE = 4;
    private const int DEVICE_STATUS = 20;
    private const int QUEUE_SELECT = 22;
    private const int QUEUE_SIZE_REGISTER = 24;
    private const int QUEUE_ENABLE = 28;
    private const int QUEUE_NOTIFY_OFF = 30;
    private const int QUEUE_DESCRIPTOR = 32;
    private const int QUEUE_DRIVER = 40;
    private const int QUEUE_DEVICE = 48;

    #endregion

    #region Fields

    private readonly VirtioPciTransport m_transport;
    private readonly KernelHandle m_memory;
    private readonly MemoryMapping m_pages;
    private readonly DmaPin[] m_pins;
    private readonly KernelHandle m_event;
    private readonly InterruptBinding m_binding;
    private readonly int m_notify;
    private ushort m_available;
    private ushort m_used;

    #endregion

    #region Constructors

    private VirtioBlockQueue(VirtioPciTransport transport, KernelHandle memory, MemoryMapping pages, DmaPin[] pins, KernelHandle signaled,
        InterruptBinding binding, int notify)
    {
        m_transport = transport;
        m_memory = memory;
        m_pages = pages;
        m_pins = pins;
        m_event = signaled;
        m_binding = binding;
        m_notify = notify;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Resets the device, negotiates VIRTIO_F_VERSION_1 alone, sets up queue 0 on pinned pages, binds the device's first
    /// line to an event and tells the device the driver is ready.
    /// </summary>
    /// <param name="transport">The device's transport.</param>
    /// <param name="device">The device, with BIND.</param>
    /// <returns>The queue.</returns>
    /// <exception cref="IOException">The device refused the features or offers too small a queue.</exception>
    public static VirtioBlockQueue Open(VirtioPciTransport transport, KernelHandle device)
    {
        var memory = MemoryObject.Create(PAGES * PAGE);
        var pages = MemoryMapping.Map(memory, 0, PAGES * PAGE, MemoryMapping.READ_WRITE);
        var pins = new DmaPin[PAGES];
        KernelHandle? signaled = null;
        InterruptBinding? binding = null;
        try
        {
            for (var i = 0; i < PAGES; i++)
                pins[i] = DmaPin.Pin(device, memory, i * PAGE, PAGE);
            var bar = transport.Bar;
            var common = transport.Common;
            bar.Write8(common + DEVICE_STATUS, 0);
            while (bar.Read8(common + DEVICE_STATUS) != 0)
            {
            }
            bar.Write8(common + DEVICE_STATUS, 3); // ACKNOWLEDGE | DRIVER
            bar.Write32(common + DEVICE_FEATURE_SELECT, 1);
            if ((bar.Read32(common + DEVICE_FEATURE) & 1) == 0)
                throw new IOException("The device does not offer VIRTIO_F_VERSION_1.");
            bar.Write32(common + DRIVER_FEATURE_SELECT, 1);
            bar.Write32(common + DRIVER_FEATURE, 1);
            bar.Write32(common + DRIVER_FEATURE_SELECT, 0);
            bar.Write32(common + DRIVER_FEATURE, 0);
            bar.Write8(common + DEVICE_STATUS, 0x0B); // | FEATURES_OK
            if ((bar.Read8(common + DEVICE_STATUS) & 8) == 0)
                throw new IOException("The device refused VIRTIO_F_VERSION_1 alone.");
            bar.Write16(common + QUEUE_SELECT, 0);
            if (bar.Read16(common + QUEUE_SIZE_REGISTER) < QUEUE_SIZE)
                throw new IOException($"The device's queue 0 holds fewer than {QUEUE_SIZE} entries.");
            bar.Write16(common + QUEUE_SIZE_REGISTER, QUEUE_SIZE);
            bar.Write64(common + QUEUE_DESCRIPTOR, pins[0].Address);
            bar.Write64(common + QUEUE_DRIVER, pins[1].Address);
            bar.Write64(common + QUEUE_DEVICE, pins[2].Address);
            var notify = transport.Notify + (int)(bar.Read16(common + QUEUE_NOTIFY_OFF) * transport.NotifyMultiplier);
            bar.Write16(common + QUEUE_ENABLE, 1);
            bar.Write8(common + DEVICE_STATUS, 0x0F); // | DRIVER_OK
            signaled = Event.Create();
            binding = InterruptBinding.Bind(device, 0, signaled);
            return new VirtioBlockQueue(transport, memory, pages, pins, signaled, binding, notify);
        }
        catch
        {
            binding?.Dispose();
            signaled?.Dispose();
            foreach (var pin in pins)
                pin?.Dispose();
            pages.Dispose();
            memory.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads one sector through the queue: three descriptors (the request, the data, the status), a notification, and
    /// the interrupt that completes it.
    /// </summary>
    /// <param name="sector">The sector's number.</param>
    /// <returns>Its bytes.</returns>
    /// <exception cref="IOException">The device completed another request or reported an error.</exception>
    public byte[] Read(ulong sector)
    {
        var pages = m_pages.Writable;
        BinaryPrimitives.WriteUInt32LittleEndian(pages[REQUEST..], TYPE_IN);
        BinaryPrimitives.WriteUInt32LittleEndian(pages[(REQUEST + 4)..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(pages[(REQUEST + 8)..], sector);
        pages[STATUS] = 0xFF;
        Describe(pages, 0, m_pins[3].Address, 16, NEXT, 1);
        Describe(pages, 1, m_pins[4].Address, SECTOR, NEXT | WRITE, 2);
        Describe(pages, 2, m_pins[3].Address + SECTOR, 1, WRITE, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(pages[(AVAILABLE + 4 + m_available % QUEUE_SIZE * 2)..], 0); // the head
        MmioRegion.Barrier();
        BinaryPrimitives.WriteUInt16LittleEndian(pages[(AVAILABLE + 2)..], ++m_available);
        MmioRegion.Barrier();
        m_transport.Bar.Write16(m_notify, 0);
        Event.Wait(Event.INFINITE, m_event);
        var isr = m_transport.Bar.Read8(m_transport.Isr); // reading it ends the interrupt's cause
        var used = BinaryPrimitives.ReadUInt16LittleEndian(m_pages.Bytes[(USED + 2)..]);
        var id = BinaryPrimitives.ReadUInt32LittleEndian(m_pages.Bytes[(USED + 4 + m_used % QUEUE_SIZE * 8)..]);
        m_binding.Acknowledge();
        if ((isr & 1) == 0 || used != (ushort)(m_used + 1) || id != 0)
            throw new IOException($"The device completed no request of ours: ISR {isr}, used {used}, head {id}.");
        m_used = used;
        if (m_pages.Bytes[STATUS] != 0)
            throw new IOException($"The device answered sector {sector} with status {m_pages.Bytes[STATUS]}.");
        return m_pages.Bytes.Slice(DATA, SECTOR).ToArray();
    }

    private static void Describe(Span<byte> pages, int index, ulong address, uint length, ushort flags, ushort next)
    {
        var descriptor = pages[(DESCRIPTORS + index * 16)..];
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor, address);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], length);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[12..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[14..], next);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Quiesces the device, then gives back the binding, the event, the pins and the pages.
    /// </summary>
    public void Dispose()
    {
        m_transport.Bar.Write8(m_transport.Common + DEVICE_STATUS, 0);
        m_binding.Dispose();
        m_event.Dispose();
        foreach (var pin in m_pins)
            pin.Dispose();
        m_pages.Dispose();
        m_memory.Dispose();
    }

    #endregion
}
