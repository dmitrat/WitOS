using System.Globalization;
using OutWit.OS.Hardware;

namespace OutWit.OS.DeviceManager.Uhi;

/// <summary>
/// The translation of a PCI descriptor into a UHI device record (ADR 0025, decision 6). The kernel knows no class; the
/// device manager reads the identity words the platform published. Virtio devices are named by the contract their
/// virtio type serves (RFC 0007 §55), every other function by its PCI class code (§48).
/// </summary>
internal static class PciTranslator
{
    #region Constants

    private const ushort VENDOR_VIRTIO = 0x1AF4;
    private const ushort VENDOR_INTEL = 0x8086;
    private const ushort VENDOR_RED_HAT = 0x1B36;
    private const ushort VENDOR_QEMU = 0x1234;

    /// <summary>
    /// The first device identifier of a modern virtio device: 0x1040 plus its virtio type.
    /// </summary>
    private const ushort VIRTIO_MODERN = 0x1040;

    #endregion

    #region Functions

    /// <summary>
    /// The record of a descriptor.
    /// </summary>
    /// <param name="descriptor">A descriptor of the device table.</param>
    /// <returns>The device record.</returns>
    public static DeviceRecord Translate(DeviceDescriptor descriptor)
    {
        if (descriptor.Bus == DeviceDescriptor.BUS_FIRMWARE)
            return FirmwareTranslator.Translate(descriptor);
        if (descriptor.Bus != DeviceDescriptor.BUS_PCI)
            return new DeviceRecord($"bus{descriptor.Bus}/{descriptor.Address:x8}", "Unknown", "Unknown", descriptor);
        var address = descriptor.Address;
        var id = string.Create(CultureInfo.InvariantCulture,
            $"pci/{address >> 16 & 0xFF:x2}:{address >> 8 & 0xFF:x2}.{address & 0xFF}/{descriptor.Vendor:x4}:{descriptor.Device:x4}");
        var virtio = descriptor.Vendor == VENDOR_VIRTIO ? VirtioType(descriptor.Device) : 0;
        return virtio != 0
            ? new DeviceRecord(id, VirtioContract(virtio), "Virtio", descriptor)
            : new DeviceRecord(id, PciClass(descriptor.ClassCode), Provider(descriptor.Vendor), descriptor);
    }

    /// <summary>
    /// The virtio type of a virtio device identifier: modern (0x1040 plus the type) or transitional; zero for none.
    /// </summary>
    /// <param name="device">The PCI device identifier of vendor 0x1AF4.</param>
    /// <returns>The virtio type.</returns>
    public static int VirtioType(ushort device) => device switch
    {
        >= VIRTIO_MODERN and <= 0x107F => device - VIRTIO_MODERN,
        0x1000 => 1,
        0x1001 => 2,
        0x1002 => 5,
        0x1003 => 3,
        0x1004 => 8,
        0x1005 => 4,
        0x1009 => 9,
        _ => 0
    };

    private static string VirtioContract(int type) => type switch
    {
        1 => "NetworkQueueDevice",
        2 => "BlockStorageController",
        3 => "SerialConsole",
        4 => "EntropySource",
        5 => "MemoryBalloon",
        8 => "ScsiController",
        9 => "FileSystemTransport",
        16 => "DisplayOutput",
        18 => "InputDevice",
        _ => string.Create(CultureInfo.InvariantCulture, $"VirtioDevice{type}")
    };

    private static string PciClass(uint classCode) => (classCode >> 16, classCode >> 8 & 0xFF) switch
    {
        (0x01, 0x00) => "ScsiController",
        (0x01, 0x01) => "IdeController",
        (0x01, 0x06) => "SataController",
        (0x01, 0x08) => "NvmeController",
        (0x01, _) => "MassStorageController",
        (0x02, _) => "NetworkController",
        (0x03, _) => "DisplayController",
        (0x04, _) => "MultimediaController",
        (0x05, _) => "MemoryController",
        (0x06, 0x00) => "HostBridge",
        (0x06, 0x01) => "IsaBridge",
        (0x06, 0x04) => "PciBridge",
        (0x06, _) => "Bridge",
        (0x07, _) => "CommunicationController",
        (0x08, _) => "SystemPeripheral",
        (0x0C, 0x03) => "UsbController",
        (0x0C, 0x05) => "SmBusController",
        (0x0C, _) => "SerialBusController",
        _ => string.Create(CultureInfo.InvariantCulture, $"PciClass{classCode:x6}")
    };

    private static string Provider(ushort vendor) => vendor switch
    {
        VENDOR_VIRTIO => "Virtio",
        VENDOR_INTEL => "Intel",
        VENDOR_RED_HAT => "RedHat",
        VENDOR_QEMU => "QEMU",
        _ => string.Create(CultureInfo.InvariantCulture, $"Vendor{vendor:x4}")
    };

    #endregion
}
