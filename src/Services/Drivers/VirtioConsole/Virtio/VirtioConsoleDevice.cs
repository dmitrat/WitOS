using System;
using System.IO;
using OutWit.OS.Hardware;
using OutWit.OS.Hardware.Virtio;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioConsole.Virtio;

/// <summary>
/// The virtio console device (virtio 1.x, §5.3) with one port and no MULTIPORT: queue 0 receives and queue 1 transmits,
/// each on three pages of the eight the driver owns and pins for the device as one window, beside a page of receive
/// buffers the device fills and a page the driver transmits from; its INTx line is bound to an event. The bytes the
/// device received wait in the driver's own queue until the client reads them. One driver thread at a time.
/// </summary>
internal sealed class VirtioConsoleDevice : IDisposable
{
    #region Constants

    /// <summary>
    /// The bytes one transmit takes at most: the transmit page.
    /// </summary>
    public const int TRANSMIT_BYTES = MemoryObject.PAGE;

    private const int PAGE = MemoryObject.PAGE;
    private const int PAGES = 8;
    private const int RECEIVE_QUEUE = 0;
    private const int TRANSMIT_QUEUE = Virtqueue.PAGES * PAGE;
    private const int RECEIVE_BUFFERS = 2 * Virtqueue.PAGES * PAGE;
    private const int TRANSMIT_BUFFER = RECEIVE_BUFFERS + PAGE;
    private const int RECEIVE_BUFFER = 256;
    private const int RECEIVE_COUNT = PAGE / RECEIVE_BUFFER;
    private const int INPUT_BYTES = 16384;

    // VIRTIO_CONSOLE_F_SIZE (virtio 1.x, §5.3.3): the configuration reports columns and rows.
    private const uint SIZE = 1U << 0;
    private const uint ACCEPTED = SIZE;

    // The console's configuration (virtio 1.x, §5.3.4).
    private const int COLUMNS = 0;
    private const int ROWS = 2;

    #endregion

    #region Fields

    private readonly VirtioPciTransport m_transport;
    private readonly KernelHandle m_memory;
    private readonly MemoryMapping m_pages;
    private readonly DmaPin m_pin;
    private readonly VirtioInterrupt m_interrupt;
    private readonly Virtqueue m_receive;
    private readonly Virtqueue m_transmit;
    private readonly byte[] m_input = new byte[INPUT_BYTES];
    private int m_inputHead;
    private int m_inputCount;
    private bool m_transmitting;

    #endregion

    #region Constructors

    private VirtioConsoleDevice(VirtioPciTransport transport, KernelHandle memory, MemoryMapping pages, DmaPin pin, VirtioInterrupt interrupt, Virtqueue receive, Virtqueue transmit, int columns, int rows)
    {
        m_transport = transport;
        m_memory = memory;
        m_pages = pages;
        m_pin = pin;
        m_interrupt = interrupt;
        m_receive = receive;
        m_transmit = transmit;
        Columns = columns;
        Rows = rows;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Resets the device, negotiates VIRTIO_F_VERSION_1 with SIZE when the device offers it, reads the size, sets up
    /// both queues on pinned pages, binds the device's first line to an event, tells the device the driver is ready and
    /// gives the device every receive buffer.
    /// </summary>
    /// <param name="transport">The device's transport.</param>
    /// <param name="device">The device, with BIND.</param>
    /// <returns>The device.</returns>
    /// <exception cref="IOException">The device refused the features or offers too small a queue.</exception>
    public static VirtioConsoleDevice Open(VirtioPciTransport transport, KernelHandle device)
    {
        var memory = MemoryObject.Create(PAGES * PAGE);
        var pages = MemoryMapping.Map(memory, 0, PAGES * PAGE, MemoryMapping.READ_WRITE);
        DmaPin? pin = null;
        VirtioInterrupt? interrupt = null;
        try
        {
            pin = DmaPin.Pin(device, memory, 0, PAGES * PAGE);
            interrupt = VirtioInterrupt.Prepare(transport); // an MSI-X message takes effect from the reset on
            transport.Reset();
            var features = transport.Negotiate(ACCEPTED);
            var (columns, rows) = ReadSize(transport, features);
            var receive = Virtqueue.Open(transport, 0, pages, pin, RECEIVE_QUEUE, RECEIVE_COUNT);
            var transmit = Virtqueue.Open(transport, 1, pages, pin, TRANSMIT_QUEUE, 1);
            transport.DriverReady();
            interrupt.Start();
            var console = new VirtioConsoleDevice(transport, memory, pages, pin, interrupt, receive, transmit, columns, rows);
            for (var i = 0; i < RECEIVE_COUNT; i++)
            {
                receive.Describe(i, pin.RunAt(RECEIVE_BUFFERS + i * RECEIVE_BUFFER).Address, RECEIVE_BUFFER, Virtqueue.WRITE, 0);
                receive.Offer((ushort)i);
            }
            receive.Notify();
            return console;
        }
        catch
        {
            interrupt?.Dispose();
            pin?.Dispose();
            pages.Dispose();
            memory.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Serves the interrupt the event reports: ends its cause, takes what the device received into the input queue,
    /// gives the device its buffers back and notes the transmit it completed.
    /// </summary>
    public void Service()
    {
        var isr = m_interrupt.ReadStatus();
        Collect();
        m_interrupt.Acknowledge();
        _ = isr;
    }

    /// <summary>
    /// Transmits bytes, in pieces of at most TRANSMIT_BYTES, each waited for; what the device receives meanwhile waits
    /// in the input queue.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <exception cref="IOException">The device completed something other than the transmit.</exception>
    public void Transmit(ReadOnlySpan<byte> bytes)
    {
        for (var done = 0; done < bytes.Length;)
        {
            var piece = Math.Min(bytes.Length - done, TRANSMIT_BYTES);
            bytes.Slice(done, piece).CopyTo(m_pages.Writable[TRANSMIT_BUFFER..]);
            m_transmit.Describe(0, m_pin.RunAt(TRANSMIT_BUFFER).Address, (uint)piece, 0, 0);
            m_transmitting = true;
            m_transmit.Submit(0);
            while (m_transmitting)
            {
                Event.Wait(Event.INFINITE, m_interrupt.Signaled);
                Service();
            }
            done += piece;
        }
    }

    /// <summary>
    /// Takes received bytes out of the input queue.
    /// </summary>
    /// <param name="buffer">Where they go.</param>
    /// <returns>The bytes taken, at most the buffer's length.</returns>
    public int Read(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, m_inputCount);
        for (var i = 0; i < count; i++)
            buffer[i] = m_input[(m_inputHead + i) % INPUT_BYTES];
        m_inputHead = (m_inputHead + count) % INPUT_BYTES;
        m_inputCount -= count;
        return count;
    }

    // What the device completed: received buffers into the input queue (bytes beyond the queue's room are dropped) and
    // given back, the transmit noted.
    private void Collect()
    {
        var offered = false;
        while (m_receive.TryComplete(out var head, out var length))
        {
            var bytes = m_pages.Bytes.Slice(RECEIVE_BUFFERS + head * RECEIVE_BUFFER, (int)Math.Min(length, RECEIVE_BUFFER));
            foreach (var value in bytes)
            {
                if (m_inputCount == INPUT_BYTES)
                    break;
                m_input[(m_inputHead + m_inputCount) % INPUT_BYTES] = value;
                m_inputCount++;
            }
            m_receive.Offer(head);
            offered = true;
        }
        if (offered)
            m_receive.Notify();
        while (m_transmit.TryComplete(out _, out _))
            m_transmitting = false;
    }

    private static (int Columns, int Rows) ReadSize(VirtioPciTransport transport, uint features)
    {
        if ((features & SIZE) == 0)
            return (0, 0);
        var bar = transport.Bar;
        var at = transport.DeviceConfiguration;
        for (; ; )
        {
            var generation = transport.ConfigurationGeneration;
            int columns = bar.Read16(at + COLUMNS), rows = bar.Read16(at + ROWS);
            if (transport.ConfigurationGeneration == generation)
                return (columns, rows);
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Quiesces the device, then gives back the interrupt, the pin and the pages.
    /// </summary>
    public void Dispose()
    {
        m_transport.Quiesce();
        m_interrupt.Dispose();
        m_pin.Dispose();
        m_pages.Dispose();
        m_memory.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// What the device interrupts through: its INTx line or an MSI-X message (H1.2b).
    /// </summary>
    public string InterruptDescription => m_interrupt.Description;

    /// <summary>
    /// The event the device's line sets: a wait on it ends when the device received bytes or completed a transmit.
    /// </summary>
    public KernelHandle Interrupt => m_interrupt.Signaled;

    /// <summary>
    /// The bytes received and not read yet.
    /// </summary>
    public int Available => m_inputCount;

    /// <summary>
    /// The columns the device reports; zero for none.
    /// </summary>
    public int Columns { get; }

    /// <summary>
    /// The rows the device reports; zero for none.
    /// </summary>
    public int Rows { get; }

    /// <summary>
    /// The entries of the receive and the transmit queue.
    /// </summary>
    public (int Receive, int Transmit) QueueSizes => (m_receive.Size, m_transmit.Size);

    #endregion
}
