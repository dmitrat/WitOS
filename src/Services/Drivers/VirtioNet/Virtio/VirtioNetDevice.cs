using System;
using System.Collections.Generic;
using System.IO;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Virtio;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioNet.Virtio;

/// <summary>
/// The virtio network device (virtio 1.x, §5.1) with one queue pair and no offloads: queue 0 receives and queue 1
/// transmits, each on three pages of the window the driver owns and pins for the device, beside the buffers of
/// RECEIVE_COUNT received frames and TRANSMIT_COUNT transmitted ones, of BUFFER bytes each: the virtio-net header of 12
/// bytes (VIRTIO_F_VERSION_1's, with its buffer count) and a frame of at most 1514 bytes. Its INTx line is bound to an
/// event. The frames the device received wait in the driver's own queue, at most PacketMessages.QUEUE of them, until the
/// client takes them. One driver thread at a time.
/// </summary>
internal sealed class VirtioNetDevice : IDisposable
{
    #region Constants

    /// <summary>
    /// The largest payload of a frame without VIRTIO_NET_F_MTU: Ethernet's.
    /// </summary>
    public const int MTU = 1500;

    private const int PAGE = MemoryObject.PAGE;
    private const int HEADER = 12;
    private const int BUFFER = 2048;
    private const int RECEIVE_COUNT = 32;
    private const int TRANSMIT_COUNT = 16;
    private const int RECEIVE_QUEUE = 0;
    private const int TRANSMIT_QUEUE = Virtqueue.PAGES * PAGE;
    private const int RECEIVE_BUFFERS = 2 * Virtqueue.PAGES * PAGE;
    private const int TRANSMIT_BUFFERS = RECEIVE_BUFFERS + RECEIVE_COUNT * BUFFER;
    private const int PAGES = (TRANSMIT_BUFFERS + TRANSMIT_COUNT * BUFFER) / PAGE;

    // VIRTIO_NET_F_MAC and VIRTIO_NET_F_STATUS (virtio 1.x, §5.1.3): the configuration holds the address and the link.
    private const uint MAC = 1U << 5;
    private const uint STATUS = 1U << 16;
    private const uint ACCEPTED = MAC | STATUS;

    // The configuration (virtio 1.x, §5.1.4): the address, then the status, whose bit 0 is VIRTIO_NET_S_LINK_UP.
    private const int CONFIG_MAC = 0;
    private const int CONFIG_STATUS = 6;
    private const ushort LINK_UP = 1;

    #endregion

    #region Fields

    private readonly VirtioPciTransport m_transport;
    private readonly KernelHandle m_memory;
    private readonly MemoryMapping m_pages;
    private readonly DmaPin m_pin;
    private readonly KernelHandle m_event;
    private readonly InterruptBinding m_binding;
    private readonly Virtqueue m_receive;
    private readonly Virtqueue m_transmit;
    private readonly uint m_features;
    private readonly Queue<byte[]> m_received = new();
    private readonly Stack<ushort> m_free = new();

    #endregion

    #region Constructors

    private VirtioNetDevice(VirtioPciTransport transport, KernelHandle memory, MemoryMapping pages, DmaPin pin, KernelHandle signaled,
        InterruptBinding binding, Virtqueue receive, Virtqueue transmit, uint features, ulong address)
    {
        m_transport = transport;
        m_memory = memory;
        m_pages = pages;
        m_pin = pin;
        m_event = signaled;
        m_binding = binding;
        m_receive = receive;
        m_transmit = transmit;
        m_features = features;
        Address = address;
        for (var i = TRANSMIT_COUNT - 1; i >= 0; i--)
            m_free.Push((ushort)i);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Resets the device, negotiates VIRTIO_F_VERSION_1 with MAC, which it must offer, and STATUS when offered, reads the
    /// address, sets up both queues on pinned pages, binds the device's first line to an event, tells the device the driver
    /// is ready and gives the device every receive buffer.
    /// </summary>
    /// <param name="transport">The device's transport.</param>
    /// <param name="device">The device, with BIND.</param>
    /// <returns>The device.</returns>
    /// <exception cref="IOException">The device refused the features, offers no address or too small a queue.</exception>
    public static VirtioNetDevice Open(VirtioPciTransport transport, KernelHandle device)
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
            var features = transport.Negotiate(ACCEPTED);
            if ((features & MAC) == 0)
                throw new IOException("The device offers no address (VIRTIO_NET_F_MAC).");
            var address = ReadAddress(transport);
            var receive = Virtqueue.Open(transport, 0, pages, pin, RECEIVE_QUEUE, RECEIVE_COUNT);
            var transmit = Virtqueue.Open(transport, 1, pages, pin, TRANSMIT_QUEUE, TRANSMIT_COUNT);
            transport.DriverReady();
            signaled = Event.Create();
            binding = InterruptBinding.Bind(device, 0, signaled);
            var adapter = new VirtioNetDevice(transport, memory, pages, pin, signaled, binding, receive, transmit, features, address);
            for (var i = 0; i < RECEIVE_COUNT; i++)
            {
                receive.Describe(i, pin.RunAt(RECEIVE_BUFFERS + i * BUFFER).Address, BUFFER, Virtqueue.WRITE, 0);
                receive.Offer((ushort)i);
            }
            receive.Notify();
            return adapter;
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
    /// Serves the interrupt the event reports: ends its cause, takes what the device received into the frame queue, gives
    /// the device its buffers back and frees the transmit buffers it completed.
    /// </summary>
    public void Service()
    {
        _ = m_transport.ReadInterruptStatus();
        Collect();
        m_binding.Acknowledge();
    }

    /// <summary>
    /// Transmits frames, each in a transmit buffer behind a zeroed virtio-net header, waiting for the device to free a
    /// buffer when none is left; what the device receives meanwhile waits in the frame queue. Returns once the device
    /// took every frame.
    /// </summary>
    /// <param name="frames">The frames, each of 14 to 14 + MTU bytes.</param>
    public void Transmit(IReadOnlyList<ReadOnlyMemory<byte>> frames)
    {
        var offered = false;
        foreach (var frame in frames)
        {
            while (m_free.Count == 0)
            {
                if (offered)
                {
                    m_transmit.Notify();
                    offered = false;
                }
                Event.Wait(Event.INFINITE, m_event);
                Service();
            }
            var buffer = m_free.Pop();
            var at = TRANSMIT_BUFFERS + buffer * BUFFER;
            m_pages.Writable.Slice(at, HEADER).Clear();
            frame.Span.CopyTo(m_pages.Writable[(at + HEADER)..]);
            m_transmit.Describe(buffer, m_pin.RunAt(at).Address, (uint)(HEADER + frame.Length), 0, 0);
            m_transmit.Offer(buffer);
            offered = true;
        }
        if (offered)
            m_transmit.Notify();
        while (m_free.Count < TRANSMIT_COUNT)
        {
            Event.Wait(Event.INFINITE, m_event);
            Service();
        }
    }

    /// <summary>
    /// Takes the oldest frame the device received, if any.
    /// </summary>
    /// <returns>The frame, or null when none waits.</returns>
    public byte[]? Take() => m_received.Count > 0 ? m_received.Dequeue() : null;

    /// <summary>
    /// The length of the oldest frame waiting, or zero.
    /// </summary>
    /// <returns>Its bytes.</returns>
    public int PeekLength() => m_received.Count > 0 ? m_received.Peek().Length : 0;

    // What the device completed: received frames into the queue (beyond its room the newest are dropped, as a full ring
    // drops them) and their buffers given back; transmit buffers freed.
    private void Collect()
    {
        var offered = false;
        while (m_receive.TryComplete(out var head, out var length))
        {
            if (length > HEADER && length <= BUFFER)
            {
                if (m_received.Count < PacketMessages.QUEUE)
                    m_received.Enqueue(m_pages.Bytes.Slice(RECEIVE_BUFFERS + head * BUFFER + HEADER, (int)length - HEADER).ToArray());
                else
                    Dropped++;
            }
            m_receive.Offer(head);
            offered = true;
        }
        if (offered)
            m_receive.Notify();
        while (m_transmit.TryComplete(out var buffer, out _))
            m_free.Push(buffer);
    }

    private static ulong ReadAddress(VirtioPciTransport transport)
    {
        var bar = transport.Bar;
        var at = transport.DeviceConfiguration + CONFIG_MAC;
        for (; ; )
        {
            var generation = transport.ConfigurationGeneration;
            ulong address = 0;
            for (var i = 0; i < 6; i++)
                address |= (ulong)bar.Read8(at + i) << (8 * i);
            if (transport.ConfigurationGeneration == generation)
                return address;
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Quiesces the device, then gives back the binding, the event, the pin and the pages.
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
    /// The event the device's line sets: a wait on it ends when the device received a frame or completed a transmit.
    /// </summary>
    public KernelHandle Interrupt => m_event;

    /// <summary>
    /// The adapter's MAC address, its first byte the lowest.
    /// </summary>
    public ulong Address { get; }

    /// <summary>
    /// Whether the link is up: as the device reports with STATUS, and up without it.
    /// </summary>
    public bool LinkUp
    {
        get
        {
            if ((m_features & STATUS) == 0)
                return true;
            for (; ; )
            {
                var generation = m_transport.ConfigurationGeneration;
                var status = m_transport.Bar.Read16(m_transport.DeviceConfiguration + CONFIG_STATUS);
                if (m_transport.ConfigurationGeneration == generation)
                    return (status & LINK_UP) != 0;
            }
        }
    }

    /// <summary>
    /// The frames waiting for the client.
    /// </summary>
    public int Pending => m_received.Count;

    /// <summary>
    /// The frames dropped because the client read slowly.
    /// </summary>
    public long Dropped { get; private set; }

    /// <summary>
    /// The entries of the receive and the transmit queue.
    /// </summary>
    public (int Receive, int Transmit) QueueSizes => (m_receive.Size, m_transmit.Size);

    #endregion
}
