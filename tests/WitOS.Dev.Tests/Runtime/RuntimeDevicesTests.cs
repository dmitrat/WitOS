using OutWit.OS.Storage;
using WitOS.Dev.Runtime;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The data disk of the runtime-devices scenario (plan step D3.2): the image the tool writes carries the test partition
/// the device manager's probe looks for, and the check after the boot requires what the probe wrote.
/// </summary>
[TestFixture]
public sealed class RuntimeDevicesTests
{
    #region Functions

    // OutWit.OS's reader finds the test partition at the blocks the tool names.
    [Test]
    public async Task DataDiskCarriesTheTestPartitionTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"witos-data-disk-{Guid.NewGuid():N}.img");
        try
        {
            await RuntimeDevices.CreateDataDiskAsync(path);
            await using var disk = new MemoryBlockStorage(await File.ReadAllBytesAsync(path));

            var table = await GptPartitionTable.ReadAsync(disk);

            Assert.That(disk.Info.Length, Is.EqualTo(RuntimeDevices.DATA_DISK_BYTES));
            Assert.That(table.FromBackup, Is.False);
            var partition = table.Partitions.Single();
            Assert.That(partition.Name, Is.EqualTo(RuntimeDevices.TEST_PARTITION));
            Assert.That(partition.FirstBlock, Is.EqualTo(RuntimeDevices.TEST_PARTITION_FIRST_BLOCK));
            Assert.That(partition.LastBlock, Is.EqualTo(RuntimeDevices.TEST_PARTITION_LAST_BLOCK));
            Assert.That((partition.LastBlock - partition.FirstBlock + 1) * 512, Is.GreaterThanOrEqualTo(RuntimeDevices.TEST_PATTERN_BYTES));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A fresh image fails the check; the probe's pattern at the partition's start passes it.
    [Test]
    public async Task CheckRequiresThePatternTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"witos-data-disk-{Guid.NewGuid():N}.img");
        try
        {
            await RuntimeDevices.CreateDataDiskAsync(path);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await RuntimeDevices.CheckDataDiskAsync(path));

            var image = await File.ReadAllBytesAsync(path);
            var start = (int)RuntimeDevices.TEST_PARTITION_FIRST_BLOCK * 512;
            for (var i = 0; i < RuntimeDevices.TEST_PATTERN_BYTES; i++)
                image[start + i] = RuntimeDevices.TestPattern(i);
            await File.WriteAllBytesAsync(path, image);

            Assert.DoesNotThrowAsync(async () => await RuntimeDevices.CheckDataDiskAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion
}
