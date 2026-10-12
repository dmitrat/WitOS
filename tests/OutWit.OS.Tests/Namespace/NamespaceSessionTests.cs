using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Namespace.Server;
using OutWit.OS.Namespaces;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.Namespace;

/// <summary>
/// A namespace session over two FAT volumes in memory (plan step D5.1): each request answered as POSIX answers it, the
/// errors as their errno values, and a listing in pages of the window.
/// </summary>
[TestFixture]
public sealed class NamespaceSessionTests
{
    #region Tests

    [Test]
    public async Task MountsNameThePointsTest()
    {
        var (session, window) = await CreateAsync();

        var reply = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.MOUNTS));

        Assert.That(reply.Error, Is.Zero);
        Assert.That(Encoding.UTF8.GetString(window.Read(0, (int)reply.Length)), Is.EqualTo("/fat\0/other\0"));
    }

    [Test]
    public async Task FilesAreCreatedWrittenAndReadTest()
    {
        var (session, window) = await CreateAsync();
        var bytes = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();

        var opened = await OpenAsync(session, window, "/fat/dir/file.bin",
            NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE | NamespaceMessages.EXCLUSIVE);
        Assert.That(opened.Error, Is.EqualTo(Errno.NOT_FOUND), "the directory is missing");
        await Path(session, window, NamespaceMessages.MKDIR, "/fat/dir");
        opened = await OpenAsync(session, window, "/fat/dir/file.bin", NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE | NamespaceMessages.EXCLUSIVE);
        window.Write(0, bytes);
        var written = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.WRITE, Handle: opened.Handle, Offset: 100, Length: bytes.Length));
        await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: opened.Handle));
        var reading = await OpenAsync(session, window, "/fat/dir/file.bin", NamespaceMessages.READ_ACCESS);
        var read = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.READ, Handle: reading.Handle, Offset: 100, Length: 5000));
        var data = window.Read(0, 3000);
        var stat = await Path(session, window, NamespaceMessages.STAT, "/fat/dir/file.bin");

        Assert.That(opened.Error, Is.Zero);
        Assert.That(opened.Stat!.Kind, Is.EqualTo(NamespaceMessages.KIND_FILE));
        Assert.That(written.Stat!.Size, Is.EqualTo(3100));
        Assert.That(read.Length, Is.EqualTo(3000));
        Assert.That(data, Is.EqualTo(bytes));
        Assert.That(stat.Stat!.Size, Is.EqualTo(3100));
        Assert.That(stat.Stat.Mode, Is.EqualTo(0x1A4)); // 0644: FAT records no mode
        Assert.That((await OpenAsync(session, window, "/fat/dir/file.bin", NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE |
            NamespaceMessages.EXCLUSIVE)).Error, Is.EqualTo(Errno.EXISTS));
    }

    [Test]
    public async Task ListingComesInPagesTest()
    {
        var (session, window) = await CreateAsync(windowBytes: 96);
        for (var i = 0; i < 9; i++)
        {
            var created = await OpenAsync(session, window, $"/fat/entry-{i}.txt", NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
            await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: created.Handle));
        }
        var directory = await OpenAsync(session, window, "/fat", NamespaceMessages.READ_ACCESS | NamespaceMessages.DIRECTORY);

        var names = new System.Collections.Generic.List<string>();
        var next = 0L;
        var pages = 0;
        do
        {
            var page = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.LIST, Handle: directory.Handle, Offset: next));
            Assert.That(page.Error, Is.Zero);
            var bytes = window.Read(0, (int)page.Length);
            for (var at = 0; at < bytes.Length;)
            {
                var length = BitConverter.ToUInt16(bytes, at + 12);
                names.Add(Encoding.UTF8.GetString(bytes, at + NamespaceMessages.ENTRY_SIZE, BitConverter.ToUInt16(bytes, at + 14)));
                at += length;
            }
            next = page.Offset;
            pages++;
        }
        while (next != 0);

        Assert.That(directory.Stat!.Kind, Is.EqualTo(NamespaceMessages.KIND_DIRECTORY));
        Assert.That(names, Is.EquivalentTo(Enumerable.Range(0, 9).Select(i => $"entry-{i}.txt")));
        Assert.That(pages, Is.GreaterThan(1));
    }

    [Test]
    public async Task ErrorsAreThePosixOnesTest()
    {
        var (session, window) = await CreateAsync();
        await Path(session, window, NamespaceMessages.MKDIR, "/fat/full");
        var file = await OpenAsync(session, window, "/fat/full/x", NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
        await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: file.Handle));
        var other = await OpenAsync(session, window, "/other/y", NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
        await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: other.Handle));

        Assert.That((await Path(session, window, NamespaceMessages.MKDIR, "/fat/full")).Error, Is.EqualTo(Errno.EXISTS));
        Assert.That((await Path(session, window, NamespaceMessages.MKDIR, "/fat/missing/deeper")).Error, Is.EqualTo(Errno.NOT_FOUND));
        Assert.That((await Path(session, window, NamespaceMessages.REMOVE, "/fat/full")).Error, Is.EqualTo(Errno.IS_DIRECTORY));
        Assert.That((await Path(session, window, NamespaceMessages.REMOVE, "/fat/full", NamespaceMessages.DIRECTORY)).Error, Is.EqualTo(Errno.NOT_EMPTY));
        Assert.That((await Path(session, window, NamespaceMessages.REMOVE, "/fat/full/x", NamespaceMessages.DIRECTORY)).Error, Is.EqualTo(Errno.NOT_DIRECTORY));
        Assert.That((await Path(session, window, NamespaceMessages.STAT, "/nowhere/z")).Error, Is.EqualTo(Errno.NOT_FOUND));
        Assert.That((await Path(session, window, NamespaceMessages.READLINK, "/fat/full/x")).Error, Is.EqualTo(Errno.INVALID));
        Assert.That((await Rename(session, window, "/fat/full/x", "/other/x")).Error, Is.EqualTo(Errno.CROSS_DEVICE));
        Assert.That((await OpenAsync(session, window, "/fat/full", NamespaceMessages.WRITE_ACCESS)).Error, Is.EqualTo(Errno.IS_DIRECTORY));
        Assert.That((await session.HandleAsync(new NamespaceRequest(NamespaceMessages.READ, Handle: 99, Length: 8))).Error, Is.EqualTo(Errno.BAD_HANDLE));
        Assert.That((await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: 99))).Error, Is.EqualTo(Errno.BAD_HANDLE));
        Assert.That((await session.HandleAsync(new NamespaceRequest(99))).Error, Is.EqualTo(Errno.NOT_IMPLEMENTED));
    }

    [Test]
    public async Task RenameReplacesAFileTest()
    {
        var (session, window) = await CreateAsync();
        foreach (var (name, text) in new[] { ("/fat/a", "first"), ("/fat/b", "second") })
        {
            var file = await OpenAsync(session, window, name, NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
            window.Write(0, Encoding.UTF8.GetBytes(text));
            await session.HandleAsync(new NamespaceRequest(NamespaceMessages.WRITE, Handle: file.Handle, Length: text.Length));
            await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: file.Handle));
        }

        var renamed = await Rename(session, window, "/fat/a", "/fat/b");
        var stat = await Path(session, window, NamespaceMessages.STAT, "/fat/b");

        Assert.That(renamed.Error, Is.Zero);
        Assert.That(stat.Stat!.Size, Is.EqualTo("first".Length));
        Assert.That((await Path(session, window, NamespaceMessages.STAT, "/fat/a")).Error, Is.EqualTo(Errno.NOT_FOUND));
    }

    [Test]
    public async Task TruncateAndCloseOnDisposeTest()
    {
        var (session, window) = await CreateAsync();
        var file = await OpenAsync(session, window, "/fat/t", NamespaceMessages.READ_ACCESS | NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
        window.Write(0, new byte[1000]);
        await session.HandleAsync(new NamespaceRequest(NamespaceMessages.WRITE, Handle: file.Handle, Length: 1000));

        var cut = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.TRUNCATE, Handle: file.Handle, Length: 10));
        var synced = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.SYNC, Handle: file.Handle));
        await session.DisposeAsync();

        Assert.That(cut.Stat!.Size, Is.EqualTo(10));
        Assert.That(synced.Error, Is.Zero);
    }

    #endregion

    #region Tools

    private static async Task<(NamespaceSession Session, ByteWindow Window)> CreateAsync(int windowBytes = 8192)
    {
        var mounts = new MountTable();
        mounts.Add("/fat", await FatFileSystem.FormatAsync(new MemoryBlockStorage(4 * 1024 * 1024)));
        mounts.Add("/other", await FatFileSystem.FormatAsync(new MemoryBlockStorage(4 * 1024 * 1024)));
        var window = new ByteWindow(windowBytes);
        return (new NamespaceSession(mounts, window), window);
    }

    private static ValueTask<NamespaceReply> OpenAsync(NamespaceSession session, ByteWindow window, string path, uint flags) =>
        Path(session, window, NamespaceMessages.OPEN, path, flags);

    private static ValueTask<NamespaceReply> Path(NamespaceSession session, ByteWindow window, uint operation, string path, uint flags = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        window.Write(0, bytes);
        return session.HandleAsync(new NamespaceRequest(operation, flags, Length: bytes.Length));
    }

    private static ValueTask<NamespaceReply> Rename(NamespaceSession session, ByteWindow window, string from, string to)
    {
        var bytes = Encoding.UTF8.GetBytes(from + "\0" + to + "\0");
        window.Write(0, bytes);
        return session.HandleAsync(new NamespaceRequest(NamespaceMessages.RENAME, Length: bytes.Length));
    }

    #endregion

    #region Nested Types

    // The window as an array of the test's.
    private sealed class ByteWindow(int bytes) : INamespaceWindow
    {
        private readonly byte[] m_bytes = new byte[bytes];

        public int Length => m_bytes.Length;

        public byte[] Read(int offset, int count) => m_bytes.AsSpan(offset, count).ToArray();

        public void Write(int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(m_bytes.AsSpan(offset));
    }

    #endregion
}
