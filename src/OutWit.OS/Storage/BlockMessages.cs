using System;
using System.Buffers.Binary;

namespace OutWit.OS.Storage;

/// <summary>
/// The block protocol's messages (witos/block.h, plan step D3.2): a client's request and a driver's reply, 40 bytes each.
/// </summary>
public static class BlockMessages
{
    #region Constants

    /// <summary>WIT_BLOCK_VERSION.</summary>
    public const uint VERSION = 1;

    /// <summary>The size of a request and of a reply.</summary>
    public const int SIZE = 40;

    /// <summary>The window, in the one handle slot.</summary>
    public const uint ATTACH = 1;

    /// <summary>The device's bytes, block, flags and transfer limit.</summary>
    public const uint INFO = 2;

    /// <summary>Bytes of the device into the window.</summary>
    public const uint READ = 3;

    /// <summary>Bytes of the window to the device.</summary>
    public const uint WRITE = 4;

    /// <summary>Every completed write durable.</summary>
    public const uint FLUSH = 5;

    /// <summary>A write's flag: durable when it completes.</summary>
    public const uint DURABLE = 1;

    /// <summary>The device refuses writes.</summary>
    public const uint READ_ONLY = 1;

    /// <summary>The device has a volatile cache a flush empties.</summary>
    public const uint FLUSHES = 2;

    /// <summary>The largest window: one mapping of an object.</summary>
    public const int WINDOW_BYTES = 64 * 4096;

    /// <summary>EIO: the device failed the request.</summary>
    public const int DEVICE_ERROR = 5;

    /// <summary>EINVAL: a request the driver cannot serve as it is.</summary>
    public const int INVALID = 22;

    /// <summary>EROFS: a write to a read-only device.</summary>
    public const int READ_ONLY_DEVICE = 30;

    /// <summary>EOPNOTSUPP: an operation of no version of the protocol.</summary>
    public const int UNKNOWN_OPERATION = 95;

    #endregion

    #region Functions

    /// <summary>
    /// The bytes of a request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Encode(BlockRequest request)
    {
        var bytes = new byte[SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), request.Operation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), request.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), request.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), request.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), request.Window);
        return bytes;
    }

    /// <summary>
    /// The request a message's bytes carry.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The request.</returns>
    /// <exception cref="FormatException">The bytes are not a request of this version.</exception>
    public static BlockRequest DecodeRequest(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, "request");
        return new BlockRequest(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]));
    }

    /// <summary>
    /// The bytes of a reply.
    /// </summary>
    /// <param name="reply">The reply.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Encode(BlockReply reply)
    {
        var bytes = new byte[SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), reply.Operation);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), reply.Error);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), reply.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), reply.BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), reply.Flags);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), reply.MaxTransfer);
        return bytes;
    }

    /// <summary>
    /// The reply a message's bytes carry.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="FormatException">The bytes are not a reply of this version.</exception>
    public static BlockReply DecodeReply(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, "reply");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]) != 0)
            throw new FormatException("A block reply's reserved word is not zero.");
        return new BlockReply(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]));
    }

    /// <summary>
    /// The errno value a driver answers a request with before anything reaches the device: zero for one it serves.
    /// ATTACH names a window of whole pages; INFO and FLUSH carry nothing; READ and WRITE name a positive length of whole
    /// blocks within the transfer limit, the device and the window, a write to a writable device alone. The window's
    /// handle is the driver's to check.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="info">The driver's INFO reply: the device's bytes, block, flags and transfer limit.</param>
    /// <param name="windowBytes">The bytes of the window attached; zero before ATTACH.</param>
    /// <returns>Zero, INVALID, READ_ONLY_DEVICE or UNKNOWN_OPERATION.</returns>
    public static int Check(BlockRequest request, BlockReply info, long windowBytes)
    {
        switch (request.Operation)
        {
            case ATTACH:
                return request.Flags == 0 && request.Offset == 0 && request.Window == 0 && request.Length > 0 &&
                    request.Length <= WINDOW_BYTES && request.Length % 4096 == 0 ? 0 : INVALID;
            case INFO:
            case FLUSH:
                return request.Flags == 0 && request.Offset == 0 && request.Length == 0 && request.Window == 0 ? 0 : INVALID;
            case READ:
            case WRITE:
                var block = info.BlockSize;
                if ((request.Flags & ~(request.Operation == WRITE ? DURABLE : 0)) != 0 || request.Length <= 0 || request.Offset < 0 ||
                    request.Window < 0 || request.Offset % block != 0 || request.Length % block != 0 || request.Window % block != 0 ||
                    request.Length > info.MaxTransfer || request.Offset > info.Length - request.Length ||
                    request.Window > windowBytes - request.Length)
                    return INVALID;
                return request.Operation == WRITE && (info.Flags & READ_ONLY) != 0 ? READ_ONLY_DEVICE : 0;
            default:
                return UNKNOWN_OPERATION;
        }
    }

    private static void CheckHeader(ReadOnlySpan<byte> bytes, string what)
    {
        if (bytes.Length != SIZE || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VERSION ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != SIZE)
            throw new FormatException($"A block {what} of {bytes.Length} bytes is not of version {VERSION}.");
    }

    #endregion
}
