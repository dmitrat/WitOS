using OutWit.OS.DeviceManager.Uhi;

namespace OutWit.OS.DeviceManager.Drivers;

/// <summary>
/// The drivers the device manager knows (ADR 0025, decision 7): a provider and a contract mapped to a driver program
/// of the boot package, until driver packages carry their match metadata (RFC 0010, RFC 0007 §110).
/// </summary>
internal static class DriverTable
{
    #region Constants

    /// <summary>
    /// The virtio block driver (D1.2).
    /// </summary>
    public const string VIRTIO_BLOCK = "/system/Drivers/VirtioBlock";

    #endregion

    #region Functions

    /// <summary>
    /// The driver program of a device record.
    /// </summary>
    /// <param name="record">A device record.</param>
    /// <returns>The driver's path, or null when no driver serves the device.</returns>
    public static string? Find(DeviceRecord record) => (record.Provider, record.Class) switch
    {
        ("Virtio", "BlockStorageController") => VIRTIO_BLOCK,
        _ => null
    };

    #endregion
}
