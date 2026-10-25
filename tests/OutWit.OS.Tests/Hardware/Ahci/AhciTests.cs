using System;
using System.Buffers.Binary;
using System.Text;
using OutWit.OS.Hardware.Ahci;

namespace OutWit.OS.Tests.Hardware.Ahci;

/// <summary>
/// The AHCI pieces of plan step H3.1 on the host: the Register Host to Device FIS of each ATA command the driver sends,
/// a command header and the PRDT's entries in the layout AHCI 1.3.1 gives them, and IDENTIFY DEVICE data as QEMU's
/// ide-hd answers it, with the 4Kn and the 28-bit cases beside it.
/// </summary>
[TestFixture]
public sealed class AhciTests
{
    #region Tests

    [Test]
    public void ReadCommandFisCarriesItsAddressAndCountTest()
    {
        var table = new byte[AhciCommands.TableBytes(1)];
        AhciCommands.WriteRegisterFis(table, AhciCommands.READ_DMA_EXT, 0x0000_1234_5678_9ABC, 512);
        Assert.That(table.AsSpan(0, 16).ToArray(), Is.EqualTo(new byte[]
        {
            0x27, 0x80, 0x25, 0x00, 0xBC, 0x9A, 0x78, 0x40, 0x56, 0x34, 0x12, 0x00, 0x00, 0x02, 0x00, 0x00,
        }), "type, C bit, command, LBA 0–23, the LBA mode bit, LBA 24–47, count");
        AhciCommands.WriteRegisterFis(table, AhciCommands.WRITE_DMA_EXT, 1, AhciCommands.MAX_SECTORS);
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(12)), Is.Zero, "65536 sectors are a count of zero");
        AhciCommands.WriteRegisterFis(table, AhciCommands.IDENTIFY_DEVICE, 0, 0);
        Assert.That((table[2], table[7]), Is.EqualTo(((byte)0xEC, (byte)0)), "IDENTIFY DEVICE without the LBA bit");
        Assert.Throws<ArgumentOutOfRangeException>(() => AhciCommands.WriteRegisterFis(table, AhciCommands.READ_DMA_EXT, 1UL << 48, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AhciCommands.WriteRegisterFis(table, AhciCommands.READ_DMA_EXT, 0, AhciCommands.MAX_SECTORS + 1));
    }

    [Test]
    public void HeaderAndPrdtFollowTheLayoutTest()
    {
        var list = new byte[AhciCommands.LIST_BYTES];
        AhciCommands.WriteHeader(list, 0, true, 3, 0x1_2345_6780);
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(list), Is.EqualTo(5U | 1U << 6 | 3U << 16), "five dwords of FIS, a write, three entries");
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(8)), Is.EqualTo(0x1_2345_6780UL), "the command table");
        Assert.That(AhciCommands.TransferredBytes(list, 0), Is.Zero);
        AhciCommands.WriteHeader(list, 2, false, 1, 0x80);
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(2 * AhciCommands.HEADER_BYTES)), Is.EqualTo(5U | 1U << 16), "slot 2, a read");
        Assert.Throws<ArgumentException>(() => AhciCommands.WriteHeader(list, 0, false, 1, 0x40), "a table not aligned to 128");
        var table = new byte[AhciCommands.TableBytes(2)];
        AhciCommands.WritePrdtEntry(table, 1, 0x8000_1000, 4096);
        var entry = table.AsSpan(AhciCommands.PRDT_OFFSET + AhciCommands.PRDT_ENTRY_BYTES);
        Assert.That((BinaryPrimitives.ReadUInt64LittleEndian(entry), BinaryPrimitives.ReadUInt32LittleEndian(entry[12..])),
            Is.EqualTo((0x8000_1000UL, 4095U)), "the address and the byte count less one");
        Assert.Throws<ArgumentException>(() => AhciCommands.WritePrdtEntry(table, 0, 0x1001, 512), "an odd address");
        Assert.Throws<ArgumentException>(() => AhciCommands.WritePrdtEntry(table, 0, 0x1000, 511), "an odd count");
        Assert.Throws<ArgumentException>(() => AhciCommands.WritePrdtEntry(table, 0, 0x1000, AhciCommands.PRDT_ENTRY_LIMIT + 2), "beyond 4 MiB");
        Assert.That(AhciCommands.TableBytes(128), Is.EqualTo(0x80 + 128 * 16));
    }

    [Test]
    public void QemuDiskIdentifiesItselfTest()
    {
        // QEMU's ide-hd (hw/ide/core.c, ide_identify): 48-bit addresses with FLUSH CACHE EXT, a write cache enabled.
        var data = Identify(sectors: 65536, model: "QEMU HARDDISK", serial: "QM00001", firmware: "2.5+");
        var identity = AtaIdentify.Read(data);
        Assert.That((identity.Model, identity.Serial, identity.Firmware), Is.EqualTo(("QEMU HARDDISK", "QM00001", "2.5+")), "strings of swapped bytes");
        Assert.That((identity.Lba48, identity.Sectors, identity.LogicalSectorBytes, identity.PhysicalSectorBytes, identity.Bytes),
            Is.EqualTo((true, 65536UL, 512, 512, 32L * 1024 * 1024)));
        Assert.That((identity.WriteCache, identity.FlushExt), Is.EqualTo((true, true)));
        Word(data, 85, 0);
        Assert.That(AtaIdentify.Read(data).WriteCache, Is.False, "supported but not enabled");
    }

    [Test]
    public void AdvancedFormatAndOlderDisksAreReadTest()
    {
        var native = Identify(sectors: 1000, model: "4KN", serial: "1", firmware: "1");
        Word(native, 106, 0x4000 | 1 << 12); // valid, the logical sector longer than 256 words
        Word(native, 117, 2048);             // 2048 words: 4096 bytes
        Assert.That((AtaIdentify.Read(native).LogicalSectorBytes, AtaIdentify.Read(native).Bytes), Is.EqualTo((4096, 4096L * 1000)));
        var emulated = Identify(sectors: 1000, model: "512E", serial: "1", firmware: "1");
        Word(emulated, 106, 0x4000 | 1 << 13 | 3); // eight logical sectors a physical one
        Assert.That((AtaIdentify.Read(emulated).LogicalSectorBytes, AtaIdentify.Read(emulated).PhysicalSectorBytes), Is.EqualTo((512, 4096)));
        var old = Identify(sectors: 0, model: "OLD", serial: "1", firmware: "1");
        Word(old, 83, 0x4000);
        Word(old, 60, 0x5678);
        Word(old, 61, 0x0012);
        var identity = AtaIdentify.Read(old);
        Assert.That((identity.Lba48, identity.Sectors, identity.FlushExt), Is.EqualTo((false, 0x12_5678UL, false)), "words 60–61 without 48-bit addresses");
        Assert.Throws<FormatException>(() => AtaIdentify.Read(new byte[100]));
        Assert.Throws<FormatException>(() => AtaIdentify.Read(Identify(sectors: 0, model: "", serial: "", firmware: "")), "no sector");
    }

    #endregion

    #region Tools

    // IDENTIFY DEVICE data with the words a host reads: the strings, 48-bit addresses and their flush, a write cache.
    private static byte[] Identify(ulong sectors, string model, string serial, string firmware)
    {
        var data = new byte[AtaIdentify.BYTES];
        Text(data, 10, 10, serial);
        Text(data, 23, 4, firmware);
        Text(data, 27, 20, model);
        Word(data, 82, 1 << 5);
        Word(data, 83, 0x4000 | 1 << 10 | 1 << 13);
        Word(data, 85, 1 << 5);
        for (var i = 0; i < 4; i++)
            Word(data, 100 + i, (ushort)(sectors >> 16 * i));
        return data;
    }

    private static void Word(byte[] data, int index, int value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(index * 2), (ushort)value);

    private static void Text(byte[] data, int word, int words, string text)
    {
        var padded = Encoding.ASCII.GetBytes(text.PadRight(words * 2));
        for (var i = 0; i < words * 2; i += 2)
        {
            data[word * 2 + i] = padded[i + 1];
            data[word * 2 + i + 1] = padded[i];
        }
    }

    #endregion
}
