using System;

namespace OutWit.OS.Networking;

/// <summary>
/// An Ethernet MAC address (plan step W1.2a), its first byte the lowest of the value, as the packet protocol reports it
/// (witos/packet.h).
/// </summary>
/// <param name="Value">The six bytes as the low 48 bits.</param>
public readonly record struct MacAddress(ulong Value)
{
    #region Constants

    /// <summary>
    /// ff:ff:ff:ff:ff:ff.
    /// </summary>
    public static readonly MacAddress Broadcast = new(0xFFFF_FFFF_FFFFUL);

    /// <summary>
    /// 00:00:00:00:00:00: no address.
    /// </summary>
    public static readonly MacAddress None = new(0);

    #endregion

    #region Functions

    /// <summary>
    /// Reads six bytes, the first byte first.
    /// </summary>
    /// <param name="bytes">At least six bytes.</param>
    /// <returns>The address.</returns>
    public static MacAddress Read(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (var i = 0; i < 6; i++)
            value |= (ulong)bytes[i] << (8 * i);
        return new MacAddress(value);
    }

    /// <summary>
    /// Writes the six bytes, the first byte first.
    /// </summary>
    /// <param name="bytes">At least six bytes.</param>
    public void Write(Span<byte> bytes)
    {
        for (var i = 0; i < 6; i++)
            bytes[i] = (byte)(Value >> (8 * i));
    }

    /// <inheritdoc />
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[6];
        Write(bytes);
        return $"{bytes[0]:x2}:{bytes[1]:x2}:{bytes[2]:x2}:{bytes[3]:x2}:{bytes[4]:x2}:{bytes[5]:x2}";
    }

    #endregion
}
