using System;
using System.Buffers.Binary;

namespace OutWit.OS.Hardware;

/// <summary>
/// The input protocol (witos/input.h, plan step C2.2): what an input device's driver serves on the channel it offers
/// with its ready answer — a window attached, then one READ outstanding, answered with the events the device reported,
/// each eight bytes of evdev's values.
/// </summary>
public static class InputMessages
{
    #region Constants

    /// <summary>
    /// WIT_INPUT_VERSION.
    /// </summary>
    public const uint VERSION = 1;

    /// <summary>
    /// Bytes of a request and of a reply.
    /// </summary>
    public const int SIZE = 32;

    /// <summary>
    /// ATTACH: the window in the one handle slot, Length its bytes.
    /// </summary>
    public const uint ATTACH = 1;

    /// <summary>
    /// READ: up to Length / EVENT_BYTES events into the window at Window.
    /// </summary>
    public const uint READ = 2;

    /// <summary>
    /// READ's flag: answer at once, with no events when none came.
    /// </summary>
    public const uint NONBLOCK = 1;

    /// <summary>
    /// The window: one page.
    /// </summary>
    public const int WINDOW_BYTES = 4096;

    /// <summary>
    /// The events the driver keeps for a client that reads slowly.
    /// </summary>
    public const int QUEUE = 256;

    /// <summary>
    /// Bytes of one event.
    /// </summary>
    public const int EVENT_BYTES = 8;

    /// <summary>
    /// A synchronization event, which ends a report.
    /// </summary>
    public const ushort SYN = 0;

    /// <summary>
    /// A key event: the code is the key, the value 1 for down, 0 for up and 2 for a repeat.
    /// </summary>
    public const ushort KEY = 1;

    /// <summary>
    /// EBUSY: a second READ while one waits.
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
    public static byte[] Encode(InputRequest request)
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
    public static InputRequest DecodeRequest(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, "request");
        return new InputRequest(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]));
    }

    /// <summary>
    /// Encodes a reply.
    /// </summary>
    /// <param name="reply">The reply.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Encode(InputReply reply)
    {
        var bytes = new byte[SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), reply.Operation);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), reply.Error);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), reply.Length);
        return bytes;
    }

    /// <summary>
    /// Decodes a reply.
    /// </summary>
    /// <param name="bytes">The message's bytes.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="FormatException">The bytes are no reply of this version.</exception>
    public static InputReply DecodeReply(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, "reply");
        return new InputReply(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]));
    }

    /// <summary>
    /// Writes an event's eight bytes.
    /// </summary>
    /// <param name="bytes">Where, at least EVENT_BYTES.</param>
    /// <param name="input">The event.</param>
    public static void WriteEvent(Span<byte> bytes, InputEvent input)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, input.Type);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], input.Code);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], input.Value);
    }

    /// <summary>
    /// Reads an event's eight bytes.
    /// </summary>
    /// <param name="bytes">The bytes, at least EVENT_BYTES.</param>
    /// <returns>The event.</returns>
    public static InputEvent ReadEvent(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt16LittleEndian(bytes), BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]));

    /// <summary>
    /// Checks a request against the window attached: zero, or the errno value the driver answers.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="windowBytes">The window's bytes; zero before an ATTACH.</param>
    /// <returns>Zero or an errno value.</returns>
    public static int Check(InputRequest request, long windowBytes)
    {
        switch (request.Operation)
        {
            case ATTACH:
                return request.Flags == 0 && request.Window == 0 && request.Length == WINDOW_BYTES ? 0 : INVALID;
            case READ:
                return (request.Flags & ~NONBLOCK) == 0 && request.Length >= EVENT_BYTES && request.Window >= 0 &&
                    request.Window % EVENT_BYTES == 0 && request.Window <= windowBytes - request.Length ? 0 : INVALID;
            default:
                return UNKNOWN_OPERATION;
        }
    }

    private static void CheckHeader(ReadOnlySpan<byte> bytes, string what)
    {
        if (bytes.Length != SIZE || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VERSION ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != SIZE)
            throw new FormatException($"An input {what} of {bytes.Length} bytes is not of version {VERSION}.");
    }

    #endregion
}

/// <summary>
/// An input request (WitInputRequest).
/// </summary>
/// <param name="Operation">ATTACH or READ.</param>
/// <param name="Flags">READ: NONBLOCK.</param>
/// <param name="Length">ATTACH: the window's bytes; READ: the bytes the events may fill.</param>
/// <param name="Window">The offset of the events in the window.</param>
public readonly record struct InputRequest(uint Operation, uint Flags, long Length, long Window = 0);

/// <summary>
/// An input reply (WitInputReply).
/// </summary>
/// <param name="Operation">The request's operation.</param>
/// <param name="Error">Zero or an errno value.</param>
/// <param name="Length">The bytes of the events written; ATTACH: the window's.</param>
public readonly record struct InputReply(uint Operation, int Error, long Length = 0);

/// <summary>
/// An input event (WitInputEvent): evdev's type, code and value.
/// </summary>
/// <param name="Type">KEY or SYN.</param>
/// <param name="Code">The key's code.</param>
/// <param name="Value">1 down, 0 up, 2 repeat.</param>
public readonly record struct InputEvent(ushort Type, ushort Code, int Value);
