using System;
using System.Buffers.Binary;

namespace OutWit.OS.Communication;

/// <summary>
/// A service's first answer on the channel its creator granted it (witos/manager.h's WitServiceStatus, plan step D1.1):
/// zero when it is ready, otherwise the errno value of what it could not do.
/// </summary>
public static class ServiceStatus
{
    #region Constants

    /// <summary>
    /// WIT_MANAGER_VERSION, the version of a WitServiceStatus.
    /// </summary>
    public const uint VERSION = 6;

    /// <summary>
    /// The size of a WitServiceStatus.
    /// </summary>
    public const int SIZE = 16;

    /// <summary>
    /// EPROTO, the errno value of a message of another protocol.
    /// </summary>
    public const int PROTOCOL_ERROR = 71;

    /// <summary>
    /// WIT_SERVICE_NAMESPACE: the ready answer carries the namespace service's clients' endpoint (D5.1).
    /// </summary>
    public const uint NAMESPACE = 1;

    /// <summary>
    /// WIT_SERVICE_TERMINAL: the ready answer carries the terminal service's clients' endpoint (N4.3).
    /// </summary>
    public const uint TERMINAL = 2;

    /// <summary>
    /// WIT_SERVICE_NETWORK: the ready answer carries the network service's clients' endpoint (W2.1a).
    /// </summary>
    public const uint NETWORK = 4;

    #endregion

    #region Functions

    /// <summary>
    /// The bytes of a status.
    /// </summary>
    /// <param name="error">Zero or an errno value.</param>
    /// <returns>The message's bytes.</returns>
    public static byte[] Encode(int error, uint capabilities = 0)
    {
        var bytes = new byte[SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)error);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), capabilities);
        return bytes;
    }

    /// <summary>
    /// The endpoints a ready answer names (NAMESPACE, TERMINAL), in the order of the bits of its handle slots.
    /// </summary>
    /// <param name="bytes">The message's bytes.</param>
    /// <returns>The capabilities; zero for a message that is no status.</returns>
    public static uint Capabilities(ReadOnlySpan<byte> bytes) =>
        bytes.Length == SIZE && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == VERSION &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == SIZE
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..])
            : 0;

    /// <summary>
    /// The error a status carries.
    /// </summary>
    /// <param name="bytes">A message's bytes.</param>
    /// <returns>Zero or the service's errno value; PROTOCOL_ERROR for bytes that are no status.</returns>
    public static int Decode(ReadOnlySpan<byte> bytes) =>
        bytes.Length == SIZE &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes) == VERSION &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == SIZE &&
        (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) & ~(NAMESPACE | TERMINAL | NETWORK)) == 0
            ? (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..])
            : PROTOCOL_ERROR;

    #endregion
}
