using System;
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

    [Test]
    public async Task LargeBlockServesTheRestOfItsBytesTest()
    {
        var inner = new MemoryBlockStorage(Pattern(32 * BLOCK));
        await using var cache = new CachedBlockStorage(inner, 4, blockSize: 4 * BLOCK);

        var first = new byte[BLOCK];
        await cache.ReadAsync(BLOCK, first);
        var second = new byte[2 * BLOCK];
        await cache.ReadAsync(2 * BLOCK, second);

        Assert.That(first, Is.EqualTo(Pattern(32 * BLOCK)[BLOCK..(2 * BLOCK)]));
        Assert.That(second, Is.EqualTo(Pattern(32 * BLOCK)[(2 * BLOCK)..(4 * BLOCK)]));
        Assert.That(cache.Misses, Is.EqualTo(1), "one read of the whole block");
        Assert.That(cache.Hits, Is.EqualTo(1));
        Assert.That(cache.BlockSize, Is.EqualTo(4 * BLOCK));
        Assert.That(cache.CapacityBytes, Is.EqualTo(16L * BLOCK));
    }

    [Test]
    public async Task PartialWriteThroughRefreshesAHeldBlockTest()
    {
        var inner = new MemoryBlockStorage(16 * BLOCK);
        await using var cache = new CachedBlockStorage(inner, 4, blockSize: 4 * BLOCK);

        await cache.ReadAsync(0, new byte[4 * BLOCK]);
        await cache.WriteAsync(BLOCK, Fill(BLOCK, 0x44));
        var read = new byte[4 * BLOCK];
        await cache.ReadAsync(0, read);

        Assert.That(inner.Bytes.Slice(BLOCK, BLOCK).ToArray().All(value => value == 0x44), Is.True, "through to the resource");
        Assert.That(read, Is.EqualTo(inner.Bytes[..(4 * BLOCK)].ToArray()));
        Assert.That(cache.Hits, Is.EqualTo(1));
    }

    [Test]
    public async Task PartialWriteThroughLeavesAnAbsentBlockOutTest()
    {
        var inner = new MemoryBlockStorage(Pattern(16 * BLOCK));
        await using var cache = new CachedBlockStorage(inner, 4, blockSize: 4 * BLOCK);

        await cache.WriteAsync(4 * BLOCK + BLOCK, Fill(BLOCK, 0x55));
        var read = new byte[4 * BLOCK];
        await cache.ReadAsync(4 * BLOCK, read);

        Assert.That(cache.Misses, Is.EqualTo(1), "the write did not enter the block");
        Assert.That(read, Is.EqualTo(inner.Bytes.Slice(4 * BLOCK, 4 * BLOCK).ToArray()));
        Assert.That(read.AsSpan(BLOCK, BLOCK).ToArray().All(value => value == 0x55), Is.True);
    }

    [Test]
    public async Task PartialWriteBackReadsTheBlockFirstTest()
    {
        var inner = new MemoryBlockStorage(Pattern(16 * BLOCK));
        await using var cache = new CachedBlockStorage(inner, 4, writeBack: true, blockSize: 4 * BLOCK);

        await cache.WriteAsync(2 * BLOCK, Fill(BLOCK, 0x66));
        Assert.That(cache.DirtyCount, Is.EqualTo(1));
        Assert.That(inner.Bytes.Slice(2 * BLOCK, BLOCK).ToArray(), Is.EqualTo(Pattern(16 * BLOCK)[(2 * BLOCK)..(3 * BLOCK)]), "not yet written");
        await cache.FlushAsync();

        var expected = Pattern(16 * BLOCK);
        Fill(BLOCK, 0x66).CopyTo(expected, 2 * BLOCK);
        Assert.That(inner.Bytes.ToArray(), Is.EqualTo(expected), "the block's other bytes kept");
        Assert.That(cache.DirtyCount, Is.Zero);
    }

    [Test]
    public async Task ShortLastBlockTest()
    {
        var inner = new MemoryBlockStorage(Pattern(10 * BLOCK));
        await using var cache = new CachedBlockStorage(inner, 4, writeBack: true, blockSize: 4 * BLOCK);

        var tail = new byte[2 * BLOCK];
        await cache.ReadAsync(8 * BLOCK, tail);
        await cache.WriteAsync(9 * BLOCK, Fill(BLOCK, 0x77));
        await cache.FlushAsync();

        Assert.That(tail, Is.EqualTo(Pattern(10 * BLOCK)[(8 * BLOCK)..]));
        Assert.That(inner.Bytes.Slice(9 * BLOCK, BLOCK).ToArray().All(value => value == 0x77), Is.True);
        Assert.That(inner.Bytes.Slice(8 * BLOCK, BLOCK).ToArray(), Is.EqualTo(Pattern(10 * BLOCK)[(8 * BLOCK)..(9 * BLOCK)]));
    }

    [Test]
    public async Task DiscardKeepsTheDirtyBytesOutsideItTest()
    {
        var inner = new MemoryBlockStorage(16 * BLOCK);
        await using var cache = new CachedBlockStorage(inner, 4, writeBack: true, blockSize: 4 * BLOCK);

        await cache.WriteAsync(0, Fill(4 * BLOCK, 0x88));
        await cache.DiscardAsync(2 * BLOCK, 2 * BLOCK);

        Assert.That(cache.DirtyCount, Is.Zero);
        Assert.That(inner.Bytes[..(2 * BLOCK)].ToArray().All(value => value == 0x88), Is.True, "written before the discard");
    }

    [Test]
    public void BlockMustBeAMultipleOfTheAlignmentTest()
    {
        var inner = new MemoryBlockStorage(16 * BLOCK);

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new CachedBlockStorage(inner, 4, blockSize: BLOCK + BLOCK / 2));
    }

    #endregion

    #region Tools

    private static byte[] Fill(int length, byte value) => [.. Enumerable.Repeat(value, length)];

    private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 + i / BLOCK))];

    #endregion
}
