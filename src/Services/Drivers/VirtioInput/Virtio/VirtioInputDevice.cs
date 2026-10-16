using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Virtio;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioInput.Virtio;

/// <summary>
/// A virtio input device (virtio 1.x, §5.8): queue 0 brings events, each an 8-byte record the device writes into one
/// of the driver's buffers, and queue 1 takes the driver's (LEDs, which this driver never sets); the configuration is
/// read through its select and subsel registers, one selection at a time. Both queues lie on pinned pages the driver
/// owns, the event buffers beside them; the device's INTx line is bound to an event. One driver thread at a time.
/// </summary>
internal sealed class VirtioInputDevice : IDisposable
{
    #region Constants

    /// <summary>
    /// EV_KEY: a key's press (1), release (0) or repeat (2).
    /// </summary>
    public const ushort EVENT_KEY = 1;

    /// <summary>
    /// EV_SYN: the end of a report.
    /// </summary>
    public const ushort EVENT_SYN = 0;

    private const int PAGE = MemoryObject.PAGE;
    private const int PAGES = 8;
    private const int EVENT_QUEUE = 0;
    private const int STATUS_QUEUE = Virtqueue.PAGES * PAGE;
    private const int EVENT_BUFFERS = 2 * Virtqueue.PAGES * PAGE;
    private const int EVENT_BYTES = 8;
    private const int EVENT_COUNT = 64; // one page holds 512; the device offers 64 entries
    private const int LEAST_EVENTS = 8;

    // The configuration (virtio 1.x, §5.8.3): select, subsel, the size the selection filled, then 128 bytes of it.
    private const int SELECT = 0;
    private const int SUBSEL = 1;
    private const int SIZE = 2;
    private const int PAYLOAD = 8;
    private const int PAYLOAD_BYTES = 128;
    private const byte CFG_UNSET = 0;
    private const byte CFG_ID_NAME = 1;
    private const byte CFG_ID_DEVIDS = 3;
    private const byte CFG_EV_BITS = 0x11;

    #endregion

    #region Fields

    private readonly VirtioPciTransport m_transport;
    private readonly KernelHandle m_memory;
    private readonly MemoryMapping m_pages;
    private readonly DmaPin m_pin;
    private readonly KernelHandle m_event;
    private readonly InterruptBinding m_binding;
    private readonly Virtqueue m_events;
    private readonly Virtqueue m_status;
    private readonly int m_buffers;

    #endregion

    #region Constructors

    private VirtioInputDevice(VirtioPciTransport transport, KernelHandle memory, MemoryMapping pages, DmaPin pin, KernelHandle signaled,
        InterruptBinding binding, Virtqueue events, Virtqueue status, int buffers, string name, (int Bus, int Vendor, int Product, int Version) ids,
        int keys)
    {
        m_transport = transport;
        m_memory = memory;
        m_pages = pages;
        m_pin = pin;
        m_event = signaled;
        m_binding = binding;
        m_events = events;
        m_status = status;
        m_buffers = buffers;
        Name = name;
        Ids = ids;
        Keys = keys;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Resets the device, negotiates VIRTIO_F_VERSION_1 alone, reads its name, its identifiers and the keys it reports,
    /// sets up both queues on pinned pages, binds the device's first line to an event, tells the device the driver is
    /// ready and gives the device every event buffer.
    /// </summary>
    /// <param name="transport">The device's transport.</param>
    /// <param name="device">The device, with BIND.</param>
    /// <returns>The device.</returns>
    /// <exception cref="IOException">The device refused the features or offers too small a queue.</exception>
    public static VirtioInputDevice Open(VirtioPciTransport transport, KernelHandle device)
    {
        var memory = MemoryObject.Create(PAGES * PAGE);
        var pages = MemoryMapping.Map(memory, 0, PAGES * PAGE, MemoryMapping.READ_WRITE);
        DmaPin? pin = null;
        KernelHandle? signaled = null;
        InterruptBinding? binding = null;
        try
        {
            pin = DmaPin.Pin(device, memory, 0, PAGES * PAGE);
            transport.Reset();
            transport.Negotiate(0);
            var name = ReadString(transport, CFG_ID_NAME, 0);
            var ids = ReadIds(transport);
            var keys = CountBits(ReadPayload(transport, CFG_EV_BITS, (byte)EVENT_KEY));
            Select(transport, CFG_UNSET, 0);
            var events = Virtqueue.Open(transport, 0, pages, pin, EVENT_QUEUE, LEAST_EVENTS);
            var status = Virtqueue.Open(transport, 1, pages, pin, STATUS_QUEUE, 1);
            transport.DriverReady();
            signaled = Event.Create();
            binding = InterruptBinding.Bind(device, 0, signaled);
            var buffers = Math.Min(EVENT_COUNT, (int)events.Size);
            var input = new VirtioInputDevice(transport, memory, pages, pin, signaled, binding, events, status, buffers, name, ids, keys);
            for (var i = 0; i < buffers; i++)
            {
                events.Describe(i, pin.RunAt(EVENT_BUFFERS + i * EVENT_BYTES).Address, EVENT_BYTES, Virtqueue.WRITE, 0);
                events.Offer((ushort)i);
            }
            events.Notify();
            return input;
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
    /// Serves the interrupt the event reports: ends its cause, takes the events the device wrote and gives their buffers
    /// back.
    /// </summary>
    /// <returns>The events, in the device's order: the type, the code and the value of each.</returns>
    public IReadOnlyList<(ushort Type, ushort Code, int Value)> Service()
    {
        _ = m_transport.ReadInterruptStatus();
        var events = new List<(ushort, ushort, int)>();
        var offered = false;
        while (m_events.TryComplete(out var head, out var length))
        {
            if (head < m_buffers && length >= EVENT_BYTES)
            {
                var record = m_pages.Bytes.Slice(EVENT_BUFFERS + head * EVENT_BYTES, EVENT_BYTES);
                events.Add((BinaryPrimitives.ReadUInt16LittleEndian(record), BinaryPrimitives.ReadUInt16LittleEndian(record[2..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[4..])));
            }
            if (head < m_buffers)
            {
                m_events.Offer(head);
                offered = true;
            }
        }
        if (offered)
            m_events.Notify();
        while (m_status.TryComplete(out _, out _))
        {
        }
        m_binding.Acknowledge();
        return events;
    }

    // The payload of a selection: the size the device reports, at most the payload's bytes; empty for a selection the
    // device does not serve.
    private static byte[] ReadPayload(VirtioPciTransport transport, byte select, byte subsel)
    {
        Select(transport, select, subsel);
        var bar = transport.Bar;
        var at = transport.DeviceConfiguration;
        var size = Math.Min((int)bar.Read8(at + SIZE), PAYLOAD_BYTES);
        var payload = new byte[size];
        for (var i = 0; i < size; i++)
            payload[i] = bar.Read8(at + PAYLOAD + i);
        return payload;
    }

    private static void Select(VirtioPciTransport transport, byte select, byte subsel)
    {
        var bar = transport.Bar;
        var at = transport.DeviceConfiguration;
        bar.Write8(at + SELECT, select);
        bar.Write8(at + SUBSEL, subsel);
    }

    private static string ReadString(VirtioPciTransport transport, byte select, byte subsel)
    {
        var payload = ReadPayload(transport, select, subsel);
        var end = Array.IndexOf(payload, (byte)0);
        return Encoding.ASCII.GetString(payload, 0, end < 0 ? payload.Length : end);
    }

    private static (int Bus, int Vendor, int Product, int Version) ReadIds(VirtioPciTransport transport)
    {
        var payload = ReadPayload(transport, CFG_ID_DEVIDS, 0);
        if (payload.Length < 8)
            return (0, 0, 0, 0);
        return (BinaryPrimitives.ReadUInt16LittleEndian(payload), BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2)),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4)), BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6)));
    }

    private static int CountBits(byte[] bitmap)
    {
        var count = 0;
        foreach (var value in bitmap)
            count += System.Numerics.BitOperations.PopCount(value);
        return count;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Quiesces the device, so that it writes no buffer after the pin goes, and releases everything.
    /// </summary>
    public void Dispose()
    {
        m_transport.Quiesce();
        m_binding.Dispose();
        m_event.Dispose();
        m_pin.Dispose();
        m_pages.Dispose();
        m_memory.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The event the device's line sets.
    /// </summary>
    public KernelHandle Interrupt => m_event;

    /// <summary>
    /// The device's name, as it reports it (QEMU Virtio Keyboard).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The device's bus type, vendor, product and version.
    /// </summary>
    public (int Bus, int Vendor, int Product, int Version) Ids { get; }

    /// <summary>
    /// The keys the device reports in its EV_KEY bits.
    /// </summary>
    public int Keys { get; }

    /// <summary>
    /// The event queue's entries.
    /// </summary>
    public int QueueSize => m_events.Size;

    #endregion
}
