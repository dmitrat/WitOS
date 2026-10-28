using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Smb;

namespace OutWit.OS.Tests.FileSystems.Smb;

/// <summary>
/// The SMB client against Samba (plan step E2): SMB 3.1.1 with mandatory signing and NTLMv2, the filesystem contract
/// over a share — lookups, listings, files read and written across pieces and credits, directories, renames, deletions,
/// the read-only attribute, times, space — each failure the contract's error, what the client wrote seen in the host's
/// directory and what the host wrote seen by the client; then SMB 3.0's key derivation, a wrong password and a share
/// that is not there. A host without Samba skips them; CI's Linux host runs them.
/// </summary>
[TestFixture]
public sealed class SmbFileSystemTests
{
    #region Tests

    [Test]
    public async Task MountsTheShareTest()
    {
        await using var server = await SambaServer.StartAsync();
        await using var volume = await server.MountAsync();

        Assert.That(volume.Dialect, Is.EqualTo("3.1.1"));
        Assert.That(volume.Volume.Format, Is.EqualTo(SmbFileSystem.FORMAT));
        Assert.That(volume.Volume.Label, Is.EqualTo(SambaServer.SHARE));
        Assert.That(volume.Volume.IsReadOnly, Is.False);
        Assert.That(volume.Volume.Identity, Does.Match("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"));
        var space = await volume.GetSpaceAsync();
        Assert.That(space.TotalBytes, Is.GreaterThan(0));
        Assert.That(space.FreeBytes, Is.GreaterThan(0).And.LessThanOrEqualTo(space.TotalBytes));

        var root = await volume.GetEntryAsync("/");
        Assert.That(root, Is.Not.Null);
        Assert.That(root!.IsDirectory, Is.True);
        Assert.That(await volume.EnumerateAsync("/"), Is.Empty);

        // A second mount of the same share has the same identity.
        await using var again = await server.MountAsync();
        Assert.That(again.Volume.Identity, Is.EqualTo(volume.Volume.Identity));
    }

    [Test]
    public async Task ReadsWhatTheHostWroteTest()
    {
        await using var server = await SambaServer.StartAsync();
        Directory.CreateDirectory(server.HostPath("/from-host"));
        await File.WriteAllTextAsync(server.HostPath("/from-host/hello.txt"), "hello from the host");
        await using var volume = await server.MountAsync();

        var listed = await volume.EnumerateAsync("/from-host");
        Assert.That(listed.Select(entry => (entry.Path, entry.Name, entry.Kind, entry.Length)),
            Is.EqualTo(new[] { ("/from-host/hello.txt", "hello.txt", FileSystemEntryKind.File, 19L) }));
        Assert.That(listed[0].Modified, Is.EqualTo(new DateTimeOffset(File.GetLastWriteTimeUtc(server.HostPath("/from-host/hello.txt")))).Within(TimeSpan.FromSeconds(2)));
        await using var file = await volume.OpenAsync("/from-host/hello.txt", FileMode.Open, FileAccess.Read);
        Assert.That(file.Length, Is.EqualTo(19));
        var buffer = new byte[64];
        Assert.That(await file.ReadAsync(0, buffer), Is.EqualTo(19));
        Assert.That(Encoding.UTF8.GetString(buffer, 0, 19), Is.EqualTo("hello from the host"));
        Assert.That(await file.ReadAsync(6, buffer.AsMemory(0, 4)), Is.EqualTo(4));
        Assert.That(Encoding.UTF8.GetString(buffer, 0, 4), Is.EqualTo("from"));
        Assert.That(await file.ReadAsync(19, buffer), Is.Zero, "at the end of the file");
    }

    // Three MiB and a bit cross the server's largest read and write and the credits of a MiB.
    [Test]
    public async Task WritesAndReadsLargeFilesTest()
    {
        await using var server = await SambaServer.StartAsync();
        await using var volume = await server.MountAsync();
        var data = new byte[3 * 1024 * 1024 + 12345];
        new Random(7).NextBytes(data);

        await using (var file = await volume.OpenAsync("/big.bin", FileMode.CreateNew, FileAccess.Write))
        {
            await file.WriteAsync(0, data);
            Assert.That(file.Length, Is.EqualTo(data.Length));
            await file.FlushAsync();
        }
        Assert.That(await File.ReadAllBytesAsync(server.HostPath("/big.bin")), Is.EqualTo(data), "the host's copy");

        await using (var file = await volume.OpenAsync("/big.bin", FileMode.Open, FileAccess.ReadWrite))
        {
            var back = new byte[data.Length + 100];
            Assert.That(await file.ReadAsync(0, back), Is.EqualTo(data.Length));
            Assert.That(back.AsSpan(0, data.Length).SequenceEqual(data), Is.True);

            // A write past the end leaves zeros; a cut shortens.
            await file.WriteAsync(data.Length + 1000, new byte[] { 9 });
            Assert.That(file.Length, Is.EqualTo(data.Length + 1001));
            var gap = new byte[1001];
            Assert.That(await file.ReadAsync(data.Length, gap), Is.EqualTo(1001));
            Assert.That(gap.Take(1000).All(value => value == 0) && gap[1000] == 9, Is.True);
            await file.SetLengthAsync(100);
            Assert.That(file.Length, Is.EqualTo(100));
        }
        Assert.That(new FileInfo(server.HostPath("/big.bin")).Length, Is.EqualTo(100));
    }

    [Test]
    public async Task OpensByModeTest()
    {
        await using var server = await SambaServer.StartAsync();
        await using var volume = await server.MountAsync();

        await using (var file = await volume.OpenAsync("/a.txt", FileMode.CreateNew, FileAccess.Write))
            await file.WriteAsync(0, "first"u8.ToArray());
        Assert.That(ErrorOf(() => volume.OpenAsync("/a.txt", FileMode.CreateNew, FileAccess.Write).AsTask()), Is.EqualTo(FileSystemError.AlreadyExists));
        Assert.That(ErrorOf(() => volume.OpenAsync("/missing.txt", FileMode.Open, FileAccess.Read).AsTask()), Is.EqualTo(FileSystemError.NotFound));
        Assert.That(ErrorOf(() => volume.OpenAsync("/no/such/dir.txt", FileMode.Create, FileAccess.Write).AsTask()), Is.EqualTo(FileSystemError.NotFound));

        await using (var file = await volume.OpenAsync("/a.txt", FileMode.Create, FileAccess.Write))
            Assert.That(file.Length, Is.Zero, "Create cuts what was there");
        await using (var file = await volume.OpenAsync("/b.txt", FileMode.OpenOrCreate, FileAccess.ReadWrite))
        {
            await file.WriteAsync(0, "bee"u8.ToArray());
            Assert.That(file.CanRead && file.CanWrite, Is.True);
        }
        await using (var file = await volume.OpenAsync("/b.txt", FileMode.Truncate, FileAccess.Write))
            Assert.That(file.Length, Is.Zero);

        await volume.CreateDirectoryAsync("/dir");
        Assert.That(ErrorOf(() => volume.OpenAsync("/dir", FileMode.Open, FileAccess.Read).AsTask()), Is.EqualTo(FileSystemError.NotAFile));
        Assert.That(ErrorOf(() => volume.EnumerateAsync("/b.txt").AsTask()), Is.EqualTo(FileSystemError.NotADirectory));
        Assert.That(ErrorOf(() => volume.OpenAsync("/back\\slash", FileMode.Create, FileAccess.Write).AsTask()), Is.EqualTo(FileSystemError.InvalidName));
    }

    [Test]
    public async Task MakesMovesAndDeletesTest()
    {
        await using var server = await SambaServer.StartAsync();
        await using var volume = await server.MountAsync();

        var made = await volume.CreateDirectoryAsync("/one/two/three");
        Assert.That((made.Path, made.Name, made.IsDirectory), Is.EqualTo(("/one/two/three", "three", true)));
        Assert.That(Directory.Exists(server.HostPath("/one/two/three")), Is.True);
        Assert.That((await volume.CreateDirectoryAsync("/one/two")).IsDirectory, Is.True, "a directory already there");
        await using (var file = await volume.OpenAsync("/one/two/file.txt", FileMode.CreateNew, FileAccess.Write))
            await file.WriteAsync(0, "moved"u8.ToArray());

        var moved = await volume.RenameAsync("/one/two/file.txt", "/one/moved.txt");
        Assert.That((moved.Path, moved.Length), Is.EqualTo(("/one/moved.txt", 5L)));
        Assert.That(await volume.GetEntryAsync("/one/two/file.txt"), Is.Null);
        Assert.That(File.ReadAllText(server.HostPath("/one/moved.txt")), Is.EqualTo("moved"));
        await using (var other = await volume.OpenAsync("/one/other.txt", FileMode.CreateNew, FileAccess.Write))
            await other.WriteAsync(0, "x"u8.ToArray());
        Assert.That(ErrorOf(() => volume.RenameAsync("/one/other.txt", "/one/moved.txt").AsTask()), Is.EqualTo(FileSystemError.AlreadyExists),
            "nothing may be at the destination");
        Assert.That((await volume.RenameAsync("/one/two", "/two")).IsDirectory, Is.True, "a directory moves with what it holds");
        Assert.That(await volume.GetEntryAsync("/two/three"), Is.Not.Null);

        Assert.That(ErrorOf(() => volume.DeleteAsync("/two").AsTask()), Is.EqualTo(FileSystemError.NotEmpty));
        await volume.DeleteAsync("/two/three");
        await volume.DeleteAsync("/two");
        await volume.DeleteAsync("/one/moved.txt");
        Assert.That(ErrorOf(() => volume.DeleteAsync("/one/moved.txt").AsTask()), Is.EqualTo(FileSystemError.NotFound));
        Assert.That((await volume.EnumerateAsync("/one")).Select(entry => entry.Name), Is.EqualTo(new[] { "other.txt" }));
        Assert.That(File.Exists(server.HostPath("/one/moved.txt")), Is.False);
    }

    [Test]
    public async Task KeepsReadOnlyAndTimesTest()
    {
        await using var server = await SambaServer.StartAsync();
        await using var volume = await server.MountAsync();
        await using (var file = await volume.OpenAsync("/kept.txt", FileMode.CreateNew, FileAccess.Write))
            await file.WriteAsync(0, "kept"u8.ToArray());

        var modified = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var entry = await volume.SetTimesAsync("/kept.txt", null, modified);
        Assert.That(entry.Modified, Is.EqualTo(modified));
        Assert.That(File.GetLastWriteTimeUtc(server.HostPath("/kept.txt")), Is.EqualTo(modified.UtcDateTime));

        entry = await volume.SetModeAsync("/kept.txt", 0x124); // 0444
        Assert.That(entry.Attributes.HasFlag(FileSystemAttributes.ReadOnly), Is.True);
        Assert.That(ErrorOf(() => volume.OpenAsync("/kept.txt", FileMode.Open, FileAccess.Write).AsTask()), Is.EqualTo(FileSystemError.AccessDenied));
        entry = await volume.SetModeAsync("/kept.txt", 0x1A4); // 0644
        Assert.That(entry.Attributes.HasFlag(FileSystemAttributes.ReadOnly), Is.False);
        Assert.That(ErrorOf(() => volume.SetModeAsync("/kept.txt", 0x1C0).AsTask()), Is.EqualTo(FileSystemError.NotPermitted), "0700 says more than read-only");
        Assert.That(ErrorOf(() => volume.ReadLinkAsync("/kept.txt").AsTask()), Is.EqualTo(FileSystemError.NotALink));
    }

    [Test]
    public async Task SpaceFollowsWritesTest()
    {
        await using var server = await SambaServer.StartAsync();
        await using var volume = await server.MountAsync();
        var before = await volume.GetSpaceAsync();
        await using (var file = await volume.OpenAsync("/space.bin", FileMode.CreateNew, FileAccess.Write))
        {
            await file.WriteAsync(0, new byte[4 << 20]);
            await file.FlushAsync();
        }
        var after = await volume.GetSpaceAsync();
        // The host's disk is shared with everything else it runs; the drop is at least most of what was written.
        Assert.That(before.FreeBytes - after.FreeBytes, Is.GreaterThan(2 << 20));
    }

    [Test]
    public async Task ReadOnlyMountRefusesChangesTest()
    {
        await using var server = await SambaServer.StartAsync();
        await File.WriteAllTextAsync(server.HostPath("/r.txt"), "r");
        await using var volume = await server.MountAsync(readOnly: true);
        Assert.That(volume.Volume.IsReadOnly, Is.True);
        Assert.That(ErrorOf(() => volume.OpenAsync("/r.txt", FileMode.Open, FileAccess.Write).AsTask()), Is.EqualTo(FileSystemError.ReadOnly));
        Assert.That(ErrorOf(() => volume.CreateDirectoryAsync("/d").AsTask()), Is.EqualTo(FileSystemError.ReadOnly));
        Assert.That(ErrorOf(() => volume.DeleteAsync("/r.txt").AsTask()), Is.EqualTo(FileSystemError.ReadOnly));
        await using var file = await volume.OpenAsync("/r.txt", FileMode.Open, FileAccess.Read);
        Assert.That(file.Length, Is.EqualTo(1));
    }

    // SMB 3.0 and 3.0.2 sign with the key their own label and context derive, without the integrity hash.
    [TestCase("SMB3_00", "3.0")]
    [TestCase("SMB3_02", "3.0.2")]
    public async Task SpeaksEarlierDialectsTest(string maxProtocol, string dialect)
    {
        await using var server = await SambaServer.StartAsync(maxProtocol);
        await using var volume = await server.MountAsync();
        Assert.That(volume.Dialect, Is.EqualTo(dialect));
        await using (var file = await volume.OpenAsync("/d.txt", FileMode.CreateNew, FileAccess.Write))
            await file.WriteAsync(0, "signed"u8.ToArray());
        Assert.That(File.ReadAllText(server.HostPath("/d.txt")), Is.EqualTo("signed"));
    }

    [Test]
    public async Task RefusesAWrongAccountAndAMissingShareTest()
    {
        await using var server = await SambaServer.StartAsync();
        var wrong = Assert.ThrowsAsync<FileSystemException>(async () =>
            await SmbFileSystem.MountAsync(await server.ConnectAsync(), "127.0.0.1", SambaServer.SHARE, server.Credentials with { Password = "wrong" }));
        Assert.That(wrong!.Error, Is.EqualTo(FileSystemError.AccessDenied));
        Assert.That(wrong.Message, Does.Contain("STATUS_LOGON_FAILURE"));
        var missing = Assert.ThrowsAsync<FileSystemException>(async () =>
            await SmbFileSystem.MountAsync(await server.ConnectAsync(), "127.0.0.1", "nothing", server.Credentials));
        Assert.That(missing!.Error, Is.EqualTo(FileSystemError.NotFound));
        Assert.That(server.Credentials.ToString(), Does.Not.Contain(SambaServer.PASSWORD));
    }

    #endregion

    #region Functions

    private static FileSystemError? ErrorOf(Func<Task> action)
    {
        try
        {
            action().GetAwaiter().GetResult();
            return null;
        }
        catch (FileSystemException exception)
        {
            return exception.Error;
        }
    }

    #endregion
}
