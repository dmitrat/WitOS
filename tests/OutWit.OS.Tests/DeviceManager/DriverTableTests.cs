using OutWit.OS.DeviceManager.Drivers;
using OutWit.OS.DeviceManager.Uhi;
using OutWit.OS.Hardware;

namespace OutWit.OS.Tests.DeviceManager;

/// <summary>
/// The device manager's driver table (ADR 0025, decision 7): the virtio contracts it serves with a driver of the boot
/// package, and what it leaves without one.
/// </summary>
[TestFixture]
public sealed class DriverTableTests
{
    #region Tests

    [Test]
    public void VirtioContractsHaveTheirDriversTest()
    {
        Assert.That(DriverTable.Find(Record("Virtio", DriverTable.BLOCK_STORAGE)), Is.EqualTo(DriverTable.VIRTIO_BLOCK));
        Assert.That(DriverTable.Find(Record("Virtio", DriverTable.SERIAL_CONSOLE)), Is.EqualTo(DriverTable.VIRTIO_CONSOLE));
        Assert.That(DriverTable.Find(Record("Virtio", DriverTable.INPUT_DEVICE)), Is.EqualTo(DriverTable.VIRTIO_INPUT), "C2.1: the keyboard");
    }

    // H2.1: a USB controller of the xHCI interface has the xHCI driver whatever its vendor, the older interfaces none,
    // and its driver serves the input protocol as the virtio keyboard's does.
    [Test]
    public void XhciControllersHaveTheirDriverTest()
    {
        var xhci = Record("RedHat", DriverTable.USB_CONTROLLER, 0x0C0330);
        Assert.That(DriverTable.Find(xhci), Is.EqualTo(DriverTable.XHCI));
        Assert.That(DriverTable.Find(Record("Vendor15ad", DriverTable.USB_CONTROLLER, 0x0C0330)), Is.EqualTo(DriverTable.XHCI), "VMware's controller");
        Assert.That(DriverTable.Find(Record("Intel", DriverTable.USB_CONTROLLER, 0x0C0320)), Is.Null, "EHCI");
        Assert.That(DriverTable.Find(Record("Vendor15ad", DriverTable.USB_CONTROLLER, 0x0C0300)), Is.Null, "UHCI");
        Assert.That(DriverTable.ServesInput(xhci), Is.True);
        Assert.That(DriverTable.ServesInput(Record("Virtio", DriverTable.INPUT_DEVICE)), Is.True);
        Assert.That(DriverTable.ServesInput(Record("Virtio", DriverTable.BLOCK_STORAGE)), Is.False);
    }

    // H3.1, H3.2: an AHCI or NVMe controller has its driver whatever its vendor, and serves the block protocol as the
    // virtio disk's driver does.
    [Test]
    public void StorageControllersHaveTheirDriversTest()
    {
        var ahci = Record("Intel", DriverTable.SATA_CONTROLLER, 0x010601);
        var nvme = Record("RedHat", DriverTable.NVME_CONTROLLER, 0x010802);
        Assert.That((DriverTable.Find(ahci), DriverTable.Find(nvme)), Is.EqualTo((DriverTable.AHCI, DriverTable.NVME)));
        Assert.That(DriverTable.Find(Record("Intel", DriverTable.SATA_CONTROLLER, 0x010600)), Is.Null, "a vendor's own SATA interface");
        Assert.That((DriverTable.ServesBlocks(ahci), DriverTable.ServesBlocks(nvme), DriverTable.ServesBlocks(Record("Virtio", DriverTable.BLOCK_STORAGE))),
            Is.EqualTo((true, true, true)));
        Assert.That(DriverTable.ServesBlocks(Record("Virtio", DriverTable.NETWORK_QUEUE)), Is.False);
    }

    // H4: Intel's 82574L, the e1000e of QEMU and VMware, has the e1000e driver, which serves the packet protocol as the
    // virtio network driver does; another Intel adapter has none yet.
    [Test]
    public void NetworkAdaptersHaveTheirDriversTest()
    {
        var e1000e = Record("Intel", DriverTable.NETWORK_CONTROLLER, 0x020000, 0x8086 | (uint)DriverTable.DEVICE_82574L << 16);
        var virtio = Record("Virtio", DriverTable.NETWORK_QUEUE);
        Assert.That((DriverTable.Find(e1000e), DriverTable.Find(virtio)), Is.EqualTo((DriverTable.E1000, DriverTable.VIRTIO_NET)));
        Assert.That(DriverTable.Find(Record("Intel", DriverTable.NETWORK_CONTROLLER, 0x020000, 0x8086 | 0x100EU << 16)), Is.Null, "the 82540EM, QEMU's e1000");
        Assert.That(DriverTable.Find(Record("Vendor15ad", DriverTable.NETWORK_CONTROLLER, 0x020000, 0x15AD | 0x07B0U << 16)), Is.Null, "VMware's vmxnet3");
        Assert.That((DriverTable.ServesPackets(e1000e), DriverTable.ServesPackets(virtio)), Is.EqualTo((true, true)));
        Assert.That(DriverTable.ServesPackets(Record("Virtio", DriverTable.BLOCK_STORAGE)), Is.False);
    }

    [Test]
    public void OtherDevicesHaveNoDriverTest()
    {
        Assert.That(DriverTable.Find(Record("Virtio", "EntropySource")), Is.Null);
        Assert.That(DriverTable.Find(Record("Intel", "HostBridge")), Is.Null);
        Assert.That(DriverTable.Find(Record("Firmware", FirmwareTranslator.FRAME_BUFFER)), Is.Null, "The framebuffer is the device manager's probe's until C2.2");
    }

    #endregion

    #region Tools

    private static DeviceRecord Record(string provider, string contract, uint classCode = 0, uint identity = 0) =>
        new("pci/00:02.0/1af4:1042", contract, provider, new DeviceDescriptor(0, DeviceDescriptor.BUS_PCI, 0x200, [identity, classCode << 8, 0, 0], [], []));

    #endregion
}
