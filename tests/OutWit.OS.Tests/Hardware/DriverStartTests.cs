using System;
using System.Buffers.Binary;
using OutWit.OS.Hardware;

namespace OutWit.OS.Tests.Hardware;

/// <summary>
/// The device manager's start of a driver (witos/driver.h): the descriptor's bytes round-trip.
/// </summary>
[TestFixture]
public sealed class DriverStartTests
{
    #region Tests

    [Test]
    public void StartCarriesDescriptorTest()
    {
        var descriptor = Descriptor(3);

        var bytes = DriverStart.Encode(descriptor);
        var decoded = DriverStart.Decode(bytes);

        Assert.That(bytes, Has.Length.EqualTo(248));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes), Is.EqualTo(DriverStart.VERSION));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), Is.EqualTo(DriverStart.START));
        Assert.That(decoded.Index, Is.EqualTo(3));
        Assert.That(decoded.Identity, Is.EqualTo(descriptor.Identity));
        Assert.That(decoded.Regions, Is.EqualTo(descriptor.Regions));
        Assert.That(decoded.Lines, Is.EqualTo(descriptor.Lines));
        Assert.That(decoded.Bytes, Is.EqualTo(descriptor.Bytes));
    }

    [Test]
    public void EncodeRefusesDescriptorWithoutBytesTest()
    {
        var descriptor = new DeviceDescriptor(0, DeviceDescriptor.BUS_PCI, 0, [0, 0, 0, 0], [], []);
        Assert.Throws<ArgumentException>(() => DriverStart.Encode(descriptor));
    }

    [Test]
    public void DecodeRefusesOtherBytesTest()
    {
        var bytes = DriverStart.Encode(Descriptor(0));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        Assert.Throws<FormatException>(() => DriverStart.Decode(bytes));
        Assert.Throws<FormatException>(() => DriverStart.Decode(new byte[16]));
    }

    #endregion

    #region Tools

    // A virtio block descriptor read from a table, so that it carries its bytes.
    private static DeviceDescriptor Descriptor(int index)
    {
        var bytes = new byte[DeviceTable.DESCRIPTOR_SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, DeviceDescriptor.BUS_PCI);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x1001_1AF4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0x0100_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 0xB000_0000);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), DeviceRegion.MEMORY | DeviceRegion.CONFIG);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(200), DeviceLine.LEVEL);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(204), 11);
        return DeviceTable.ParseDescriptor(index, bytes);
    }

    #endregion
}
