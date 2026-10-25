using System;
using System.Buffers.Binary;

namespace OutWit.OS.Hardware.E1000;

/// <summary>
/// What an e1000e's rings hold (Intel 82574 datasheet §7.1.4.1 and §7.2.10.1, plan step H4): legacy descriptors of 16
/// bytes, the format the adapter uses until RFCTL.EXTEN asks for extended receive descriptors and a transmit descriptor's
/// DEXT for extended transmit ones. A receive descriptor names its buffer, and the adapter writes back the frame's length,
/// its status — done, end of packet — and its errors. A transmit descriptor names a frame's bytes and asks the adapter to
/// add the frame check sequence and to report the descriptor done.
/// </summary>
public static class E1000Descriptors
{
    #region Constants

    /// <summary>
    /// Bytes of a descriptor.
    /// </summary>
    public const int BYTES = 16;

    /// <summary>
    /// The alignment and the multiple of a ring's bytes (RDLEN, TDLEN).
    /// </summary>
    public const int RING_ALIGNMENT = 128;

    /// <summary>
    /// A receive descriptor's status: the adapter wrote it back (DD).
    /// </summary>
    public const byte RECEIVE_DONE = 0x01;

    /// <summary>
    /// A receive descriptor's status: the last descriptor of its frame (EOP).
    /// </summary>
    public const byte RECEIVE_END = 0x02;

    /// <summary>
    /// The receive errors that make a frame unusable: CRC or alignment (CE), symbol (SE), sequence (SEQ) and data (RXE).
    /// The checksum errors (TCPE, IPE) concern offloads the driver does not ask for.
    /// </summary>
    public const byte RECEIVE_ERRORS = 0x01 | 0x02 | 0x04 | 0x80;

    /// <summary>
    /// A transmit descriptor's command: the last descriptor of its frame (EOP).
    /// </summary>
    public const byte TRANSMIT_END = 0x01;

    /// <summary>
    /// A transmit descriptor's command: the adapter adds the frame check sequence (IFCS).
    /// </summary>
    public const byte TRANSMIT_FCS = 0x02;

    /// <summary>
    /// A transmit descriptor's command: the adapter reports the descriptor done (RS).
    /// </summary>
    public const byte TRANSMIT_REPORT = 0x08;

    /// <summary>
    /// A transmit descriptor's status: the adapter is done with it (DD).
    /// </summary>
    public const byte TRANSMIT_DONE = 0x01;

    // A receive descriptor: the buffer's address, then the length, the checksum, the status, the errors and the VLAN tag.
    private const int ADDRESS = 0;
    private const int LENGTH = 8;
    private const int RECEIVE_STATUS = 12;
    private const int RECEIVE_ERROR = 13;

    // A transmit descriptor: the buffer's address, then the length, CSO, the command, the status, CSS and the VLAN tag.
    private const int TRANSMIT_COMMAND = 11;
    private const int TRANSMIT_STATUS = 12;

    #endregion

    #region Functions

    /// <summary>
    /// Gives a receive descriptor its buffer, the written-back fields cleared.
    /// </summary>
    /// <param name="descriptor">The descriptor's bytes.</param>
    /// <param name="buffer">The buffer's physical address.</param>
    public static void WriteReceive(Span<byte> descriptor, ulong buffer)
    {
        Check(descriptor.Length);
        descriptor[..BYTES].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[ADDRESS..], buffer);
    }

    /// <summary>
    /// Reads what the adapter wrote back into a receive descriptor.
    /// </summary>
    /// <param name="descriptor">The descriptor's bytes.</param>
    /// <returns>The frame's length, status and errors.</returns>
    public static E1000Received ReadReceive(ReadOnlySpan<byte> descriptor)
    {
        Check(descriptor.Length);
        return new E1000Received(BinaryPrimitives.ReadUInt16LittleEndian(descriptor[LENGTH..]), descriptor[RECEIVE_STATUS], descriptor[RECEIVE_ERROR]);
    }

    /// <summary>
    /// Writes a transmit descriptor of one whole frame: the adapter adds the frame check sequence and reports the
    /// descriptor done.
    /// </summary>
    /// <param name="descriptor">The descriptor's bytes.</param>
    /// <param name="buffer">The frame's physical address.</param>
    /// <param name="length">The frame's bytes, without the frame check sequence.</param>
    public static void WriteTransmit(Span<byte> descriptor, ulong buffer, int length)
    {
        Check(descriptor.Length);
        if (length is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length), $"A frame of {length} bytes.");
        descriptor[..BYTES].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[ADDRESS..], buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[LENGTH..], (ushort)length);
        descriptor[TRANSMIT_COMMAND] = TRANSMIT_END | TRANSMIT_FCS | TRANSMIT_REPORT;
    }

    /// <summary>
    /// Whether the adapter is done with a transmit descriptor.
    /// </summary>
    /// <param name="descriptor">The descriptor's bytes.</param>
    /// <returns>Whether its status reports it done.</returns>
    public static bool TransmitDone(ReadOnlySpan<byte> descriptor)
    {
        Check(descriptor.Length);
        return (descriptor[TRANSMIT_STATUS] & TRANSMIT_DONE) != 0;
    }

    private static void Check(int length)
    {
        if (length < BYTES)
            throw new ArgumentOutOfRangeException(nameof(length), $"A descriptor of {length} bytes.");
    }

    #endregion
}

/// <summary>
/// What the adapter wrote back into a receive descriptor.
/// </summary>
/// <param name="Length">The bytes it wrote into the buffer.</param>
/// <param name="Status">Its status: DD, EOP and the offloads' bits.</param>
/// <param name="Errors">Its errors.</param>
public readonly record struct E1000Received(int Length, byte Status, byte Errors)
{
    #region Properties

    /// <summary>
    /// Whether the adapter wrote the descriptor back.
    /// </summary>
    public bool Done => (Status & E1000Descriptors.RECEIVE_DONE) != 0;

    /// <summary>
    /// Whether the descriptor holds a whole frame without errors: the last of its frame, whose errors leave it usable.
    /// </summary>
    public bool Usable => (Status & E1000Descriptors.RECEIVE_END) != 0 && (Errors & E1000Descriptors.RECEIVE_ERRORS) == 0;

    #endregion
}
