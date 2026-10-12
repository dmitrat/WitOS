using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.Storage;

/// <summary>
/// The storage contract on a session of the block protocol (plan step D3.2): what the INFO reply says, transfers cut at
/// the driver's limit and the window, the durable flag on a write's last transfer, a driver's errors, and the session's
/// end.
/// </summary>
[TestFixture]
public sealed class ChannelBlockStorageTests
{
    #region Constants

    private const int BLOCK = 512;
    private const int PAGE = 4096;

    #endregion

    #region Tests

    [Test]
    public async Task InfoDescribesTheDeviceTest()
    {
        var session = new LoopbackBlockSession(new MemoryBlockStorage(64 * BLOCK, BLOCK, isReadOnly: true), PAGE, 2 * BLOCK);
        await using var storage = ChannelBlockStorage.Open(session);

        Assert.That(storage.Info.Length, Is.EqualTo(64 * BLOCK));
        Assert.That(storage.Info.LogicalBlockSize, Is.EqualTo(BLOCK));
        Assert.That(storage.Info.IsReadOnly, Is.True);
        Assert.That(storage.Info.SupportsFlush, Is.True);
        Assert.That(storage.Info.OptimalTransferSize, Is.EqualTo(2 * BLOCK));
        Assert.That(storage.Info.MaxTransferSize, Is.Zero);
        Assert.That(session.Requests.Single().Operation, Is.EqualTo(BlockMessages.INFO));
    }

    [Test]
    public async Task TransfersAreCutAtTheLimitTest()
    {
        var session = new LoopbackBlockSession(new MemoryBlockStorage(64 * BLOCK, BLOCK), PAGE, 2 * BLOCK);
        await using var storage = ChannelBlockStorage.Open(session);
        var written = Enumerable.Range(0, 5 * BLOCK).Select(i => (byte)(i * 7 + 3)).ToArray();

        await storage.WriteAsync(4 * BLOCK, written);
        var read = new byte[5 * BLOCK];
        await storage.ReadAsync(4 * BLOCK, read);

        var writes = session.Requests.Where(request => request.Operation == BlockMessages.WRITE).ToArray();
        Assert.That(writes.Select(request => (request.Offset, request.Length, request.Window)),
            Is.EqualTo(new[] { (4L * BLOCK, 2L * BLOCK, 0L), (6L * BLOCK, 2L * BLOCK, 0L), (8L * BLOCK, (long)BLOCK, 0L) }));
        Assert.That(session.Requests.Count(request => request.Operation == BlockMessages.READ), Is.EqualTo(3));
        Assert.That(read, Is.EqualTo(written));
    }

    [Test]
    public async Task TransfersAreCutAtTheWindowTest()
    {
        var session = new LoopbackBlockSession(new MemoryBlockStorage(64 * BLOCK, BLOCK), PAGE, 64 * BLOCK);
        await using var storage = ChannelBlockStorage.Open(session);

        await storage.ReadAsync(0, new byte[20 * BLOCK]);

        Assert.That(session.Requests.Where(request => request.Operation == BlockMessages.READ).Select(request => request.Length),
            Is.EqualTo(new[] { (long)PAGE, PAGE, 4L * BLOCK }));
    }

    [Test]
    public async Task DurableWriteMarksItsLastTransferTest()
    {
        var session = new LoopbackBlockSession(new MemoryBlockStorage(64 * BLOCK, BLOCK), PAGE, 2 * BLOCK);
        await using var storage = ChannelBlockStorage.Open(session);

        await storage.WriteAsync(0, new byte[5 * BLOCK], WriteOptions.Durable);
        await storage.FlushAsync();

        var writes = session.Requests.Where(request => request.Operation == BlockMessages.WRITE).ToArray();
        Assert.That(writes.Select(request => request.Flags), Is.EqualTo(new[] { 0U, 0U, BlockMessages.DURABLE }));
        Assert.That(session.Flushes, Is.EqualTo(1));
    }

    [Test]
    public async Task DriverErrorIsAnIOExceptionTest()
    {
        var session = new LoopbackBlockSession(new MemoryBlockStorage(64 * BLOCK, BLOCK), PAGE, 2 * BLOCK);
        await using var storage = ChannelBlockStorage.Open(session);
        session.Fail = BlockMessages.DEVICE_ERROR;

        var problem = Assert.ThrowsAsync<IOException>(async () => await storage.ReadAsync(0, new byte[BLOCK]));
        Assert.That(problem!.Message, Does.Contain("errno 5"));
    }

    [Test]
    public void InconsistentDriverIsRefusedTest()
    {
        var session = new LoopbackBlockSession(new MemoryBlockStorage(64 * BLOCK, BLOCK), PAGE, BLOCK / 2);

        Assert.Throws<IOException>(() => ChannelBlockStorage.Open(session));
        Assert.That(session.IsDisposed, Is.True);
    }

    [Test]
    public async Task DisposeEndsTheSessionTest()
    {
        var session = new LoopbackBlockSession(new MemoryBlockStorage(64 * BLOCK, BLOCK), PAGE, 2 * BLOCK);
        var storage = ChannelBlockStorage.Open(session);

        await storage.DisposeAsync();

        Assert.That(session.IsDisposed, Is.True);
    }

    #endregion
}
