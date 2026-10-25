using System;
using System.Collections.Generic;
using OutWit.OS.Communication;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware;

/// <summary>
/// The packet protocol (witos/packet.h, plan step W1.1) served over a network adapter (H4: the virtio network device's
/// and an e1000e's drivers alike): a window attached, INFO from the adapter, a SEND's frames checked whole and
/// transmitted, and a RECEIVE answered from the adapter's frame queue or held until a frame comes; every request checked
/// before anything is done.
/// </summary>
public sealed class PacketServer : IDisposable
{
    #region Fields

    private readonly IPacketDevice m_device;
    private readonly List<(int Offset, int Length)> m_frames = [];
    private MemoryMapping? m_window;
    private PacketRequest? m_pendingReceive;

    #endregion

    #region Constructors

    /// <summary>
    /// A server of the adapter, with no window attached.
    /// </summary>
    /// <param name="device">The adapter.</param>
    public PacketServer(IPacketDevice device)
    {
        m_device = device;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Serves one message of the packet channel; the message's handles stay the caller's to close.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The reply's bytes, or null for a RECEIVE held until a frame comes.</returns>
    public byte[]? Serve(ChannelMessage message)
    {
        PacketRequest request;
        try
        {
            request = PacketMessages.DecodeRequest(message.Bytes);
        }
        catch (FormatException)
        {
            return PacketMessages.Encode(new PacketReply(0, ServiceStatus.PROTOCOL_ERROR));
        }
        var error = PacketMessages.Check(request, m_window?.Length ?? 0);
        if (error != 0)
            return PacketMessages.Encode(new PacketReply(request.Operation, error));
        switch (request.Operation)
        {
            case PacketMessages.ATTACH:
                return PacketMessages.Encode(message.Handles.Count == 1
                    ? Attach(request, message.Handles[0])
                    : new PacketReply(PacketMessages.ATTACH, PacketMessages.INVALID));
            case PacketMessages.INFO:
                return PacketMessages.Encode(new PacketReply(PacketMessages.INFO, 0, Link: m_device.LinkUp ? PacketMessages.LINK_UP : 0,
                    Address: m_device.Address, Mtu: (ushort)m_device.Mtu));
        }
        if (m_window is null)
            return PacketMessages.Encode(new PacketReply(request.Operation, PacketMessages.INVALID));
        if (request.Operation == PacketMessages.SEND)
            return PacketMessages.Encode(Send(request));
        if (m_pendingReceive is not null)
            return PacketMessages.Encode(new PacketReply(PacketMessages.RECEIVE, PacketMessages.BUSY));
        if (m_device.Pending > 0)
            return PacketMessages.Encode(Receive(request));
        if ((request.Flags & PacketMessages.NONBLOCK) != 0)
            return PacketMessages.Encode(new PacketReply(PacketMessages.RECEIVE, 0));
        m_pendingReceive = request;
        return null;
    }

    /// <summary>
    /// The reply of the RECEIVE held, once a frame is there.
    /// </summary>
    /// <returns>The reply's bytes, or null while no RECEIVE waits or nothing came.</returns>
    public byte[]? Completed()
    {
        if (m_pendingReceive is not { } request || m_device.Pending == 0)
            return null;
        m_pendingReceive = null;
        return PacketMessages.Encode(Receive(request));
    }

    /// <summary>
    /// Ends the window's mapping and forgets the RECEIVE held: the client is gone.
    /// </summary>
    public void Detach()
    {
        m_window?.Dispose();
        m_window = null;
        m_pendingReceive = null;
    }

    private PacketReply Attach(PacketRequest request, KernelHandle window)
    {
        MemoryMapping mapping;
        try
        {
            mapping = MemoryMapping.Map(window, 0, (int)request.Length, MemoryMapping.READ_WRITE);
        }
        catch (CapabilityException exception)
        {
            return new PacketReply(PacketMessages.ATTACH, exception.Error);
        }
        Detach();
        m_window = mapping;
        return new PacketReply(PacketMessages.ATTACH, 0, request.Length);
    }

    // The frames are copied out of the window before the device sees them, so the client's later writes cannot change
    // what was checked.
    private PacketReply Send(PacketRequest request)
    {
        var bytes = m_window!.Bytes.Slice((int)request.Window, (int)request.Length).ToArray();
        var error = PacketMessages.Unpack(bytes, request.Count, m_device.Mtu, m_frames);
        if (error != 0)
            return new PacketReply(PacketMessages.SEND, error);
        var frames = new ReadOnlyMemory<byte>[m_frames.Count];
        long sent = 0;
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = bytes.AsMemory(m_frames[i].Offset, m_frames[i].Length);
            sent += m_frames[i].Length;
        }
        m_device.Transmit(frames);
        return new PacketReply(PacketMessages.SEND, 0, sent, frames.Length);
    }

    // As many whole frames as the request's bytes hold; a RECEIVE whose bytes cannot hold the oldest frame waiting is
    // EMSGSIZE, and the frame waits for one that can.
    private PacketReply Receive(PacketRequest request)
    {
        var target = m_window!.Writable.Slice((int)request.Window, (int)request.Length);
        var offset = 0;
        var count = 0;
        while (m_device.Pending > 0 && PacketMessages.PackedBytes(m_device.PeekLength()) <= target.Length - offset)
        {
            offset += PacketMessages.Pack(target[offset..], m_device.Take()!);
            count++;
        }
        if (count == 0)
            return new PacketReply(PacketMessages.RECEIVE, PacketMessages.FRAME_SIZE);
        return new PacketReply(PacketMessages.RECEIVE, 0, offset, count);
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Detach();
    }

    #endregion
}
