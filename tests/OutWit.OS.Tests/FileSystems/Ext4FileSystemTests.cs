using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Ext4;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.FileSystems;

/// <summary>
/// The ext provider (plan step D4.2) against volumes e2fsprogs made from a known tree (storage document §73): every
/// directory, file, link and mode as the tree has it, a clean check, refused changes and the failures' errors, for each
/// variant mkfs makes.
/// </summary>
[TestFixture("ext4")]
[TestFixture("ext4-1k")]
[TestFixture("ext4-nocsum")]
[TestFixture("ext4-small-inodes")]
[TestFixture("ext3")]
[TestFixture("ext2")]
public sealed class Ext4FileSystemTests(string variant)
{
    #region Tests

    [Test]
    public async Task TreeIsTheSourceTest()
    {
        var image = Ext4Images.Create(variant, out var source);
        try
        {
            await using var disk = new MemoryBlockStorage(image, 512, isReadOnly: true);
            await using var volume = await Ext4FileSystem.MountAsync(disk);

            Assert.That(volume.Volume.Format, Is.EqualTo(variant.StartsWith("ext4", StringComparison.Ordinal) ? "ext4" : variant));
            Assert.That(volume.Volume.Label, Is.EqualTo("WITOS"));
            Assert.That(volume.Volume.IsReadOnly, Is.True);
            await CompareAsync(volume, source, "/");
            var root = await volume.EnumerateAsync("/");
            Assert.That(root.Select(entry => entry.Name), Does.Contain("lost+found"));
            Assert.That((await volume.GetEntryAsync("/run.sh"))!.Unix!.Mode, Is.EqualTo(Convert.ToInt32("755", 8)));
            Assert.That((await volume.GetEntryAsync("/hello.txt"))!.Unix!.Mode, Is.EqualTo(Convert.ToInt32("644", 8)));
            Assert.That(await volume.ReadLinkAsync("/longlink"), Is.EqualTo(Ext4Images.LONG_TARGET));
            Assert.That((await volume.EnumerateAsync("/many")).Count, Is.EqualTo(Ext4Images.MANY));
            // What the tree exercises: /many is an htree, and big.bin's extent tree has an index (ext4) or its block
            // map an indirect block (ext2, ext3).
            var many = Ext4Images.InodeOffset(image, (await volume.GetEntryAsync("/many"))!.Unix!.Id);
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)many + 0x20)) & 0x1000, Is.Not.Zero, "/many is no htree");
            if (variant == "ext4-1k")
            {
                var first = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)many + 0x28 + 12 + 8)); // depth 0: the first extent's start
                Assert.That(image[first * 1024L + 24 + 6], Is.Positive, "/many's htree has no index nodes below its root");
            }
            var big = Ext4Images.InodeOffset(image, (await volume.GetEntryAsync("/big.bin"))!.Unix!.Id);
            if (variant.StartsWith("ext4", StringComparison.Ordinal))
                Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)big + 0x28 + 6)), Is.Positive, "big.bin's extent tree has no index");
            else
                Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)big + 0x28 + 12 * 4)), Is.Not.Zero, "big.bin has no indirect block");
        }
        finally
        {
            Ext4Images.Delete(source);
        }
    }

    [Test]
    public async Task CheckIsCleanTest()
    {
        var image = Ext4Images.Create(variant, out var source);
        try
        {
            await using var disk = new MemoryBlockStorage(image, 512, isReadOnly: true);
            await using var volume = await Ext4FileSystem.MountAsync(disk);

            var report = await volume.CheckAsync();

            Assert.That(report.Problems, Is.Empty);
            Assert.That(report.Files, Is.EqualTo(Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Count(file => new FileInfo(file).LinkTarget is null)));
            Assert.That(report.Directories, Is.EqualTo(Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Count() + 2));
        }
        finally
        {
            Ext4Images.Delete(source);
        }
    }

    [Test]
    public async Task FailuresCarryTheirErrorTest()
    {
        var image = Ext4Images.Create(variant, out var source);
        try
        {
            await using var disk = new MemoryBlockStorage(image, 512, isReadOnly: true);
            await using var volume = await Ext4FileSystem.MountAsync(disk);

            Assert.That(await volume.GetEntryAsync("/missing"), Is.Null);
            Assert.That(Error(() => volume.OpenAsync("/missing", FileMode.Open, FileAccess.Read).AsTask()), Is.EqualTo(FileSystemError.NotFound));
            Assert.That(Error(() => volume.GetEntryAsync("/hello.txt/inside").AsTask()), Is.EqualTo(FileSystemError.NotADirectory));
            Assert.That(Error(() => volume.OpenAsync("/docs", FileMode.Open, FileAccess.Read).AsTask()), Is.EqualTo(FileSystemError.NotAFile));
            Assert.That(Error(() => volume.ReadLinkAsync("/hello.txt").AsTask()), Is.EqualTo(FileSystemError.NotALink));
            Assert.That(Error(() => volume.GetEntryAsync("/../x").AsTask()), Is.EqualTo(FileSystemError.InvalidName));
            Assert.That(Error(() => volume.CreateDirectoryAsync("/new").AsTask()), Is.EqualTo(FileSystemError.ReadOnly));
            Assert.That(Error(() => volume.OpenAsync("/hello.txt", FileMode.Open, FileAccess.ReadWrite).AsTask()), Is.EqualTo(FileSystemError.ReadOnly));
            Assert.That((await volume.GetEntryAsync("/docs/nested/../readme.md"))!.Path, Is.EqualTo("/docs/readme.md"));
        }
        finally
        {
            Ext4Images.Delete(source);
        }
    }

    #endregion

    #region Tools

    // Every entry of a source directory and the volume's, recursively: the same names, kinds, bytes and targets.
    private static async Task CompareAsync(IFileSystem volume, string source, string path)
    {
        var entries = (await volume.EnumerateAsync(path)).Where(entry => entry.Name != "lost+found").ToDictionary(entry => entry.Name);
        var names = Directory.EnumerateFileSystemEntries(source).Select(Path.GetFileName).ToArray();
        Assert.That(entries.Keys, Is.EquivalentTo(names), path);
        foreach (var name in names)
        {
            var local = Path.Combine(source, name!);
            var entry = entries[name!];
            var info = new FileInfo(local);
            if (info.LinkTarget is { } target)
            {
                Assert.That(entry.Kind, Is.EqualTo(FileSystemEntryKind.SymbolicLink), entry.Path);
                Assert.That(await volume.ReadLinkAsync(entry.Path), Is.EqualTo(target), entry.Path);
            }
            else if (Directory.Exists(local))
            {
                Assert.That(entry.Kind, Is.EqualTo(FileSystemEntryKind.Directory), entry.Path);
                await CompareAsync(volume, local, entry.Path);
            }
            else
            {
                Assert.That(entry.Kind, Is.EqualTo(FileSystemEntryKind.File), entry.Path);
                var expected = await File.ReadAllBytesAsync(local);
                await using var file = await volume.OpenAsync(entry.Path, FileMode.Open, FileAccess.Read);
                var bytes = new byte[expected.Length + 16];
                Assert.That(await file.ReadAsync(0, bytes), Is.EqualTo(expected.Length), entry.Path);
                Assert.That(bytes[..expected.Length], Is.EqualTo(expected), entry.Path);
            }
        }
    }

    private static FileSystemError Error(Func<Task> operation) =>
        Assert.ThrowsAsync<FileSystemException>(async () => await operation())!.Error;

    #endregion
}
