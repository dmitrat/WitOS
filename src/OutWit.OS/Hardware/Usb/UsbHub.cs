using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// The hub class of USB 2.0 (§11.23, §11.24, plan step H2.2): the hub descriptor a hub answers its class's
/// GET_DESCRIPTOR with, the four bytes of a port's status and change, and the port features a host sets and clears to
/// power, reset and acknowledge a port. A hub reports which of its ports changed on its interrupt endpoint, one bit a
/// port from bit 1 (bit 0 the hub itself).
/// </summary>
public static class UsbHub
{
    #region Constants

    /// <summary>
    /// bDescriptorType of a USB 2.0 hub descriptor.
    /// </summary>
    public const byte DESCRIPTOR = 0x29;

    /// <summary>
    /// The bytes a host asks for: the seven of the fixed part and the two bitmaps of up to 31 ports.
    /// </summary>
    public const ushort DESCRIPTOR_BYTES = 15;

    /// <summary>
    /// bDeviceProtocol of a high-speed hub with one Transaction Translator for every port.
    /// </summary>
    public const byte PROTOCOL_MULTIPLE_TT = 2;

    /// <summary>
    /// The port feature PORT_RESET.
    /// </summary>
    public const ushort PORT_RESET = 4;

    /// <summary>
    /// The port feature PORT_POWER.
    /// </summary>
    public const ushort PORT_POWER = 8;

    /// <summary>
    /// The port feature C_PORT_CONNECTION.
    /// </summary>
    public const ushort C_PORT_CONNECTION = 16;

    /// <summary>
    /// The port feature C_PORT_ENABLE.
    /// </summary>
    public const ushort C_PORT_ENABLE = 17;

    /// <summary>
    /// The port feature C_PORT_RESET.
    /// </summary>
    public const ushort C_PORT_RESET = 20;

    /// <summary>
    /// The hub feature C_HUB_LOCAL_POWER; C_HUB_OVER_CURRENT follows it.
    /// </summary>
    public const ushort C_HUB_LOCAL_POWER = 0;

    /// <summary>
    /// The highest port a route string names: each tier of the route is four bits.
    /// </summary>
    public const int ROUTABLE_PORTS = 15;

    /// <summary>
    /// The tiers of hubs below the root a route string holds.
    /// </summary>
    public const int TIERS = 5;

    #endregion

    #region Functions

    /// <summary>
    /// Reads a USB 2.0 hub descriptor.
    /// </summary>
    /// <param name="bytes">The descriptor's bytes.</param>
    /// <returns>The descriptor.</returns>
    /// <exception cref="FormatException">The bytes are not a hub descriptor.</exception>
    public static UsbHubDescriptor ReadDescriptor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 7 || bytes[1] != DESCRIPTOR || bytes[0] < 7)
            throw new FormatException($"Not a hub descriptor: {bytes.Length} bytes, type {(bytes.Length > 1 ? bytes[1] : 0)}.");
        return new UsbHubDescriptor(bytes[2], BinaryPrimitives.ReadUInt16LittleEndian(bytes[3..]), bytes[5] * 2);
    }

    /// <summary>
    /// Reads the four bytes GET_STATUS of a port answers.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The port's status and change.</returns>
    /// <exception cref="FormatException">Fewer than four bytes.</exception>
    public static UsbPortStatus ReadPortStatus(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
            throw new FormatException($"A port status of {bytes.Length} bytes.");
        return new UsbPortStatus(BinaryPrimitives.ReadUInt16LittleEndian(bytes), BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]));
    }

    /// <summary>
    /// The ports a status change report names: bit n of the bitmap for port n.
    /// </summary>
    /// <param name="bitmap">The report's bytes.</param>
    /// <param name="ports">The hub's ports.</param>
    /// <returns>The ports, ascending.</returns>
    public static int[] ChangedPorts(ReadOnlySpan<byte> bitmap, int ports)
    {
        var changed = new List<int>();
        for (var port = 1; port <= ports && port / 8 < bitmap.Length; port++)
        {
            if ((bitmap[port / 8] & 1 << port % 8) != 0)
                changed.Add(port);
        }
        return [.. changed];
    }

    #endregion
}

/// <summary>
/// A USB 2.0 hub descriptor's fields a host needs.
/// </summary>
/// <param name="Ports">bNbrPorts.</param>
/// <param name="Characteristics">wHubCharacteristics.</param>
/// <param name="PowerOnToGoodMilliseconds">bPwrOn2PwrGood in milliseconds: how long a port's power takes to be good.</param>
public sealed record UsbHubDescriptor(int Ports, ushort Characteristics, int PowerOnToGoodMilliseconds)
{
    /// <summary>
    /// The TT Think Time in units of eight full-speed bit times less one, as a Slot Context takes it: bits 6–5.
    /// </summary>
    public int ThinkTime => Characteristics >> 5 & 3;
}

/// <summary>
/// A port's status and change (USB 2.0 §11.24.2.7).
/// </summary>
/// <param name="Status">wPortStatus.</param>
/// <param name="Change">wPortChange.</param>
public readonly record struct UsbPortStatus(ushort Status, ushort Change)
{
    /// <summary>
    /// A device is connected.
    /// </summary>
    public bool Connected => (Status & 0x0001) != 0;

    /// <summary>
    /// The port is enabled: the device behind it was reset and takes traffic.
    /// </summary>
    public bool Enabled => (Status & 0x0002) != 0;

    /// <summary>
    /// The port is being reset.
    /// </summary>
    public bool Resetting => (Status & 0x0010) != 0;

    /// <summary>
    /// The port is powered.
    /// </summary>
    public bool Powered => (Status & 0x0100) != 0;

    /// <summary>
    /// The connection changed since the host last cleared C_PORT_CONNECTION.
    /// </summary>
    public bool ConnectionChanged => (Change & 0x0001) != 0;

    /// <summary>
    /// The port's enable changed since the host last cleared C_PORT_ENABLE: set by a reset that enabled it, or by the hub
    /// disabling the port after an error.
    /// </summary>
    public bool EnableChanged => (Change & 0x0002) != 0;

    /// <summary>
    /// The port's reset ended since the host last cleared C_PORT_RESET.
    /// </summary>
    public bool ResetChanged => (Change & 0x0010) != 0;

    /// <summary>
    /// The device's speed as a Slot Context and PORTSC name it: low, high or else full.
    /// </summary>
    public int Speed => (Status & 0x0200) != 0 ? XhciContexts.LOW_SPEED : (Status & 0x0400) != 0 ? XhciContexts.HIGH_SPEED : XhciContexts.FULL_SPEED;

    /// <summary>
    /// The change features set, each as the feature a host clears: C_PORT_CONNECTION (16) to C_PORT_RESET (20).
    /// </summary>
    public ushort[] ChangeFeatures()
    {
        var features = new List<ushort>();
        for (var bit = 0; bit < 5; bit++)
        {
            if ((Change & 1 << bit) != 0)
                features.Add((ushort)(UsbHub.C_PORT_CONNECTION + bit));
        }
        return [.. features];
    }
}
