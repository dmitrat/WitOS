using System;
using System.Buffers.Binary;
using System.Threading;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.Terminal.Server;

/// <summary>
/// The service's side of the console driver's serial channel (witos/serial.h): a window attached, a READ always
/// outstanding so that every byte the device receives reaches the discipline at once, and the writes the sessions make
/// sent in turn. One pump thread holds the channel: it waits on the channel and on the sessions' write requests, hands
/// the bytes a READ brought to the hub, sends the next READ, sends each WRITE and wakes its writer when the driver
/// answered.
/// </summary>
internal sealed class ConsoleLink : IDisposable
{
    #region Constants

    private const int WINDOW_BYTES = SerialMessages.WINDOW_BYTES;
    private const int READ_WINDOW = 0;
    private const int READ_BYTES = WINDOW_BYTES / 2;
    private const int WRITE_WINDOW = WINDOW_BYTES / 2;

    /// <summary>
    /// The bytes one write sends at most: half the window.
    /// </summary>
    public const int WRITE_BYTES = WINDOW_BYTES / 2;

    private const string PREFIX = "[TERMINAL] ";

    #endregion

    #region Fields

    private readonly Channel m_channel;
    private readonly KernelHandle m_windowObject;
    private readonly MemoryMapping m_window;
    private readonly Channel m_writes; // the pump's end: the sessions' write requests
    private readonly Channel m_writer; // the sessions' end
    private readonly Action<byte[]> m_input;
    private readonly object m_writeGate = new();
    private Thread? m_pump;
    private bool m_writeDone;
    private volatile bool m_gone;

    #endregion

    #region Constructors

    private ConsoleLink(Channel channel, KernelHandle windowObject, MemoryMapping window, Channel writes, Channel writer, Action<byte[]> input)
    {
        m_channel = channel;
        m_windowObject = windowObject;
        m_window = window;
        m_writes = writes;
        m_writer = writer;
        m_input = input;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Opens the link: the window attached, the device's size read, the first READ sent and the pump started.
    /// </summary>
    /// <param name="serial">The driver's serial channel, the service's end.</param>
    /// <param name="input">Where the bytes the device received go: the hub.</param>
    /// <returns>The link.</returns>
    /// <exception cref="CapabilityException">A capability could not be had.</exception>
    /// <exception cref="InvalidOperationException">The driver refused the window.</exception>
    public static ConsoleLink Open(KernelHandle serial, Action<byte[]> input)
    {
        var channel = new Channel(serial);
        var windowObject = MemoryObject.Create(WINDOW_BYTES);
        var window = MemoryMapping.Map(windowObject, 0, WINDOW_BYTES, MemoryMapping.READ_WRITE);
        var (writes, writer) = Channel.Create();
        var link = new ConsoleLink(channel, windowObject, window, writes, new Channel(writer), input);
        try
        {
            using (var attached = windowObject.Duplicate(Rights.Map | Rights.Write | Rights.Transfer))
                channel.Send(SerialMessages.Encode(new SerialRequest(SerialMessages.ATTACH, 0, WINDOW_BYTES)), attached);
            var attach = link.Reply();
            if (attach.Error != 0)
                throw new InvalidOperationException($"The console driver refused the window: errno {attach.Error}.");
            channel.Send(SerialMessages.Encode(new SerialRequest(SerialMessages.SIZE_QUERY, 0, 0)));
            var size = link.Reply();
            if (size.Error == 0)
                link.Size = (size.Columns, size.Rows);
            channel.Send(SerialMessages.Encode(new SerialRequest(SerialMessages.READ, 0, READ_BYTES, READ_WINDOW)));
            link.m_pump = new Thread(link.Pump) { IsBackground = true, Name = "console link" };
            link.m_pump.Start();
            return link;
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends bytes to the device, in pieces of at most WRITE_BYTES, each waited for; the sessions' writes go one at a
    /// time, in the order they came.
    /// </summary>
    /// <param name="bytes">The device's bytes, after output processing.</param>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        lock (m_writeGate)
        {
            for (var done = 0; done < bytes.Length;)
            {
                if (m_gone)
                    return;
                var piece = Math.Min(bytes.Length - done, WRITE_BYTES);
                bytes.Slice(done, piece).CopyTo(m_window.Writable[WRITE_WINDOW..]);
                var request = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(request, piece);
                m_writeDone = false;
                m_writer.Send(request);
                while (!m_writeDone && !m_gone)
                    Monitor.Wait(m_writeGate);
                done += piece;
            }
        }
    }

    // The pump: the channel's replies and the sessions' write requests.
    private void Pump()
    {
        try
        {
            for (; ; )
            {
                if (Event.Wait(Event.INFINITE, m_channel.Endpoint, m_writes.Endpoint) == 1)
                {
                    using var request = m_writes.Receive();
                    var length = BinaryPrimitives.ReadInt32LittleEndian(request.Bytes);
                    m_channel.Send(SerialMessages.Encode(new SerialRequest(SerialMessages.WRITE, 0, length, WRITE_WINDOW)));
                    continue;
                }
                var reply = Reply();
                switch (reply.Operation)
                {
                    case SerialMessages.READ:
                        var bytes = reply.Error == 0 ? m_window.Bytes.Slice(READ_WINDOW, (int)reply.Length).ToArray() : [];
                        m_channel.Send(SerialMessages.Encode(new SerialRequest(SerialMessages.READ, 0, READ_BYTES, READ_WINDOW)));
                        if (bytes.Length > 0)
                            m_input(bytes);
                        break;
                    case SerialMessages.WRITE:
                        if (reply.Error != 0)
                            Console.WriteLine($"{PREFIX}the console driver failed a write: errno {reply.Error}");
                        lock (m_writeGate)
                        {
                            m_writeDone = true;
                            Monitor.PulseAll(m_writeGate);
                        }
                        break;
                    default:
                        Console.WriteLine($"{PREFIX}the console driver answered operation {reply.Operation}, which nothing asked");
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is CapabilityException or FormatException)
        {
            // The driver went, or the link is closing: the writers go on, and the input ends.
            Console.WriteLine($"{PREFIX}the console link ended: {exception.Message}");
        }
        lock (m_writeGate)
        {
            m_gone = true;
            Monitor.PulseAll(m_writeGate);
        }
    }

    private SerialReply Reply()
    {
        using var message = m_channel.Receive();
        return SerialMessages.DecodeReply(message.Bytes);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Closes the channel, which ends the pump, and gives back the window.
    /// </summary>
    public void Dispose()
    {
        m_gone = true;
        m_writer.Dispose();
        m_channel.Dispose();
        m_pump?.Join();
        m_writes.Dispose();
        m_window.Dispose();
        m_windowObject.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The columns and rows the device reports; zeros for none.
    /// </summary>
    public (int Columns, int Rows) Size { get; private set; }

    #endregion
}
