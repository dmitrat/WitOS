using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems.Fat;
using OutWit.OS.Namespace.Server;
using OutWit.OS.Namespaces;
using OutWit.OS.Resources;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.Namespace;

/// <summary>
/// The names local sockets bind (plan step W4.1): a path a stat, a listing, an unlink and a rename see beside the
/// volume's entries and that stays until it is unlinked; an abstract name that goes with its session; and a connection
/// that receives the listener's end, or ENOENT, ECONNREFUSED or EPROTOTYPE as Linux's connect answers.
/// </summary>
[TestFixture]
public sealed class SocketNameTests
{
    #region Constants

    private const uint STREAM = SocketNames.STREAM;

    private const uint SEQUENCED_PACKET = SocketNames.SEQUENCED_PACKET;

    #endregion

    #region Tests

    [Test]
    public async Task APathIsASocketBesideTheVolumeTest()
    {
        var (mounts, _) = await CreateAsync();
        var (session, window) = Session(mounts);
        await Call(session, window, NamespaceMessages.MKDIR, "/fat/run");

        var bound = await Call(session, window, NamespaceMessages.SOCKET_BIND, "/fat/run/app.sock", mode: STREAM);
        var stat = await Call(session, window, NamespaceMessages.STAT, "/fat/run/app.sock");
        var names = await ListAsync(session, window, "/fat/run");

        Assert.That(bound.Error, Is.Zero);
        Assert.That(bound.Handle, Is.Not.Zero);
        Assert.That(stat.Stat!.Kind, Is.EqualTo(NamespaceMessages.KIND_SOCKET));
        Assert.That(stat.Stat.Mode, Is.EqualTo(0x1ED)); // 0755
        Assert.That(names, Is.EquivalentTo(new[] { ("app.sock", NamespaceMessages.KIND_SOCKET) }));
        Assert.That((await Call(session, window, NamespaceMessages.SOCKET_BIND, "/fat/run/app.sock", mode: STREAM)).Error, Is.EqualTo(Errno.ADDRESS_IN_USE));
        Assert.That((await Call(session, window, NamespaceMessages.OPEN, "/fat/run/app.sock", NamespaceMessages.READ_ACCESS)).Error,
            Is.EqualTo(Errno.NO_DEVICE));
        Assert.That((await Call(session, window, NamespaceMessages.OPEN, "/fat/run/app.sock",
            NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE | NamespaceMessages.EXCLUSIVE)).Error, Is.EqualTo(Errno.EXISTS));
        Assert.That((await Call(session, window, NamespaceMessages.MKDIR, "/fat/run/app.sock")).Error, Is.EqualTo(Errno.EXISTS));
        Assert.That((await Call(session, window, NamespaceMessages.REMOVE, "/fat/run", NamespaceMessages.DIRECTORY)).Error, Is.EqualTo(Errno.NOT_EMPTY));
        Assert.That((await Call(session, window, NamespaceMessages.REMOVE, "/fat/run/app.sock", NamespaceMessages.DIRECTORY)).Error,
            Is.EqualTo(Errno.NOT_DIRECTORY));
        Assert.That((await Call(session, window, NamespaceMessages.READLINK, "/fat/run/app.sock")).Error, Is.EqualTo(Errno.INVALID));
        Assert.That((await Call(session, window, NamespaceMessages.REMOVE, "/fat/run/app.sock")).Error, Is.Zero);
        Assert.That((await Call(session, window, NamespaceMessages.STAT, "/fat/run/app.sock")).Error, Is.EqualTo(Errno.NOT_FOUND));
        Assert.That((await Call(session, window, NamespaceMessages.REMOVE, "/fat/run", NamespaceMessages.DIRECTORY)).Error, Is.Zero);
    }

    [Test]
    public async Task ABindNeedsAFreeNameInADirectoryTest()
    {
        var (mounts, _) = await CreateAsync();
        var (session, window) = Session(mounts);
        var file = await Call(session, window, NamespaceMessages.OPEN, "/fat/file", NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
        await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: file.Handle));

        Assert.That((await Call(session, window, NamespaceMessages.SOCKET_BIND, "/fat/missing/app.sock", mode: STREAM)).Error, Is.EqualTo(Errno.NOT_FOUND));
        Assert.That((await Call(session, window, NamespaceMessages.SOCKET_BIND, "/fat/file", mode: STREAM)).Error, Is.EqualTo(Errno.ADDRESS_IN_USE));
        Assert.That((await Call(session, window, NamespaceMessages.SOCKET_BIND, "/fat/file/app.sock", mode: STREAM)).Error, Is.EqualTo(Errno.NOT_DIRECTORY));
        Assert.That((await Call(session, window, NamespaceMessages.SOCKET_BIND, "/fat", mode: STREAM)).Error, Is.EqualTo(Errno.ADDRESS_IN_USE));
        Assert.That((await Call(session, window, NamespaceMessages.SOCKET_BIND, "/nowhere/app.sock", mode: STREAM)).Error, Is.EqualTo(Errno.NOT_FOUND));
        Assert.That((await Call(session, window, NamespaceMessages.SOCKET_BIND, "/fat/dgram.sock", mode: 2)).Error, Is.EqualTo(Errno.INVALID));
    }

    [Test]
    public async Task AConnectionReceivesTheListenersEndTest()
    {
        var (mounts, ends) = await CreateAsync();
        var (server, serverWindow) = Session(mounts);
        var (client, clientWindow) = Session(mounts);
        var file = await Call(server, serverWindow, NamespaceMessages.OPEN, "/fat/plain", NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
        await server.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: file.Handle));
        var bound = await Call(server, serverWindow, NamespaceMessages.SOCKET_BIND, "/fat/app.sock", mode: STREAM);

        var beforeListening = await Call(client, clientWindow, NamespaceMessages.SOCKET_CONNECT, "/fat/app.sock", mode: STREAM);
        server.Receive(new KernelHandle(0));
        var listening = await server.HandleAsync(new NamespaceRequest(NamespaceMessages.SOCKET_LISTEN, Handle: bound.Handle));
        var connected = await Call(client, clientWindow, NamespaceMessages.SOCKET_CONNECT, "/fat/app.sock", mode: STREAM);
        var transfer = client.TakeTransfer();
        var otherType = await Call(client, clientWindow, NamespaceMessages.SOCKET_CONNECT, "/fat/app.sock", mode: SEQUENCED_PACKET);
        var toAFile = await Call(client, clientWindow, NamespaceMessages.SOCKET_CONNECT, "/fat/plain", mode: STREAM);
        var toNothing = await Call(client, clientWindow, NamespaceMessages.SOCKET_CONNECT, "/fat/none.sock", mode: STREAM);
        client.Receive(new KernelHandle(0));
        var notTheirs = await client.HandleAsync(new NamespaceRequest(NamespaceMessages.SOCKET_LISTEN, Handle: bound.Handle));
        var closed = await server.HandleAsync(new NamespaceRequest(NamespaceMessages.SOCKET_CLOSE, Handle: bound.Handle));
        var afterClose = await Call(client, clientWindow, NamespaceMessages.SOCKET_CONNECT, "/fat/app.sock", mode: STREAM);

        Assert.That(beforeListening.Error, Is.EqualTo(Errno.CONNECTION_REFUSED));
        Assert.That(listening.Error, Is.Zero);
        Assert.That(connected.Error, Is.Zero);
        Assert.That(transfer, Is.Not.Null);
        Assert.That(ends.Adopted, Has.Count.EqualTo(2));
        Assert.That(ends.Adopted[0].Shared, Is.EqualTo(1));
        Assert.That(ends.Adopted[0].Disposed, Is.True, "the close let the end go");
        Assert.That(ends.Adopted[1].Disposed, Is.True, "another session's listen is refused and its end closed");
        Assert.That(otherType.Error, Is.EqualTo(Errno.PROTOCOL_TYPE));
        Assert.That(toAFile.Error, Is.EqualTo(Errno.CONNECTION_REFUSED));
        Assert.That(toNothing.Error, Is.EqualTo(Errno.NOT_FOUND));
        Assert.That(notTheirs.Error, Is.EqualTo(Errno.BAD_HANDLE));
        Assert.That(closed.Error, Is.Zero);
        Assert.That(afterClose.Error, Is.EqualTo(Errno.CONNECTION_REFUSED), "a path stays until it is unlinked");
    }

    [Test]
    public async Task AnUnlinkedNameFindsNoListenerTest()
    {
        var (mounts, ends) = await CreateAsync();
        var (session, window) = Session(mounts);
        var bound = await Call(session, window, NamespaceMessages.SOCKET_BIND, "/fat/app.sock", mode: STREAM);
        session.Receive(new KernelHandle(0));
        await session.HandleAsync(new NamespaceRequest(NamespaceMessages.SOCKET_LISTEN, Handle: bound.Handle));

        var removed = await Call(session, window, NamespaceMessages.REMOVE, "/fat/app.sock");
        var connected = await Call(session, window, NamespaceMessages.SOCKET_CONNECT, "/fat/app.sock", mode: STREAM);
        var again = await Call(session, window, NamespaceMessages.SOCKET_BIND, "/fat/app.sock", mode: STREAM);

        Assert.That(removed.Error, Is.Zero);
        Assert.That(connected.Error, Is.EqualTo(Errno.NOT_FOUND));
        Assert.That(ends.Adopted[0].Disposed, Is.True);
        Assert.That(again.Error, Is.Zero, "the name is free once unlinked");
    }

    [Test]
    public async Task AnAbstractNameGoesWithItsSessionTest()
    {
        var (mounts, _) = await CreateAsync();
        var (first, firstWindow) = Session(mounts);
        var (second, secondWindow) = Session(mounts);
        var name = new byte[] { (byte)'d', (byte)'i', (byte)'a', (byte)'g', 0, (byte)'/' }; // any bytes, a zero among them
        var bound = await Abstract(first, firstWindow, NamespaceMessages.SOCKET_BIND, name);
        first.Receive(new KernelHandle(0));
        await first.HandleAsync(new NamespaceRequest(NamespaceMessages.SOCKET_LISTEN, Handle: bound.Handle));
        var path = await Call(first, firstWindow, NamespaceMessages.SOCKET_BIND, "/fat/kept.sock", mode: STREAM);

        var taken = await Abstract(second, secondWindow, NamespaceMessages.SOCKET_BIND, name);
        var connected = await Abstract(second, secondWindow, NamespaceMessages.SOCKET_CONNECT, name);
        second.TakeTransfer();
        await first.DisposeAsync();
        var afterwards = await Abstract(second, secondWindow, NamespaceMessages.SOCKET_CONNECT, name);
        var kept = await Call(second, secondWindow, NamespaceMessages.STAT, "/fat/kept.sock");

        Assert.That(bound.Error, Is.Zero);
        Assert.That(path.Error, Is.Zero);
        Assert.That(taken.Error, Is.EqualTo(Errno.ADDRESS_IN_USE));
        Assert.That(connected.Error, Is.Zero);
        Assert.That(afterwards.Error, Is.EqualTo(Errno.CONNECTION_REFUSED), "Linux refuses a connection to an abstract name nobody has");
        Assert.That(kept.Stat!.Kind, Is.EqualTo(NamespaceMessages.KIND_SOCKET), "a path outlives its session");
    }

    [Test]
    public async Task ARenameMovesTheNamesTest()
    {
        var (mounts, _) = await CreateAsync();
        var (session, window) = Session(mounts);
        await Call(session, window, NamespaceMessages.MKDIR, "/fat/a");
        await Call(session, window, NamespaceMessages.MKDIR, "/fat/b");
        await Call(session, window, NamespaceMessages.SOCKET_BIND, "/fat/a/one.sock", mode: STREAM);
        await Call(session, window, NamespaceMessages.SOCKET_BIND, "/fat/two.sock", mode: STREAM);

        var moved = await Rename(session, window, "/fat/two.sock", "/fat/b/two.sock");
        var directory = await Rename(session, window, "/fat/a", "/fat/c");
        var acrossVolumes = await Rename(session, window, "/fat/b/two.sock", "/other/two.sock");

        Assert.That(moved.Error, Is.Zero);
        Assert.That(directory.Error, Is.Zero);
        Assert.That(acrossVolumes.Error, Is.EqualTo(Errno.CROSS_DEVICE));
        Assert.That((await Call(session, window, NamespaceMessages.STAT, "/fat/b/two.sock")).Stat!.Kind, Is.EqualTo(NamespaceMessages.KIND_SOCKET));
        Assert.That((await Call(session, window, NamespaceMessages.STAT, "/fat/c/one.sock")).Stat!.Kind, Is.EqualTo(NamespaceMessages.KIND_SOCKET));
        Assert.That((await Call(session, window, NamespaceMessages.STAT, "/fat/two.sock")).Error, Is.EqualTo(Errno.NOT_FOUND));
        Assert.That((await Call(session, window, NamespaceMessages.STAT, "/fat/a/one.sock")).Error, Is.EqualTo(Errno.NOT_FOUND));
    }

    #endregion

    #region Tools

    private static async Task<(MountTable Mounts, FakeEnds Ends)> CreateAsync()
    {
        var ends = new FakeEnds();
        var mounts = new MountTable(new MemoryLessChunks(), ends);
        mounts.Add("/fat", await FatFileSystem.FormatAsync(new MemoryBlockStorage(4 * 1024 * 1024)));
        mounts.Add("/other", await FatFileSystem.FormatAsync(new MemoryBlockStorage(4 * 1024 * 1024)));
        return (mounts, ends);
    }

    private static (NamespaceSession Session, ByteWindow Window) Session(MountTable mounts)
    {
        var window = new ByteWindow(8192);
        return (new NamespaceSession(mounts, window), window);
    }

    private static ValueTask<NamespaceReply> Call(NamespaceSession session, ByteWindow window, uint operation, string path, uint flags = 0,
        uint mode = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        window.Write(0, bytes);
        return session.HandleAsync(new NamespaceRequest(operation, flags, Length: bytes.Length, Mode: mode));
    }

    private static ValueTask<NamespaceReply> Abstract(NamespaceSession session, ByteWindow window, uint operation, byte[] name)
    {
        window.Write(0, name);
        return session.HandleAsync(new NamespaceRequest(operation, NamespaceMessages.ABSTRACT, Length: name.Length, Mode: STREAM));
    }

    private static ValueTask<NamespaceReply> Rename(NamespaceSession session, ByteWindow window, string from, string to)
    {
        var bytes = Encoding.UTF8.GetBytes(from + "\0" + to + "\0");
        window.Write(0, bytes);
        return session.HandleAsync(new NamespaceRequest(NamespaceMessages.RENAME, Length: bytes.Length));
    }

    // A directory's listing as names and kinds, page by page.
    private static async Task<List<(string Name, uint Kind)>> ListAsync(NamespaceSession session, ByteWindow window, string path)
    {
        var directory = await Call(session, window, NamespaceMessages.OPEN, path, NamespaceMessages.READ_ACCESS | NamespaceMessages.DIRECTORY);
        var names = new List<(string, uint)>();
        var next = 0L;
        do
        {
            var page = await session.HandleAsync(new NamespaceRequest(NamespaceMessages.LIST, Handle: directory.Handle, Offset: next));
            var bytes = window.Read(0, (int)page.Length);
            for (var at = 0; at < bytes.Length;)
            {
                var length = BitConverter.ToUInt16(bytes, at + 12);
                names.Add((Encoding.UTF8.GetString(bytes, at + NamespaceMessages.ENTRY_SIZE, BitConverter.ToUInt16(bytes, at + 14)),
                    BitConverter.ToUInt32(bytes, at + 8)));
                at += length;
            }
            next = page.Offset;
        }
        while (next != 0);
        await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: directory.Handle));
        return names;
    }

    #endregion

    #region Nested Types

    // Listener ends without handles: what the service shares and closes.
    private sealed class FakeEnds : IListenerEndSource
    {
        public List<FakeEnd> Adopted { get; } = [];

        public IListenerEnd Adopt(KernelHandle handle)
        {
            var end = new FakeEnd();
            Adopted.Add(end);
            return end;
        }
    }

    private sealed class FakeEnd : IListenerEnd
    {
        public int Shared { get; private set; }

        public bool Disposed { get; private set; }

        public KernelHandle Share()
        {
            Shared++;
            return new KernelHandle(0); // no handle: nothing to close
        }

        public void Dispose() => Disposed = true;
    }

    // No file is mapped here.
    private sealed class MemoryLessChunks : IChunkSource
    {
        public ISharedChunk Create(int bytes) => throw new NotSupportedException();
    }

    private sealed class ByteWindow(int bytes) : INamespaceWindow
    {
        private readonly byte[] m_bytes = new byte[bytes];

        public int Length => m_bytes.Length;

        public byte[] Read(int offset, int count) => m_bytes.AsSpan(offset, count).ToArray();

        public void Write(int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(m_bytes.AsSpan(offset));
    }

    #endregion
}
