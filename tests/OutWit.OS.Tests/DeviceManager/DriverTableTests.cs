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

    [Test]
    public void OtherDevicesHaveNoDriverTest()
    {
        Assert.That(DriverTable.Find(Record("Virtio", "EntropySource")), Is.Null);
        Assert.That(DriverTable.Find(Record("Intel", "HostBridge")), Is.Null);
        Assert.That(DriverTable.Find(Record("Firmware", FirmwareTranslator.FRAME_BUFFER)), Is.Null, "The framebuffer is the device manager's probe's until C2.2");
    }

    #endregion

    #region Tools

    private static DeviceRecord Record(string provider, string contract) =>
        new("pci/00:02.0/1af4:1042", contract, provider, new DeviceDescriptor(0, DeviceDescriptor.BUS_PCI, 0x200, [0, 0, 0, 0], [], []));

    #endregion
}
