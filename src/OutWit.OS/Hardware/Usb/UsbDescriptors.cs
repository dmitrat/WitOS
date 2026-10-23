using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// The standard descriptors a USB device answers GET_DESCRIPTOR with (USB 2.0 §9.6, HID 1.11 §6.2.1), read as the
/// device wrote them: every length is checked against the bytes before a field is read, and a configuration's
/// descriptors are walked by their own lengths, the class descriptors between them kept with their interface.
/// </summary>
public static class UsbDescriptors
{
    #region Constants

    /// <summary>
    /// bDescriptorType of a device descriptor.
    /// </summary>
    public const byte DEVICE = 1;

    /// <summary>
    /// bDescriptorType of a configuration descriptor.
    /// </summary>
    public const byte CONFIGURATION = 2;

    /// <summary>
    /// bDescriptorType of an interface descriptor.
    /// </summary>
    public const byte INTERFACE = 4;

    /// <summary>
    /// bDescriptorType of an endpoint descriptor.
    /// </summary>
    public const byte ENDPOINT = 5;

    /// <summary>
    /// bDescriptorType of a HID descriptor.
    /// </summary>
    public const byte HID = 0x21;

    /// <summary>
    /// bDescriptorType of a HID report descriptor, as the HID descriptor names it.
    /// </summary>
    public const byte REPORT = 0x22;

    /// <summary>
    /// Bytes of a device descriptor.
    /// </summary>
    public const int DEVICE_BYTES = 18;

    /// <summary>
    /// Bytes of a configuration descriptor alone, whose wTotalLength gives the whole set.
    /// </summary>
    public const int CONFIGURATION_BYTES = 9;

    /// <summary>
    /// bInterfaceClass of HID.
    /// </summary>
    public const byte CLASS_HID = 3;

    /// <summary>
    /// bDeviceClass and bInterfaceClass of a hub.
    /// </summary>
    public const byte CLASS_HUB = 9;

    /// <summary>
    /// bInterfaceSubClass of a HID interface that speaks the boot protocol.
    /// </summary>
    public const byte SUBCLASS_BOOT = 1;

    /// <summary>
    /// bInterfaceProtocol of a boot keyboard.
    /// </summary>
    public const byte PROTOCOL_KEYBOARD = 1;

    #endregion

    #region Functions

    /// <summary>
    /// Reads a device descriptor, or its first eight bytes, which give bMaxPacketSize0 before the endpoint is set up.
    /// </summary>
    /// <param name="bytes">The descriptor's bytes.</param>
    /// <returns>The descriptor; with eight bytes, the fields beyond them are zero.</returns>
    /// <exception cref="FormatException">The bytes are not a device descriptor.</exception>
    public static UsbDeviceDescriptor ReadDevice(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || bytes[1] != DEVICE || bytes[0] != DEVICE_BYTES)
            throw new FormatException($"Not a device descriptor: {bytes.Length} bytes, length {(bytes.Length > 0 ? bytes[0] : 0)}, type {(bytes.Length > 1 ? bytes[1] : 0)}.");
        var whole = bytes.Length >= DEVICE_BYTES;
        return new UsbDeviceDescriptor(
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]),
            bytes[4], bytes[5], bytes[6], bytes[7],
            whole ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]) : (ushort)0,
            whole ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]) : (ushort)0,
            whole ? bytes[17] : (byte)0);
    }

    /// <summary>
    /// The whole length of a configuration's descriptors, from the configuration descriptor alone.
    /// </summary>
    /// <param name="bytes">At least the configuration descriptor's nine bytes.</param>
    /// <returns>wTotalLength.</returns>
    /// <exception cref="FormatException">The bytes are not a configuration descriptor.</exception>
    public static int TotalLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < CONFIGURATION_BYTES || bytes[1] != CONFIGURATION || bytes[0] < CONFIGURATION_BYTES)
            throw new FormatException("Not a configuration descriptor.");
        var total = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        if (total < CONFIGURATION_BYTES)
            throw new FormatException($"A configuration of {total} bytes.");
        return total;
    }

    /// <summary>
    /// Reads a configuration with its interfaces, their endpoints and their HID descriptors.
    /// </summary>
    /// <param name="bytes">The configuration's descriptors, wTotalLength bytes.</param>
    /// <returns>The configuration.</returns>
    /// <exception cref="FormatException">A descriptor's length runs past the bytes, or an endpoint comes before any interface.</exception>
    public static UsbConfiguration ReadConfiguration(ReadOnlySpan<byte> bytes)
    {
        var total = TotalLength(bytes);
        if (bytes.Length < total)
            throw new FormatException($"A configuration of {total} bytes in {bytes.Length}.");
        bytes = bytes[..total];
        var interfaces = new List<UsbInterface>();
        List<UsbEndpoint>? endpoints = null;
        (byte Number, byte Alternate, byte Class, byte SubClass, byte Protocol, int ReportLength) current = default;
        void Close()
        {
            if (endpoints is not null)
                interfaces.Add(new UsbInterface(current.Number, current.Alternate, current.Class, current.SubClass, current.Protocol,
                    current.ReportLength, endpoints));
        }
        for (int at = bytes[0]; at < bytes.Length;)
        {
            int length = bytes[at];
            if (length < 2 || at + length > bytes.Length)
                throw new FormatException($"A descriptor of {length} bytes at {at} of {bytes.Length}.");
            var descriptor = bytes.Slice(at, length);
            switch (descriptor[1])
            {
                case INTERFACE when length >= 9:
                    Close();
                    current = (descriptor[2], descriptor[3], descriptor[5], descriptor[6], descriptor[7], 0);
                    endpoints = [];
                    break;
                case ENDPOINT when length >= 7:
                    if (endpoints is null)
                        throw new FormatException("An endpoint before any interface.");
                    endpoints.Add(new UsbEndpoint(descriptor[2], descriptor[3], BinaryPrimitives.ReadUInt16LittleEndian(descriptor[4..]), descriptor[6]));
                    break;
                case HID when length >= 9 && endpoints is not null:
                    for (var i = 0; i < descriptor[5] && 6 + 3 * i + 2 < length; i++)
                    {
                        if (descriptor[6 + 3 * i] == REPORT)
                            current.ReportLength = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[(7 + 3 * i)..]);
                    }
                    break;
            }
            at += length;
        }
        Close();
        return new UsbConfiguration(bytes[5], bytes[4], interfaces);
    }

    #endregion
}

/// <summary>
/// A device descriptor's fields a host needs.
/// </summary>
/// <param name="Usb">bcdUSB.</param>
/// <param name="Class">bDeviceClass.</param>
/// <param name="SubClass">bDeviceSubClass.</param>
/// <param name="Protocol">bDeviceProtocol.</param>
/// <param name="MaxPacketSize0">bMaxPacketSize0: bytes for low, full and high speed, the exponent of two for SuperSpeed.</param>
/// <param name="Vendor">idVendor.</param>
/// <param name="Product">idProduct.</param>
/// <param name="Configurations">bNumConfigurations.</param>
public sealed record UsbDeviceDescriptor(ushort Usb, byte Class, byte SubClass, byte Protocol, byte MaxPacketSize0, ushort Vendor,
    ushort Product, byte Configurations);

/// <summary>
/// A configuration and its interfaces.
/// </summary>
/// <param name="Value">bConfigurationValue, what SET_CONFIGURATION names.</param>
/// <param name="InterfaceCount">bNumInterfaces.</param>
/// <param name="Interfaces">Every interface descriptor with its endpoints, alternate settings among them.</param>
public sealed record UsbConfiguration(byte Value, byte InterfaceCount, IReadOnlyList<UsbInterface> Interfaces);

/// <summary>
/// An interface and its endpoints.
/// </summary>
/// <param name="Number">bInterfaceNumber.</param>
/// <param name="AlternateSetting">bAlternateSetting.</param>
/// <param name="Class">bInterfaceClass.</param>
/// <param name="SubClass">bInterfaceSubClass.</param>
/// <param name="Protocol">bInterfaceProtocol.</param>
/// <param name="ReportLength">The length of its HID report descriptor, zero without a HID descriptor.</param>
/// <param name="Endpoints">Its endpoints.</param>
public sealed record UsbInterface(byte Number, byte AlternateSetting, byte Class, byte SubClass, byte Protocol, int ReportLength,
    IReadOnlyList<UsbEndpoint> Endpoints)
{
    /// <summary>
    /// Whether the interface is a keyboard of the HID boot protocol.
    /// </summary>
    public bool IsBootKeyboard => Class == UsbDescriptors.CLASS_HID && SubClass == UsbDescriptors.SUBCLASS_BOOT &&
        Protocol == UsbDescriptors.PROTOCOL_KEYBOARD;
}

/// <summary>
/// An endpoint descriptor.
/// </summary>
/// <param name="Address">bEndpointAddress: the number in bits 3–0, IN in bit 7.</param>
/// <param name="Attributes">bmAttributes: the transfer type in bits 1–0.</param>
/// <param name="MaxPacketSize">wMaxPacketSize: the bytes in bits 10–0, high speed's extra transactions in bits 12–11.</param>
/// <param name="Interval">bInterval.</param>
public sealed record UsbEndpoint(byte Address, byte Attributes, ushort MaxPacketSize, byte Interval)
{
    /// <summary>
    /// The transfer type of an interrupt endpoint.
    /// </summary>
    public const int INTERRUPT = 3;

    /// <summary>
    /// The endpoint's number.
    /// </summary>
    public int Number => Address & 0x0F;

    /// <summary>
    /// Whether the endpoint sends to the host.
    /// </summary>
    public bool IsIn => (Address & 0x80) != 0;

    /// <summary>
    /// The transfer type: 0 control, 1 isochronous, 2 bulk, 3 interrupt.
    /// </summary>
    public int Type => Attributes & 3;

    /// <summary>
    /// The bytes of one packet.
    /// </summary>
    public int PacketBytes => MaxPacketSize & 0x7FF;
}
