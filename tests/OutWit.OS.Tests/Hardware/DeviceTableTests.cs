using System;
using System.Buffers.Binary;
using OutWit.OS.Hardware;

namespace OutWit.OS.Tests.Hardware;

/// <summary>
/// The reader of the kernel's device table (witos/device.h): its layout, and the tables it refuses.
/// </summary>
[TestFixture]
public sealed class DeviceTableTests
{
    #region Tests

    [Test]
    public void ParseReadsDescriptorsTest()
    {
        var table = Table(2);
        Descriptor(table, 0, address: 0x0000_0200, identity0: 0x1042_1AF4, classWord: 0x0100_0001, regions: 3, lines: 1);
        Descriptor(table, 1, address: 0x0000_0000, identity0: 0x29C0_8086, classWord: 0x0600_0002, regions: 1, lines: 0);

        var descriptors = DeviceTable.Parse(table);

        Assert.That(descriptors, Has.Count.EqualTo(2));
        var block = descriptors[0];
        Assert.That(block.Index, Is.Zero);
        Assert.That(block.Bus, Is.EqualTo(DeviceDescriptor.BUS_PCI));
        Assert.That(block.Address, Is.EqualTo(0x200u));
        Assert.That(block.Vendor, Is.EqualTo((ushort)0x1AF4));
        Assert.That(block.Device, Is.EqualTo((ushort)0x1042));
        Assert.That(block.ClassCode, Is.EqualTo(0x010000u));
        Assert.That(block.Regions, Has.Count.EqualTo(3));
        Assert.That(block.Regions[0].Base, Is.EqualTo(0xB000_0000UL));
        Assert.That(block.Regions[0].Size, Is.EqualTo(0x1000UL));
        Assert.That(block.Regions[0].Flags, Is.EqualTo(DeviceRegion.MEMORY | DeviceRegion.CONFIG));
        Assert.That(block.Regions[0].IsMemory, Is.True);
        Assert.That(block.Lines, Has.Count.EqualTo(1));
        Assert.That(block.Lines[0], Is.EqualTo(new DeviceLine(DeviceLine.LEVEL, 11)));
        Assert.That(descriptors[1].Index, Is.EqualTo(1));
        Assert.That(descriptors[1].Lines, Is.Empty);
    }

    [Test]
    public void ParseRefusesShortTableTest()
    {
        Assert.Throws<FormatException>(() => DeviceTable.Parse(new byte[8]));
    }

    [Test]
    public void ParseRefusesForeignHeaderTest()
    {
        var table = Table(0);
        BinaryPrimitives.WriteUInt32LittleEndian(table, 2);
        Assert.Throws<FormatException>(() => DeviceTable.Parse(table));

        table = Table(0);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(12), 200);
        Assert.Throws<FormatException>(() => DeviceTable.Parse(table));
    }

    [Test]
    public void ParseRefusesCountBeyondBytesTest()
    {
        var table = Table(0);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(8), 18);
        Assert.Throws<FormatException>(() => DeviceTable.Parse(table));
    }

    [Test]
    public void ParseRefusesTooManyRegionsTest()
    {
        var table = Table(1);
        Descriptor(table, 0, address: 0, identity0: 0, classWord: 0, regions: 8, lines: 0);
        Assert.Throws<FormatException>(() => DeviceTable.Parse(table));
    }

    [Test]
    public void TableBytesRoundsToPagesTest()
    {
        // The header and 17 descriptors fill one page; the 18th takes a second, and 64 take four (ABI-1 1.5).
        Assert.That(DeviceTable.TableBytes(0), Is.EqualTo(4096));
        Assert.That(DeviceTable.TableBytes(17), Is.EqualTo(4096));
        Assert.That(DeviceTable.TableBytes(18), Is.EqualTo(8192));
        Assert.That(DeviceTable.TableBytes(DeviceTable.CAPACITY), Is.EqualTo(16384));
    }

    [Test]
    public void ParseReadsDescriptorsBeyondTheFirstPageTest()
    {
        var table = Table(40);
        Descriptor(table, 39, address: 0x0300, identity0: 0x10011AF4, classWord: 0x01000000, regions: 1, lines: 1);
        var descriptors = DeviceTable.Parse(table);
        Assert.That(descriptors, Has.Count.EqualTo(40));
        Assert.That(descriptors[39].Identity[0], Is.EqualTo(0x10011AF4u));
    }

    #endregion

    #region Tools

    // A table's pages with its header and count descriptors left zero.
    private static byte[] Table(uint count)
    {
        var table = new byte[DeviceTable.TableBytes((int)count)];
        BinaryPrimitives.WriteUInt32LittleEndian(table, DeviceTable.VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), DeviceTable.HEADER_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(8), count);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(12), DeviceTable.DESCRIPTOR_SIZE);
        return table;
    }

    // A PCI descriptor whose first region is a configuration page and whose first line is INTx 11.
    private static void Descriptor(byte[] table, int index, uint address, uint identity0, uint classWord, uint regions, uint lines)
    {
        var bytes = table.AsSpan(DeviceTable.HEADER_SIZE + index * DeviceTable.DESCRIPTOR_SIZE, DeviceTable.DESCRIPTOR_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, DeviceDescriptor.BUS_PCI);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], address);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], identity0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], classWord);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[24..], regions);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[28..], lines);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], 0xB000_0000);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[48..], DeviceRegion.MEMORY | DeviceRegion.CONFIG);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[200..], DeviceLine.LEVEL);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[204..], 11);
    }

    #endregion
}
