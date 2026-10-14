using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Memory;

namespace OutWit.OS.Tests.FileSystems;

/// <summary>
/// The volume in memory (plan step N4.1): the contract's operations with the errors every provider gives, modes kept
/// as given, a capacity past which a write is NoSpace, and a file's bytes living while a handle holds them.
/// </summary>
[TestFixture]
public sealed class MemoryFileSystemTests
{
    #region Tests

    [Test]
    public async Task FilesAndDirectoriesFollowTheContractTest()
    {
        await using var volume = new MemoryFileSystem(1 << 20, "tmp");
        var bytes = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 13)).ToArray();

        Assert.That(volume.Volume.Format, Is.EqualTo("tmpfs"));
        Assert.That(volume.Volume.IsReadOnly, Is.False);
        Assert.That(await volume.GetEntryAsync("/"), Is.Not.Null);
        Assert.That((await volume.GetEntryAsync("/"))!.IsDirectory, Is.True);
        Assert.That(await volume.GetEntryAsync("/missing"), Is.Null);
        var made = await volume.CreateDirectoryAsync("/a/b");
        Assert.That(made.Path, Is.EqualTo("/a/b"));
        Assert.That(made.Unix!.Mode, Is.EqualTo(MemoryFileSystem.DIRECTORY_MODE));
        await using (var file = await volume.OpenAsync("/a/b/data.bin", FileMode.CreateNew, FileAccess.ReadWrite))
        {
            await file.WriteAsync(5_000, bytes);
            Assert.That(file.Length, Is.EqualTo(75_000));
            var head = new byte[5_000];
            Assert.That(await file.ReadAsync(0, head), Is.EqualTo(5_000));
            Assert.That(head.All(value => value == 0), Is.True, "the gap reads as zeros");
            var tail = new byte[100_000];
            Assert.That(await file.ReadAsync(5_000, tail), Is.EqualTo(70_000));
            Assert.That(tail.AsSpan(0, 70_000).ToArray(), Is.EqualTo(bytes));
            await file.SetLengthAsync(6_000);
            Assert.That(file.Length, Is.EqualTo(6_000));
            await file.SetLengthAsync(8_000);
            var grown = new byte[2_000];
            Assert.That(await file.ReadAsync(6_000, grown), Is.EqualTo(2_000));
            Assert.That(grown.All(value => value == 0), Is.True, "a cut and grown file reads zeros past the cut");
        }
        var entry = await volume.GetEntryAsync("/a/b/data.bin");
        Assert.That(entry!.Length, Is.EqualTo(8_000));
        Assert.That(entry.Kind, Is.EqualTo(FileSystemEntryKind.File));
        Assert.That(entry.Unix!.Mode, Is.EqualTo(MemoryFileSystem.FILE_MODE));
        Assert.That((await volume.EnumerateAsync("/a")).Select(child => child.Name), Is.EqualTo(new[] { "b" }));
        Assert.That(Error(() => volume.OpenAsync("/a/b/data.bin", FileMode.CreateNew, FileAccess.Write).AsTask()), Is.EqualTo(FileSystemError.AlreadyExists));
        Assert.That(Error(() => volume.OpenAsync("/a/b/none", FileMode.Open, FileAccess.Read).AsTask()), Is.EqualTo(FileSystemError.NotFound));
        Assert.That(Error(() => volume.OpenAsync("/a/b", FileMode.Open, FileAccess.Read).AsTask()), Is.EqualTo(FileSystemError.NotAFile));
        Assert.That(Error(() => volume.OpenAsync("/none/file", FileMode.Create, FileAccess.Write).AsTask()), Is.EqualTo(FileSystemError.NotFound));
        Assert.That(Error(() => volume.EnumerateAsync("/a/b/data.bin").AsTask()), Is.EqualTo(FileSystemError.NotADirectory));
        Assert.That(Error(() => volume.DeleteAsync("/a").AsTask()), Is.EqualTo(FileSystemError.NotEmpty));
        Assert.That(Error(() => volume.ReadLinkAsync("/a").AsTask()), Is.EqualTo(FileSystemError.NotALink));
        Assert.That(Error(() => volume.RenameAsync("/a", "/a/b/inside").AsTask()), Is.EqualTo(FileSystemError.InvalidName));
        var moved = await volume.RenameAsync("/a/b/data.bin", "/a/moved.bin");
        Assert.That(moved.Path, Is.EqualTo("/a/moved.bin"));
        Assert.That(await volume.GetEntryAsync("/a/b/data.bin"), Is.Null);
        await using (var file = await volume.OpenAsync("/a/moved.bin", FileMode.Truncate, FileAccess.Write))
            Assert.That(file.Length, Is.Zero);
        await volume.DeleteAsync("/a/moved.bin");
        await volume.DeleteAsync("/a/b");
        await volume.DeleteAsync("/a");
        Assert.That(await volume.EnumerateAsync("/"), Is.Empty);
        var report = await volume.CheckAsync();
        Assert.That(report.IsClean, Is.True);
    }

    [Test]
    public async Task ModesAreKeptAsGivenTest()
    {
        await using var volume = new MemoryFileSystem(1 << 20);
        await using (await volume.OpenAsync("/f", FileMode.CreateNew, FileAccess.Write))
        {
        }

        var changed = await volume.SetModeAsync("/f", 0x1C0 | 0xFFF000); // 0700 with bits above the mode
        var directory = await volume.SetModeAsync("/", 0x1FF); // 0777

        Assert.That(changed.Unix!.Mode, Is.EqualTo(0x1C0));
        Assert.That((await volume.GetEntryAsync("/f"))!.Unix!.Mode, Is.EqualTo(0x1C0));
        Assert.That(directory.Unix!.Mode, Is.EqualTo(0x1FF));
        Assert.That(Error(() => volume.SetModeAsync("/none", 0x1C0).AsTask()), Is.EqualTo(FileSystemError.NotFound));
    }

    [Test]
    public async Task TheCapacityBoundsTheFilesTest()
    {
        await using var volume = new MemoryFileSystem(10_000);
        await using var first = await volume.OpenAsync("/first", FileMode.CreateNew, FileAccess.Write);
        await using var second = await volume.OpenAsync("/second", FileMode.CreateNew, FileAccess.Write);

        await first.WriteAsync(0, new byte[6_000]);
        Assert.That(Error(() => second.WriteAsync(0, new byte[5_000]).AsTask()), Is.EqualTo(FileSystemError.NoSpace));
        await second.WriteAsync(0, new byte[4_000]);
        Assert.That(Error(() => first.SetLengthAsync(6_001).AsTask()), Is.EqualTo(FileSystemError.NoSpace));
        await first.SetLengthAsync(1_000);
        await second.WriteAsync(4_000, new byte[5_000]);
        Assert.That(second.Length, Is.EqualTo(9_000));
        Assert.That((await volume.CheckAsync()).IsClean, Is.True);
    }

    [Test]
    public async Task AnOpenFileOutlivesItsNameTest()
    {
        await using var volume = new MemoryFileSystem(1 << 20);
        var text = Encoding.UTF8.GetBytes("kept while open");
        await using var reader = await volume.OpenAsync("/gone.txt", FileMode.CreateNew, FileAccess.ReadWrite);
        await reader.WriteAsync(0, text);

        await volume.DeleteAsync("/gone.txt");
        var back = new byte[text.Length];

        Assert.That(await volume.GetEntryAsync("/gone.txt"), Is.Null);
        Assert.That(await reader.ReadAsync(0, back), Is.EqualTo(text.Length));
        Assert.That(back, Is.EqualTo(text));
        Assert.That(Error(() => reader.WriteAsync(0, text).AsTask()), Is.Null, "the handle still writes");
    }

    #endregion

    #region Tools

    private static FileSystemError? Error(Func<Task> operation)
    {
        try
        {
            operation().GetAwaiter().GetResult();
            return null;
        }
        catch (FileSystemException exception)
        {
            return exception.Error;
        }
    }

    #endregion
}
