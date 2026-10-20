using System;
using System.Collections.Generic;
using OutWit.OS.Communication;
using OutWit.OS.Hardware;
using OutWit.OS.Networking;
using OutWit.OS.Resources;

namespace OutWit.OS.Network.Server;

/// <summary>
/// An adapter as the stack's link (plan step W1.2a): the network driver's packet channel (witos/packet.h) and a window
/// of the service's, whose first half receives frames and whose second half sends them. The link keeps one RECEIVE
/// outstanding, so that the driver answers as frames come, and one SEND in flight, packing the frames the stack queued
/// meanwhile into the next; the driver answers a SEND while the RECEIVE waits, so the stack's thread tells the replies
/// apart by their operation. Everything but <see cref="Attach"/> runs under the stack's lock.
/// </summary>
internal sealed class AdapterLink : ILink, IDisposable
{
    #region Constants

    private const string PREFIX = "[NETWORK] ";

    private const int WINDOW_BYTES = PacketMessages.WINDOW_BYTES;

    private const int RECEIVE_AT = 0;

    private const int SEND_AT = WINDOW_BYTES / 2;

    private const int HALF_BYTES = WINDOW_BYTES / 2;

    /// <summary>
    /// The frames the stack may queue while a SEND is in flight; a frame beyond them is dropped, as a full transmit ring
    /// drops it.
    /// </summary>
    private const int OUTGOING = 512;

    #endregion

    #region Fields

    private readonly Channel m_channel;
    private readonly KernelHandle m_window;
    private readonly MemoryMapping m_mapping;
    private readonly Queue<byte[]> m_outgoing = new();
    private readonly List<(int Offset, int Length)> m_frames = [];
    private bool m_sending;
    private bool m_receiving;

    #endregion

    #region Constructors

    private AdapterLink(string device, Channel channel, KernelHandle window, MemoryMapping mapping, PacketReply info)
    {
        Device = device;
        m_channel = channel;
        m_window = window;
        m_mapping = mapping;
        Address = new MacAddress(info.Address);
        Mtu = info.Mtu;
        LinkUp = (info.Link & PacketMessages.LINK_UP) != 0;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Attaches the service's window to a driver's packet channel and reads the adapter's address, MTU and link.
    /// </summary>
    /// <param name="device">The adapter's device identity.</param>
    /// <param name="channel">The packet channel, which the link takes.</param>
    /// <returns>The link.</returns>
    /// <exception cref="CapabilityException">The window could not be made or the driver refused it.</exception>
    public static AdapterLink Attach(string device, Channel channel)
    {
        KernelHandle? window = null;
        MemoryMapping? mapping = null;
        try
        {
            window = MemoryObject.Create(WINDOW_BYTES);
            mapping = MemoryMapping.Map(window, 0, WINDOW_BYTES, MemoryMapping.READ_WRITE);
            using (var theirs = window.Duplicate(Rights.Map | Rights.Write | Rights.Transfer))
                Require(Call(channel, new PacketRequest(PacketMessages.ATTACH, 0, WINDOW_BYTES), theirs), "ATTACH");
            var info = Require(Call(channel, new PacketRequest(PacketMessages.INFO, 0, 0)), "INFO");
            return new AdapterLink(device, channel, window, mapping, info);
        }
        catch
        {
            mapping?.Dispose();
            window?.Dispose();
            channel.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Transmit(ReadOnlySpan<byte> frame)
    {
        if (m_outgoing.Count >= OUTGOING || frame.Length > PacketMessages.HEADER_BYTES + Mtu)
        {
            Dropped++;
            return;
        }
        m_outgoing.Enqueue(frame.ToArray());
    }

    /// <summary>
    /// Sends what the stack queued when no SEND is in flight, and keeps a RECEIVE outstanding.
    /// </summary>
    /// <exception cref="CapabilityException">The channel failed.</exception>
    public void Flush()
    {
        if (!m_receiving)
        {
            m_channel.Send(PacketMessages.Encode(new PacketRequest(PacketMessages.RECEIVE, 0, HALF_BYTES, RECEIVE_AT)));
            m_receiving = true;
        }
        if (m_sending || m_outgoing.Count == 0)
            return;
        var target = m_mapping.Writable.Slice(SEND_AT, HALF_BYTES);
        var bytes = 0;
        var count = 0;
        while (m_outgoing.Count > 0 && PacketMessages.PackedBytes(m_outgoing.Peek().Length) <= HALF_BYTES - bytes)
        {
            var frame = m_outgoing.Dequeue();
            bytes += PacketMessages.Pack(target[bytes..], frame);
            count++;
        }
        m_channel.Send(PacketMessages.Encode(new PacketRequest(PacketMessages.SEND, 0, bytes, SEND_AT, count)));
        m_sending = true;
        Sent += count;
    }

    /// <summary>
    /// Takes a reply of the driver's: a RECEIVE's frames go to the stack, a SEND's answer lets the next SEND go.
    /// </summary>
    /// <param name="message">The reply.</param>
    /// <param name="deliver">What takes each received frame.</param>
    /// <exception cref="FormatException">The bytes are no reply.</exception>
    public void Handle(ChannelMessage message, ReceiveFrame deliver)
    {
        var reply = PacketMessages.DecodeReply(message.Bytes);
        if (reply.Operation == PacketMessages.SEND)
        {
            m_sending = false;
            if (reply.Error != 0)
                Console.WriteLine($"{PREFIX}{Device}: SEND failed, errno {reply.Error}");
            return;
        }
        if (reply.Operation != PacketMessages.RECEIVE)
            throw new FormatException($"A packet reply to operation {reply.Operation}, which the link never asks.");
        m_receiving = false;
        if (reply.Error != 0)
        {
            Console.WriteLine($"{PREFIX}{Device}: RECEIVE failed, errno {reply.Error}");
            return;
        }
        if (reply.Length < 0 || reply.Length > HALF_BYTES)
            throw new FormatException($"A RECEIVE of {reply.Length} bytes in a half window of {HALF_BYTES}.");
        var bytes = m_mapping.Bytes.Slice(RECEIVE_AT, (int)reply.Length);
        if (PacketMessages.Unpack(bytes, reply.Count, Mtu, m_frames) != 0)
            throw new FormatException($"A RECEIVE's {reply.Count} frames in {reply.Length} bytes are not packed as the protocol packs them.");
        Received += m_frames.Count;
        foreach (var (offset, length) in m_frames)
            deliver(bytes.Slice(offset, length));
    }

    // A request answered before the next one goes: ATTACH and INFO, before the link's own requests start. A reply to
    // another operation is the device manager's probe's (W1.1), a RECEIVE it left waiting when its wait ended, which the
    // driver answered before the ATTACH forgot it, and is passed over.
    private static PacketReply Call(Channel channel, PacketRequest request, params ReadOnlySpan<KernelHandle> handles)
    {
        channel.Send(PacketMessages.Encode(request), handles);
        for (; ; )
        {
            using var message = channel.Receive();
            var reply = PacketMessages.DecodeReply(message.Bytes);
            if (reply.Operation == request.Operation)
                return reply;
        }
    }

    private static PacketReply Require(PacketReply reply, string operation) => reply.Error == 0
        ? reply
        : throw new CapabilityException(operation, reply.Error);

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        m_mapping.Dispose();
        m_window.Dispose();
        m_channel.Dispose();
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public MacAddress Address { get; }

    /// <inheritdoc />
    public int Mtu { get; }

    /// <summary>
    /// The adapter's device identity.
    /// </summary>
    public string Device { get; }

    /// <summary>
    /// Whether the adapter reported its link up.
    /// </summary>
    public bool LinkUp { get; }

    /// <summary>
    /// The packet channel's endpoint, which the stack's thread waits on.
    /// </summary>
    public KernelHandle Endpoint => m_channel.Endpoint;

    /// <summary>
    /// The channel the stack's thread receives the driver's replies from.
    /// </summary>
    public Channel Channel => m_channel;

    /// <summary>
    /// The frames sent, received and dropped for want of room.
    /// </summary>
    public long Sent { get; private set; }

    /// <inheritdoc cref="Sent" />
    public long Received { get; private set; }

    /// <inheritdoc cref="Sent" />
    public long Dropped { get; private set; }

    #endregion
}

/// <summary>
/// What takes a frame the link received.
/// </summary>
/// <param name="frame">An Ethernet II frame, valid for the call alone.</param>
internal delegate void ReceiveFrame(ReadOnlySpan<byte> frame);
