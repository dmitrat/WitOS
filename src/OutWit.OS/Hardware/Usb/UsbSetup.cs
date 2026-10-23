using System;
using System.Buffers.Binary;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// A control transfer's setup packet (USB 2.0 §9.3): eight bytes, which xHCI carries in a Setup Stage TRB as its
/// immediate data. The requests a host's enumeration and the HID boot protocol need.
/// </summary>
/// <param name="RequestType">bmRequestType: the direction in bit 7 (1 device to host), the type in bits 6–5, the recipient in bits 4–0.</param>
/// <param name="Request">bRequest.</param>
/// <param name="Value">wValue.</param>
/// <param name="Index">wIndex.</param>
/// <param name="Length">wLength: the bytes of the data stage.</param>
public readonly record struct UsbSetup(byte RequestType, byte Request, ushort Value, ushort Index, ushort Length)
{
    #region Constants

    /// <summary>
    /// bRequest GET_DESCRIPTOR.
    /// </summary>
    public const byte GET_DESCRIPTOR = 6;

    /// <summary>
    /// bRequest SET_CONFIGURATION.
    /// </summary>
    public const byte SET_CONFIGURATION = 9;

    /// <summary>
    /// The HID class's bRequest SET_IDLE.
    /// </summary>
    public const byte SET_IDLE = 0x0A;

    /// <summary>
    /// The HID class's bRequest SET_PROTOCOL.
    /// </summary>
    public const byte SET_PROTOCOL = 0x0B;

    /// <summary>
    /// SET_PROTOCOL's wValue for the boot protocol.
    /// </summary>
    public const ushort BOOT_PROTOCOL = 0;

    /// <summary>
    /// bmRequestType: device to host, standard, the device.
    /// </summary>
    private const byte IN_STANDARD_DEVICE = 0x80;

    /// <summary>
    /// bmRequestType: host to device, standard, the device.
    /// </summary>
    private const byte OUT_STANDARD_DEVICE = 0x00;

    /// <summary>
    /// bmRequestType: host to device, class, an interface.
    /// </summary>
    private const byte OUT_CLASS_INTERFACE = 0x21;

    #endregion

    #region Functions

    /// <summary>
    /// GET_DESCRIPTOR of the device.
    /// </summary>
    /// <param name="type">The descriptor's type.</param>
    /// <param name="index">Its index.</param>
    /// <param name="length">The bytes asked for.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup GetDescriptor(byte type, byte index, ushort length) =>
        new(IN_STANDARD_DEVICE, GET_DESCRIPTOR, (ushort)(type << 8 | index), 0, length);

    /// <summary>
    /// SET_CONFIGURATION.
    /// </summary>
    /// <param name="value">The configuration's bConfigurationValue.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup SetConfiguration(byte value) => new(OUT_STANDARD_DEVICE, SET_CONFIGURATION, value, 0, 0);

    /// <summary>
    /// The HID class's SET_PROTOCOL of an interface.
    /// </summary>
    /// <param name="interfaceNumber">The interface.</param>
    /// <param name="protocol">0 boot, 1 report.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup SetProtocol(byte interfaceNumber, ushort protocol) =>
        new(OUT_CLASS_INTERFACE, SET_PROTOCOL, protocol, interfaceNumber, 0);

    /// <summary>
    /// The HID class's SET_IDLE of an interface: with a duration of zero the device reports only on a change.
    /// </summary>
    /// <param name="interfaceNumber">The interface.</param>
    /// <param name="duration">The duration in units of 4 ms, zero for none.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup SetIdle(byte interfaceNumber, byte duration) =>
        new(OUT_CLASS_INTERFACE, SET_IDLE, (ushort)(duration << 8), interfaceNumber, 0);

    /// <summary>
    /// The packet's eight bytes as the 64-bit little-endian word a Setup Stage TRB carries.
    /// </summary>
    /// <returns>The word.</returns>
    public ulong Encode()
    {
        Span<byte> bytes = stackalloc byte[8];
        bytes[0] = RequestType;
        bytes[1] = Request;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], Value);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], Index);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], Length);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    #endregion

    #region Properties

    /// <summary>
    /// Whether the data stage goes from the device to the host.
    /// </summary>
    public bool IsIn => (RequestType & 0x80) != 0;

    #endregion
}
