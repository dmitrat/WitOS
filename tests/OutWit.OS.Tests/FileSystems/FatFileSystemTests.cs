using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Storage;
using OutWit.OS.Tests.Storage;

namespace OutWit.OS.Tests.FileSystems;

/// <summary>
/// The FAT provider through OutWit.Common.Fat (plan step D4.1) on every backend the storage document's substitution test
/// names (§71): memory, a partition of a disk, and the block protocol over a driver in the test's process, whose
/// transfers are smaller than the library's requests.
/// </summary>
[TestFixture("Memory")]
[TestFixture("Partition")]
[TestFixture("Channel")]
public sealed class FatFileSystemTests(string backend)
{
    #region Constants

    private const int BLOCK = 512;
    private const long BYTES = 8 * 1024 * 1024;

    #endregion

    #region Tests

    [Test]
    public async Task FormatDescribesTheVolumeTest()
    {
        await using var storage = Create();
        await using var fileSystem = await FatFileSystem.FormatAsync(storage, "WITOS");

        Assert.That(fileSystem.Volume.Format, Is.EqualTo("FAT12")); // the library's choice for 8 MiB
        Assert.That(fileSystem.Volume.Label, Is.EqualTo("WITOS"));
        Assert.That(fileSystem.Volume.Length, Is.LessThanOrEqualTo(storage.Info.Length));
        Assert.That(fileSystem.Volume.AllocationUnit % BLOCK, Is.Zero);
        Assert.That(fileSystem.Volume.IsReadOnly, Is.False);
        Assert.That(fileSystem.Volume.Identity, Does.Match("^[0-9A-F]{4}-[0-9A-F]{4}$"), "the serial number as Windows prints it (E1)");
        Assert.That(await fileSystem.EnumerateAsync("/"), Is.Empty);
        Assert.That(await FatFileSystem.DetectAsync(storage), Is.True);
    }

    // A format asked by its name (E1: a drive as Windows formats it), and a name that is no FAT format refused.
    [Test]
    public async Task FormatTakesTheFormatAskedTest()
    {
        await using var storage = new MemoryBlockStorage(40L << 20, BLOCK);
        await using (var fileSystem = await FatFileSystem.FormatAsync(storage, "STICK", "FAT32"))
        {
            Assert.That(fileSystem.Volume.Format, Is.EqualTo("FAT32"));
            Assert.That(fileSystem.Volume.Label, Is.EqualTo("STICK"));
        }
        Assert.ThrowsAsync<ArgumentException>(async () => await FatFileSystem.FormatAsync(storage, "STICK", "NTFS"));
    }

    // The volume's space (E1, statfs): the clusters of the data area, and the free ones counted through the table.
    [Test]
    public async Task SpaceFollowsTheFilesTest()
    {
        await using var storage = Create();
        await using var fileSystem = await FatFileSystem.FormatAsync(storage);
        var before = await fileSystem.GetSpaceAsync();
        Assert.That(before.TotalBytes, Is.GreaterThan(0).And.LessThanOrEqualTo(fileSystem.Volume.Length));
        Assert.That(before.FreeBytes, Is.GreaterThan(0).And.LessThanOrEqualTo(before.TotalBytes));
        await using (var file = await fileSystem.OpenAsync("/big.bin", FileMode.CreateNew, FileAccess.Write))
            await file.WriteAsync(0, new byte[200_000]);
        var after = await fileSystem.GetSpaceAsync();
        Assert.That(after.TotalBytes, Is.EqualTo(before.TotalBytes));
        Assert.That(before.FreeBytes - after.FreeBytes, Is.GreaterThanOrEqualTo(200_000).And.LessThan(200_000 + 2L * fileSystem.Volume.AllocationUnit),
            "the file's clusters, and a directory's at most");
        await fileSystem.DeleteAsync("/big.bin");
        Assert.That((await fileSystem.GetSpaceAsync()).FreeBytes, Is.EqualTo(before.FreeBytes));
    }

    [Test]
    public async Task FilesSurviveTheMountTest()
    {
        await using var storage = Create();
        var bytes = Pattern(100_000, 3);
        await using (var fileSystem = await FatFileSystem.FormatAsync(storage))
        {
            await fileSystem.CreateDirectoryAsync("/data/logs");
            await using var file = await fileSystem.OpenAsync("/data/logs/first.bin", FileMode.CreateNew, FileAccess.ReadWrite);
            await file.WriteAsync(0, bytes);
        }

        await using var again = await FatFileSystem.MountAsync(storage);
        var entry = await again.GetEntryAsync("/data/logs/first.bin");
        await using var read = await again.OpenAsync("/data/logs/first.bin", FileMode.Open, FileAccess.Read);
        var back = new byte[bytes.Length];

        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Length, Is.EqualTo(bytes.Length));
        Assert.That(entry.IsDirectory, Is.False);
        Assert.That(entry.Modified, Is.Not.Null);
        Assert.That(entry.Modified!.Value.Offset, Is.EqualTo(TimeSpan.Zero));
        Assert.That(await read.ReadAsync(0, back), Is.EqualTo(bytes.Length));
        Assert.That(back, Is.EqualTo(bytes));
        Assert.That((await again.CheckAsync()).IsClean, Is.True);
    }

    [Test]
    public async Task HandleReadsAndWritesAtOffsetsTest()
    {
        await using var storage = Create();
        await using var fileSystem = await FatFileSystem.FormatAsync(storage);
        await using var file = await fileSystem.OpenAsync("/sparse.bin", FileMode.Create, FileAccess.ReadWrite);

        await file.WriteAsync(10_000, Pattern(10, 7));
        await file.WriteAsync(2, Pattern(3, 9));
        var all = new byte[20_000];
        var read = await file.ReadAsync(0, all);
        var past = await file.ReadAsync(10_010, new byte[16]);

        Assert.That(file.Length, Is.EqualTo(10_010));
        Assert.That(read, Is.EqualTo(10_010));
        Assert.That(past, Is.Zero);
        Assert.That(all[2..5], Is.EqualTo(Pattern(3, 9)));
        Assert.That(all[5..10_000].All(value => value == 0), Is.True);
        Assert.That(all[10_000..10_010], Is.EqualTo(Pattern(10, 7)));

        await file.SetLengthAsync(4);
        Assert.That(file.Length, Is.EqualTo(4));
        Assert.That(await file.ReadAsync(0, new byte[16]), Is.EqualTo(4));
    }

    [Test]
    public async Task DirectoryOperationsTest()
    {
        await using var storage = Create();
        await using var fileSystem = await FatFileSystem.FormatAsync(storage);
        await fileSystem.CreateDirectoryAsync("/a");
        await using (var file = await fileSystem.OpenAsync("/a/Long File Name.txt", FileMode.CreateNew, FileAccess.Write))
            await file.WriteAsync(0, Pattern(5, 1));

        var renamed = await fileSystem.RenameAsync("/a/Long File Name.txt", "/b.txt");
        var root = await fileSystem.EnumerateAsync("/");
        await fileSystem.DeleteAsync("/a");

        Assert.That(renamed.Path, Is.EqualTo("/b.txt"));
        Assert.That(renamed.Length, Is.EqualTo(5));
        Assert.That(root.Select(entry => (entry.Name, entry.IsDirectory)), Is.EquivalentTo(new[] { ("a", true), ("b.txt", false) }));
        Assert.That(await fileSystem.GetEntryAsync("/a"), Is.Null);
        Assert.That(await fileSystem.GetEntryAsync("/a/Long File Name.txt"), Is.Null);
    }

    [Test]
    public async Task FailuresCarryTheirErrorTest()
    {
        await using var storage = Create();
        await using var fileSystem = await FatFileSystem.FormatAsync(storage);
        await fileSystem.CreateDirectoryAsync("/full");
        await using (var file = await fileSystem.OpenAsync("/full/x", FileMode.CreateNew, FileAccess.Write))
            await file.WriteAsync(0, Pattern(1, 1));

        Assert.That(Error(() => fileSystem.OpenAsync("/missing", FileMode.Open, FileAccess.Read).AsTask()), Is.EqualTo(FileSystemError.NotFound));
        Assert.That(Error(() => fileSystem.OpenAsync("/full/x", FileMode.CreateNew, FileAccess.Write).AsTask()), Is.EqualTo(FileSystemError.AlreadyExists));
        Assert.That(Error(() => fileSystem.DeleteAsync("/full").AsTask()), Is.EqualTo(FileSystemError.NotEmpty));
        Assert.That(Error(() => fileSystem.EnumerateAsync("/full/x").AsTask()), Is.EqualTo(FileSystemError.NotADirectory));
        Assert.That(Error(() => fileSystem.GetEntryAsync("/../x").AsTask()), Is.EqualTo(FileSystemError.InvalidName));
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await fileSystem.OpenAsync("/full/x", FileMode.Append, FileAccess.Write));
    }

    [Test]
    public async Task ReadOnlyStorageMountsReadOnlyTest()
    {
        var disk = new MemoryBlockStorage(BYTES, BLOCK);
        await using (var fileSystem = await FatFileSystem.FormatAsync(disk))
        await using (var file = await fileSystem.OpenAsync("/kept.txt", FileMode.CreateNew, FileAccess.Write))
            await file.WriteAsync(0, Pattern(64, 5));
        await using var readOnly = new MemoryBlockStorage(disk.Bytes.ToArray(), BLOCK, isReadOnly: true);

        await using var mounted = await FatFileSystem.MountAsync(readOnly);

        Assert.That(mounted.Volume.IsReadOnly, Is.True);
        Assert.That((await mounted.GetEntryAsync("/kept.txt"))!.Length, Is.EqualTo(64));
        Assert.That(Error(() => mounted.CreateDirectoryAsync("/new").AsTask()), Is.EqualTo(FileSystemError.ReadOnly));
        Assert.That(Error(() => mounted.OpenAsync("/kept.txt", FileMode.Open, FileAccess.Write).AsTask()), Is.EqualTo(FileSystemError.ReadOnly));
    }

    [Test]
    public async Task StorageWithoutVolumeIsRefusedTest()
    {
        await using var storage = Create();

        Assert.That(await FatFileSystem.DetectAsync(storage), Is.False);
        Assert.That(Error(() => FatFileSystem.MountAsync(storage).AsTask()), Is.EqualTo(FileSystemError.Unsupported));
    }

    #endregion

    #region Tools

    private IBlockStorage Create() => backend switch
    {
        "Memory" => new MemoryBlockStorage(BYTES, BLOCK),
        "Partition" => new PartitionBlockStorage(new MemoryBlockStorage(BYTES + 2 * 1024 * 1024, BLOCK), 1024 * 1024, BYTES),
        "Channel" => ChannelBlockStorage.Open(new LoopbackBlockSession(new MemoryBlockStorage(BYTES, BLOCK), 4096, 2048)),
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };

    private static FileSystemError Error(Func<Task> operation) =>
        Assert.ThrowsAsync<FileSystemException>(async () => await operation())!.Error;

    private static byte[] Pattern(int length, byte seed) =>
        [.. Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed))];

    #endregion
}
