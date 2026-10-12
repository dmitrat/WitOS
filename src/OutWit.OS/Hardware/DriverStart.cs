using System;
using System.Buffers.Binary;

namespace OutWit.OS.Hardware;

/// <summary>
/// The device manager's first message to a driver it starts (witos/driver.h, plan step D1.2): the device's descriptor in
/// the bytes and the device handle, the device manager's last reference to it, in the one handle slot.
/// </summary>
public static class DriverStart
{
    #region Constants

    /// <summary>
    /// WIT_DRIVER_VERSION.
    /// </summary>
    public const uint VERSION = 1;

    /// <summary>
    /// WIT_DRIVER_START: the device moves to the driver.
    /// </summary>
    public const uint START = 1;

    /// <summary>
    /// The size of a WitDriverStart: its header and one descriptor.
    /// </summary>
    public const int SIZE = 16 + DeviceTable.DESCRIPTOR_SIZE;

    #endregion

    #region Functions

    /// <summary>
    /// The bytes of the start of a driver of the descriptor.
    /// </summary>
    /// <param name="descriptor">A descriptor of the device table, with its bytes.</param>
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
    /// The descriptor a driver's start carries.
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
            throw new FormatException($"The driver's start has {bytes.Length} bytes of another protocol.");
        return DeviceTable.ParseDescriptor((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]), bytes[16..]);
    }

    #endregion
}
