using System;
using System.Buffers.Binary;

namespace OutWit.OS.DeviceManager;

/// <summary>
/// The device manager's messages on the channel the root task granted it (witos/device_manager.h, witos/manager.h):
/// the root task's start, which carries the device table, and the service status the device manager answers with.
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
    /// WIT_MANAGER_VERSION, the version of a WitServiceStatus.
    /// </summary>
    public const uint MANAGER_VERSION = 3;

    /// <summary>
    /// The size of a WitDeviceManagerMessage and of a WitServiceStatus.
    /// </summary>
    public const int MESSAGE_SIZE = 16;

    /// <summary>
    /// EPROTO, the errno value of a message of another protocol.
    /// </summary>
    public const int PROTOCOL_ERROR = 71;

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

    /// <summary>
    /// A WitServiceStatus: zero when the device manager is ready, otherwise the errno value of what it could not do.
    /// </summary>
    /// <param name="error">Zero or an errno value.</param>
    /// <returns>The message's bytes.</returns>
    public static byte[] Status(int error)
    {
        var bytes = new byte[MESSAGE_SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, MANAGER_VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), MESSAGE_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)error);
        return bytes;
    }

    #endregion
}
