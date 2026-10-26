using OutWit.OS.DeviceManager.Uhi;

namespace OutWit.OS.DeviceManager.Drivers;

/// <summary>
/// The drivers the device manager knows (ADR 0025, decision 7): a provider and a contract mapped to a driver program
/// of the boot package, until driver packages carry their match metadata (RFC 0010, RFC 0007 §110); a USB
/// controller of the xHCI interface, whatever its vendor, to the xHCI driver (H2.1), a SATA controller of the AHCI
/// interface to the AHCI driver (H3.1), an NVMe controller to the NVMe driver (H3.2), and Intel's 82574L, the e1000e
/// that QEMU and VMware present, to the e1000e driver (H4).
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
    /// The AHCI driver (H3.1): a SATA controller's first ATA disk, served over the block protocol as the virtio disk is.
    /// </summary>
    public const string AHCI = "/system/Drivers/Ahci";

    /// <summary>
    /// The UHI class of a SATA controller of any interface, which the PCI class 01/06 names; the programming interface
    /// tells AHCI (0x01) from a vendor's own.
    /// </summary>
    public const string SATA_CONTROLLER = "SataController";

    /// <summary>
    /// The PCI programming interface of an AHCI controller.
    /// </summary>
    public const uint AHCI_INTERFACE = 0x01;

    /// <summary>
    /// The NVMe driver (H3.2): an NVMe controller's first active namespace, served over the block protocol.
    /// </summary>
    public const string NVME = "/system/Drivers/Nvme";

    /// <summary>
    /// The UHI class of a non-volatile memory controller, which the PCI class 01/08 names; the programming interface 0x02
    /// is NVM Express's I/O controller.
    /// </summary>
    public const string NVME_CONTROLLER = "NvmeController";

    /// <summary>
    /// The PCI programming interface of an NVM Express I/O controller.
    /// </summary>
    public const uint NVME_INTERFACE = 0x02;

    /// <summary>
    /// The e1000e driver (H4): an Intel 82574L, served over the packet protocol as the virtio network device is.
    /// </summary>
    public const string E1000 = "/system/Drivers/E1000";

    /// <summary>
    /// The PS/2 keyboard driver (H5.2, ADR 0031): the PC's i8042 of the legacy bus, served over the input protocol as the
    /// xHCI and virtio keyboards are.
    /// </summary>
    public const string I8042 = "/system/Drivers/I8042";

    /// <summary>
    /// The UHI class of a network controller of any kind, which the PCI class 02 names; Ethernet has no programming
    /// interface, so the vendor and the device tell an adapter's driver.
    /// </summary>
    public const string NETWORK_CONTROLLER = "NetworkController";

    /// <summary>
    /// The PCI device identifier of Intel's 82574L (vendor 0x8086).
    /// </summary>
    public const ushort DEVICE_82574L = 0x10D3;

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
        (_, SATA_CONTROLLER) when (record.Descriptor.ClassCode & 0xFF) == AHCI_INTERFACE => AHCI,
        (_, NVME_CONTROLLER) when (record.Descriptor.ClassCode & 0xFF) == NVME_INTERFACE => NVME,
        ("Intel", NETWORK_CONTROLLER) when record.Descriptor.Device == DEVICE_82574L => E1000,
        (LegacyTranslator.PROVIDER, LegacyTranslator.KEYBOARD_CONTROLLER) => I8042,
        _ => null
    };

    /// <summary>
    /// Whether a device's driver serves the input protocol, a keyboard's keys for the display (C2.2, H2.1, H5.2).
    /// </summary>
    /// <param name="record">A device record.</param>
    /// <returns>Whether it does.</returns>
    public static bool ServesInput(DeviceRecord record) => record.Class == INPUT_DEVICE || Find(record) is XHCI or I8042;

    /// <summary>
    /// Whether a device's driver serves the block protocol, a disk for the namespace service (D3.2, H3.1, H3.2).
    /// </summary>
    /// <param name="record">A device record.</param>
    /// <returns>Whether it does.</returns>
    public static bool ServesBlocks(DeviceRecord record) => record.Class == BLOCK_STORAGE || Find(record) is AHCI or NVME;

    /// <summary>
    /// Whether a device's driver serves the packet protocol, a network adapter for the network service (W1.1, H4).
    /// </summary>
    /// <param name="record">A device record.</param>
    /// <returns>Whether it does.</returns>
    public static bool ServesPackets(DeviceRecord record) => record.Class == NETWORK_QUEUE || Find(record) == E1000;

    #endregion
}
