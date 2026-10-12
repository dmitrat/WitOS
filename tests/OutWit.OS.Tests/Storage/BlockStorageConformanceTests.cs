using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.Storage;

/// <summary>
/// The conformance of a block storage resource (storage document §113): range and alignment checks, read after write,
/// flush, read-only refusals, cancellation, disposal and concurrent requests, for every provider of D3.1 over memory.
/// </summary>
[TestFixture("Memory")]
[TestFixture("Partition")]
[TestFixture("CachedThrough")]
[TestFixture("CachedBack")]
public sealed class BlockStorageConformanceTests(string provider)
{
    #region Constants

    private const int BLOCK = 512;
    private const int BLOCKS = 64;

    #endregion

    #region Tests

    [Test]
    public async Task InfoIsConsistentTest()
    {
        await using var storage = Create(false);

        Assert.That(storage.Info.Length, Is.EqualTo(BLOCKS * BLOCK));
        Assert.That(storage.Info.LogicalBlockSize, Is.EqualTo(BLOCK));
        Assert.That(storage.Info.RequiredAlignment % storage.Info.LogicalBlockSize, Is.Zero);
        Assert.That(storage.Info.PhysicalBlockSize % storage.Info.LogicalBlockSize, Is.Zero);
        Assert.That(storage.Info.MaxOutstandingRequests, Is.Positive);
    }

    [Test]
    public async Task ReadAfterWriteTest()
    {
        await using var storage = Create(false);
        var written = Pattern(3 * BLOCK, 7);

        await storage.WriteAsync(5 * BLOCK, written);
        var read = new byte[3 * BLOCK];
        await storage.ReadAsync(5 * BLOCK, read);

        Assert.That(read, Is.EqualTo(written));
        var untouched = new byte[BLOCK];
        await storage.ReadAsync(4 * BLOCK, untouched);
        Assert.That(untouched.All(value => value == 0), Is.True);
    }

    [Test]
    public async Task WriteSurvivesFlushTest()
    {
        await using var storage = Create(false);
        var written = Pattern(BLOCK, 3);

        await storage.WriteAsync(0, written, WriteOptions.Durable);
        await storage.FlushAsync();
        var read = new byte[BLOCK];
        await storage.ReadAsync(0, read);

        Assert.That(read, Is.EqualTo(written));
    }

    [Test]
    public async Task UnalignedRequestIsRefusedTest()
    {
        await using var storage = Create(false);

        Assert.ThrowsAsync<ArgumentException>(async () => await storage.ReadAsync(1, new byte[BLOCK]));
        Assert.ThrowsAsync<ArgumentException>(async () => await storage.ReadAsync(0, new byte[BLOCK + 1]));
        Assert.ThrowsAsync<ArgumentException>(async () => await storage.WriteAsync(BLOCK / 2, new byte[BLOCK]));
    }

    [Test]
    public async Task RequestBeyondEndIsRefusedTest()
    {
        await using var storage = Create(false);

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.ReadAsync(BLOCKS * BLOCK, new byte[BLOCK]));
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.WriteAsync((BLOCKS - 1) * BLOCK, new byte[2 * BLOCK]));
        Assert.ThrowsAsync<ArgumentException>(async () => await storage.ReadAsync(-BLOCK, new byte[BLOCK]));
    }

    [Test]
    public async Task ReadOnlyRefusesWritesTest()
    {
        await using var storage = Create(true);

        Assert.That(storage.Info.IsReadOnly, Is.True);
        Assert.ThrowsAsync<NotSupportedException>(async () => await storage.WriteAsync(0, new byte[BLOCK]));
        Assert.ThrowsAsync<NotSupportedException>(async () => await storage.DiscardAsync(0, BLOCK));
        await storage.ReadAsync(0, new byte[BLOCK]);
    }

    [Test]
    public async Task CancelledRequestGoesNoFurtherTest()
    {
        await using var storage = Create(false);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await storage.WriteAsync(0, Pattern(BLOCK, 1), WriteOptions.None, cancelled.Token));
        var read = new byte[BLOCK];
        await storage.ReadAsync(0, read);
        Assert.That(read.All(value => value == 0), Is.True);
    }

    [Test]
    public async Task DisposedResourceRefusesRequestsTest()
    {
        var storage = Create(false);
        await storage.DisposeAsync();
        await storage.DisposeAsync();

        Assert.ThrowsAsync<ObjectDisposedException>(async () => await storage.ReadAsync(0, new byte[BLOCK]));
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await storage.FlushAsync());
    }

    [Test]
    public async Task ConcurrentRequestsTest()
    {
        await using var storage = Create(false);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => storage.WriteAsync(i * 2 * BLOCK, Pattern(2 * BLOCK, (byte)i)).AsTask()));
        var reads = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            var read = new byte[2 * BLOCK];
            await storage.ReadAsync(i * 2 * BLOCK, read);
            return read;
        }));

        for (var i = 0; i < 16; i++)
            Assert.That(reads[i], Is.EqualTo(Pattern(2 * BLOCK, (byte)i)));
    }

    #endregion

    #region Tools

    private IBlockStorage Create(bool isReadOnly) => provider switch
    {
        "Memory" => new MemoryBlockStorage(BLOCKS * BLOCK, BLOCK, isReadOnly),
        "Partition" => new PartitionBlockStorage(new MemoryBlockStorage((BLOCKS + 8) * BLOCK, BLOCK), 3 * BLOCK, BLOCKS * BLOCK, isReadOnly),
        "CachedThrough" => new CachedBlockStorage(new MemoryBlockStorage(BLOCKS * BLOCK, BLOCK, isReadOnly), 8),
        "CachedBack" => new CachedBlockStorage(new MemoryBlockStorage(BLOCKS * BLOCK, BLOCK, isReadOnly), 8, writeBack: true),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static byte[] Pattern(int length, byte seed) =>
        [.. Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed + 1))];

    #endregion
}
