using System;
using System.IO;
using OutWit.OS.Communication;
using OutWit.OS.Resources;
using OutWit.OS.Storage;

namespace OutWit.OS.Hardware;

/// <summary>
/// The block protocol on the driver's side (witos/block.h, plan step D3.2; shared by the block drivers since H3.1): the
/// client's window pinned for the device, so that a request's data are the window's physical runs, split where the
/// device's segments end; every request checked before anything reaches the device, and answered with a reply.
/// </summary>
public sealed class BlockServer : IDisposable
{
    #region Constants

    private const int PAGE = MemoryObject.PAGE;

    #endregion

    #region Fields

    private readonly IBlockDevice m_disk;
    private readonly KernelHandle m_device;
    private readonly (ulong Address, int Length)[] m_segments;
    private readonly string m_prefix;
    private readonly string m_name;
    private DmaPin? m_pin;
    private long m_windowBytes;

    #endregion

    #region Constructors

    private BlockServer(IBlockDevice disk, KernelHandle device, string prefix, string name, BlockReply info)
    {
        m_disk = disk;
        m_device = device;
        m_prefix = prefix;
        m_name = name;
        m_segments = new (ulong, int)[disk.MaxSegments];
        Info = info;
    }

    #endregion

    #region Functions

    /// <summary>
    /// The server of a disk. A transfer touches at most one page more than its whole pages, so the disk's segments less
    /// one bound the transfer limit in pages, within the largest window and the disk's largest request.
    /// </summary>
    /// <param name="disk">The disk.</param>
    /// <param name="device">The device, with BIND, for the window's pin.</param>
    /// <param name="prefix">The driver's prefix of its lines, such as "[VIRTIO-BLOCK] ".</param>
    /// <param name="name">The device's identity, for the lines.</param>
    /// <returns>The server.</returns>
    /// <exception cref="IOException">The disk's segments cannot carry a block.</exception>
    public static BlockServer Create(IBlockDevice disk, KernelHandle device, string prefix, string name)
    {
        var transfer = disk.MaxSegments > BlockMessages.WINDOW_BYTES / PAGE
            ? BlockMessages.WINDOW_BYTES
            : (disk.MaxSegments - 1) * PAGE;
        transfer = (int)Math.Min(transfer, disk.MaxTransferBytes);
        transfer -= transfer % disk.BlockSize;
        if (transfer < disk.BlockSize)
            throw new IOException($"The device's {disk.MaxSegments} segments cannot carry a block of {disk.BlockSize} bytes.");
        var flags = (disk.IsReadOnly ? BlockMessages.READ_ONLY : 0) | (disk.Flushes ? BlockMessages.FLUSHES : 0);
        return new BlockServer(disk, device, prefix, name, new BlockReply(BlockMessages.INFO, 0, disk.Capacity, disk.BlockSize, flags, transfer));
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
                    m_disk.Read(request.Offset, Segments(request));
                    break;
                case BlockMessages.WRITE:
                    m_disk.Write(request.Offset, Segments(request));
                    if ((request.Flags & BlockMessages.DURABLE) != 0 && m_disk.Flushes)
                        m_disk.Flush();
                    break;
                case BlockMessages.FLUSH:
                    if (m_disk.Flushes)
                        m_disk.Flush();
                    break;
            }
            return BlockMessages.Encode(new BlockReply(request.Operation, 0));
        }
        catch (IOException exception)
        {
            // The device failed the request (D6.3): the log names the device and what failed, the client gets EIO.
            Console.WriteLine($"{m_prefix}driver of {m_name}: {exception.Message}; the client gets EIO");
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

    // The window's bytes of a request as the device reaches them: each physical run of the pin, cut at the device's
    // largest segment.
    private ReadOnlySpan<(ulong Address, int Length)> Segments(BlockRequest request)
    {
        var count = 0;
        for (var done = 0L; done < request.Length;)
        {
            var (address, run) = m_pin!.RunAt(request.Window + done);
            var length = (int)Math.Min(Math.Min(run, request.Length - done), m_disk.MaxSegmentBytes);
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
