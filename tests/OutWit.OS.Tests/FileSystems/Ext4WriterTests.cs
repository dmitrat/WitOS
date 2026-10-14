using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Ext4;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.FileSystems;

/// <summary>
/// The ext4 provider writes (plan step D4.3), judged by e2fsprogs (storage document §73): after files and directories
/// are created, written, cut, removed and moved through the journal, e2fsck finds the volume clean and debugfs reads
/// what the provider wrote; for each variant of ext4 mkfs makes.
/// </summary>
[TestFixture("ext4")]
[TestFixture("ext4-1k")]
[TestFixture("ext4-nocsum")]
[TestFixture("ext4-small-inodes")]
public sealed class Ext4WriterTests(string variant)
{
    #region Constants

    // A modification time the provider sets by hand (N4.1): seconds and nanoseconds.
    private static readonly DateTimeOffset STAMP = new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(1_234_567);

    #endregion

    #region Tests

    [Test]
    public async Task ChangesPassE2fsckTest()
    {
        var image = Ext4Images.Create(variant, out var source);
        Ext4Images.Delete(source);
        var big = Pattern(1_100_000, 5);
        var note = Encoding.UTF8.GetBytes("Written by WitOS's ext4 provider through the journal.");
        await using (var disk = new MemoryBlockStorage(image, 512))
        await using (var volume = await Ext4FileSystem.MountAsync(disk))
        {
            Assert.That(volume.Volume.IsReadOnly, Is.False);
            await volume.CreateDirectoryAsync("/made/sub");
            await using (var file = await volume.OpenAsync("/made/sub/big.bin", FileMode.CreateNew, FileAccess.ReadWrite))
            {
                await file.WriteAsync(600_000, big.AsMemory(600_000));       // a hole before it first
                await file.WriteAsync(0, big.AsMemory(0, 300_000));
                await file.WriteAsync(300_000, big.AsMemory(300_000, 300_000)); // the hole filled
            }
            await using (var file = await volume.OpenAsync("/note.txt", FileMode.Create, FileAccess.Write))
                await file.WriteAsync(0, note);
            await using (var file = await volume.OpenAsync("/docs/nested/deep/file.bin", FileMode.Open, FileAccess.ReadWrite))
                await file.SetLengthAsync(1234);                              // a cut inside a block
            await using (var file = await volume.OpenAsync("/big.bin", FileMode.Open, FileAccess.ReadWrite))
                await file.WriteAsync(20_000, Pattern(50_000, 9));            // across mkfs's chunks and holes
            await volume.DeleteAsync("/docs/readme.md");
            await volume.RenameAsync("/hello.txt", "/made/hello-moved.txt");
            await volume.RenameAsync("/docs/nested", "/made/nested");        // a directory to another parent
            for (var i = 0; i < 60; i++)                                       // into the htree, which becomes linear
                await using (var file = await volume.OpenAsync($"/many/added-by-witos-{i:00}", FileMode.CreateNew, FileAccess.Write))
                    await file.WriteAsync(0, Encoding.UTF8.GetBytes(i.ToString()));
            for (var i = 0; i < 40; i++)
                await volume.DeleteAsync($"/many/an-entry-with-a-rather-long-name-{i:0000}.txt");
            await volume.DeleteAsync("/empty.txt");
            var mode = await volume.SetModeAsync("/note.txt", 0x1A0);                 // 0640 (N4.1)
            Assert.That(mode.Unix!.Mode, Is.EqualTo(0x1A0));
            Assert.That((await volume.SetModeAsync("/made", 0x1C0)).Unix!.Mode, Is.EqualTo(0x1C0)); // 0700 on a directory
            var stamped = await volume.SetTimesAsync("/note.txt", null, STAMP);        // utimensat (N4.1)
            Assert.That(stamped.Modified, Is.EqualTo(STAMP));
            Assert.That(Error(() => volume.DeleteAsync("/made").AsTask()), Is.EqualTo(FileSystemError.NotEmpty));
            Assert.That(Error(() => volume.RenameAsync("/made", "/made/sub/inside").AsTask()), Is.EqualTo(FileSystemError.InvalidName));
            Assert.That(Error(() => volume.OpenAsync("/note.txt", FileMode.CreateNew, FileAccess.Write).AsTask()), Is.EqualTo(FileSystemError.AlreadyExists));
        }

        var (exit, report) = Ext4Tools.Check(image);
        Assert.That(exit, Is.Zero, report);
        Assert.That(Ext4Tools.Dump(image, "/made/sub/big.bin"), Is.EqualTo(big));
        Assert.That(Ext4Tools.Dump(image, "/note.txt"), Is.EqualTo(note));
        Assert.That(Ext4Tools.Dump(image, "/made/hello-moved.txt"), Is.EqualTo(Encoding.UTF8.GetBytes(Ext4Images.HELLO)));
        Assert.That(Ext4Tools.Dump(image, "/made/nested/deep/file.bin").Length, Is.EqualTo(1234));
        Assert.That(Ext4Tools.Script(image, "stat /note.txt"), Does.Contain("Mode:  0640"), "debugfs reads the mode the provider set");
        Assert.That(Ext4Tools.Script(image, "stat /made"), Does.Contain("Mode:  0700"));
        await using var again = await Ext4FileSystem.MountAsync(new MemoryBlockStorage(image, 512, isReadOnly: true));
        Assert.That((await again.GetEntryAsync("/note.txt"))!.Modified, Is.EqualTo(variant == "ext4-small-inodes" ? STAMP.AddTicks(-STAMP.Ticks % TimeSpan.TicksPerSecond) : STAMP),
            "the time the provider set, to the second where the inode has no room for nanoseconds");
        Assert.That((await again.EnumerateAsync("/many")).Count, Is.EqualTo(Ext4Images.MANY + 60 - 40));
        Assert.That(await again.GetEntryAsync("/docs/readme.md"), Is.Null);
        Assert.That((await again.EnumerateAsync("/made")).Select(entry => entry.Name), Is.EquivalentTo(new[] { "sub", "hello-moved.txt", "nested" }));
        Assert.That((await again.CheckAsync()).Problems, Is.Empty);
    }

    [Test]
    public async Task ReadOnlyResourceMountsReadOnlyTest()
    {
        var image = Ext4Images.Create(variant, out var source);
        Ext4Images.Delete(source);
        await using var volume = await Ext4FileSystem.MountAsync(new MemoryBlockStorage(image, 512, isReadOnly: true));

        Assert.That(volume.Volume.IsReadOnly, Is.True);
        Assert.That(Error(() => volume.CreateDirectoryAsync("/new").AsTask()), Is.EqualTo(FileSystemError.ReadOnly));
    }

    [Test]
    public async Task UnmountLeavesNoRecoveryTest()
    {
        var image = Ext4Images.Create(variant, out var source);
        Ext4Images.Delete(source);
        await using (var disk = new MemoryBlockStorage(image, 512))
        {
            var volume = await Ext4FileSystem.MountAsync(disk);
            Assert.That((image[1024 + 0x60] & 0x4) != 0, Is.True, "needs_recovery while mounted");
            await volume.DisposeAsync();
        }

        Assert.That(image[1024 + 0x60] & 0x4, Is.Zero, "needs_recovery after the unmount");
        Assert.That(Ext4Tools.Check(image).Exit, Is.Zero);
    }

    #endregion

    #region Tools

    private static FileSystemError Error(Func<Task> operation) =>
        Assert.ThrowsAsync<FileSystemException>(async () => await operation())!.Error;

    private static byte[] Pattern(int length, byte seed) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed + i / 4096))];

    #endregion
}
