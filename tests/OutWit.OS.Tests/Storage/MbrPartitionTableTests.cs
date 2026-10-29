using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.Storage;

/// <summary>
/// The master boot record's table (plan step E1): primary partitions written and read back as resources of their own,
/// the logical partitions an extended one chains, and the block 0s that hold no table — a GUID disk's protective record,
/// a FAT boot sector, an unknown active flag — with the tables whose partitions leave the disk, overlap or loop.
/// </summary>
[TestFixture]
public sealed class MbrPartitionTableTests
{
    #region Constants

    private const int BLOCK = 512;
    private const long BLOCKS = 4096;
    private const uint SIGNATURE = 0x5749544F;

    #endregion

    #region Tests

    [Test]
    public async Task WritesAndReadsPrimaryPartitionsTest()
    {
        await using var disk = new MemoryBlockStorage(BLOCKS * BLOCK);
        var table = await MbrPartitionTable.WriteAsync(disk, SIGNATURE,
            [new MbrPartition(1, 0x0C, 2048, 1024, true), new MbrPartition(3, 0x83, 3072, 1024, false)]);

        Assert.That(table.DiskSignature, Is.EqualTo(SIGNATURE));
        Assert.That(table.Partitions, Is.EqualTo(new[] { new MbrPartition(1, 0x0C, 2048, 1024, true), new MbrPartition(3, 0x83, 3072, 1024, false) }));
        await using var first = table.Partitions[0].Open(disk);
        Assert.That(first.Info.Length, Is.EqualTo(1024L * BLOCK));
        await first.WriteAsync(0, Enumerable.Repeat((byte)7, BLOCK).ToArray());
        var bytes = new byte[BLOCK];
        await disk.ReadAsync(2048L * BLOCK, bytes);
        Assert.That(bytes, Is.All.EqualTo(7), "the partition's window starts at its first block");
    }

    [Test]
    public async Task ReadsTheLogicalPartitionsOfAnExtendedOneTest()
    {
        var bytes = new byte[BLOCKS * BLOCK];
        Record(bytes, 0, (0x0C, 100, 900), (0x0F, 1000, 2000));
        Record(bytes, 1000, (0x83, 63, 200), (0x05, 300, 400));
        Record(bytes, 1300, (0x07, 63, 100));
        await using var disk = new MemoryBlockStorage(bytes);

        var table = await MbrPartitionTable.ReadAsync(disk);

        Assert.That(table.Partitions.Select(partition => (partition.Index, partition.Type, partition.FirstBlock, partition.BlockCount)),
            Is.EqualTo(new[] { (1, (byte)0x0C, 100L, 900L), (5, (byte)0x83, 1063L, 200L), (6, (byte)0x07, 1363L, 100L) }),
            "a logical partition from its record, the next record from the extended partition's start");
    }

    [Test]
    public async Task BlockZerosWithoutATableAreRefusedTest()
    {
        await using (var empty = new MemoryBlockStorage(BLOCKS * BLOCK))
            Assert.ThrowsAsync<InvalidDataException>(async () => await MbrPartitionTable.ReadAsync(empty), "no boot signature");

        await using (var gpt = new MemoryBlockStorage(BLOCKS * BLOCK))
        {
            await GptPartitionTable.WriteAsync(gpt, Guid.NewGuid(), []);
            var problem = Assert.ThrowsAsync<InvalidDataException>(async () => await MbrPartitionTable.ReadAsync(gpt));
            Assert.That(problem!.Message, Does.Contain("protective"));
        }

        await using (var fat = new MemoryBlockStorage(BLOCKS * BLOCK))
        {
            await (await FatFileSystem.FormatAsync(fat, "STICK")).DisposeAsync();
            var problem = Assert.ThrowsAsync<InvalidDataException>(async () => await MbrPartitionTable.ReadAsync(fat));
            Assert.That(problem!.Message, Does.Contain("FAT"), "a superfloppy's boot sector carries the signature too");
        }

        var flagged = new byte[BLOCKS * BLOCK];
        Record(flagged, 0, (0x0C, 100, 900));
        flagged[446] = 0x12;
        await using (var disk = new MemoryBlockStorage(flagged))
            Assert.ThrowsAsync<InvalidDataException>(async () => await MbrPartitionTable.ReadAsync(disk), "an active flag of 0x12");
    }

    [Test]
    public async Task TablesOutsideTheDiskOverlappingOrLoopingAreRefusedTest()
    {
        var beyond = new byte[BLOCKS * BLOCK];
        Record(beyond, 0, (0x0C, 100, BLOCKS));
        var overlapping = new byte[BLOCKS * BLOCK];
        Record(overlapping, 0, (0x0C, 100, 900), (0x83, 900, 100));
        var looping = new byte[BLOCKS * BLOCK];
        Record(looping, 0, (0x0F, 1000, 2000));
        Record(looping, 1000, (0x83, 63, 200), (0x05, 0, 400));
        foreach (var (bytes, what) in new[] { (beyond, "outside the disk"), (overlapping, "overlap"), (looping, "loops") })
        {
            await using var disk = new MemoryBlockStorage(bytes);
            var problem = Assert.ThrowsAsync<InvalidDataException>(async () => await MbrPartitionTable.ReadAsync(disk));
            Assert.That(problem!.Message, Does.Contain(what));
        }
    }

    [Test]
    public async Task WriterRefusesWhatNoTableHoldsTest()
    {
        await using var disk = new MemoryBlockStorage(BLOCKS * BLOCK);
        Assert.ThrowsAsync<ArgumentException>(async () => await MbrPartitionTable.WriteAsync(disk, SIGNATURE, [new MbrPartition(5, 0x0C, 100, 100, false)]),
            "primary entries alone");
        Assert.ThrowsAsync<ArgumentException>(async () => await MbrPartitionTable.WriteAsync(disk, SIGNATURE,
            [new MbrPartition(1, 0x0C, 100, 100, false), new MbrPartition(2, 0x0C, 150, 100, false)]), "overlapping");
        Assert.ThrowsAsync<ArgumentException>(async () => await MbrPartitionTable.WriteAsync(disk, SIGNATURE, [new MbrPartition(1, 0x0C, 100, BLOCKS, false)]),
            "beyond the disk");
        Assert.ThrowsAsync<ArgumentException>(async () => await MbrPartitionTable.WriteAsync(disk, SIGNATURE,
            [new MbrPartition(1, MbrPartitionTable.PROTECTIVE, 1, 100, false)]), "a protective entry");
    }

    #endregion

    #region Functions

    // An MBR or an extended boot record at a block: its entries' types, first blocks relative to the record's base, and
    // block counts, then the boot signature.
    private static void Record(byte[] bytes, long block, params (byte Type, long First, long Count)[] entries)
    {
        var record = bytes.AsSpan((int)(block * BLOCK), BLOCK);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = record.Slice(446 + i * 16, 16);
            entry[4] = entries[i].Type;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)entries[i].First);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)entries[i].Count);
        }
        record[510] = 0x55;
        record[511] = 0xAA;
    }

    #endregion
}
