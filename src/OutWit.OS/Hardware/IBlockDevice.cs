using System;
using System.IO;

namespace OutWit.OS.Hardware;

/// <summary>
/// A disk as a block driver drives it (plan steps D3.2 and H3.1): its size and block, whether it refuses writes or keeps
/// a volatile cache, how many physical segments one request may carry, and its requests, each served whole before the
/// call returns — what <see cref="BlockServer"/> serves the block protocol over. The virtio block device, an AHCI port's
/// disk and an NVMe namespace are disks.
/// </summary>
public interface IBlockDevice
{
    /// <summary>
    /// Reads bytes of the disk into physical segments.
    /// </summary>
    /// <param name="offset">The first byte, a multiple of <see cref="BlockSize"/>.</param>
    /// <param name="segments">The segments, at most <see cref="MaxSegments"/> of at most <see cref="MaxSegmentBytes"/> each, a
    /// multiple of the block in all.</param>
    /// <exception cref="IOException">The device failed the request; the message names the device's failure.</exception>
    void Read(long offset, ReadOnlySpan<(ulong Address, int Length)> segments);

    /// <summary>
    /// Writes bytes of the disk from physical segments.
    /// </summary>
    /// <param name="offset">The first byte, a multiple of <see cref="BlockSize"/>.</param>
    /// <param name="segments">The segments, as for <see cref="Read"/>.</param>
    /// <exception cref="IOException">The device failed the request.</exception>
    void Write(long offset, ReadOnlySpan<(ulong Address, int Length)> segments);

    /// <summary>
    /// Makes every completed write durable: the device's volatile cache written to its medium.
    /// </summary>
    /// <exception cref="IOException">The device failed the flush.</exception>
    void Flush();

    /// <summary>
    /// The disk's bytes.
    /// </summary>
    long Capacity { get; }

    /// <summary>
    /// The disk's logical block, a multiple of 512.
    /// </summary>
    int BlockSize { get; }

    /// <summary>
    /// Whether the disk refuses writes.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Whether the disk keeps a volatile cache that a flush empties.
    /// </summary>
    bool Flushes { get; }

    /// <summary>
    /// The physical segments one request may carry.
    /// </summary>
    int MaxSegments { get; }

    /// <summary>
    /// The bytes of one segment.
    /// </summary>
    long MaxSegmentBytes { get; }

    /// <summary>
    /// The bytes of the largest request, whatever its segments: an NVMe controller's MDTS, the sectors of one ATA command.
    /// </summary>
    long MaxTransferBytes { get; }
}
