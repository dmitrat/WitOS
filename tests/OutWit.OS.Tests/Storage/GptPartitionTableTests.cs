using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.Storage;

/// <summary>
/// The GUID partition table (UEFI 2.10, §5.3): the reader with partitions as resources of their own, the backup table and
/// the tables it refuses; the writer against the tests' own image and the partitions it refuses.
/// </summary>
[TestFixture]
public sealed class GptPartitionTableTests
{
    #region Constants

    private const int BLOCK = 512;
    private const long BLOCKS = 2048;

    #endregion

    #region Fields

    private static readonly Guid DISK = Guid.Parse("8f1d2e3c-4b5a-6978-8a9b-0c1d2e3f4a5b");
    private static readonly Guid BASIC_DATA = Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    private static readonly Guid FIRST = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid SECOND = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    #endregion

    #region Tests

    [Test]
    public async Task ReadsPartitionsTest()
    {
        await using var disk = new MemoryBlockStorage(Image());

        var table = await GptPartitionTable.ReadAsync(disk);

        Assert.That(table.DiskId, Is.EqualTo(DISK));
        Assert.That(table.FromBackup, Is.False);
        Assert.That(table.Partitions, Has.Count.EqualTo(2));
        Assert.That(table.Partitions[0], Is.EqualTo(new GptPartition(0, BASIC_DATA, FIRST, 64, 1023, 0, "Programs")));
        Assert.That(table.Partitions[1].Name, Is.EqualTo("Data"));
        Assert.That(table.Partitions[1].Id, Is.EqualTo(SECOND));
    }

    [Test]
    public async Task PartitionIsWindowOfDiskTest()
    {
        var bytes = Image();
        await using var disk = new MemoryBlockStorage(bytes);
        var table = await GptPartitionTable.ReadAsync(disk);
        await using var partition = table.Partitions[1].Open(disk);

        await partition.WriteAsync(0, Enumerable.Repeat((byte)0x7E, BLOCK).ToArray());

        Assert.That(partition.Info.Length, Is.EqualTo((1999 - 1024 + 1) * (long)BLOCK));
        Assert.That(bytes.AsSpan(1024 * BLOCK, BLOCK).ToArray().All(value => value == 0x7E), Is.True);
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await partition.ReadAsync(partition.Info.Length, new byte[BLOCK]));
    }

    [Test]
    public async Task BackupServesWhenPrimaryFailsTest()
    {
        var bytes = Image();
        bytes[BLOCK + 20] ^= 0xFF; // within the primary header, after its CRC
        await using var disk = new MemoryBlockStorage(bytes);

        var table = await GptPartitionTable.ReadAsync(disk);

        Assert.That(table.FromBackup, Is.True);
        Assert.That(table.Partitions, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task CorruptEntriesAreRefusedTest()
    {
        var bytes = Image();
        bytes[2 * BLOCK + 40] ^= 0x01; // the primary entries
        bytes[(int)(BLOCKS - 33) * BLOCK + 40] ^= 0x01; // the backup entries
        await using var disk = new MemoryBlockStorage(bytes);

        var problem = Assert.ThrowsAsync<InvalidDataException>(async () => await GptPartitionTable.ReadAsync(disk));
        Assert.That(problem!.Message, Does.Contain("CRC-32"));
    }

    [Test]
    public async Task OverlappingEntriesAreRefusedTest()
    {
        var bytes = GptImage.Create(BLOCK, BLOCKS, DISK,
            [(BASIC_DATA, FIRST, 64, 1023, "A"), (BASIC_DATA, SECOND, 1000, 1999, "B")]);
        await using var disk = new MemoryBlockStorage(bytes);

        var problem = Assert.ThrowsAsync<InvalidDataException>(async () => await GptPartitionTable.ReadAsync(disk));
        Assert.That(problem!.Message, Does.Contain("overlap"));
    }

    [Test]
    public async Task DiskWithoutTableIsRefusedTest()
    {
        await using var disk = new MemoryBlockStorage(BLOCKS * BLOCK);

        Assert.ThrowsAsync<InvalidDataException>(async () => await GptPartitionTable.ReadAsync(disk));
    }

    [Test]
    public async Task WrittenTableIsTheReferenceImageTest()
    {
        await using var disk = new MemoryBlockStorage(BLOCKS * BLOCK);

        var table = await GptPartitionTable.WriteAsync(disk, DISK,
            [new GptPartition(0, BASIC_DATA, FIRST, 64, 1023, 0, "Programs"), new GptPartition(1, BASIC_DATA, SECOND, 1024, 1999, 0, "Data")]);

        Assert.That(table.Partitions, Is.EqualTo(new[]
        {
            new GptPartition(0, BASIC_DATA, FIRST, 64, 1023, 0, "Programs"), new GptPartition(1, BASIC_DATA, SECOND, 1024, 1999, 0, "Data")
        }));
        // Everything after the protective MBR, whose CHS fields the tests' image leaves zero, is the same bytes.
        Assert.That(disk.Bytes[BLOCK..].ToArray(), Is.EqualTo(Image()[BLOCK..]));
        Assert.That(disk.Bytes[446 + 4], Is.EqualTo(0xEE));
        Assert.That(disk.Bytes[510], Is.EqualTo(0x55));
        Assert.That(disk.Bytes[511], Is.EqualTo(0xAA));
    }

    [Test]
    public async Task WrittenBackupServesTest()
    {
        await using var disk = new MemoryBlockStorage(BLOCKS * BLOCK, 4096);
        await GptPartitionTable.WriteAsync(disk, DISK, [new GptPartition(5, BASIC_DATA, FIRST, 8, 100, 1, "Five")]);
        await disk.WriteAsync(4096, new byte[4096]);

        var table = await GptPartitionTable.ReadAsync(disk);

        Assert.That(table.FromBackup, Is.True);
        Assert.That(table.FirstUsableBlock, Is.EqualTo(2 + 4));
        Assert.That(table.Partitions.Single(), Is.EqualTo(new GptPartition(5, BASIC_DATA, FIRST, 8, 100, 1, "Five")));
    }

    [TestCase(0, 64, 1023, 1, 1000, 1999)]
    [TestCase(0, 33, 1023, 1, 1024, 1999)]
    [TestCase(0, 64, 1023, 1, 1024, 2015)]
    [TestCase(0, 64, 1023, 0, 1024, 1999)]
    [TestCase(0, 64, 1023, 128, 1024, 1999)]
    [TestCase(0, 1023, 64, 1, 1024, 1999)]
    public async Task PartitionsThatDoNotFitAreRefusedTest(int index, long first, long last, int otherIndex, long otherFirst, long otherLast)
    {
        await using var disk = new MemoryBlockStorage(BLOCKS * BLOCK);

        Assert.ThrowsAsync<ArgumentException>(async () => await GptPartitionTable.WriteAsync(disk, DISK,
            [new GptPartition(index, BASIC_DATA, FIRST, first, last, 0, "A"), new GptPartition(otherIndex, BASIC_DATA, SECOND, otherFirst, otherLast, 0, "B")]));
        Assert.That(disk.Bytes.ToArray().All(value => value == 0), Is.True);
    }

    [Test]
    public async Task WriterRefusesWhatNoEntryHoldsTest()
    {
        await using var disk = new MemoryBlockStorage(BLOCKS * BLOCK);
        await using var small = new MemoryBlockStorage(66 * BLOCK);

        Assert.ThrowsAsync<ArgumentException>(async () => await GptPartitionTable.WriteAsync(disk, DISK,
            [new GptPartition(0, Guid.Empty, FIRST, 64, 1023, 0, "Typeless")]));
        Assert.ThrowsAsync<ArgumentException>(async () => await GptPartitionTable.WriteAsync(disk, DISK,
            [new GptPartition(0, BASIC_DATA, FIRST, 64, 1023, 0, new string('n', 37))]));
        Assert.ThrowsAsync<ArgumentException>(async () => await GptPartitionTable.WriteAsync(small, DISK, []));
    }

    #endregion

    #region Tools

    private static byte[] Image() => GptImage.Create(BLOCK, BLOCKS, DISK,
        [(BASIC_DATA, FIRST, 64, 1023, "Programs"), (BASIC_DATA, SECOND, 1024, 1999, "Data")]);

    #endregion
}
