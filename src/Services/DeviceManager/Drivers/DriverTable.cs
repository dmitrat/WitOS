using OutWit.OS.DeviceManager.Uhi;

namespace OutWit.OS.DeviceManager.Drivers;

/// <summary>
/// The drivers the device manager knows (ADR 0025, decision 7): a provider and a contract mapped to a driver program
/// of the boot package, until driver packages carry their match metadata (RFC 0010, RFC 0007 §110); a USB
/// controller of the xHCI interface, whatever its vendor, to the xHCI driver (H2.1).
/// </summary>
internal static class DriverTable
{
    #region Constants

    /// <summary>
    /// The virtio block driver (D1.2).
    /// </summary>
    public const string VIRTIO_BLOCK = "/system/Drivers/VirtioBlock";

    /// <summary>
    /// The virtio console driver (N4.3a).
    /// </summary>
    public const string VIRTIO_CONSOLE = "/system/Drivers/VirtioConsole";

    /// <summary>
    /// The virtio input driver (C2.1).
    /// </summary>
    public const string VIRTIO_INPUT = "/system/Drivers/VirtioInput";

    /// <summary>
    /// The virtio network driver (W1.1).
    /// </summary>
    public const string VIRTIO_NET = "/system/Drivers/VirtioNet";

    /// <summary>
    /// The xHCI driver (H2.1): the USB keyboards of the controller, served as the virtio keyboard is.
    /// </summary>
    public const string XHCI = "/system/Drivers/Xhci";

    /// <summary>
    /// The UHI class of a USB host controller of any interface, which the PCI class 0C/03 names; the programming
    /// interface tells xHCI (0x30) from the older ones.
    /// </summary>
    public const string USB_CONTROLLER = "UsbController";

    /// <summary>
    /// The PCI programming interface of an xHCI controller.
    /// </summary>
    public const uint XHCI_INTERFACE = 0x30;

    /// <summary>
    /// The UHI class of a network adapter, whose driver serves the packet protocol on its service channel (W1.1).
    /// </summary>
    public const string NETWORK_QUEUE = "NetworkQueueDevice";

    /// <summary>
    /// The contract of a console: what the terminal service drives through its driver.
    /// </summary>
    public const string SERIAL_CONSOLE = "SerialConsole";

    /// <summary>
    /// The contract of an input device, a keyboard (C2.1): what the display's terminal takes keys from.
    /// </summary>
    public const string INPUT_DEVICE = "InputDevice";

    /// <summary>
    /// The UHI class of a block device, whose driver serves the block protocol on its service channel (D3.2).
    /// </summary>
    public const string BLOCK_STORAGE = "BlockStorageController";

    #endregion

    #region Functions

    /// <summary>
    /// The driver program of a device record.
    /// </summary>
    /// <param name="record">A device record.</param>
    /// <returns>The driver's path, or null when no driver serves the device.</returns>
    public static string? Find(DeviceRecord record) => (record.Provider, record.Class) switch
    {
        ("Virtio", BLOCK_STORAGE) => VIRTIO_BLOCK,
        ("Virtio", SERIAL_CONSOLE) => VIRTIO_CONSOLE,
        ("Virtio", INPUT_DEVICE) => VIRTIO_INPUT,
        ("Virtio", NETWORK_QUEUE) => VIRTIO_NET,
        (_, USB_CONTROLLER) when (record.Descriptor.ClassCode & 0xFF) == XHCI_INTERFACE => XHCI,
        _ => null
    };

    /// <summary>
    /// Whether a device's driver serves the input protocol, a keyboard's keys for the display (C2.2, H2.1).
    /// </summary>
    /// <param name="record">A device record.</param>
    /// <returns>Whether it does.</returns>
    public static bool ServesInput(DeviceRecord record) => record.Class == INPUT_DEVICE || Find(record) == XHCI;

    #endregion
}
