using System;
using System.Buffers.Binary;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// A control transfer's setup packet (USB 2.0 §9.3): eight bytes, which xHCI carries in a Setup Stage TRB as its
/// immediate data. The requests a host's enumeration, a hub's class (H2.2) and HID need.
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
    /// bRequest GET_STATUS.
    /// </summary>
    public const byte GET_STATUS = 0;

    /// <summary>
    /// bRequest CLEAR_FEATURE.
    /// </summary>
    public const byte CLEAR_FEATURE = 1;

    /// <summary>
    /// bRequest SET_FEATURE.
    /// </summary>
    public const byte SET_FEATURE = 3;

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
    /// SET_PROTOCOL's wValue for the report protocol, whose reports the report descriptor describes.
    /// </summary>
    public const ushort REPORT_PROTOCOL = 1;

    /// <summary>
    /// The endpoint feature ENDPOINT_HALT, which CLEAR_FEATURE clears after a stall.
    /// </summary>
    public const ushort ENDPOINT_HALT = 0;

    /// <summary>
    /// bmRequestType: device to host, standard, the device.
    /// </summary>
    private const byte IN_STANDARD_DEVICE = 0x80;

    /// <summary>
    /// bmRequestType: device to host, standard, an interface.
    /// </summary>
    private const byte IN_STANDARD_INTERFACE = 0x81;

    /// <summary>
    /// bmRequestType: host to device, standard, the device.
    /// </summary>
    private const byte OUT_STANDARD_DEVICE = 0x00;

    /// <summary>
    /// bmRequestType: host to device, standard, an endpoint.
    /// </summary>
    private const byte OUT_STANDARD_ENDPOINT = 0x02;

    /// <summary>
    /// bmRequestType: host to device, class, an interface.
    /// </summary>
    private const byte OUT_CLASS_INTERFACE = 0x21;

    private const byte IN_CLASS_INTERFACE = 0xA1;

    // Bulk-Only Transport's class requests (USB MSC BOT 1.0 §3.1, §3.2).
    private const byte BULK_ONLY_RESET = 0xFF;

    private const byte GET_MAX_LUN = 0xFE;

    /// <summary>
    /// bmRequestType: device to host, class, the device (a hub's descriptor).
    /// </summary>
    private const byte IN_CLASS_DEVICE = 0xA0;

    /// <summary>
    /// bmRequestType: host to device, class, the device (a hub's own feature).
    /// </summary>
    private const byte OUT_CLASS_DEVICE = 0x20;

    /// <summary>
    /// bmRequestType: device to host, class, other (a hub's port).
    /// </summary>
    private const byte IN_CLASS_OTHER = 0xA3;

    /// <summary>
    /// bmRequestType: host to device, class, other (a hub's port).
    /// </summary>
    private const byte OUT_CLASS_OTHER = 0x23;

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
    /// Bulk-Only Transport's Get Max LUN of an interface (E3): one byte, the highest logical unit; a STALL means one unit.
    /// </summary>
    /// <param name="interfaceNumber">The interface.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup GetMaxLun(byte interfaceNumber) => new(IN_CLASS_INTERFACE, GET_MAX_LUN, 0, interfaceNumber, 1);

    /// <summary>
    /// Bulk-Only Transport's Bulk-Only Mass Storage Reset of an interface (E3), the first step of its reset recovery.
    /// </summary>
    /// <param name="interfaceNumber">The interface.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup BulkOnlyReset(byte interfaceNumber) => new(OUT_CLASS_INTERFACE, BULK_ONLY_RESET, 0, interfaceNumber, 0);

    /// <summary>
    /// GET_DESCRIPTOR of an interface's HID report descriptor (HID 1.11 §7.1.1).
    /// </summary>
    /// <param name="interfaceNumber">The interface.</param>
    /// <param name="length">The descriptor's length, as the HID descriptor names it.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup GetReportDescriptor(byte interfaceNumber, ushort length) =>
        new(IN_STANDARD_INTERFACE, GET_DESCRIPTOR, (ushort)(UsbDescriptors.REPORT << 8), interfaceNumber, length);

    /// <summary>
    /// CLEAR_FEATURE of ENDPOINT_HALT: the device's endpoint takes traffic again after it stalled.
    /// </summary>
    /// <param name="endpointAddress">The endpoint's bEndpointAddress.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup ClearEndpointHalt(byte endpointAddress) => new(OUT_STANDARD_ENDPOINT, CLEAR_FEATURE, ENDPOINT_HALT, endpointAddress, 0);

    /// <summary>
    /// A hub's GET_DESCRIPTOR of its hub descriptor.
    /// </summary>
    /// <param name="length">The bytes asked for.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup GetHubDescriptor(ushort length) => new(IN_CLASS_DEVICE, GET_DESCRIPTOR, (ushort)(UsbHub.DESCRIPTOR << 8), 0, length);

    /// <summary>
    /// A hub's GET_STATUS of itself: four bytes of status and change, the local power and the over-current.
    /// </summary>
    /// <returns>The packet.</returns>
    public static UsbSetup GetHubStatus() => new(IN_CLASS_DEVICE, GET_STATUS, 0, 0, 4);

    /// <summary>
    /// A hub's CLEAR_FEATURE of itself: <see cref="UsbHub.C_HUB_LOCAL_POWER"/> or the over-current's change after it.
    /// </summary>
    /// <param name="feature">The feature.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup ClearHubFeature(ushort feature) => new(OUT_CLASS_DEVICE, CLEAR_FEATURE, feature, 0, 0);

    /// <summary>
    /// A hub's GET_STATUS of a port: four bytes of status and change.
    /// </summary>
    /// <param name="port">The port, from 1.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup GetPortStatus(int port) => new(IN_CLASS_OTHER, GET_STATUS, 0, (ushort)port, 4);

    /// <summary>
    /// A hub's SET_FEATURE of a port: <see cref="UsbHub.PORT_POWER"/> or <see cref="UsbHub.PORT_RESET"/>.
    /// </summary>
    /// <param name="port">The port, from 1.</param>
    /// <param name="feature">The feature.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup SetPortFeature(int port, ushort feature) => new(OUT_CLASS_OTHER, SET_FEATURE, feature, (ushort)port, 0);

    /// <summary>
    /// A hub's CLEAR_FEATURE of a port: a change feature acknowledged.
    /// </summary>
    /// <param name="port">The port, from 1.</param>
    /// <param name="feature">The feature.</param>
    /// <returns>The packet.</returns>
    public static UsbSetup ClearPortFeature(int port, ushort feature) => new(OUT_CLASS_OTHER, CLEAR_FEATURE, feature, (ushort)port, 0);

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
