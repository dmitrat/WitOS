using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// The volume's internal jbd2 journal (the kernel's Documentation/filesystems/ext4, "Journal (jbd2)"; every field
/// big-endian), in the journal inode's blocks (D4.3).
/// <list type="bullet">
/// <item>Recovery replays what Linux or another implementation left: it scans the log from s_start for the transactions
/// whose commit block is there and sound, gathers their revoke records, and puts each block they logged in place unless
/// a revoke of that transaction or a later one names it, checking descriptor, commit and data checksums (v2, v3).</item>
/// <item>A commit writes one transaction at the log's start with CSUM_V3 checksums: the descriptor blocks with their
/// tags, the blocks, escaped where they begin with the journal's magic, and the commit block; it points the journal's
/// superblock at it, flushes, puts the blocks in place, flushes, and empties the journal again. The log never holds more
/// than one transaction, so no revoke record is ever needed.</item>
/// </list>
/// </summary>
internal sealed class Ext4Journal
{
    #region Constants

    private const uint MAGIC = 0xC03B3998;
    private const uint DESCRIPTOR = 1;
    private const uint COMMIT = 2;
    private const uint SUPERBLOCK_V1 = 3;
    private const uint SUPERBLOCK_V2 = 4;
    private const uint REVOKE = 5;
    private const uint INCOMPAT_REVOKE = 0x1;
    private const uint INCOMPAT_64BIT = 0x2;
    private const uint INCOMPAT_ASYNC_COMMIT = 0x4;
    private const uint INCOMPAT_CSUM_V2 = 0x8;
    private const uint INCOMPAT_CSUM_V3 = 0x10;
    private const uint INCOMPAT_SERVED = INCOMPAT_REVOKE | INCOMPAT_64BIT | INCOMPAT_ASYNC_COMMIT | INCOMPAT_CSUM_V2 | INCOMPAT_CSUM_V3;
    private const uint TAG_ESCAPE = 0x1;
    private const uint TAG_SAME_UUID = 0x2;
    private const uint TAG_LAST = 0x8;
    private const byte CRC32C = 4;
    private const int HEADER = 12;
    private const int SUPERBLOCK_BYTES = 1024;
    private const int SB_BLOCK_SIZE = 0x0C;
    private const int SB_MAXLEN = 0x10;
    private const int SB_FIRST = 0x14;
    private const int SB_SEQUENCE = 0x18;
    private const int SB_START = 0x1C;
    private const int SB_INCOMPAT = 0x28;
    private const int SB_UUID = 0x30;
    private const int SB_CHECKSUM_TYPE = 0x50;
    private const int SB_CHECKSUM = 0xFC;
    private const int COMMIT_CHECKSUM = 16;
    private const int COMMIT_SECONDS = 48;
    private const int COMMIT_NANOSECONDS = 56;

    #endregion

    #region Fields

    private readonly Ext4Volume m_volume;
    private readonly Ext4DataMap m_map;
    private readonly byte[] m_superblock;
    private readonly uint m_blockSize;
    private readonly uint m_length;
    private readonly uint m_first;
    private readonly uint m_seed;

    #endregion

    #region Constructors

    private Ext4Journal(Ext4Volume volume, Ext4DataMap map, byte[] superblock)
    {
        m_volume = volume;
        m_map = map;
        m_superblock = superblock;
        m_blockSize = Read(SB_BLOCK_SIZE);
        m_length = Read(SB_MAXLEN);
        m_first = Read(SB_FIRST);
        m_seed = Ext4Checksums.Crc32C(~0U, superblock.AsSpan(SB_UUID, 16));
    }

    #endregion

    #region Functions

    /// <summary>
    /// Opens the volume's journal: its inode's map and its superblock, checked.
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The journal.</returns>
    /// <exception cref="FileSystemException">The journal uses what the provider does not serve (Unsupported) or is damaged (Corrupt).</exception>
    public static async ValueTask<Ext4Journal> OpenAsync(Ext4Volume volume, CancellationToken cancellationToken)
    {
        var inode = await volume.ReadInodeAsync(volume.Superblock.JournalInode, cancellationToken).ConfigureAwait(false);
        var map = await Ext4DataMap.ReadAsync(volume, inode, cancellationToken).ConfigureAwait(false);
        var block = new byte[volume.Superblock.BlockSize];
        await ReadLogAsync(volume, map, 0, block, cancellationToken).ConfigureAwait(false);
        var journal = new Ext4Journal(volume, map, block[..SUPERBLOCK_BYTES]);
        journal.Check(inode.Size);
        return journal;
    }

    /// <summary>
    /// Replays the committed transactions the log holds and empties it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the replay.</param>
    /// <returns>The transactions replayed.</returns>
    public async ValueTask<int> ReplayAsync(CancellationToken cancellationToken)
    {
        if (IsEmpty)
            return 0;
        var start = Read(SB_START);
        var first = Read(SB_SEQUENCE);
        // Scan: the transactions whose commit block is there and sound.
        var end = first;
        var revoked = new Dictionary<ulong, uint>();
        var replay = new List<(ulong Target, uint LogBlock, uint Flags, uint Checksum, uint Sequence)>();
        var block = start;
        var buffer = new byte[m_blockSize];
        var pending = new List<(ulong Target, uint LogBlock, uint Flags, uint Checksum, uint Sequence)>();
        var pendingRevokes = new List<ulong>();
        for (var steps = 0U; steps < m_length; steps++)
        {
            await ReadLogAsync(m_volume, m_map, block, buffer, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32BigEndian(buffer) != MAGIC || BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(8)) != end)
                break;
            var type = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4));
            if (type == DESCRIPTOR)
            {
                if (!TailIsSound(buffer))
                    break;
                foreach (var (target, flags, checksum) in Tags(buffer))
                {
                    block = Next(block);
                    pending.Add((target, block, flags, checksum, end));
                }
            }
            else if (type == REVOKE)
            {
                if (!TailIsSound(buffer))
                    break;
                pendingRevokes.AddRange(Revokes(buffer));
            }
            else if (type == COMMIT)
            {
                if (!CommitIsSound(buffer))
                    break;
                replay.AddRange(pending);
                foreach (var target in pendingRevokes)
                    revoked[target] = end;
                pending.Clear();
                pendingRevokes.Clear();
                end++;
            }
            else
            {
                break;
            }
            block = Next(block);
        }
        // Replay: each logged block of a committed transaction that no revoke of that transaction or a later one names.
        var data = new byte[m_blockSize];
        foreach (var (target, logBlock, flags, checksum, sequence) in replay)
        {
            if (revoked.TryGetValue(target, out var revokedIn) && revokedIn >= sequence)
                continue;
            await ReadLogAsync(m_volume, m_map, logBlock, data, cancellationToken).ConfigureAwait(false);
            if (!DataIsSound(data, checksum, sequence))
                continue; // the kernel skips a logged block that fails its checksum
            if ((flags & TAG_ESCAPE) != 0)
                BinaryPrimitives.WriteUInt32BigEndian(data, MAGIC);
            if (target >= m_volume.Superblock.BlocksCount)
                throw new FileSystemException(FileSystemError.Corrupt, $"The journal logs block {target}, beyond the volume.");
            await m_volume.Disk.WriteAsync((long)target * m_volume.Superblock.BlockSize, data, cancellationToken).ConfigureAwait(false);
        }
        await m_volume.Disk.FlushAsync(cancellationToken).ConfigureAwait(false);
        await WriteSuperblockAsync(0, end, cancellationToken).ConfigureAwait(false);
        await m_volume.Disk.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (int)(end - first);
    }

    /// <summary>
    /// Commits one transaction: the log, the journal's superblock pointing at it, the blocks in place, the journal empty.
    /// </summary>
    /// <param name="blocks">The metadata blocks, in ascending order.</param>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>When the blocks are in place and the journal is empty.</returns>
    /// <exception cref="FileSystemException">The transaction needs more blocks than the log holds (TooLarge).</exception>
    public async ValueTask CommitAsync(IReadOnlyList<(ulong Block, byte[] Bytes)> blocks, CancellationToken cancellationToken)
    {
        if (blocks.Count == 0)
            return;
        var sequence = Read(SB_SEQUENCE);
        var log = Log(blocks, sequence);
        if (log.Count > m_length - m_first)
            throw new FileSystemException(FileSystemError.TooLarge, $"A change of {blocks.Count} blocks needs {log.Count} journal blocks of {m_length - m_first}.");
        await m_volume.Disk.FlushAsync(cancellationToken).ConfigureAwait(false); // ordered: the data before the metadata
        for (var i = 0; i < log.Count; i++)
            await WriteLogAsync(m_first + (uint)i, log[i], cancellationToken).ConfigureAwait(false);
        await WriteSuperblockAsync(m_first, sequence, cancellationToken).ConfigureAwait(false);
        await m_volume.Disk.FlushAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (block, bytes) in blocks)
            await m_volume.Disk.WriteAsync((long)block * m_volume.Superblock.BlockSize, bytes, cancellationToken).ConfigureAwait(false);
        await m_volume.Disk.FlushAsync(cancellationToken).ConfigureAwait(false);
        await WriteSuperblockAsync(0, sequence + 1, cancellationToken).ConfigureAwait(false);
        await m_volume.Disk.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // The log of one transaction: descriptor blocks, each followed by the blocks it tags, then the commit block.
    private List<byte[]> Log(IReadOnlyList<(ulong Block, byte[] Bytes)> blocks, uint sequence)
    {
        var log = new List<byte[]>();
        var size = (int)m_blockSize;
        for (var next = 0; next < blocks.Count;)
        {
            var descriptor = Header(DESCRIPTOR, sequence);
            log.Add(descriptor);
            var at = HEADER;
            var tags = new List<int>();
            while (next < blocks.Count && at + 16 + (tags.Count == 0 ? 16 : 0) <= size - 4)
            {
                var (target, bytes) = blocks[next++];
                var copy = (byte[])bytes.Clone();
                var flags = tags.Count == 0 ? 0U : TAG_SAME_UUID;
                if (BinaryPrimitives.ReadUInt32BigEndian(copy) == MAGIC)
                {
                    flags |= TAG_ESCAPE;
                    copy.AsSpan(0, 4).Clear();
                }
                var tag = descriptor.AsSpan(at, 16);
                BinaryPrimitives.WriteUInt32BigEndian(tag, (uint)target);
                BinaryPrimitives.WriteUInt32BigEndian(tag[4..], flags);
                BinaryPrimitives.WriteUInt32BigEndian(tag[8..], (uint)(target >> 32));
                BinaryPrimitives.WriteUInt32BigEndian(tag[12..], Ext4Checksums.Crc32C(Ext4Checksums.Crc32C(m_seed, BigEndian(sequence)), copy));
                tags.Add(at);
                at += 16;
                if ((flags & TAG_SAME_UUID) == 0)
                {
                    m_superblock.AsSpan(SB_UUID, 16).CopyTo(descriptor.AsSpan(at));
                    at += 16;
                }
                log.Add(copy);
            }
            var last = descriptor.AsSpan(tags[^1] + 4);
            BinaryPrimitives.WriteUInt32BigEndian(last, BinaryPrimitives.ReadUInt32BigEndian(last) | TAG_LAST);
            BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(size - 4), Ext4Checksums.Crc32C(m_seed, descriptor)); // over the block, tail zero
        }
        var commit = Header(COMMIT, sequence);
        var now = DateTimeOffset.UtcNow;
        BinaryPrimitives.WriteUInt64BigEndian(commit.AsSpan(COMMIT_SECONDS), (ulong)now.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32BigEndian(commit.AsSpan(COMMIT_NANOSECONDS), (uint)(now.Ticks % TimeSpan.TicksPerSecond * 100));
        BinaryPrimitives.WriteUInt32BigEndian(commit.AsSpan(COMMIT_CHECKSUM), Ext4Checksums.Crc32C(m_seed, commit));
        log.Add(commit);
        return log;
    }

    // The tags of a descriptor block: each logged block's target, flags and checksum, in the log's order.
    private List<(ulong Target, uint Flags, uint Checksum)> Tags(byte[] descriptor)
    {
        var tags = new List<(ulong, uint, uint)>();
        var incompat = Read(SB_INCOMPAT);
        var v3 = (incompat & INCOMPAT_CSUM_V3) != 0;
        var wide = (incompat & INCOMPAT_64BIT) != 0;
        var tagBytes = v3 ? 16 : wide ? 12 : 8;
        var limit = descriptor.Length - ((incompat & (INCOMPAT_CSUM_V2 | INCOMPAT_CSUM_V3)) != 0 ? 4 : 0);
        for (var at = HEADER; at + tagBytes <= limit;)
        {
            var tag = descriptor.AsSpan(at);
            var low = BinaryPrimitives.ReadUInt32BigEndian(tag);
            uint flags, checksum;
            ulong high;
            if (v3)
            {
                flags = BinaryPrimitives.ReadUInt32BigEndian(tag[4..]);
                high = wide ? BinaryPrimitives.ReadUInt32BigEndian(tag[8..]) : 0;
                checksum = BinaryPrimitives.ReadUInt32BigEndian(tag[12..]);
            }
            else
            {
                checksum = BinaryPrimitives.ReadUInt16BigEndian(tag[4..]);
                flags = BinaryPrimitives.ReadUInt16BigEndian(tag[6..]);
                high = wide ? BinaryPrimitives.ReadUInt32BigEndian(tag[8..]) : 0;
            }
            tags.Add((high << 32 | low, flags, checksum));
            at += tagBytes + ((flags & TAG_SAME_UUID) == 0 ? 16 : 0);
            if ((flags & TAG_LAST) != 0)
                break;
        }
        return tags;
    }

    // The blocks a revoke block names.
    private List<ulong> Revokes(byte[] block)
    {
        var wide = (Read(SB_INCOMPAT) & INCOMPAT_64BIT) != 0;
        var count = Math.Min((int)BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(HEADER)), block.Length);
        var revokes = new List<ulong>();
        for (var at = 16; at + (wide ? 8 : 4) <= count; at += wide ? 8 : 4)
            revokes.Add(wide ? BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at)));
        return revokes;
    }

    private bool HasChecksums => (Read(SB_INCOMPAT) & (INCOMPAT_CSUM_V2 | INCOMPAT_CSUM_V3)) != 0;

    // A descriptor's or a revoke block's tail: CRC-32C of the whole block with the tail's checksum zero.
    private bool TailIsSound(byte[] block)
    {
        if (!HasChecksums)
            return true;
        var copy = (byte[])block.Clone();
        copy.AsSpan(copy.Length - 4).Clear();
        return Ext4Checksums.Crc32C(m_seed, copy) == BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(block.Length - 4));
    }

    private bool CommitIsSound(byte[] block)
    {
        if (!HasChecksums)
            return true;
        var copy = (byte[])block.Clone();
        var stored = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(COMMIT_CHECKSUM));
        copy.AsSpan(COMMIT_CHECKSUM, 4).Clear();
        copy[HEADER] = copy[HEADER + 1] = 0; // the checksum's type and size, zero when it was computed
        return Ext4Checksums.Crc32C(m_seed, copy) == stored;
    }

    private bool DataIsSound(byte[] data, uint checksum, uint sequence)
    {
        var incompat = Read(SB_INCOMPAT);
        if ((incompat & (INCOMPAT_CSUM_V2 | INCOMPAT_CSUM_V3)) == 0)
            return true;
        var crc = Ext4Checksums.Crc32C(Ext4Checksums.Crc32C(m_seed, BigEndian(sequence)), data);
        return (incompat & INCOMPAT_CSUM_V3) != 0 ? crc == checksum : (crc & 0xFFFF) == checksum;
    }

    private uint Next(uint block) => block + 1 >= m_length ? m_first : block + 1;

    private byte[] Header(uint type, uint sequence)
    {
        var block = new byte[m_blockSize];
        BinaryPrimitives.WriteUInt32BigEndian(block, MAGIC);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), type);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8), sequence);
        return block;
    }

    private void Check(long inodeBytes)
    {
        var type = Read(4);
        var incompat = type == SUPERBLOCK_V2 ? Read(SB_INCOMPAT) : 0;
        if (Read(0) != MAGIC || type is not (SUPERBLOCK_V1 or SUPERBLOCK_V2) || m_blockSize != m_volume.Superblock.BlockSize ||
            m_first == 0 || m_first >= m_length || (long)m_length * m_blockSize > inodeBytes || Read(SB_START) >= m_length)
            throw new FileSystemException(FileSystemError.Corrupt, $"The journal's superblock is of type {type}, blocks of {m_blockSize}, log {m_first} " +
                $"to {m_length} in an inode of {inodeBytes} bytes.");
        if ((incompat & ~INCOMPAT_SERVED) != 0)
            throw new FileSystemException(FileSystemError.Unsupported, $"The journal uses incompatible features {incompat & ~INCOMPAT_SERVED:x}.");
        if ((incompat & (INCOMPAT_CSUM_V2 | INCOMPAT_CSUM_V3)) != 0 && Ext4Checksums.Crc32C(~0U, SealedCopy()) != Read(SB_CHECKSUM))
            throw new FileSystemException(FileSystemError.Corrupt, "The journal's superblock fails its checksum.");
    }

    // The superblock with its checksum zero, as the checksum covers it.
    private byte[] SealedCopy()
    {
        var copy = (byte[])m_superblock.Clone();
        copy.AsSpan(SB_CHECKSUM, 4).Clear();
        return copy;
    }

    // The journal's superblock with a start and a sequence, and the features a commit writes: CSUM_V3, and 64BIT on a
    // 64-bit volume.
    private async ValueTask WriteSuperblockAsync(uint start, uint sequence, CancellationToken cancellationToken)
    {
        BinaryPrimitives.WriteUInt32BigEndian(m_superblock.AsSpan(4), SUPERBLOCK_V2);
        BinaryPrimitives.WriteUInt32BigEndian(m_superblock.AsSpan(SB_START), start);
        BinaryPrimitives.WriteUInt32BigEndian(m_superblock.AsSpan(SB_SEQUENCE), sequence);
        var incompat = (Read(SB_INCOMPAT) & ~INCOMPAT_CSUM_V2) | INCOMPAT_CSUM_V3 | (m_volume.Superblock.Has64Bit ? INCOMPAT_64BIT : 0);
        BinaryPrimitives.WriteUInt32BigEndian(m_superblock.AsSpan(SB_INCOMPAT), incompat);
        m_superblock[SB_CHECKSUM_TYPE] = CRC32C;
        BinaryPrimitives.WriteUInt32BigEndian(m_superblock.AsSpan(SB_CHECKSUM), Ext4Checksums.Crc32C(~0U, SealedCopy()));
        var block = new byte[m_blockSize];
        await ReadLogAsync(m_volume, m_map, 0, block, cancellationToken).ConfigureAwait(false);
        m_superblock.CopyTo(block, 0);
        await WriteLogAsync(0, block, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteLogAsync(uint logical, byte[] bytes, CancellationToken cancellationToken)
    {
        var (physical, _) = m_map.Find(logical);
        if (physical == 0)
            throw new FileSystemException(FileSystemError.Corrupt, $"The journal's block {logical} is a hole.");
        await m_volume.Disk.WriteAsync((long)physical * m_volume.Superblock.BlockSize, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReadLogAsync(Ext4Volume volume, Ext4DataMap map, uint logical, byte[] buffer, CancellationToken cancellationToken)
    {
        var (physical, _) = map.Find(logical);
        if (physical == 0)
            throw new FileSystemException(FileSystemError.Corrupt, $"The journal's block {logical} is a hole.");
        await volume.Disk.ReadAsync((long)physical * volume.Superblock.BlockSize, buffer, cancellationToken).ConfigureAwait(false);
    }

    private uint Read(int offset) => BinaryPrimitives.ReadUInt32BigEndian(m_superblock.AsSpan(offset));

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Whether the log holds nothing to replay.
    /// </summary>
    public bool IsEmpty => Read(SB_START) == 0;

    #endregion
}
