using System.Text;
using OutWit.OS.FileSystems.Ext4;
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
            await RuntimeDataDisk.CreateAsync(path, RuntimeExt4Volume.FindMkfs() is not null);
            await using var disk = new MemoryBlockStorage(await File.ReadAllBytesAsync(path));

            var table = await GptPartitionTable.ReadAsync(disk);
            await using var partition = table.Partitions[1].Open(disk);
            await using var volume = await FatFileSystem.MountAsync(partition);

            Assert.That(disk.Info.Length, Is.EqualTo(RuntimeDataDisk.BYTES));
            Assert.That(table.Partitions.Select(entry => (entry.Name, entry.FirstBlock, entry.LastBlock)), Is.EqualTo(new[]
            {
                (RuntimeDataDisk.TEST_PARTITION, RuntimeDataDisk.TEST_PARTITION_FIRST_BLOCK, RuntimeDataDisk.TEST_PARTITION_LAST_BLOCK),
                (RuntimeDataDisk.FAT_PARTITION, RuntimeDataDisk.FAT_PARTITION_FIRST_BLOCK, RuntimeDataDisk.FAT_PARTITION_LAST_BLOCK),
                (RuntimeDataDisk.EXT4_PARTITION, RuntimeDataDisk.EXT4_PARTITION_FIRST_BLOCK, RuntimeDataDisk.EXT4_PARTITION_LAST_BLOCK)
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
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // A fresh image fails the check; the probes' writes, made here as the guest makes them, pass it.
    [Test]
    public async Task CheckRequiresTheProbesWritesTest()
    {
        var path = TemporaryImage();
        try
        {
            await RuntimeDataDisk.CreateAsync(path, RuntimeExt4Volume.FindMkfs() is not null);
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
            if (RuntimeExt4Volume.FindMkfs() is not null)
            {
                // The ext4 probe's files, written as the guest writes them, through the provider's journal.
                await using var disk = new MemoryBlockStorage(image);
                var table = await GptPartitionTable.ReadAsync(disk);
                await using var partition = table.Partitions[2].Open(disk);
                await using var volume = await Ext4FileSystem.MountAsync(partition);
                await volume.CreateDirectoryAsync("/guest/logs");
                await using (var file = await volume.OpenAsync("/guest/logs/written.bin", FileMode.CreateNew, FileAccess.Write))
                    await file.WriteAsync(0, Enumerable.Range(0, RuntimeExt4Volume.GUEST_BYTES).Select(RuntimeExt4Volume.GuestPattern).ToArray());
                await using (var file = await volume.OpenAsync("/guest/note.txt", FileMode.CreateNew, FileAccess.Write))
                    await file.WriteAsync(0, Encoding.UTF8.GetBytes(RuntimeExt4Volume.GUEST_NOTE));
                await using (var file = await volume.OpenAsync("/posix-kept.txt", FileMode.CreateNew, FileAccess.Write))
                    await file.WriteAsync(0, Encoding.UTF8.GetBytes(RuntimeExt4Volume.INIT_TEXT)); // /bin/init's, through the namespace
                await using (var file = await volume.OpenAsync("/dotnet-kept.txt", FileMode.CreateNew, FileAccess.Write))
                    await file.WriteAsync(0, Encoding.UTF8.GetBytes(RuntimeExt4Volume.DOTNET_TEXT)); // the .NET program's, through System.IO
            }
            await File.WriteAllBytesAsync(path, image);

            var report = await RuntimeDataDisk.CheckAsync(path);

            Assert.That(report.IsClean, Is.True);
            Assert.That(report.Files, Is.EqualTo(3));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // The ext4 volume the host's e2fsprogs makes: the provider reads the tree.
    [Test]
    public async Task Ext4PartitionHoldsTheTreeTest()
    {
        if (RuntimeExt4Volume.FindMkfs() is null)
            Assert.Ignore("The host has no e2fsprogs (mkfs.ext4).");
        var path = TemporaryImage();
        try
        {
            await RuntimeDataDisk.CreateAsync(path);
            var image = await File.ReadAllBytesAsync(path);
            await using (var disk = new MemoryBlockStorage(image, 512, isReadOnly: true))
            {
                var table = await GptPartitionTable.ReadAsync(disk);
                await using var partition = table.Partitions[2].Open(disk, isReadOnly: true);
                await using var volume = await Ext4FileSystem.MountAsync(partition);
                Assert.That(volume.Volume.Label, Is.EqualTo(RuntimeExt4Volume.LABEL));
                Assert.That((await volume.EnumerateAsync("/many")).Count, Is.EqualTo(RuntimeExt4Volume.MANY));
                Assert.That(await volume.ReadLinkAsync("/link"), Is.EqualTo(RuntimeExt4Volume.LINK_TARGET));
                Assert.That((await volume.GetEntryAsync("/big.bin"))!.Length, Is.EqualTo(RuntimeExt4Volume.BIG_BYTES));
                Assert.That((await volume.CheckAsync()).IsClean, Is.True);
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // The PC's one disk (H3.3): the data partitions where a data disk keeps them, the boot disk's bytes as an EFI system
    // partition after them, and a VMDK descriptor that names the raw image as its one flat extent.
    [Test]
    public async Task PcDiskCarriesTheSystemPartitionAfterTheDataTest()
    {
        var path = TemporaryImage();
        try
        {
            var boot = Path.Combine(Path.GetDirectoryName(path)!, "boot.img");
            var system = new byte[2 * 1024 * 1024];
            system[0] = 0xEB;
            system[^1] = 0x5A;
            await File.WriteAllBytesAsync(boot, system);
            await RuntimeDataDisk.CreateAsync(path, ext4: false, bootImage: boot);
            var image = await File.ReadAllBytesAsync(path);
            await using var disk = new MemoryBlockStorage(image);
            var table = await GptPartitionTable.ReadAsync(disk);
            Assert.That(disk.Info.Length, Is.EqualTo(RuntimeDataDisk.BYTES + system.Length));
            Assert.That(table.Partitions.Select(entry => entry.Name), Is.EqualTo(new[]
                { RuntimeDataDisk.TEST_PARTITION, RuntimeDataDisk.FAT_PARTITION, RuntimeDataDisk.EXT4_PARTITION, RuntimeDataDisk.SYSTEM_PARTITION }));
            Assert.That(table.Partitions[2].LastBlock, Is.EqualTo(RuntimeDataDisk.EXT4_PARTITION_LAST_BLOCK), "the ext4 partition keeps a data disk's size");
            var esp = table.Partitions[3];
            Assert.That((esp.Type, esp.FirstBlock, esp.LastBlock),
                Is.EqualTo((GptPartition.EFI_SYSTEM, RuntimeDataDisk.SYSTEM_PARTITION_FIRST_BLOCK, RuntimeDataDisk.SYSTEM_PARTITION_FIRST_BLOCK + system.Length / 512 - 1)));
            Assert.That(image.AsSpan((int)(esp.FirstBlock * 512), system.Length).SequenceEqual(system), Is.True, "the boot disk's bytes as they are");
            var vmdk = Path.ChangeExtension(path, ".vmdk");
            await RuntimeDataDisk.WriteVmdkAsync(path, vmdk);
            var descriptor = await File.ReadAllTextAsync(vmdk);
            Assert.That(descriptor, Does.StartWith("# Disk DescriptorFile\n"));
            Assert.That(descriptor, Does.Contain("createType=\"monolithicFlat\"\n"));
            Assert.That(descriptor, Does.Contain($"RW {image.Length / 512} FLAT \"data-disk.img\" 0\n"), "the raw image as its one extent");
            Assert.That(descriptor, Does.Not.Contain("\r"));
            Assert.ThrowsAsync<ArgumentException>(async () => await RuntimeDataDisk.CreateAsync(path, ext4: false, bootImage: vmdk), "a boot disk of no whole MiB");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // A data disk's path in a directory of its own, beside which CreateAsync keeps the ext4 volume.
    private static string TemporaryImage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"witos-data-disk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "data-disk.img");
    }

    #endregion
}
