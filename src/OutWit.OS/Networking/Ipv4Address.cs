using System;
using System.Buffers.Binary;

namespace OutWit.OS.Networking;

/// <summary>
/// An IPv4 address (plan step W1.2a), its first octet the highest byte of the value, as the network orders it.
/// </summary>
/// <param name="Value">The address as a 32-bit value.</param>
public readonly record struct Ipv4Address(uint Value)
{
    #region Constants

    /// <summary>
    /// 0.0.0.0: no address, or any address of a binding.
    /// </summary>
    public static readonly Ipv4Address Any = new(0);

    /// <summary>
    /// 255.255.255.255: the limited broadcast.
    /// </summary>
    public static readonly Ipv4Address Broadcast = new(0xFFFF_FFFF);

    /// <summary>
    /// 127.0.0.1.
    /// </summary>
    public static readonly Ipv4Address Loopback = new(0x7F00_0001);

    #endregion

    #region Constructors

    /// <summary>
    /// The address of four octets.
    /// </summary>
    public Ipv4Address(byte a, byte b, byte c, byte d)
        : this(((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d)
    {
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads an address in network order.
    /// </summary>
    /// <param name="bytes">At least four bytes.</param>
    /// <returns>The address.</returns>
    public static Ipv4Address Read(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt32BigEndian(bytes));

    /// <summary>
    /// Writes the address in network order.
    /// </summary>
    /// <param name="bytes">At least four bytes.</param>
    public void Write(Span<byte> bytes) => BinaryPrimitives.WriteUInt32BigEndian(bytes, Value);

    /// <summary>
    /// Parses dotted decimal.
    /// </summary>
    /// <param name="text">Four decimal octets separated by dots.</param>
    /// <returns>The address.</returns>
    /// <exception cref="FormatException">The text is no IPv4 address.</exception>
    public static Ipv4Address Parse(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4)
            throw new FormatException($"'{text}' is no IPv4 address.");
        uint value = 0;
        foreach (var part in parts)
        {
            if (!byte.TryParse(part, out var octet))
                throw new FormatException($"'{text}' is no IPv4 address.");
            value = (value << 8) | octet;
        }
        return new Ipv4Address(value);
    }

    /// <summary>
    /// Whether the address lies in the subnet of another under a mask.
    /// </summary>
    /// <param name="network">An address of the subnet.</param>
    /// <param name="mask">The subnet's mask.</param>
    /// <returns>Whether it does.</returns>
    public bool IsInSubnet(Ipv4Address network, Ipv4Address mask) => (Value & mask.Value) == (network.Value & mask.Value);

    /// <inheritdoc />
    public override string ToString() => $"{Value >> 24}.{(Value >> 16) & 0xFF}.{(Value >> 8) & 0xFF}.{Value & 0xFF}";

    #endregion

    #region Properties

    /// <summary>
    /// Whether the address is in 127.0.0.0/8.
    /// </summary>
    public bool IsLoopback => Value >> 24 == 127;

    /// <summary>
    /// Whether the address is a multicast one (224.0.0.0/4).
    /// </summary>
    public bool IsMulticast => Value >> 28 == 0xE;

    #endregion
}
