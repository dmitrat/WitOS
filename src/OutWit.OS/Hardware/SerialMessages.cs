using System;
using System.Buffers.Binary;

namespace OutWit.OS.Hardware;

/// <summary>
/// The serial protocol's messages (witos/serial.h, plan step N4.3a): what the driver of a byte-stream device serves
/// on its channel, and what the terminal service sends it. One request at a time of each kind; bytes travel in the
/// window the client attached.
/// </summary>
public static class SerialMessages
{
    #region Constants

    /// <summary>
    /// WIT_SERIAL_VERSION.
    /// </summary>
    public const uint VERSION = 1;

    /// <summary>
    /// The bytes of a request and of a reply.
    /// </summary>
    public const int SIZE = 32;

    /// <summary>
    /// WIT_SERIAL_ATTACH: the window in the one handle slot.
    /// </summary>
    public const uint ATTACH = 1;

    /// <summary>
    /// WIT_SERIAL_WRITE: bytes of the window to the device.
    /// </summary>
    public const uint WRITE = 2;

    /// <summary>
    /// WIT_SERIAL_READ: bytes the device received into the window.
    /// </summary>
    public const uint READ = 3;

    /// <summary>
    /// WIT_SERIAL_SIZE: the device's columns and rows.
    /// </summary>
    public const uint SIZE_QUERY = 4;

    /// <summary>
    /// WIT_SERIAL_NONBLOCK: a READ answered at once.
    /// </summary>
    public const uint NONBLOCK = 1;

    /// <summary>
    /// WIT_SERIAL_WINDOW_BYTES.
    /// </summary>
    public const int WINDOW_BYTES = 16 * 4096;

    /// <summary>
    /// EIO: the device failed.
    /// </summary>
    public const int DEVICE_ERROR = 5;

    /// <summary>
    /// EBUSY: a READ while one waits.
    /// </summary>
    public const int BUSY = 16;

    /// <summary>
    /// EINVAL: a request the driver cannot serve as it is.
    /// </summary>
    public const int INVALID = 22;

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
    public static byte[] Encode(SerialRequest request)
    {
        var bytes = new byte[SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), request.Operation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), request.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), request.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), request.Window);
        return bytes;
    }

    /// <summary>
    /// Decodes a request.
    /// </summary>
    /// <param name="bytes">The message's bytes.</param>
    /// <returns>The request.</returns>
    /// <exception cref="FormatException">The bytes are no request of this version.</exception>
    public static SerialRequest DecodeRequest(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, "request");
        return new SerialRequest(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]));
    }

    /// <summary>
    /// Encodes a reply.
    /// </summary>
    /// <param name="reply">The reply.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Encode(SerialReply reply)
    {
        var bytes = new byte[SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), reply.Operation);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), reply.Error);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), reply.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), reply.Columns);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), reply.Rows);
        return bytes;
    }

    /// <summary>
    /// Decodes a reply.
    /// </summary>
    /// <param name="bytes">The message's bytes.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="FormatException">The bytes are no reply of this version.</exception>
    public static SerialReply DecodeReply(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, "reply");
        return new SerialReply(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]));
    }

    /// <summary>
    /// Checks a request against the window attached: zero, or the errno value the driver answers.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="windowBytes">The window's bytes; zero before an ATTACH.</param>
    /// <returns>Zero or an errno value.</returns>
    public static int Check(SerialRequest request, long windowBytes)
    {
        switch (request.Operation)
        {
            case ATTACH:
                return request.Flags == 0 && request.Window == 0 && request.Length > 0 && request.Length <= WINDOW_BYTES &&
                    request.Length % 4096 == 0 ? 0 : INVALID;
            case SIZE_QUERY:
                return request.Flags == 0 && request.Length == 0 && request.Window == 0 ? 0 : INVALID;
            case WRITE:
            case READ:
                return (request.Flags & ~(request.Operation == READ ? NONBLOCK : 0)) == 0 && request.Length > 0 && request.Window >= 0 &&
                    request.Window <= windowBytes - request.Length ? 0 : INVALID;
            default:
                return UNKNOWN_OPERATION;
        }
    }

    private static void CheckHeader(ReadOnlySpan<byte> bytes, string what)
    {
        if (bytes.Length != SIZE || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VERSION ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != SIZE)
            throw new FormatException($"A serial {what} of {bytes.Length} bytes is not of version {VERSION}.");
    }

    #endregion
}

/// <summary>
/// A serial request (WitSerialRequest).
/// </summary>
/// <param name="Operation">ATTACH, WRITE, READ or SIZE_QUERY.</param>
/// <param name="Flags">READ: NONBLOCK.</param>
/// <param name="Length">Bytes to write or at most to read; ATTACH: the window's bytes.</param>
/// <param name="Window">The offset of the bytes in the window.</param>
public readonly record struct SerialRequest(uint Operation, uint Flags, long Length, long Window = 0);

/// <summary>
/// A serial reply (WitSerialReply).
/// </summary>
/// <param name="Operation">The request's operation.</param>
/// <param name="Error">Zero or an errno value.</param>
/// <param name="Length">Bytes written or read; ATTACH: the window's.</param>
/// <param name="Columns">SIZE_QUERY: the device's columns, or zero.</param>
/// <param name="Rows">SIZE_QUERY: the device's rows, or zero.</param>
public readonly record struct SerialReply(uint Operation, int Error, long Length = 0, int Columns = 0, int Rows = 0);
