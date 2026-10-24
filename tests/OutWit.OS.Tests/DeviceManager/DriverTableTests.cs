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

    [Test]
    public void OtherDevicesHaveNoDriverTest()
    {
        Assert.That(DriverTable.Find(Record("Virtio", "EntropySource")), Is.Null);
        Assert.That(DriverTable.Find(Record("Intel", "HostBridge")), Is.Null);
        Assert.That(DriverTable.Find(Record("Firmware", FirmwareTranslator.FRAME_BUFFER)), Is.Null, "The framebuffer is the device manager's probe's until C2.2");
    }

    #endregion

    #region Tools

    private static DeviceRecord Record(string provider, string contract, uint classCode = 0) =>
        new("pci/00:02.0/1af4:1042", contract, provider, new DeviceDescriptor(0, DeviceDescriptor.BUS_PCI, 0x200, [0, classCode << 8, 0, 0], [], []));

    #endregion
}
