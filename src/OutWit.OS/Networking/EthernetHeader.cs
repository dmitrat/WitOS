using System;
using System.Buffers.Binary;

namespace OutWit.OS.Networking;

/// <summary>
/// An Ethernet II header (plan step W1.2a): the destination, the source and the type of the payload; the frame check
/// sequence is the adapter's.
/// </summary>
/// <param name="Destination">The destination address.</param>
/// <param name="Source">The source address.</param>
/// <param name="Type">The payload's EtherType.</param>
public readonly record struct EthernetHeader(MacAddress Destination, MacAddress Source, ushort Type)
{
    #region Constants

    /// <summary>
    /// The header's bytes.
    /// </summary>
    public const int SIZE = 14;

    /// <summary>
    /// The EtherType of IPv4.
    /// </summary>
    public const ushort IPV4 = 0x0800;

    /// <summary>
    /// The EtherType of ARP.
    /// </summary>
    public const ushort ARP = 0x0806;

    #endregion

    #region Functions

    /// <summary>
    /// Reads the header of a frame.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <param name="header">The header, when the frame holds one.</param>
    /// <returns>Whether it does.</returns>
    public static bool TryRead(ReadOnlySpan<byte> frame, out EthernetHeader header)
    {
        header = default;
        if (frame.Length < SIZE)
            return false;
        header = new EthernetHeader(MacAddress.Read(frame), MacAddress.Read(frame[6..]), BinaryPrimitives.ReadUInt16BigEndian(frame[12..]));
        return true;
    }

    /// <summary>
    /// Writes the header at the start of a frame.
    /// </summary>
    /// <param name="frame">At least SIZE bytes.</param>
    public void Write(Span<byte> frame)
    {
        Destination.Write(frame);
        Source.Write(frame[6..]);
        BinaryPrimitives.WriteUInt16BigEndian(frame[12..], Type);
    }

    #endregion
}
