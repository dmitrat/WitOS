using System;
using OutWit.OS.Communication;
using OutWit.OS.Resources;

namespace OutWit.OS.Storage;

/// <summary>
/// A session with a block driver over its block channel (witos/block.h, plan step D3.2): a window of the client's own
/// memory, mapped here and attached to the driver, which pins it for the device, so that the device reads and writes the
/// window in place.
/// </summary>
public sealed class ChannelBlockSession : IBlockSession
{
    #region Fields

    private readonly Channel m_channel;
    private readonly KernelHandle m_window;
    private readonly MemoryMapping m_mapping;
    private bool m_detached;

    #endregion

    #region Constructors

    private ChannelBlockSession(Channel channel, KernelHandle window, MemoryMapping mapping)
    {
        m_channel = channel;
        m_window = window;
        m_mapping = mapping;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Creates a window and attaches it to the driver: a duplicate with MAP, WRITE and TRANSFER moves in the ATTACH
    /// request's handle slot.
    /// </summary>
    /// <param name="channel">The client's end of the block channel; the session owns it.</param>
    /// <param name="windowBytes">The window's bytes, whole pages, at most WINDOW_BYTES.</param>
    /// <returns>The session.</returns>
    /// <exception cref="CapabilityException">The window could not be had or the channel failed.</exception>
    /// <exception cref="System.IO.IOException">The driver refused the window.</exception>
    public static ChannelBlockSession Attach(Channel channel, int windowBytes = BlockMessages.WINDOW_BYTES)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var window = MemoryObject.Create(windowBytes);
        MemoryMapping? mapping = null;
        KernelHandle? theirs = null;
        try
        {
            mapping = MemoryMapping.Map(window, 0, windowBytes, MemoryMapping.READ_WRITE);
            theirs = window.Duplicate(Rights.Map | Rights.Write | Rights.Transfer);
            var session = new ChannelBlockSession(channel, window, mapping);
            var reply = session.Call(new BlockRequest(BlockMessages.ATTACH, Length: windowBytes), theirs);
            if (reply.Error != 0)
                throw new System.IO.IOException($"The driver refused a window of {windowBytes} bytes with errno {reply.Error}.");
            return session;
        }
        catch
        {
            theirs?.Dispose();
            mapping?.Dispose();
            window.Dispose();
            channel.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public BlockReply Call(BlockRequest request) => Call(request, null);

    private BlockReply Call(BlockRequest request, KernelHandle? handle)
    {
        var bytes = BlockMessages.Encode(request);
        if (handle is null)
            m_channel.Send(bytes);
        else
            m_channel.Send(bytes, handle);
        using var message = m_channel.Receive();
        if (message.Handles.Count != 0)
            throw new FormatException($"The driver's reply carries {message.Handles.Count} handles.");
        var reply = BlockMessages.DecodeReply(message.Bytes);
        if (reply.Operation != request.Operation)
            throw new FormatException($"The driver answered operation {request.Operation} with a reply of {reply.Operation}.");
        return reply;
    }

    /// <summary>
    /// Gives the channel up without closing it, so that another client may attach its own window (D5.1: the device
    /// manager moves a disk to the namespace service); the session's window is given back.
    /// </summary>
    /// <returns>The channel, the caller's now.</returns>
    public Channel Detach()
    {
        m_detached = true;
        m_mapping.Dispose();
        m_window.Dispose();
        return m_channel;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Closes the channel and gives back the window; the driver's pin ends when it sees the channel closed. A detached
    /// session gave its channel up already.
    /// </summary>
    public void Dispose()
    {
        if (m_detached)
            return;
        m_channel.Dispose();
        m_mapping.Dispose();
        m_window.Dispose();
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public Span<byte> Window => m_mapping.Writable;

    #endregion
}
