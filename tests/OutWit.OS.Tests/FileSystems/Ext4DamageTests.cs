using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Ext4;
using OutWit.OS.Storage;
using OutWit.OS.Tests.Storage;

namespace OutWit.OS.Tests.FileSystems;

/// <summary>
/// What the ext provider refuses (storage document §72): a damaged superblock, inode or directory block fails its
/// checksum; a journal that needs recovery and a resource without a volume are Unsupported. And the substitution test
/// of §71: the same volume read over a partition and over the block protocol.
/// </summary>
[TestFixture]
public sealed class Ext4DamageTests
{
    #region Tests

    [Test]
    public void ChecksumsAreTheKernelsTest()
    {
        // CRC-32C's check value is 0xE3069283 for "123456789" with the usual inversions; ext4 keeps the raw update.
        Assert.That(~Ext4Checksums.Crc32C(~0U, Encoding.ASCII.GetBytes("123456789")), Is.EqualTo(0xE3069283));
        Assert.That(Ext4Checksums.Crc16(0, Encoding.ASCII.GetBytes("123456789")), Is.EqualTo(0xBB3D)); // CRC-16/ARC
    }

    [Test]
    public async Task StorageWithoutVolumeIsRefusedTest()
    {
        await using var disk = new MemoryBlockStorage(1024 * 1024);

        Assert.That(await Ext4FileSystem.DetectAsync(disk), Is.False);
        Assert.That(Error(() => Ext4FileSystem.MountAsync(disk).AsTask()), Is.EqualTo(FileSystemError.Unsupported));
    }

    [Test]
    public async Task DamagedSuperblockIsRefusedTest()
    {
        var image = Ext4Images.Create("ext4", out var source);
        Ext4Images.Delete(source);
        image[1024 + 0x78] ^= 0x20; // the label, under the superblock's checksum
        await using var disk = new MemoryBlockStorage(image, 512, isReadOnly: true);

        Assert.That(await Ext4FileSystem.DetectAsync(disk), Is.True);
        Assert.That(Error(() => Ext4FileSystem.MountAsync(disk).AsTask()), Is.EqualTo(FileSystemError.Corrupt));
    }

    [Test]
    public async Task JournalNeedingRecoveryIsRefusedTest()
    {
        var image = Ext4Images.Create("ext4", out var source);
        Ext4Images.Delete(source);
        var superblock = image.AsSpan(1024, 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[0x60..], BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x60..]) | 0x4);
        BinaryPrimitives.WriteUInt32LittleEndian(superblock[0x3FC..], Ext4Checksums.Crc32C(~0U, superblock[..0x3FC]));
        await using var disk = new MemoryBlockStorage(image, 512, isReadOnly: true);

        var failure = Assert.ThrowsAsync<FileSystemException>(async () => await Ext4FileSystem.MountAsync(disk))!;
        Assert.That(failure.Error, Is.EqualTo(FileSystemError.Unsupported));
        Assert.That(failure.Message, Does.Contain("recovery"));
    }

    [Test]
    public async Task DamagedInodeIsRefusedTest()
    {
        var image = Ext4Images.Create("ext4", out var source);
        Ext4Images.Delete(source);
        long inode;
        await using (var clean = new MemoryBlockStorage((byte[])image.Clone(), 512, isReadOnly: true))
        await using (var volume = await Ext4FileSystem.MountAsync(clean))
            inode = (await volume.GetEntryAsync("/hello.txt"))!.Unix!.Id;
        image[InodeOffset(image, inode) + 0x08] ^= 0x01; // its access time, under the inode's checksum
        await using var disk = new MemoryBlockStorage(image, 512, isReadOnly: true);
        await using var damaged = await Ext4FileSystem.MountAsync(disk);

        Assert.That(Error(() => damaged.GetEntryAsync("/hello.txt").AsTask()), Is.EqualTo(FileSystemError.Corrupt));
        Assert.That((await damaged.GetEntryAsync("/docs"))!.IsDirectory, Is.True);
        Assert.That((await damaged.CheckAsync()).Problems.Single(), Does.StartWith("/hello.txt: "));
    }

    [Test]
    public async Task DamagedDirectoryBlockIsRefusedTest()
    {
        var image = Ext4Images.Create("ext4", out var source);
        Ext4Images.Delete(source);
        var root = InodeOffset(image, 2);
        var block = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)root + 0x28 + 12 + 8)); // the first extent's start
        image[block * 4096L + 12 + 12 + 8] ^= 0x01; // a byte of the third entry's name, past "." and ".."
        await using var disk = new MemoryBlockStorage(image, 512, isReadOnly: true);
        await using var volume = await Ext4FileSystem.MountAsync(disk);

        Assert.That(Error(() => volume.EnumerateAsync("/").AsTask()), Is.EqualTo(FileSystemError.Corrupt));
    }

    [Test]
    public async Task BackendsServeTheSameVolumeTest()
    {
        var image = Ext4Images.Create("ext4", out var source);
        try
        {
            var expected = await File.ReadAllBytesAsync(Path.Combine(source, "big.bin"));
            var disk = new MemoryBlockStorage(new byte[image.Length + 2 * 1024 * 1024]);
            await disk.WriteAsync(1024 * 1024, image);
            IBlockStorage[] backends =
            [
                new PartitionBlockStorage(disk, 1024 * 1024, image.Length, isReadOnly: true),
                ChannelBlockStorage.Open(new LoopbackBlockSession(new MemoryBlockStorage(image, 512, isReadOnly: true), 8192, 4096))
            ];
            foreach (var backend in backends)
            {
                await using var volume = await Ext4FileSystem.MountAsync(backend);
                await using var file = await volume.OpenAsync("/big.bin", FileMode.Open, FileAccess.Read);
                var bytes = new byte[expected.Length];
                Assert.That(await file.ReadAsync(0, bytes), Is.EqualTo(expected.Length));
                Assert.That(bytes, Is.EqualTo(expected));
                await backend.DisposeAsync();
            }
        }
        finally
        {
            Ext4Images.Delete(source);
        }
    }

    #endregion

    #region Tools

    private static long InodeOffset(byte[] image, long inode) => Ext4Images.InodeOffset(image, inode);

    private static FileSystemError Error(Func<Task> operation) =>
        Assert.ThrowsAsync<FileSystemException>(async () => await operation())!.Error;

    #endregion
}
