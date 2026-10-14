using System;
using System.Buffers.Binary;

namespace OutWit.OS.Terminals;

/// <summary>
/// The terminal service's messages (witos/terminal.h, plan step N4.3): a client's requests on its session and the
/// service's replies, the attributes the discipline keeps, the events the service sends, and the device manager's
/// message that gives the service its console.
/// </summary>
public static class TerminalMessages
{
    #region Constants

    /// <summary>
    /// WIT_TERMINAL_VERSION.
    /// </summary>
    public const uint VERSION = 1;

    /// <summary>
    /// The bytes of a request and of a reply.
    /// </summary>
    public const int SIZE = 32;

    /// <summary>
    /// The bytes of the attributes.
    /// </summary>
    public const int ATTRIBUTES_SIZE = 48;

    /// <summary>
    /// The bytes of an event.
    /// </summary>
    public const int EVENT_BYTES = 16;

    /// <summary>
    /// WIT_TERMINAL_CONTROLS: the control characters.
    /// </summary>
    public const int CONTROLS = 32;

    /// <summary>
    /// WIT_TERMINAL_WINDOW_BYTES: a client's window.
    /// </summary>
    public const int WINDOW_BYTES = 4 * 4096;

    /// <summary>
    /// WIT_TERMINAL_PATH.
    /// </summary>
    public const string PATH = "/system/Terminal";

    public const uint CONNECT = 1;

    public const uint READ = 2;

    public const uint WRITE = 3;

    public const uint GET_ATTRIBUTES = 4;

    public const uint SET_ATTRIBUTES = 5;

    public const uint GET_SIZE = 6;

    public const uint POLL = 7;

    public const uint FLUSH = 8;

    public const uint DRAIN = 9;

    public const uint DISCONNECT = 10;

    public const uint DEVICE = 16;

    public const uint NONBLOCK = 1;

    public const uint INPUT = 1;

    public const uint OUTPUT = 2;

    public const uint INPUT_READY = 1;

    public const uint OUTPUT_READY = 2;

    public const uint NOW = 0;

    public const uint AFTER_OUTPUT = 1;

    public const uint AFTER_FLUSH = 2;

    public const uint EVENT_INPUT = 1;

    public const uint EVENT_INTERRUPT = 2;

    public const uint EVENT_QUIT = 3;

    public const uint EVENT_SIZE = 4;

    public const uint EVENT_HANGUP = 5;

    /// <summary>
    /// EINTR: a signal character came before the bytes a READ waited for.
    /// </summary>
    public const int INTERRUPTED = 4;

    /// <summary>
    /// EAGAIN: a non-blocking READ found nothing.
    /// </summary>
    public const int AGAIN = 11;

    /// <summary>
    /// EINVAL: a request the service cannot serve as it is.
    /// </summary>
    public const int INVALID = 22;

    /// <summary>
    /// EPROTO: bytes that are no request of this version.
    /// </summary>
    public const int PROTOCOL_ERROR = 71;

    /// <summary>
    /// EOPNOTSUPP: an unknown operation.
    /// </summary>
    public const int UNKNOWN_OPERATION = 95;

    /// <summary>
    /// ESHUTDOWN: the service is stopping.
    /// </summary>
    public const int SHUTDOWN = 108;

    #endregion

    #region Functions

    /// <summary>
    /// Encodes a request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Encode(TerminalRequest request)
    {
        var bytes = new byte[SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), request.Operation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), request.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), request.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), request.Offset);
        return bytes;
    }

    /// <summary>
    /// Decodes a request.
    /// </summary>
    /// <param name="bytes">The message's bytes.</param>
    /// <returns>The request.</returns>
    /// <exception cref="FormatException">The bytes are no request of this version.</exception>
    public static TerminalRequest DecodeRequest(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, SIZE, "request");
        return new TerminalRequest(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]));
    }

    /// <summary>
    /// Encodes a reply.
    /// </summary>
    /// <param name="reply">The reply.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Encode(TerminalReply reply)
    {
        var bytes = new byte[SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), reply.Operation);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), reply.Error);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), reply.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), reply.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), reply.Columns);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(30), reply.Rows);
        return bytes;
    }

    /// <summary>
    /// Decodes a reply.
    /// </summary>
    /// <param name="bytes">The message's bytes.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="FormatException">The bytes are no reply of this version.</exception>
    public static TerminalReply DecodeReply(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, SIZE, "reply");
        return new TerminalReply(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[30..]));
    }

    /// <summary>
    /// Encodes the attributes into a window.
    /// </summary>
    /// <param name="attributes">The attributes.</param>
    /// <param name="bytes">ATTRIBUTES_SIZE bytes of the window.</param>
    public static void Encode(TerminalAttributes attributes, Span<byte> bytes)
    {
        if (bytes.Length < ATTRIBUTES_SIZE)
            throw new ArgumentException("The attributes need 48 bytes.", nameof(bytes));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, attributes.Input);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], attributes.Output);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], attributes.Control);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], attributes.Local);
        attributes.Characters.AsSpan(0, CONTROLS).CopyTo(bytes[16..]);
    }

    /// <summary>
    /// Decodes the attributes from a window.
    /// </summary>
    /// <param name="bytes">ATTRIBUTES_SIZE bytes of the window.</param>
    /// <returns>The attributes.</returns>
    public static TerminalAttributes DecodeAttributes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ATTRIBUTES_SIZE)
            throw new FormatException("The attributes need 48 bytes.");
        return new TerminalAttributes(BinaryPrimitives.ReadUInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            bytes.Slice(16, CONTROLS).ToArray());
    }

    /// <summary>
    /// Encodes an event.
    /// </summary>
    /// <param name="kind">EVENT_INPUT, EVENT_INTERRUPT, EVENT_QUIT, EVENT_SIZE or EVENT_HANGUP.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] EncodeEvent(uint kind)
    {
        var bytes = new byte[EVENT_BYTES];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), EVENT_BYTES);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), kind);
        return bytes;
    }

    /// <summary>
    /// Decodes an event.
    /// </summary>
    /// <param name="bytes">The message's bytes.</param>
    /// <returns>Its kind.</returns>
    /// <exception cref="FormatException">The bytes are no event of this version.</exception>
    public static uint DecodeEvent(ReadOnlySpan<byte> bytes)
    {
        CheckHeader(bytes, EVENT_BYTES, "event");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0)
            throw new FormatException("A terminal event's value is not zero.");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
    }

    /// <summary>
    /// Encodes the device manager's message that gives the service its console (DEVICE), as a request with no bytes
    /// of its own.
    /// </summary>
    /// <returns>Its bytes.</returns>
    public static byte[] EncodeDevice() => Encode(new TerminalRequest(DEVICE, 0, 0));

    private static void CheckHeader(ReadOnlySpan<byte> bytes, int size, string what)
    {
        if (bytes.Length != size || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VERSION ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != (uint)size)
            throw new FormatException($"A terminal {what} of {bytes.Length} bytes is not of version {VERSION}.");
    }

    #endregion
}

/// <summary>
/// A terminal request (WitTerminalRequest).
/// </summary>
/// <param name="Operation">The operation.</param>
/// <param name="Flags">READ: NONBLOCK; SET_ATTRIBUTES: when; FLUSH: what.</param>
/// <param name="Length">Bytes.</param>
/// <param name="Offset">The offset in the window.</param>
public readonly record struct TerminalRequest(uint Operation, uint Flags, long Length, long Offset = 0);

/// <summary>
/// A terminal reply (WitTerminalReply).
/// </summary>
/// <param name="Operation">The request's operation.</param>
/// <param name="Error">Zero or an errno value.</param>
/// <param name="Length">READ and WRITE: the bytes.</param>
/// <param name="Flags">POLL: the readiness.</param>
/// <param name="Columns">GET_SIZE: the columns.</param>
/// <param name="Rows">GET_SIZE: the rows.</param>
public readonly record struct TerminalReply(uint Operation, int Error, long Length = 0, uint Flags = 0, ushort Columns = 0, ushort Rows = 0);

/// <summary>
/// The discipline's attributes (WitTerminalAttributes): termios' flag words and control characters with Linux's
/// generic values.
/// </summary>
/// <param name="Input">c_iflag.</param>
/// <param name="Output">c_oflag.</param>
/// <param name="Control">c_cflag.</param>
/// <param name="Local">c_lflag.</param>
/// <param name="Characters">c_cc: CONTROLS bytes.</param>
public sealed record TerminalAttributes(uint Input, uint Output, uint Control, uint Local, byte[] Characters)
{
    /// <summary>
    /// Whether another attributes record holds the same flags and characters.
    /// </summary>
    /// <param name="other">The other record.</param>
    /// <returns>Whether they are equal.</returns>
    public bool Equals(TerminalAttributes? other) =>
        other is not null && Input == other.Input && Output == other.Output && Control == other.Control && Local == other.Local &&
        Characters.AsSpan().SequenceEqual(other.Characters);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Input, Output, Control, Local);
}
