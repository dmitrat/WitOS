using System;
using System.Buffers.Binary;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The root task's start on the channel it granted the device manager (witos/device_manager.h): the device table moves
/// to the device manager. The device manager answers with a service status (OutWit.OS.Communication.ServiceStatus).
/// </summary>
internal static class ServiceProtocol
{
    #region Constants

    /// <summary>
    /// WIT_DEVICE_MANAGER_VERSION.
    /// </summary>
    public const uint DEVICE_MANAGER_VERSION = 1;

    /// <summary>
    /// WIT_DEVICE_MANAGER_START: the device table moves to the device manager.
    /// </summary>
    public const uint START = 1;

    /// <summary>
    /// The size of a WitDeviceManagerMessage.
    /// </summary>
    public const int MESSAGE_SIZE = 16;

    #endregion

    #region Functions

    /// <summary>
    /// Whether the bytes are the root task's start message.
    /// </summary>
    /// <param name="bytes">A message's bytes.</param>
    /// <returns>True for a WitDeviceManagerMessage of this version with the START operation.</returns>
    public static bool IsStart(ReadOnlySpan<byte> bytes) =>
        bytes.Length == MESSAGE_SIZE &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes) == DEVICE_MANAGER_VERSION &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == MESSAGE_SIZE &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) == START &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) == 0;

    #endregion
}
