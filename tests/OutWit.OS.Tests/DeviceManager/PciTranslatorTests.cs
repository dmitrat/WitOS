using OutWit.OS.DeviceManager.Uhi;
using OutWit.OS.Hardware;

namespace OutWit.OS.Tests.DeviceManager;

/// <summary>
/// The device manager's translation of PCI descriptors into UHI device records (ADR 0025, decision 6).
/// </summary>
[TestFixture]
public sealed class PciTranslatorTests
{
    #region Tests

    [Test]
    public void VirtioBlockIsBlockStorageControllerTest()
    {
        var record = PciTranslator.Translate(Pci(0x0000_0200, 0x1AF4, 0x1042, 0x010000));

        Assert.That(record.Class, Is.EqualTo("BlockStorageController"));
        Assert.That(record.Provider, Is.EqualTo("Virtio"));
        Assert.That(record.Id, Is.EqualTo("pci/00:02.0/1af4:1042"));
        Assert.That(record.DiscoveryLine,
            Is.EqualTo("Device discovered: Class: BlockStorageController, Provider: Virtio, DeviceId: pci/00:02.0/1af4:1042, Regions: 0, Lines: 0"));
    }

    [Test]
    public void TransitionalVirtioIsNamedByTypeTest()
    {
        Assert.That(PciTranslator.Translate(Pci(0x0000_0300, 0x1AF4, 0x1001, 0x010000)).Class, Is.EqualTo("BlockStorageController"));
        Assert.That(PciTranslator.Translate(Pci(0x0000_0400, 0x1AF4, 0x1005, 0x00FF00)).Class, Is.EqualTo("EntropySource"));
        Assert.That(PciTranslator.Translate(Pci(0x0000_0500, 0x1AF4, 0x1044, 0x00FF00)).Class, Is.EqualTo("EntropySource"));
    }

    [Test]
    public void VirtioTypeTest()
    {
        Assert.That(PciTranslator.VirtioType(0x1041), Is.EqualTo(1));
        Assert.That(PciTranslator.VirtioType(0x1050), Is.EqualTo(16));
        Assert.That(PciTranslator.VirtioType(0x1000), Is.EqualTo(1));
        Assert.That(PciTranslator.VirtioType(0x1110), Is.Zero);
    }

    [Test]
    public void OtherFunctionsAreNamedByClassTest()
    {
        var bridge = PciTranslator.Translate(Pci(0, 0x8086, 0x29C0, 0x060000));
        Assert.That(bridge.Class, Is.EqualTo("HostBridge"));
        Assert.That(bridge.Provider, Is.EqualTo("Intel"));

        var sata = PciTranslator.Translate(Pci(0x0000_1F02, 0x8086, 0x2922, 0x010601));
        Assert.That(sata.Class, Is.EqualTo("SataController"));
        Assert.That(sata.Id, Is.EqualTo("pci/00:1f.2/8086:2922"));

        var unknown = PciTranslator.Translate(Pci(0x0001_0000, 0x10EC, 0x8139, 0xFF0000));
        Assert.That(unknown.Class, Is.EqualTo("PciClassff0000"));
        Assert.That(unknown.Provider, Is.EqualTo("Vendor10ec"));
        Assert.That(unknown.Id, Is.EqualTo("pci/01:00.0/10ec:8139"));
    }

    [Test]
    public void UnknownVirtioDeviceIsNamedByClassTest()
    {
        var record = PciTranslator.Translate(Pci(0x0000_0600, 0x1AF4, 0x1110, 0x050000));
        Assert.That(record.Class, Is.EqualTo("MemoryController"));
        Assert.That(record.Provider, Is.EqualTo("Virtio"));
    }

    #endregion

    #region Tools

    private static DeviceDescriptor Pci(uint address, ushort vendor, ushort device, uint classCode) =>
        new(0, DeviceDescriptor.BUS_PCI, address, [(uint)vendor | (uint)device << 16, classCode << 8, 0, 0], [], []);

    #endregion
}
