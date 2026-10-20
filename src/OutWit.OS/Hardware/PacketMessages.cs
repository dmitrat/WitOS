using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace OutWit.OS.Hardware;

/// <summary>
/// The packet protocol's messages (witos/packet.h, plan step W1.1): what the driver of a network adapter serves on its
/// channel, and what its one client sends it. Frames travel packed in the window the client attached, each after a
/// frame header of 8 bytes and starting at a multiple of 8.
/// </summary>
public static class PacketMessages
{
    #region Constants

    /// <summary>
    /// WIT_PACKET_VERSION.
    /// </summary>
    public const uint VERSION = 1;

    /// <summary>
    /// The bytes of a request.
    /// </summary>
    public const int REQUEST_SIZE = 40;

    /// <summary>
    /// The bytes of a reply.
    /// </summary>
    public const int REPLY_SIZE = 48;

    /// <summary>
    /// WIT_PACKET_ATTACH: the window in the one handle slot.
    /// </summary>
    public const uint ATTACH = 1;

    /// <summary>
    /// WIT_PACKET_INFO: the adapter's address, MTU and link.
    /// </summary>
    public const uint INFO = 2;

    /// <summary>
    /// WIT_PACKET_SEND: frames packed in the window to the device.
    /// </summary>
    public const uint SEND = 3;

    /// <summary>
    /// WIT_PACKET_RECEIVE: frames the device received, packed into the window.
    /// </summary>
    public const uint RECEIVE = 4;

    /// <summary>
    /// WIT_PACKET_NONBLOCK: a RECEIVE answered at once.
    /// </summary>
    public const uint NONBLOCK = 1;

    /// <summary>
    /// WIT_PACKET_LINK_UP.
    /// </summary>
    public const uint LINK_UP = 1;

    /// <summary>
    /// WIT_PACKET_WINDOW_BYTES.
    /// </summary>
    public const int WINDOW_BYTES = 16 * 4096;

    /// <summary>
    /// WIT_PACKET_QUEUE: the received frames the driver keeps.
    /// </summary>
    public const int QUEUE = 256;

    /// <summary>
    /// WIT_PACKET_HEADER_BYTES: an Ethernet II header.
    /// </summary>
    public const int HEADER_BYTES = 14;

    /// <summary>
    /// The bytes of a packed frame's header (WitPacketFrame).
    /// </summary>
    public const int FRAME_HEADER_BYTES = 8;

    /// <summary>
    /// EIO: the device failed.
    /// </summary>
    public const int DEVICE_ERROR = 5;

    /// <summary>
    /// EBUSY: a RECEIVE while one waits.
    /// </summary>
    public const int BUSY = 16;

    /// <summary>
    /// EINVAL: a request the driver cannot serve as it is.
    /// </summary>
    public const int INVALID = 22;

    /// <summary>
    /// EMSGSIZE: a frame shorter than its header or longer than the MTU allows.
    /// </summary>
    public const int FRAME_SIZE = 90;

    /// <summary>
    /// EOPNOTSUPP: an unknown operation.
    /// </summary>
    public const int UNKNOWN_OPERATION = 95;

    #endregion

    #region Functions

    /// <summary>
    /// Encodes a request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Encode(PacketRequest request)
    {
        var bytes = new byte[REQUEST_SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), REQUEST_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), request.Operation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), request.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), request.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), request.Window);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), request.Count);
        return bytes;
    }

    /// <summary>
    /// Decodes a request.
    /// </summary>
    /// <param name="bytes">The message's bytes.</param>
    /// <returns>The request.</returns>
    /// <exception cref="FormatException">The bytes are no request of this version.</exception>
    public static PacketRequest DecodeRequest(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, REQUEST_SIZE, "request");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]) != 0)
            throw new FormatException("A packet request's reserved word is not zero.");
        return new PacketRequest(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]));
    }

    /// <summary>
    /// Encodes a reply.
    /// </summary>
    /// <param name="reply">The reply.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Encode(PacketReply reply)
    {
        var bytes = new byte[REPLY_SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), REPLY_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), reply.Operation);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), reply.Error);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), reply.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), reply.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), reply.Link);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), reply.Address & 0xFFFF_FFFF_FFFFUL);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(38), reply.Mtu);
        return bytes;
    }

    /// <summary>
    /// Decodes a reply.
    /// </summary>
    /// <param name="bytes">The message's bytes.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="FormatException">The bytes are no reply of this version.</exception>
    public static PacketReply DecodeReply(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, REPLY_SIZE, "reply");
        return new PacketReply(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]) & 0xFFFF_FFFF_FFFFUL,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[38..]));
    }

    /// <summary>
    /// Checks a request against the window attached: zero, or the errno value the driver answers. The frames of a SEND
    /// are checked by <see cref="Unpack"/>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="windowBytes">The window's bytes; zero before an ATTACH.</param>
    /// <returns>Zero or an errno value.</returns>
    public static int Check(PacketRequest request, long windowBytes)
    {
        switch (request.Operation)
        {
            case ATTACH:
                return request.Flags == 0 && request.Window == 0 && request.Count == 0 && request.Length > 0 &&
                    request.Length <= WINDOW_BYTES && request.Length % 4096 == 0 ? 0 : INVALID;
            case INFO:
                return request.Flags == 0 && request.Length == 0 && request.Window == 0 && request.Count == 0 ? 0 : INVALID;
            case SEND:
                return request.Flags == 0 && request.Count > 0 && InWindow(request, windowBytes) ? 0 : INVALID;
            case RECEIVE:
                return (request.Flags & ~NONBLOCK) == 0 && request.Count == 0 && InWindow(request, windowBytes) ? 0 : INVALID;
            default:
                return UNKNOWN_OPERATION;
        }
    }

    /// <summary>
    /// The bytes a frame takes packed: its header and its bytes, rounded up to a multiple of 8.
    /// </summary>
    /// <param name="frameBytes">The frame's bytes.</param>
    /// <returns>The packed bytes.</returns>
    public static int PackedBytes(int frameBytes) => (FRAME_HEADER_BYTES + frameBytes + 7) & ~7;

    /// <summary>
    /// Packs a frame at the start of the target, its header first.
    /// </summary>
    /// <param name="target">Where the frame goes; at least <see cref="PackedBytes"/> of it.</param>
    /// <param name="frame">The frame.</param>
    /// <returns>The packed bytes.</returns>
    public static int Pack(Span<byte> target, ReadOnlySpan<byte> frame)
    {
        var packed = PackedBytes(frame.Length);
        target[..packed].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(target, frame.Length);
        frame.CopyTo(target[FRAME_HEADER_BYTES..]);
        return packed;
    }

    /// <summary>
    /// The frames packed in bytes, each checked: its header within the bytes, its reserved word zero, its length from an
    /// Ethernet header to the MTU's frame, the last one ending the bytes.
    /// </summary>
    /// <param name="bytes">The packed frames.</param>
    /// <param name="count">The frames the request names.</param>
    /// <param name="mtu">The adapter's MTU.</param>
    /// <param name="frames">Each frame's offset and length in the bytes.</param>
    /// <returns>Zero, or the errno value the driver answers.</returns>
    public static int Unpack(ReadOnlySpan<byte> bytes, int count, int mtu, List<(int Offset, int Length)> frames)
    {
        frames.Clear();
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            if (bytes.Length - offset < FRAME_HEADER_BYTES || BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]) != 0)
                return INVALID;
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
            if (length < HEADER_BYTES || length > HEADER_BYTES + mtu)
                return FRAME_SIZE;
            if (length > bytes.Length - offset - FRAME_HEADER_BYTES)
                return INVALID;
            frames.Add((offset + FRAME_HEADER_BYTES, length));
            offset += Math.Min(PackedBytes(length), bytes.Length - offset);
        }
        return offset == bytes.Length ? 0 : INVALID;
    }

    private static bool InWindow(PacketRequest request, long windowBytes) =>
        request.Length > 0 && request.Window >= 0 && request.Window % 8 == 0 && request.Window <= windowBytes - request.Length;

    private static void CheckHeader(ReadOnlySpan<byte> bytes, int size, string what)
    {
        if (bytes.Length != size || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VERSION ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != size)
            throw new FormatException($"A packet {what} of {bytes.Length} bytes is not of version {VERSION}.");
    }

    #endregion
}

/// <summary>
/// A packet request (WitPacketRequest).
/// </summary>
/// <param name="Operation">ATTACH, INFO, SEND or RECEIVE.</param>
/// <param name="Flags">RECEIVE: NONBLOCK.</param>
/// <param name="Length">ATTACH: the window's bytes; SEND: the packed frames' bytes; RECEIVE: the bytes they may fill.</param>
/// <param name="Window">The offset of the packed frames in the window, a multiple of 8.</param>
/// <param name="Count">SEND: the frames.</param>
public readonly record struct PacketRequest(uint Operation, uint Flags, long Length, long Window = 0, int Count = 0);

/// <summary>
/// A packet reply (WitPacketReply).
/// </summary>
/// <param name="Operation">The request's operation.</param>
/// <param name="Error">Zero or an errno value.</param>
/// <param name="Length">RECEIVE: the packed frames' bytes; SEND: the bytes sent; ATTACH: the window's.</param>
/// <param name="Count">SEND and RECEIVE: the frames.</param>
/// <param name="Link">INFO: LINK_UP or zero.</param>
/// <param name="Address">INFO: the adapter's MAC address, its first byte the lowest.</param>
/// <param name="Mtu">INFO: the largest payload of a frame.</param>
public readonly record struct PacketReply(uint Operation, int Error, long Length = 0, int Count = 0, uint Link = 0, ulong Address = 0,
    ushort Mtu = 0);
