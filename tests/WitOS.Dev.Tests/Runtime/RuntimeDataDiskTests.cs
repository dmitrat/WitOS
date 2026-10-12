using System.Text;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Storage;
using WitOS.Dev.Runtime;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The data disk of the runtime-devices scenario (plan steps D3.2 and D4.1): the image the tool writes carries the
/// partitions the device manager's probes look for, the FAT partition holds the host's file, and the check after the
/// boot requires what the probes wrote.
/// </summary>
[TestFixture]
public sealed class RuntimeDataDiskTests
{
    #region Functions

    // OutWit.OS's reader finds both partitions at the blocks the tool names, and the FAT partition mounts.
    [Test]
    public async Task DiskCarriesThePartitionsTest()
    {
        var path = TemporaryImage();
        try
        {
            await RuntimeDataDisk.CreateAsync(path);
            await using var disk = new MemoryBlockStorage(await File.ReadAllBytesAsync(path));

            var table = await GptPartitionTable.ReadAsync(disk);
            await using var partition = table.Partitions[1].Open(disk);
            await using var volume = await FatFileSystem.MountAsync(partition);

            Assert.That(disk.Info.Length, Is.EqualTo(RuntimeDataDisk.BYTES));
            Assert.That(table.Partitions.Select(entry => (entry.Name, entry.FirstBlock, entry.LastBlock)), Is.EqualTo(new[]
            {
                (RuntimeDataDisk.TEST_PARTITION, RuntimeDataDisk.TEST_PARTITION_FIRST_BLOCK, RuntimeDataDisk.TEST_PARTITION_LAST_BLOCK),
                (RuntimeDataDisk.FAT_PARTITION, RuntimeDataDisk.FAT_PARTITION_FIRST_BLOCK, RuntimeDataDisk.FAT_PARTITION_LAST_BLOCK)
            }));
            Assert.That((RuntimeDataDisk.TEST_PARTITION_LAST_BLOCK - RuntimeDataDisk.TEST_PARTITION_FIRST_BLOCK + 1) * 512,
                Is.GreaterThanOrEqualTo(RuntimeDataDisk.TEST_PATTERN_BYTES));
            Assert.That(volume.Volume.Label, Is.EqualTo(RuntimeDataDisk.FAT_LABEL));
            Assert.That((await volume.GetEntryAsync(RuntimeDataDisk.HOST_FILE))!.Length,
                Is.EqualTo(Encoding.UTF8.GetByteCount(RuntimeDataDisk.HOST_TEXT)));
            Assert.That((await volume.CheckAsync()).IsClean, Is.True);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A fresh image fails the check; the probes' writes, made here as the guest makes them, pass it.
    [Test]
    public async Task CheckRequiresTheProbesWritesTest()
    {
        var path = TemporaryImage();
        try
        {
            await RuntimeDataDisk.CreateAsync(path);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await RuntimeDataDisk.CheckAsync(path));

            var image = await File.ReadAllBytesAsync(path);
            var start = (int)RuntimeDataDisk.TEST_PARTITION_FIRST_BLOCK * 512;
            for (var i = 0; i < RuntimeDataDisk.TEST_PATTERN_BYTES; i++)
                image[start + i] = RuntimeDataDisk.TestPattern(i);
            await using (var disk = new MemoryBlockStorage(image))
            {
                var table = await GptPartitionTable.ReadAsync(disk);
                await using var partition = table.Partitions[1].Open(disk);
                await using var volume = await FatFileSystem.MountAsync(partition);
                await volume.CreateDirectoryAsync("/guest");
                await using (var file = await volume.OpenAsync(RuntimeDataDisk.GUEST_FILE, FileMode.Create, FileAccess.Write))
                    await file.WriteAsync(0, Enumerable.Range(0, RuntimeDataDisk.GUEST_FILE_BYTES).Select(RuntimeDataDisk.GuestPattern).ToArray());
                await using (var file = await volume.OpenAsync(RuntimeDataDisk.GUEST_NOTE, FileMode.Create, FileAccess.Write))
                    await file.WriteAsync(0, Encoding.UTF8.GetBytes(RuntimeDataDisk.GUEST_TEXT));
            }
            await File.WriteAllBytesAsync(path, image);

            var report = await RuntimeDataDisk.CheckAsync(path);

            Assert.That(report.IsClean, Is.True);
            Assert.That(report.Files, Is.EqualTo(3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TemporaryImage() => Path.Combine(Path.GetTempPath(), $"witos-data-disk-{Guid.NewGuid():N}.img");

    #endregion
}
