using System;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;
using OutWit.OS.Storage;

namespace OutWit.OS.Drivers.VirtioBlock.Virtio;

/// <summary>
/// The block protocol on the driver's side (witos/block.h, plan step D3.2): the client's window pinned for the device, so
/// that a request's data are the window's physical runs, split where the device's segments end; every request checked
/// before anything reaches the device, and answered with a reply.
/// </summary>
internal sealed class BlockServer : IDisposable
{
    #region Constants

    private const string PREFIX = "[VIRTIO-BLOCK] ";
    private const int PAGE = MemoryObject.PAGE;

    #endregion

    #region Fields

    private readonly VirtioBlockQueue m_queue;
    private readonly KernelHandle m_device;
    private readonly (ulong Address, int Length)[] m_segments;
    private DmaPin? m_pin;
    private readonly string m_name;
    private long m_windowBytes;

    #endregion

    #region Constructors

    private BlockServer(VirtioBlockQueue queue, KernelHandle device, string name, BlockReply info)
    {
        m_name = name;
        m_queue = queue;
        m_device = device;
        m_segments = new (ulong, int)[queue.MaxSegments];
        Info = info;
    }

    #endregion

    #region Functions

    /// <summary>
    /// The server of a device's queue. A transfer touches at most one page more than its whole pages, so the device's
    /// segments less one bound the transfer limit in pages, within the largest window.
    /// </summary>
    /// <param name="queue">The device's queue.</param>
    /// <param name="device">The device, with BIND, for the window's pin.</param>
    /// <param name="name">The device's identity, for the lines.</param>
    /// <returns>The server.</returns>
    /// <exception cref="IOException">The device's segments cannot carry a block.</exception>
    public static BlockServer Create(VirtioBlockQueue queue, KernelHandle device, string name)
    {
        var transfer = queue.MaxSegments > BlockMessages.WINDOW_BYTES / PAGE
            ? BlockMessages.WINDOW_BYTES
            : (queue.MaxSegments - 1) * PAGE;
        transfer -= transfer % queue.BlockSize;
        if (transfer < queue.BlockSize)
            throw new IOException($"The device's {queue.MaxSegments} segments cannot carry a block of {queue.BlockSize} bytes.");
        var flags = (queue.IsReadOnly ? BlockMessages.READ_ONLY : 0) | (queue.Flushes ? BlockMessages.FLUSHES : 0);
        return new BlockServer(queue, device, name, new BlockReply(BlockMessages.INFO, 0, queue.Capacity, queue.BlockSize, flags, transfer));
    }

    /// <summary>
    /// Serves one message of the block channel; the message's handles stay the caller's to close.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The reply's bytes.</returns>
    public byte[] Serve(ChannelMessage message)
    {
        BlockRequest request;
        try
        {
            request = BlockMessages.DecodeRequest(message.Bytes);
        }
        catch (FormatException)
        {
            return BlockMessages.Encode(new BlockReply(0, ServiceStatus.PROTOCOL_ERROR));
        }
        var handles = request.Operation == BlockMessages.ATTACH ? 1 : 0;
        var error = message.Handles.Count != handles ? BlockMessages.INVALID : BlockMessages.Check(request, Info, m_windowBytes);
        if (error != 0)
            return BlockMessages.Encode(new BlockReply(request.Operation, error));
        try
        {
            switch (request.Operation)
            {
                case BlockMessages.ATTACH:
                    return BlockMessages.Encode(Attach(request, message.Handles[0]));
                case BlockMessages.INFO:
                    return BlockMessages.Encode(Info);
                case BlockMessages.READ:
                    m_queue.Submit(VirtioBlockQueue.TYPE_IN, Sector(request), Segments(request));
                    break;
                case BlockMessages.WRITE:
                    m_queue.Submit(VirtioBlockQueue.TYPE_OUT, Sector(request), Segments(request));
                    if ((request.Flags & BlockMessages.DURABLE) != 0 && m_queue.Flushes)
                        m_queue.Submit(VirtioBlockQueue.TYPE_FLUSH, 0, []);
                    break;
                case BlockMessages.FLUSH:
                    if (m_queue.Flushes)
                        m_queue.Submit(VirtioBlockQueue.TYPE_FLUSH, 0, []);
                    break;
            }
            return BlockMessages.Encode(new BlockReply(request.Operation, 0));
        }
        catch (IOException exception)
        {
            // The device failed the request (D6.3): the log names the device and what failed, the client gets EIO.
            Console.WriteLine($"{PREFIX}driver of {m_name}: {exception.Message}; the client gets EIO");
            return BlockMessages.Encode(new BlockReply(request.Operation, BlockMessages.DEVICE_ERROR));
        }
    }

    /// <summary>
    /// Ends the window's pin: its client is gone.
    /// </summary>
    public void Detach()
    {
        m_pin?.Dispose();
        m_pin = null;
        m_windowBytes = 0;
    }

    // The window, pinned for the device in place of the one before; the pin holds the object, so its handle closes
    // with the message.
    private BlockReply Attach(BlockRequest request, KernelHandle window)
    {
        DmaPin pin;
        try
        {
            pin = DmaPin.Pin(m_device, window, 0, (int)request.Length);
        }
        catch (CapabilityException exception)
        {
            return new BlockReply(BlockMessages.ATTACH, exception.Error);
        }
        Detach();
        m_pin = pin;
        m_windowBytes = request.Length;
        return new BlockReply(BlockMessages.ATTACH, 0, request.Length);
    }

    private static ulong Sector(BlockRequest request) => (ulong)(request.Offset / VirtioBlockQueue.SECTOR);

    // The window's bytes of a request as the device reaches them: each physical run of the pin, cut at the device's
    // largest segment.
    private ReadOnlySpan<(ulong Address, int Length)> Segments(BlockRequest request)
    {
        var count = 0;
        for (var done = 0L; done < request.Length;)
        {
            var (address, run) = m_pin!.RunAt(request.Window + done);
            var length = (int)Math.Min(Math.Min(run, request.Length - done), m_queue.MaxSegmentBytes);
            if (count == m_segments.Length)
                throw new IOException($"A transfer of {request.Length} bytes needs more than the device's {m_segments.Length} segments.");
            m_segments[count++] = (address, length);
            done += length;
        }
        return m_segments.AsSpan(0, count);
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Detach();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The INFO reply: the device's bytes, block, flags and transfer limit.
    /// </summary>
    public BlockReply Info { get; }

    #endregion
}
