using System.Linq;
using System.Threading.Tasks;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.Storage;

/// <summary>
/// The cache's own promises (storage document §35–36, §48): hits and misses, writes that wait for a flush, durable writes
/// that do not, evictions that write dirty blocks, and the cache's end that writes the rest.
/// </summary>
[TestFixture]
public sealed class CachedBlockStorageTests
{
    #region Constants

    private const int BLOCK = 512;

    #endregion

    #region Tests

    [Test]
    public async Task ReadsHitAfterMissTest()
    {
        var inner = new MemoryBlockStorage(16 * BLOCK);
        await using var cache = new CachedBlockStorage(inner, 4);

        await cache.ReadAsync(0, new byte[2 * BLOCK]);
        await cache.ReadAsync(0, new byte[2 * BLOCK]);

        Assert.That(cache.Misses, Is.EqualTo(2));
        Assert.That(cache.Hits, Is.EqualTo(2));
    }

    [Test]
    public async Task WriteBackWaitsForFlushTest()
    {
        var inner = new MemoryBlockStorage(16 * BLOCK);
        await using var cache = new CachedBlockStorage(inner, 8, writeBack: true);

        await cache.WriteAsync(2 * BLOCK, Fill(2 * BLOCK, 0x5A));

        Assert.That(cache.DirtyCount, Is.EqualTo(2));
        Assert.That(inner.Bytes.Slice(2 * BLOCK, 2 * BLOCK).ToArray().All(value => value == 0), Is.True);
        await cache.FlushAsync();
        Assert.That(cache.DirtyCount, Is.Zero);
        Assert.That(inner.Bytes.Slice(2 * BLOCK, 2 * BLOCK).ToArray().All(value => value == 0x5A), Is.True);
    }

    [Test]
    public async Task DurableWriteGoesThroughTest()
    {
        var inner = new MemoryBlockStorage(16 * BLOCK);
        await using var cache = new CachedBlockStorage(inner, 8, writeBack: true);

        await cache.WriteAsync(0, Fill(BLOCK, 0x11), WriteOptions.Durable);

        Assert.That(cache.DirtyCount, Is.Zero);
        Assert.That(inner.Bytes[..BLOCK].ToArray().All(value => value == 0x11), Is.True);
    }

    [Test]
    public async Task EvictionWritesDirtyBlockTest()
    {
        var inner = new MemoryBlockStorage(16 * BLOCK);
        await using var cache = new CachedBlockStorage(inner, 2, writeBack: true);

        await cache.WriteAsync(0, Fill(BLOCK, 0x22));
        await cache.ReadAsync(4 * BLOCK, new byte[2 * BLOCK]); // two more blocks evict the dirty one

        Assert.That(inner.Bytes[..BLOCK].ToArray().All(value => value == 0x22), Is.True);
    }

    [Test]
    public async Task DisposeWritesDirtyBlocksTest()
    {
        var inner = new MemoryBlockStorage(16 * BLOCK);
        var cache = new CachedBlockStorage(inner, 8, writeBack: true, leaveOpen: true);

        await cache.WriteAsync(BLOCK, Fill(BLOCK, 0x33));
        await cache.DisposeAsync();

        Assert.That(inner.Bytes.Slice(BLOCK, BLOCK).ToArray().All(value => value == 0x33), Is.True);
        await inner.ReadAsync(0, new byte[BLOCK]); // left open
    }

    #endregion

    #region Tools

    private static byte[] Fill(int length, byte value) => [.. Enumerable.Repeat(value, length)];

    #endregion
}
