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

    [Test]
    public async Task MappedChunksAreWrittenBackTest()
    {
        var chunks = new FakeChunkSource();
        var mounts = new MountTable(chunks);
        mounts.Add("/fat", await FatFileSystem.FormatAsync(new MemoryBlockStorage(4 * 1024 * 1024)));
        var window = new ByteWindow(8192);
        var session = new NamespaceSession(mounts, window);
        var bytes = Enumerable.Range(0, 6000).Select(i => (byte)(i * 3)).ToArray();
        var opened = await OpenAsync(session, window, "/fat/mapped.bin",
            NamespaceMessages.READ_ACCESS | NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
        window.Write(0, bytes);
        await session.HandleAsync(new NamespaceRequest(NamespaceMessages.WRITE, Handle: opened.Handle, Length: bytes.Length));

        var map = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.MAP, NamespaceMessages.MAP_WRITE, opened.Handle));
        var transfer = session.TakeTransfer();
        var chunk = chunks.Created.Single();
        chunk.Bytes[100] = 0xAB; // a mapping writes
        var read = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.READ, Handle: opened.Handle, Offset: 100, Length: 1));
        var seen = window.Read(0, 1)[0];
        window.Write(0, "wxyz"u8);
        await session.HandleAsync(new NamespaceRequest(NamespaceMessages.WRITE, Handle: opened.Handle, Offset: 200, Length: 4));
        var synced = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.SYNC, Handle: opened.Handle));
        var closed = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: opened.Handle));
        var again = await OpenAsync(session, window, "/fat/mapped.bin", NamespaceMessages.READ_ACCESS);
        await session.HandleAsync(new NamespaceRequest(NamespaceMessages.READ, Handle: again.Handle, Length: 6000));
        var file = window.Read(0, 6000);

        Assert.That(map.Error, Is.Zero);
        Assert.That(map.Length, Is.EqualTo(NamespaceMessages.CHUNK_BYTES));
        Assert.That(transfer, Is.Not.Null);
        Assert.That(chunk.Shared, Is.EqualTo(new[] { true }));
        Assert.That(chunk.Bytes[5999], Is.EqualTo(bytes[5999]), "the chunk holds the file's bytes");
        Assert.That(chunk.Bytes[6000], Is.Zero, "and zeros past its end");
        Assert.That((read.Error, seen), Is.EqualTo((0, (byte)0xAB)), "a read sees what the mapping wrote");
        Assert.That(chunk.Bytes.Slice(200, 4).ToArray(), Is.EqualTo("wxyz"u8.ToArray()), "a write reaches the mapping");
        Assert.That((synced.Error, closed.Error), Is.EqualTo((0, 0)));
        Assert.That(chunk.Disposed, Is.True, "the last handle that mapped the file dropped its chunk");
        Assert.That(file[100], Is.EqualTo(0xAB), "the mapping's write reached the file");
        Assert.That(file.AsSpan(200, 4).ToArray(), Is.EqualTo("wxyz"u8.ToArray()));
        Assert.That(file[101], Is.EqualTo(bytes[101]));
    }

    [Test]
    public async Task HandlesShareAChunkTest()
    {
        var chunks = new FakeChunkSource();
        var mounts = new MountTable(chunks);
        mounts.Add("/fat", await FatFileSystem.FormatAsync(new MemoryBlockStorage(4 * 1024 * 1024)));
        var window = new ByteWindow(8192);
        var writer = new NamespaceSession(mounts, window);
        var made = await OpenAsync(writer, window, "/fat/shared.bin", NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
        window.Write(0, new byte[8192]);
        await writer.HandleAsync(new NamespaceRequest(NamespaceMessages.WRITE, Handle: made.Handle, Length: 8192));
        await writer.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: made.Handle));
        var a = new NamespaceSession(mounts, window);
        var b = new NamespaceSession(mounts, window);
        var readA = await OpenAsync(a, window, "/fat/shared.bin", NamespaceMessages.READ_ACCESS);
        var readB = await OpenAsync(b, window, "/fat/shared.bin", NamespaceMessages.READ_ACCESS);

        var mapA = await a.HandleAsync(new NamespaceRequest(NamespaceMessages.MAP, Handle: readA.Handle));
        var mapB = await b.HandleAsync(new NamespaceRequest(NamespaceMessages.MAP, Handle: readB.Handle));
        var writable = await b.HandleAsync(new NamespaceRequest(NamespaceMessages.MAP, NamespaceMessages.MAP_WRITE, readB.Handle));
        var misaligned = await b.HandleAsync(new NamespaceRequest(NamespaceMessages.MAP, Handle: readB.Handle, Offset: 4096));
        await a.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: readA.Handle));
        var kept = chunks.Created.Single().Disposed;
        await b.DisposeAsync();

        Assert.That((mapA.Error, mapB.Error), Is.EqualTo((0, 0)));
        Assert.That(chunks.Created, Has.Count.EqualTo(1), "both handles map the same chunk");
        Assert.That(chunks.Created[0].Shared, Is.EqualTo(new[] { false, false }));
        Assert.That(writable.Error, Is.EqualTo(Errno.ACCESS), "a handle opened for reading maps nothing writable");
        Assert.That(misaligned.Error, Is.EqualTo(Errno.INVALID));
        Assert.That(kept, Is.False, "the chunk lives while a handle maps the file");
        Assert.That(chunks.Created[0].Disposed, Is.True);
    }

    [Test]
    public async Task AFailingDiskGivesEioTest()
    {
        var disk = new FailingBlockStorage(new MemoryBlockStorage(4 * 1024 * 1024));
        await using (var volume = await FatFileSystem.FormatAsync(disk))
        await using (var file = await volume.OpenAsync("/file.bin", System.IO.FileMode.Create, System.IO.FileAccess.Write))
            await file.WriteAsync(0, Enumerable.Range(0, 20_000).Select(i => (byte)i).ToArray());
        var mounts = new MountTable();
        mounts.Add("/fat", await FatFileSystem.MountAsync(disk));
        var window = new ByteWindow(8192);
        var session = new NamespaceSession(mounts, window);
        var opened = await OpenAsync(session, window, "/fat/file.bin", NamespaceMessages.READ_ACCESS);

        disk.Failing = true;
        var read = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.READ, Handle: opened.Handle, Offset: 0, Length: 8192));
        disk.Failing = false;
        var again = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.READ, Handle: opened.Handle, Offset: 0, Length: 8192));

        Assert.That(opened.Error, Is.Zero);
        Assert.That(read.Error, Is.EqualTo(Errno.IO), "a device that fails a read gives EIO, never an exception");
        Assert.That(again.Error, Is.Zero, "the session goes on once the device answers again");
        Assert.That(window.Read(0, 4), Is.EqualTo(new byte[] { 0, 1, 2, 3 }));
    }

    [Test]
    public async Task ModesFollowTheVolumeTest()
    {
        // chmod (N4.1): a volume in memory keeps the bits, FAT keeps read-only alone and refuses the rest with ENOTSUP.
        var mounts = new MountTable();
        mounts.Add("/tmp", new OutWit.OS.FileSystems.Memory.MemoryFileSystem(1 << 20));
        mounts.Add("/fat", await FatFileSystem.FormatAsync(new MemoryBlockStorage(4 * 1024 * 1024)));
        var window = new ByteWindow(8192);
        var session = new NamespaceSession(mounts, window);
        foreach (var path in new[] { "/tmp/f", "/fat/f" })
        {
            var made = await OpenAsync(session, window, path, NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
            await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: made.Handle));
        }
        await Path(session, window, NamespaceMessages.MKDIR, "/tmp/d");

        var kept = await Mode(session, window, "/tmp/f", 0x180);                      // 0600
        var directory = await Mode(session, window, "/tmp/d", 0x1FF);                 // 0777
        var opened = await OpenAsync(session, window, "/tmp/f", NamespaceMessages.READ_ACCESS);
        var byHandle = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.FCHMOD, Handle: opened.Handle, Mode: 0x1A4)); // 0644
        var stat = await Path(session, window, NamespaceMessages.STAT, "/tmp/f");
        var readOnly = await Mode(session, window, "/fat/f", 0x124);                  // 0444
        var writable = await Mode(session, window, "/fat/f", 0x1A4);                  // 0644
        var refused = await Mode(session, window, "/fat/f", 0x180);                   // 0600
        var missing = await Mode(session, window, "/tmp/none", 0x180);

        Assert.That(kept.Error, Is.Zero);
        Assert.That(kept.Stat!.Mode, Is.EqualTo(0x180));
        Assert.That(directory.Stat!.Mode, Is.EqualTo(0x1FF));
        Assert.That(byHandle.Error, Is.Zero);
        Assert.That(stat.Stat!.Mode, Is.EqualTo(0x1A4));
        Assert.That(readOnly.Error, Is.Zero);
        Assert.That(readOnly.Stat!.Mode, Is.EqualTo(0x124));
        Assert.That(writable.Stat!.Mode, Is.EqualTo(0x1A4));
        Assert.That(refused.Error, Is.EqualTo(Errno.PERMISSION), "a mode FAT cannot keep is EPERM, as Linux's vfat answers");
        Assert.That(missing.Error, Is.EqualTo(Errno.NOT_FOUND));
    }

    [Test]
    public async Task TimesFollowTheVolumeTest()
    {
        // utimensat (N4.1): the times given in the window as the flags say, the service's clock for "now"; FAT refuses with
        // EPERM, which System.Native's CopyFile tolerates.
        var mounts = new MountTable();
        mounts.Add("/tmp", new OutWit.OS.FileSystems.Memory.MemoryFileSystem(1 << 20));
        mounts.Add("/fat", await FatFileSystem.FormatAsync(new MemoryBlockStorage(4 * 1024 * 1024)));
        var window = new ByteWindow(8192);
        var session = new NamespaceSession(mounts, window);
        foreach (var path in new[] { "/tmp/f", "/fat/f" })
        {
            var made = await OpenAsync(session, window, path, NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
            await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: made.Handle));
        }
        var accessed = new DateTimeOffset(2020, 9, 13, 12, 26, 40, TimeSpan.Zero);
        var modified = accessed.AddSeconds(1).AddTicks(5_000_000);

        var both = await Times(session, window, "/tmp/f", accessed, modified, NamespaceMessages.TIME_ACCESSED | NamespaceMessages.TIME_MODIFIED);
        var omitted = await Times(session, window, "/tmp/f", accessed.AddDays(1), modified.AddDays(1), NamespaceMessages.TIME_MODIFIED);
        var before = DateTimeOffset.UtcNow;
        var opened = await OpenAsync(session, window, "/tmp/f", NamespaceMessages.READ_ACCESS);
        var now = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.FUTIMES, NamespaceMessages.TIME_MODIFIED_NOW, Handle: opened.Handle));
        var refused = await Times(session, window, "/fat/f", accessed, modified, NamespaceMessages.TIME_ACCESSED | NamespaceMessages.TIME_MODIFIED);
        var outside = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.FUTIMES, NamespaceMessages.TIME_MODIFIED, Handle: opened.Handle,
            Offset: 8192));

        Assert.That(both.Error, Is.Zero);
        Assert.That(both.Stat!.Accessed, Is.EqualTo(accessed));
        Assert.That(both.Stat.Modified, Is.EqualTo(modified));
        Assert.That(omitted.Stat!.Accessed, Is.EqualTo(accessed), "a time the flags leave out stays");
        Assert.That(omitted.Stat.Modified, Is.EqualTo(modified.AddDays(1)));
        Assert.That(now.Error, Is.Zero);
        Assert.That(now.Stat!.Modified, Is.GreaterThanOrEqualTo(before.AddSeconds(-1)).And.LessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.That(now.Stat.Accessed, Is.EqualTo(accessed));
        Assert.That(refused.Error, Is.EqualTo(Errno.PERMISSION));
        Assert.That(outside.Error, Is.EqualTo(Errno.INVALID));
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

    private static ValueTask<NamespaceReply> Mode(NamespaceSession session, ByteWindow window, string path, uint mode)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        window.Write(0, bytes);
        return session.HandleAsync(new NamespaceRequest(NamespaceMessages.CHMOD, Length: bytes.Length, Mode: mode));
    }

    private static ValueTask<NamespaceReply> Times(NamespaceSession session, ByteWindow window, string path, DateTimeOffset accessed,
        DateTimeOffset modified, uint flags)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        window.Write(0, bytes);
        var at = (bytes.Length + 7) & ~7;
        var times = new byte[NamespaceMessages.TIMES_SIZE];
        NamespaceMessages.WriteTimes(times, accessed, modified);
        window.Write(at, times);
        return session.HandleAsync(new NamespaceRequest(NamespaceMessages.UTIMES, flags, Offset: at, Length: bytes.Length));
    }

    private static ValueTask<NamespaceReply> Rename(NamespaceSession session, ByteWindow window, string from, string to)
    {
        var bytes = Encoding.UTF8.GetBytes(from + "\0" + to + "\0");
        window.Write(0, bytes);
        return session.HandleAsync(new NamespaceRequest(NamespaceMessages.RENAME, Length: bytes.Length));
    }

    #endregion

    #region Nested Types

    // Chunks as arrays (R4.2): what the service shares and how, without memory objects.
    private sealed class FakeChunkSource : IChunkSource
    {
        public System.Collections.Generic.List<FakeChunk> Created { get; } = [];

        public ISharedChunk Create(int bytes)
        {
            var chunk = new FakeChunk(bytes);
            Created.Add(chunk);
            return chunk;
        }
    }

    private sealed class FakeChunk(int bytes) : ISharedChunk
    {
        private readonly byte[] m_bytes = new byte[bytes];

        public Span<byte> Bytes => m_bytes;

        public System.Collections.Generic.List<bool> Shared { get; } = [];

        public bool Disposed { get; private set; }

        public OutWit.OS.Resources.KernelHandle Share(bool writable)
        {
            Shared.Add(writable);
            return new OutWit.OS.Resources.KernelHandle(0); // no handle: nothing to close
        }

        public void Dispose() => Disposed = true;
    }

    // A disk whose reads fail with the device's IOException while Failing is set (D6.3).
    private sealed class FailingBlockStorage(MemoryBlockStorage disk) : BlockStorageBase(disk.Info)
    {
        public bool Failing { get; set; }

        protected override ValueTask ReadCoreAsync(long offset, Memory<byte> buffer, System.Threading.CancellationToken cancellationToken) =>
            Failing ? throw new System.IO.IOException($"the device failed a read at {offset}") : disk.ReadAsync(offset, buffer, cancellationToken);

        protected override ValueTask WriteCoreAsync(long offset, ReadOnlyMemory<byte> buffer, WriteOptions options,
            System.Threading.CancellationToken cancellationToken) => disk.WriteAsync(offset, buffer, options, cancellationToken);
    }

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
