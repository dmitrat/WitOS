using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.Storage;

/// <summary>
/// A GUID partition table read from a disk or written to one (UEFI 2.10, §5.3; storage document §24, plan steps D3.1 and
/// D3.2). The primary header at block 1 is checked whole — its signature, revision, size, its own CRC-32, its place, the
/// usable range and the entry array with its CRC-32 — and the backup header at the last block serves when the primary
/// fails. Every used entry lies within the usable range, and no two overlap. Nothing of the table is trusted before it
/// is checked (§137).
/// </summary>
public sealed class GptPartitionTable
{
    #region Constants

    /// <summary>
    /// The header's signature, "EFI PART".
    /// </summary>
    public const ulong SIGNATURE = 0x5452415020494645;

    /// <summary>
    /// The header's revision 1.0.
    /// </summary>
    public const uint REVISION = 0x00010000;

    /// <summary>
    /// The header's size from revision 1.0.
    /// </summary>
    public const int HEADER_SIZE = 92;

    /// <summary>
    /// The entries of a table WriteAsync makes, the count UEFI requires room for.
    /// </summary>
    public const int ENTRY_COUNT = 128;

    /// <summary>
    /// The bytes of an entry WriteAsync makes.
    /// </summary>
    public const int ENTRY_SIZE = 128;

    private const int ENTRY_MINIMUM = 128;
    private const int ENTRIES_LIMIT = 1024 * 1024;
    private const int NAME_CHARACTERS = 36;
    private const int MBR_BYTES = 512;

    #endregion

    #region Constructors

    private GptPartitionTable(Guid disk, long firstUsable, long lastUsable, IReadOnlyList<GptPartition> partitions, bool fromBackup)
    {
        DiskId = disk;
        FirstUsableBlock = firstUsable;
        LastUsableBlock = lastUsable;
        Partitions = partitions;
        FromBackup = fromBackup;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads the table of a disk.
    /// </summary>
    /// <param name="disk">The disk; its logical block is the table's.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The table.</returns>
    /// <exception cref="InvalidDataException">Neither the primary nor the backup table is valid.</exception>
    public static async ValueTask<GptPartitionTable> ReadAsync(IBlockStorage disk, CancellationToken cancellationToken = default)
    {
        var blocks = disk.Info.Length / disk.Info.LogicalBlockSize;
        if (blocks < 3)
            throw new InvalidDataException($"A disk of {blocks} blocks holds no GUID partition table.");
        var primary = await TryReadAsync(disk, 1, blocks, cancellationToken).ConfigureAwait(false);
        if (primary.Table is not null)
            return primary.Table;
        var backup = await TryReadAsync(disk, blocks - 1, blocks, cancellationToken).ConfigureAwait(false);
        if (backup.Table is not null)
            return backup.Table;
        throw new InvalidDataException($"The disk has no valid GUID partition table: the primary {primary.Problem}, the backup {backup.Problem}.");
    }

    /// <summary>
    /// Writes a GUID partition table to a disk (UEFI 2.10, §5.2.3 and §5.3): a protective MBR at block 0, the primary
    /// header at block 1 with ENTRY_COUNT entries after it, the backup entries and header at the end, then a flush; the
    /// table is read back as ReadAsync checks it. Each partition takes the entry its index names and lies within the
    /// usable blocks, apart from the others.
    /// </summary>
    /// <param name="disk">The disk, with blocks of at least 512 bytes; what it held of the table's blocks is replaced.</param>
    /// <param name="diskId">The disk's GUID.</param>
    /// <param name="partitions">The partitions.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The table as read back.</returns>
    /// <exception cref="ArgumentException">The disk is too small for a table, or a partition does not fit it.</exception>
    public static async ValueTask<GptPartitionTable> WriteAsync(IBlockStorage disk, Guid diskId, IReadOnlyList<GptPartition> partitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partitions);
        var size = disk.Info.LogicalBlockSize;
        var blocks = disk.Info.Length / size;
        var entryBlocks = (ENTRY_COUNT * ENTRY_SIZE + size - 1) / size;
        var firstUsable = 2L + entryBlocks;
        var lastUsable = blocks - 2 - entryBlocks;
        if (size < MBR_BYTES || firstUsable > lastUsable)
            throw new ArgumentException($"A disk of {blocks} blocks of {size} bytes holds no GUID partition table.", nameof(disk));
        var entries = new byte[entryBlocks * size];
        var used = new bool[ENTRY_COUNT];
        for (var i = 0; i < partitions.Count; i++)
        {
            var partition = partitions[i];
            if (partition.Index is < 0 or >= ENTRY_COUNT || used[partition.Index] || partition.Type == Guid.Empty ||
                partition.FirstBlock < firstUsable || partition.LastBlock > lastUsable || partition.FirstBlock > partition.LastBlock ||
                partition.Name.Length > NAME_CHARACTERS)
                throw new ArgumentException($"Partition {partition.Index} on blocks {partition.FirstBlock} to {partition.LastBlock} does not fit " +
                    $"the usable blocks {firstUsable} to {lastUsable}.", nameof(partitions));
            for (var j = 0; j < i; j++)
            {
                if (partition.FirstBlock <= partitions[j].LastBlock && partitions[j].FirstBlock <= partition.LastBlock)
                    throw new ArgumentException($"Partitions {partitions[j].Index} and {partition.Index} overlap.", nameof(partitions));
            }
            used[partition.Index] = true;
            var entry = entries.AsSpan(partition.Index * ENTRY_SIZE, ENTRY_SIZE);
            partition.Type.TryWriteBytes(entry);
            partition.Id.TryWriteBytes(entry[16..]);
            BinaryPrimitives.WriteInt64LittleEndian(entry[32..], partition.FirstBlock);
            BinaryPrimitives.WriteInt64LittleEndian(entry[40..], partition.LastBlock);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[48..], partition.Attributes);
            Encoding.Unicode.GetBytes(partition.Name, entry[56..]);
        }
        var entriesCrc = Crc32.Compute(entries.AsSpan(0, ENTRY_COUNT * ENTRY_SIZE));
        var backupEntries = blocks - 1 - entryBlocks;
        await disk.WriteAsync(0, ProtectiveMbr(size, blocks), cancellationToken: cancellationToken).ConfigureAwait(false);
        await disk.WriteAsync(size, Header(size, 1, blocks - 1, firstUsable, lastUsable, diskId, 2, entriesCrc),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await disk.WriteAsync(2L * size, entries, cancellationToken: cancellationToken).ConfigureAwait(false);
        await disk.WriteAsync(backupEntries * size, entries, cancellationToken: cancellationToken).ConfigureAwait(false);
        await disk.WriteAsync((blocks - 1) * size, Header(size, blocks - 1, 1, firstUsable, lastUsable, diskId, backupEntries, entriesCrc),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await disk.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(disk, cancellationToken).ConfigureAwait(false);
    }

    // The protective MBR (UEFI 2.10, §5.2.3): one partition of type 0xEE from block 1 over the disk, at most 2^32 - 1
    // blocks, and the boot signature.
    private static byte[] ProtectiveMbr(int size, long blocks)
    {
        var mbr = new byte[size];
        var record = mbr.AsSpan(446, 16);
        record[2] = 0x02; // starting CHS 0x000200
        record[4] = 0xEE;
        record[5] = record[6] = record[7] = 0xFF; // ending CHS
        BinaryPrimitives.WriteUInt32LittleEndian(record[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)Math.Min(blocks - 1, uint.MaxValue));
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        return mbr;
    }

    private static byte[] Header(int size, long mine, long alternate, long firstUsable, long lastUsable, Guid diskId, long entriesAt,
        uint entriesCrc)
    {
        var block = new byte[size];
        var header = block.AsSpan(0, HEADER_SIZE);
        BinaryPrimitives.WriteUInt64LittleEndian(header, SIGNATURE);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], REVISION);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], HEADER_SIZE);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], mine);
        BinaryPrimitives.WriteInt64LittleEndian(header[32..], alternate);
        BinaryPrimitives.WriteInt64LittleEndian(header[40..], firstUsable);
        BinaryPrimitives.WriteInt64LittleEndian(header[48..], lastUsable);
        diskId.TryWriteBytes(header[56..]);
        BinaryPrimitives.WriteInt64LittleEndian(header[72..], entriesAt);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], ENTRY_COUNT);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], ENTRY_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(header[88..], entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32.Compute(header));
        return block;
    }

    // The table whose header is at the block given, or the reason it is not valid.
    private static async ValueTask<(GptPartitionTable? Table, string Problem)> TryReadAsync(IBlockStorage disk, long at, long blocks,
        CancellationToken cancellationToken)
    {
        var size = disk.Info.LogicalBlockSize;
        var header = new byte[size];
        await disk.ReadAsync(at * size, header, cancellationToken).ConfigureAwait(false);
        var span = header.AsSpan();
        if (BinaryPrimitives.ReadUInt64LittleEndian(span) != SIGNATURE)
            return (null, "has no signature");
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[8..]) != REVISION || headerSize < HEADER_SIZE || headerSize > size)
            return (null, $"has revision {BinaryPrimitives.ReadUInt32LittleEndian(span[8..]):x8} and size {headerSize}");
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 0);
        if (Crc32.Compute(span[..(int)headerSize]) != crc)
            return (null, "fails its CRC-32");
        var mine = BinaryPrimitives.ReadInt64LittleEndian(span[24..]);
        var firstUsable = BinaryPrimitives.ReadInt64LittleEndian(span[40..]);
        var lastUsable = BinaryPrimitives.ReadInt64LittleEndian(span[48..]);
        var diskId = new Guid(span.Slice(56, 16));
        var entriesAt = BinaryPrimitives.ReadInt64LittleEndian(span[72..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(span[80..]);
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(span[84..]);
        var entriesCrc = BinaryPrimitives.ReadUInt32LittleEndian(span[88..]);
        if (mine != at || firstUsable < 2 || lastUsable >= blocks - 1 || firstUsable > lastUsable)
            return (null, $"names block {mine} and the usable blocks {firstUsable} to {lastUsable} of {blocks}");
        if (entrySize < ENTRY_MINIMUM || entrySize % 8 != 0 || (long)count * entrySize > ENTRIES_LIMIT)
            return (null, $"has {count} entries of {entrySize} bytes");
        var entryBytes = (int)(count * entrySize);
        var entryBlocks = (entryBytes + size - 1) / size;
        if (entriesAt < 2 || entriesAt > blocks - entryBlocks || (entriesAt <= lastUsable && entriesAt + entryBlocks > firstUsable))
            return (null, $"places its entries at block {entriesAt}");
        var entries = new byte[entryBlocks * size];
        await disk.ReadAsync(entriesAt * size, entries, cancellationToken).ConfigureAwait(false);
        if (Crc32.Compute(entries.AsSpan(0, entryBytes)) != entriesCrc)
            return (null, "has entries that fail their CRC-32");
        var partitions = new List<GptPartition>();
        for (var i = 0; i < count; i++)
        {
            var entry = entries.AsSpan(i * (int)entrySize, (int)entrySize);
            var type = new Guid(entry[..16]);
            if (type == Guid.Empty)
                continue;
            var first = BinaryPrimitives.ReadInt64LittleEndian(entry[32..]);
            var last = BinaryPrimitives.ReadInt64LittleEndian(entry[40..]);
            if (first < firstUsable || last > lastUsable || first > last)
                return (null, $"has entry {i} on blocks {first} to {last}, outside {firstUsable} to {lastUsable}");
            foreach (var other in partitions)
            {
                if (first <= other.LastBlock && other.FirstBlock <= last)
                    return (null, $"has entries {other.Index} and {i} that overlap");
            }
            var name = Encoding.Unicode.GetString(entry.Slice(56, 72)).TrimEnd('\0');
            partitions.Add(new GptPartition(i, type, new Guid(entry.Slice(16, 16)), first, last, BinaryPrimitives.ReadUInt64LittleEndian(entry[48..]), name));
        }
        return (new GptPartitionTable(diskId, firstUsable, lastUsable, partitions, at != 1), "");
    }

    #endregion

    #region Properties

    /// <summary>
    /// The disk's GUID.
    /// </summary>
    public Guid DiskId { get; }

    /// <summary>
    /// The first block a partition may use.
    /// </summary>
    public long FirstUsableBlock { get; }

    /// <summary>
    /// The last block a partition may use.
    /// </summary>
    public long LastUsableBlock { get; }

    /// <summary>
    /// The used entries, in the table's order.
    /// </summary>
    public IReadOnlyList<GptPartition> Partitions { get; }

    /// <summary>
    /// Whether the primary table failed and the backup served.
    /// </summary>
    public bool FromBackup { get; }

    #endregion
}
