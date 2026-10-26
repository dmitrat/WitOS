using System;
using System.Buffers.Binary;
using OutWit.OS.Hardware;

namespace OutWit.OS.Display;

/// <summary>
/// The display service's start (witos/display.h, plan step C2.2): the framebuffer's descriptor in the bytes, the
/// framebuffer device in the first handle slot and the keyboard driver's input channel in the second when the board has
/// a keyboard; the service answers with its console channel's end.
/// </summary>
public static class DisplayStart
{
    #region Constants

    /// <summary>
    /// WIT_DISPLAY_VERSION.
    /// </summary>
    public const uint VERSION = 1;

    /// <summary>
    /// WIT_DISPLAY_START.
    /// </summary>
    public const uint START = 1;

    /// <summary>
    /// Bytes of the message.
    /// </summary>
    public const int SIZE = 16 + DeviceTable.DESCRIPTOR_SIZE;

    /// <summary>
    /// The service's program in the boot package.
    /// </summary>
    public const string PATH = "/system/Display";

    /// <summary>
    /// The bitmap font of the boot package, by its pin.
    /// </summary>
    public const string FONT = "/system/fonts/spleen-8x16.bdf";

    /// <summary>
    /// The keyboards' input channels a start carries at most, after the framebuffer (H5.2: a PC's PS/2 keyboard beside its
    /// USB keyboards), within the four handles of a message and the four of a wait.
    /// </summary>
    public const int KEYBOARDS = 2;

    #endregion

    #region Functions

    /// <summary>
    /// The bytes of the start with the framebuffer's descriptor.
    /// </summary>
    /// <param name="descriptor">The framebuffer's descriptor of the device table, with its bytes.</param>
    /// <returns>The message's bytes.</returns>
    /// <exception cref="ArgumentException">The descriptor carries no bytes of the table.</exception>
    public static byte[] Encode(DeviceDescriptor descriptor)
    {
        if (descriptor.Bytes.Length != DeviceTable.DESCRIPTOR_SIZE)
            throw new ArgumentException("The descriptor carries no bytes of the table.", nameof(descriptor));
        var bytes = new byte[SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), START);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)descriptor.Index);
        descriptor.Bytes.CopyTo(bytes.AsSpan(16));
        return bytes;
    }

    /// <summary>
    /// The descriptor the start carries.
    /// </summary>
    /// <param name="bytes">The message's bytes.</param>
    /// <returns>The descriptor, with its index in the table.</returns>
    /// <exception cref="FormatException">The bytes are not a start of this version, or the descriptor is not valid.</exception>
    public static DeviceDescriptor Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != SIZE ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != VERSION ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != SIZE ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != START)
            throw new FormatException($"The display's start has {bytes.Length} bytes of another protocol.");
        return DeviceTable.ParseDescriptor((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]), bytes[16..]);
    }

    #endregion
}
