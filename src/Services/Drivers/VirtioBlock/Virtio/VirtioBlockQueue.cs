using System;
using System.Buffers.Binary;
using System.IO;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioBlock.Virtio;

/// <summary>
/// The virtio block device's request queue (virtio 1.x, §5.2): queue 0 of up to 128 entries in five pages the driver
/// owns and pins for the device as one window — the descriptors, the available ring, the used ring, the request header
/// with its status, and a sector of its own — driven by the device's INTx line bound to an event. A request's data are
/// segments of physical memory, the driver's own sector or a client's pinned window (plan step D3.2). One request at a
/// time.
/// </summary>
internal sealed class VirtioBlockQueue : IDisposable
{
    #region Constants

    /// <summary>
    /// The bytes of a virtio sector, the unit of a request's address whatever the device's block.
    /// </summary>
    public const int SECTOR = 512;

    /// <summary>
    /// VIRTIO_BLK_T_IN: the device writes the segments.
    /// </summary>
    public const uint TYPE_IN = 0;

    /// <summary>
    /// VIRTIO_BLK_T_OUT: the device reads the segments.
    /// </summary>
    public const uint TYPE_OUT = 1;

    /// <summary>
    /// VIRTIO_BLK_T_FLUSH: no segments.
    /// </summary>
    public const uint TYPE_FLUSH = 4;

    private const ushort LARGEST_QUEUE = 128;
    private const int PAGES = 5;
    private const int PAGE = MemoryObject.PAGE;
    private const int DESCRIPTORS = 0;
    private const int AVAILABLE = PAGE;
    private const int USED = 2 * PAGE;
    private const int REQUEST = 3 * PAGE;
    private const int STATUS = REQUEST + 16;
    private const int OWN_SECTOR = 4 * PAGE;
    private const ushort NEXT = 1;
    private const ushort WRITE = 2;

    // The feature bits the driver accepts when the device offers them (virtio 1.x, §5.2.3); VIRTIO_F_VERSION_1 is bit 32.
    private const uint SIZE_MAX = 1U << 1;
    private const uint SEG_MAX = 1U << 2;
    private const uint READ_ONLY = 1U << 5;
    private const uint BLOCK_SIZE = 1U << 6;
    private const uint FLUSH = 1U << 9;
    private const uint ACCEPTED = SIZE_MAX | SEG_MAX | READ_ONLY | BLOCK_SIZE | FLUSH;

    // The common configuration's registers (virtio 1.x, §4.1.4.3).
    private const int DRIVER_FEATURE_SELECT = 8;
    private const int DRIVER_FEATURE = 12;
    private const int DEVICE_FEATURE_SELECT = 0;
    private const int DEVICE_FEATURE = 4;
    private const int DEVICE_STATUS = 20;
    private const int CONFIG_GENERATION = 21;
    private const int QUEUE_SELECT = 22;
    private const int QUEUE_SIZE_REGISTER = 24;
    private const int QUEUE_ENABLE = 28;
    private const int QUEUE_NOTIFY_OFF = 30;
    private const int QUEUE_DESCRIPTOR = 32;
    private const int QUEUE_DRIVER = 40;
    private const int QUEUE_DEVICE = 48;

    // The block device's configuration (virtio 1.x, §5.2.4).
    private const int CAPACITY = 0;
    private const int SIZE_MAX_FIELD = 8;
    private const int SEG_MAX_FIELD = 12;
    private const int BLOCK_SIZE_FIELD = 20;

    #endregion

    #region Fields

    private readonly VirtioPciTransport m_transport;
    private readonly KernelHandle m_memory;
    private readonly MemoryMapping m_pages;
    private readonly DmaPin m_pin;
    private readonly KernelHandle m_event;
    private readonly InterruptBinding m_binding;
    private readonly int m_notify;
    private readonly ushort m_size;
    private ushort m_available;
    private ushort m_used;

    #endregion

    #region Constructors

    private VirtioBlockQueue(VirtioPciTransport transport, KernelHandle memory, MemoryMapping pages, DmaPin pin, KernelHandle signaled,
        InterruptBinding binding, int notify, ushort size, uint features, DeviceConfiguration configuration)
    {
        m_transport = transport;
        m_memory = memory;
        m_pages = pages;
        m_pin = pin;
        m_event = signaled;
        m_binding = binding;
        m_notify = notify;
        m_size = size;
        IsReadOnly = (features & READ_ONLY) != 0;
        Flushes = (features & FLUSH) != 0;
        Capacity = configuration.Capacity;
        BlockSize = configuration.BlockSize;
        MaxSegments = Math.Min(configuration.MaxSegments, size - 2);
        MaxSegmentBytes = configuration.MaxSegmentBytes;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Resets the device, negotiates VIRTIO_F_VERSION_1 with what the device offers of SIZE_MAX, SEG_MAX, RO, BLK_SIZE and
    /// FLUSH, reads its capacity and limits, sets up queue 0 on pinned pages, binds the device's first line to an event
    /// and tells the device the driver is ready.
    /// </summary>
    /// <param name="transport">The device's transport.</param>
    /// <param name="device">The device, with BIND.</param>
    /// <returns>The queue.</returns>
    /// <exception cref="IOException">The device refused the features, offers too small a queue or reports a configuration
    /// the driver cannot serve.</exception>
    public static VirtioBlockQueue Open(VirtioPciTransport transport, KernelHandle device)
    {
        var memory = MemoryObject.Create(PAGES * PAGE);
        var pages = MemoryMapping.Map(memory, 0, PAGES * PAGE, MemoryMapping.READ_WRITE);
        DmaPin? pin = null;
        KernelHandle? signaled = null;
        InterruptBinding? binding = null;
        try
        {
            pin = DmaPin.Pin(device, memory, 0, PAGES * PAGE);
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
            bar.Write32(common + DEVICE_FEATURE_SELECT, 0);
            var features = bar.Read32(common + DEVICE_FEATURE) & ACCEPTED;
            bar.Write32(common + DRIVER_FEATURE_SELECT, 1);
            bar.Write32(common + DRIVER_FEATURE, 1);
            bar.Write32(common + DRIVER_FEATURE_SELECT, 0);
            bar.Write32(common + DRIVER_FEATURE, features);
            bar.Write8(common + DEVICE_STATUS, 0x0B); // | FEATURES_OK
            if ((bar.Read8(common + DEVICE_STATUS) & 8) == 0)
                throw new IOException($"The device refused the features {features:x} with VIRTIO_F_VERSION_1.");
            var configuration = DeviceConfiguration.Read(transport, features);
            bar.Write16(common + QUEUE_SELECT, 0);
            var offered = bar.Read16(common + QUEUE_SIZE_REGISTER);
            if (offered < 3)
                throw new IOException($"The device's queue 0 holds {offered} entries, fewer than a request's three.");
            var size = Math.Min(offered, LARGEST_QUEUE);
            bar.Write16(common + QUEUE_SIZE_REGISTER, size);
            bar.Write64(common + QUEUE_DESCRIPTOR, pin.RunAt(DESCRIPTORS).Address);
            bar.Write64(common + QUEUE_DRIVER, pin.RunAt(AVAILABLE).Address);
            bar.Write64(common + QUEUE_DEVICE, pin.RunAt(USED).Address);
            var notify = transport.Notify + (int)(bar.Read16(common + QUEUE_NOTIFY_OFF) * transport.NotifyMultiplier);
            bar.Write16(common + QUEUE_ENABLE, 1);
            bar.Write8(common + DEVICE_STATUS, 0x0F); // | DRIVER_OK
            signaled = Event.Create();
            binding = InterruptBinding.Bind(device, 0, signaled);
            return new VirtioBlockQueue(transport, memory, pages, pin, signaled, binding, notify, size, features, configuration);
        }
        catch
        {
            binding?.Dispose();
            signaled?.Dispose();
            pin?.Dispose();
            pages.Dispose();
            memory.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads one sector into the queue's own page.
    /// </summary>
    /// <param name="sector">The sector's number.</param>
    /// <returns>Its bytes.</returns>
    /// <exception cref="IOException">The device completed another request or reported an error.</exception>
    public byte[] Read(ulong sector)
    {
        Submit(TYPE_IN, sector, [(m_pin.RunAt(OWN_SECTOR).Address, SECTOR)]);
        return m_pages.Bytes.Slice(OWN_SECTOR, SECTOR).ToArray();
    }

    /// <summary>
    /// Serves one request through the queue: the header, the data's segments, the status, a notification, and the
    /// interrupt that completes it.
    /// </summary>
    /// <param name="type">TYPE_IN, TYPE_OUT or TYPE_FLUSH.</param>
    /// <param name="sector">The first sector; zero for a flush.</param>
    /// <param name="segments">The data's physical segments, at most MaxSegments of at most MaxSegmentBytes each.</param>
    /// <exception cref="IOException">The device completed another request or reported an error.</exception>
    public void Submit(uint type, ulong sector, ReadOnlySpan<(ulong Address, int Length)> segments)
    {
        if (segments.Length > MaxSegments)
            throw new IOException($"A request of {segments.Length} segments exceeds the device's {MaxSegments}.");
        var pages = m_pages.Writable;
        BinaryPrimitives.WriteUInt32LittleEndian(pages[REQUEST..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(pages[(REQUEST + 4)..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(pages[(REQUEST + 8)..], sector);
        pages[STATUS] = 0xFF;
        var header = m_pin.RunAt(REQUEST).Address;
        Describe(pages, 0, header, 16, NEXT, 1);
        var data = type == TYPE_IN ? (ushort)(NEXT | WRITE) : NEXT;
        for (var i = 0; i < segments.Length; i++)
            Describe(pages, i + 1, segments[i].Address, (uint)segments[i].Length, data, (ushort)(i + 2));
        Describe(pages, segments.Length + 1, header + 16, 1, WRITE, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(pages[(AVAILABLE + 4 + m_available % m_size * 2)..], 0); // the head
        MmioRegion.Barrier();
        BinaryPrimitives.WriteUInt16LittleEndian(pages[(AVAILABLE + 2)..], ++m_available);
        MmioRegion.Barrier();
        m_transport.Bar.Write16(m_notify, 0);
        Event.Wait(Event.INFINITE, m_event);
        var isr = m_transport.Bar.Read8(m_transport.Isr); // reading it ends the interrupt's cause
        var used = BinaryPrimitives.ReadUInt16LittleEndian(m_pages.Bytes[(USED + 2)..]);
        var id = BinaryPrimitives.ReadUInt32LittleEndian(m_pages.Bytes[(USED + 4 + m_used % m_size * 8)..]);
        m_binding.Acknowledge();
        if ((isr & 1) == 0 || used != (ushort)(m_used + 1) || id != 0)
            throw new IOException($"The device completed no request of ours: ISR {isr}, used {used}, head {id}.");
        m_used = used;
        if (m_pages.Bytes[STATUS] is var status and not 0)
            throw new IOException($"the device failed a {(type == TYPE_IN ? "read" : type == TYPE_OUT ? "write" : type == TYPE_FLUSH ? "flush" : $"request of type {type}")}" +
                $"{(type == TYPE_FLUSH ? "" : $" at sector {sector}")}: status {status} " +
                $"({(status == 1 ? "VIRTIO_BLK_S_IOERR" : status == 2 ? "VIRTIO_BLK_S_UNSUPP" : "unknown")})");
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
    /// Quiesces the device, then gives back the binding, the event, the pin and the pages.
    /// </summary>
    public void Dispose()
    {
        m_transport.Bar.Write8(m_transport.Common + DEVICE_STATUS, 0);
        m_binding.Dispose();
        m_event.Dispose();
        m_pin.Dispose();
        m_pages.Dispose();
        m_memory.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The device's bytes.
    /// </summary>
    public long Capacity { get; }

    /// <summary>
    /// The device's logical block, a multiple of SECTOR: what BLK_SIZE reports, otherwise SECTOR.
    /// </summary>
    public int BlockSize { get; }

    /// <summary>
    /// Whether the device offered RO: it refuses writes.
    /// </summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// Whether FLUSH was negotiated: the device has a volatile cache a flush empties.
    /// </summary>
    public bool Flushes { get; }

    /// <summary>
    /// The data segments one request may carry: what SEG_MAX reports, within the queue less the header and the status.
    /// </summary>
    public int MaxSegments { get; }

    /// <summary>
    /// The bytes of one segment: what SIZE_MAX reports, at least a page.
    /// </summary>
    public long MaxSegmentBytes { get; }

    #endregion

    #region Nested Types

    // The block device's configuration, read until its generation holds still (virtio 1.x, §4.1.4.3.1): 64-bit fields
    // are read as two 32-bit halves, as the transport requires.
    private readonly record struct DeviceConfiguration(long Capacity, int BlockSize, int MaxSegments, long MaxSegmentBytes)
    {
        public static DeviceConfiguration Read(VirtioPciTransport transport, uint features)
        {
            var bar = transport.Bar;
            var common = transport.Common;
            var at = transport.DeviceConfiguration;
            for (; ; )
            {
                var generation = bar.Read8(common + CONFIG_GENERATION);
                var sectors = bar.Read32(at + CAPACITY) | (ulong)bar.Read32(at + CAPACITY + 4) << 32;
                var blockSize = (features & BLOCK_SIZE) != 0 ? bar.Read32(at + BLOCK_SIZE_FIELD) : SECTOR;
                var segments = (features & SEG_MAX) != 0 ? bar.Read32(at + SEG_MAX_FIELD) : LARGEST_QUEUE;
                var segmentBytes = (features & SIZE_MAX) != 0 ? bar.Read32(at + SIZE_MAX_FIELD) : uint.MaxValue;
                if (bar.Read8(common + CONFIG_GENERATION) != generation)
                    continue;
                if (blockSize < SECTOR || blockSize % SECTOR != 0 || segments == 0 || segmentBytes < PAGE ||
                    sectors > long.MaxValue / SECTOR)
                    throw new IOException($"The device reports {sectors} sectors in blocks of {blockSize}, {segments} segments of at most " +
                        $"{segmentBytes} bytes.");
                return new DeviceConfiguration((long)sectors * SECTOR, (int)blockSize, (int)Math.Min(segments, LARGEST_QUEUE), segmentBytes);
            }
        }
    }

    #endregion
}
