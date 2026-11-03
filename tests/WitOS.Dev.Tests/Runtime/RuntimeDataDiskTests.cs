using System.Buffers.Binary;
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
            var vmx = Path.ChangeExtension(path, ".vmx");
            await RuntimeDataDisk.WriteVmxAsync(vmdk, vmx);
            var machine = await File.ReadAllTextAsync(vmx);
            Assert.That(machine, Does.Contain("firmware = \"efi\"\n").And.Contain("hpet0.present = \"TRUE\"\n"), "the firmware and the clock H5 measured");
            Assert.That(machine, Does.Contain("rtc.diffFromUTC = \"0\"\n"), "the real-time clock in UTC, as the kernel reads it");
            Assert.That(machine, Does.Contain("sata0:0.fileName = \"data-disk.vmdk\"\n"), "the descriptor beside it");
            Assert.That(machine, Does.Contain("ethernet0.virtualDev = \"e1000e\"\n").And.Contain("serial0.fileName = \"serial.log\"\n"));
            Assert.That(machine, Does.Contain("keyboard.vusb.enable = \"FALSE\"\n").And.Contain("memsize = \"2048\"\n"));
            Assert.That(machine, Does.Not.Contain("RemoteDisplay").And.Not.Contain("\r"), "no remote display without a password");
            Assert.ThrowsAsync<ArgumentException>(async () => await RuntimeDataDisk.CreateAsync(path, ext4: false, bootImage: vmdk), "a boot disk of no whole MiB");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // The VMware machine's USB drive (E4.5): a fixed VHD whose disk is an MBR with one FAT32 volume, which VMware attaches
    // through a descriptor whose extent leaves the footer out, on the xHCI controller's port 6.
    [Test]
    public async Task UsbDriveIsAFixedVhdTest()
    {
        var path = TemporaryImage();
        try
        {
            var drive = Path.Combine(Path.GetDirectoryName(path)!, "WitOS-usb.vhd");
            await RuntimeDataDisk.WriteUsbDriveAsync(drive);
            var image = await File.ReadAllBytesAsync(drive);
            Assert.That(image.LongLength, Is.EqualTo(RuntimeDataDisk.USB_DRIVE_BYTES + RuntimeDataDisk.VHD_FOOTER_BYTES));

            var footer = image.AsSpan(image.Length - RuntimeDataDisk.VHD_FOOTER_BYTES);
            Assert.That(Encoding.ASCII.GetString(footer[..8]), Is.EqualTo("conectix"));
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(footer[60..]), Is.EqualTo(2), "a fixed disk");
            Assert.That(BinaryPrimitives.ReadUInt64BigEndian(footer[16..]), Is.EqualTo(ulong.MaxValue), "no dynamic header");
            Assert.That(BinaryPrimitives.ReadUInt64BigEndian(footer[48..]), Is.EqualTo((ulong)RuntimeDataDisk.USB_DRIVE_BYTES), "the current size");
            var geometry = (Cylinders: BinaryPrimitives.ReadUInt16BigEndian(footer[56..]), Heads: footer[58], Sectors: footer[59]);
            Assert.That((long)geometry.Cylinders * geometry.Heads * geometry.Sectors * 512, Is.EqualTo(RuntimeDataDisk.USB_DRIVE_BYTES),
                "the geometry covers the disk exactly");
            uint sum = 0;
            for (var i = 0; i < footer.Length; i++)
                sum += i is >= 64 and < 68 ? 0u : footer[i];
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(footer[64..]), Is.EqualTo(~sum), "the footer's checksum");

            // The file as a disk, the footer its last sector, which the table and the volume never reach.
            await using (var disk = new MemoryBlockStorage(image, 512, isReadOnly: true))
            {
                var table = await MbrPartitionTable.ReadAsync(disk);
                Assert.That(table.Partitions.Select(entry => (entry.Type, entry.FirstBlock)), Is.EqualTo(new[] { ((byte)0x0C, 2048L) }));
                await using var partition = table.Partitions[0].Open(disk, isReadOnly: true);
                await using var volume = await FatFileSystem.MountAsync(partition);
                Assert.That(volume.Volume.Label, Is.EqualTo(RuntimeDataDisk.USB_DRIVE_LABEL));
                Assert.That(volume.Volume.Format, Is.EqualTo("FAT32"));
                Assert.That((await volume.GetEntryAsync(RuntimeDataDisk.USB_DRIVE_README))!.Length,
                    Is.EqualTo(Encoding.UTF8.GetByteCount(RuntimeDataDisk.USB_DRIVE_TEXT)));
                Assert.That((await volume.CheckAsync()).IsClean, Is.True);
            }

            var vmdk = Path.ChangeExtension(drive, ".vmdk");
            await RuntimeDataDisk.WriteVmdkAsync(drive, vmdk, RuntimeDataDisk.USB_DRIVE_BYTES);
            Assert.That(await File.ReadAllTextAsync(vmdk), Does.Contain($"RW {RuntimeDataDisk.USB_DRIVE_BYTES / 512} FLAT \"WitOS-usb.vhd\" 0\n"),
                "the disk without the footer");
            var vmx = Path.ChangeExtension(path, ".vmx");
            await RuntimeDataDisk.WriteVmxAsync(Path.ChangeExtension(path, ".vmdk"), vmx, vmdk);
            Assert.That(await File.ReadAllTextAsync(vmx), Does.Contain(
                "usb_xhci.present = \"TRUE\"\nusb_xhci:6.present = \"TRUE\"\nusb_xhci:6.deviceType = \"disk\"\n" +
                "usb_xhci:6.fileName = \"WitOS-usb.vmdk\"\nusb_xhci:6.readonly = \"FALSE\"\nkeyboard.vusb.enable = \"FALSE\"\n"));
            await RuntimeDataDisk.WriteVmxAsync(Path.ChangeExtension(path, ".vmdk"), vmx);
            Assert.That(await File.ReadAllTextAsync(vmx), Does.Contain("usb_xhci.present = \"TRUE\"\nkeyboard.vusb.enable = \"FALSE\"\n"), "no drive unless named");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // The VHD specification's geometry: Virtual PC's 10 MiB disk and a gigabyte, which it covers short of the end.
    [Test]
    public void VhdGeometryFollowsTheSpecificationTest()
    {
        Assert.That(RuntimeDataDisk.VhdGeometry(10 * 2048), Is.EqualTo((301, 4, 17)));
        Assert.That(RuntimeDataDisk.VhdGeometry(1024 * 2048), Is.EqualTo((2080, 16, 63)));
        Assert.That(RuntimeDataDisk.VhdGeometry(RuntimeDataDisk.USB_DRIVE_BYTES / 512), Is.EqualTo((2048, 16, 63)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeDataDisk.VhdFooter(1000, Guid.Empty));
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
