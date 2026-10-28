using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.Storage;

/// <summary>
/// The master boot record's partition table (plan step E1): the four primary entries of block 0 and the logical
/// partitions an extended entry chains through its extended boot records, as Linux's msdos partition parser reads them.
/// A disk whose block 0 is a protective MBR, a FAT or exFAT boot sector, or holds an entry whose active flag is neither
/// 0x00 nor 0x80 has no MBR table; every partition lies within the disk, apart from the others.
/// </summary>
public sealed class MbrPartitionTable
{
    #region Constants

    /// <summary>
    /// The partition type of a GUID disk's protective entry.
    /// </summary>
    public const byte PROTECTIVE = 0xEE;

    private const int RECORD_BYTES = 512;
    private const int ENTRIES_AT = 446;
    private const int ENTRY_BYTES = 16;
    private const int PRIMARY_ENTRIES = 4;
    private const int SIGNATURE_AT = 510;
    private const int DISK_SIGNATURE_AT = 440;
    private const int FIRST_LOGICAL = 5;

    // The extended boot records a chain may hold: Linux stops a chain at a fixed count too.
    private const int MAX_LOGICAL = 64;

    #endregion

    #region Constructors

    private MbrPartitionTable(uint diskSignature, IReadOnlyList<MbrPartition> partitions)
    {
        DiskSignature = diskSignature;
        Partitions = partitions;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads the table of a disk.
    /// </summary>
    /// <param name="disk">The disk; its logical block is the table's.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The table.</returns>
    /// <exception cref="InvalidDataException">Block 0 holds no MBR partition table, or the table describes partitions outside
    /// the disk or over one another.</exception>
    public static async ValueTask<MbrPartitionTable> ReadAsync(IBlockStorage disk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var size = disk.Info.LogicalBlockSize;
        var blocks = disk.Info.Length / size;
        if (size < RECORD_BYTES || blocks < 2)
            throw new InvalidDataException($"A disk of {blocks} blocks of {size} bytes holds no MBR partition table.");
        var record = await ReadBlockAsync(disk, 0, cancellationToken).ConfigureAwait(false);
        if (!IsTable(record, out var problem))
            throw new InvalidDataException($"Block 0 holds no MBR partition table: {problem}.");
        var partitions = new List<MbrPartition>();
        MbrPartition? extended = null;
        for (var i = 0; i < PRIMARY_ENTRIES; i++)
        {
            var entry = Entry(record, i, 0, i + 1);
            if (entry is null)
                continue;
            if (entry.Type == PROTECTIVE)
                throw new InvalidDataException("Block 0 is the protective MBR of a GUID partition table.");
            if (IsExtended(entry.Type))
            {
                if (extended is not null)
                    throw new InvalidDataException("Block 0 names two extended partitions.");
                extended = entry;
            }
            else
                partitions.Add(entry);
        }
        if (extended is not null)
            partitions.AddRange(await LogicalAsync(disk, extended, cancellationToken).ConfigureAwait(false));
        var all = extended is null ? partitions : [.. partitions, extended];
        foreach (var partition in all)
        {
            if (partition.FirstBlock < 1 || partition.FirstBlock + partition.BlockCount > blocks)
                throw new InvalidDataException($"Partition {partition.Index} on blocks {partition.FirstBlock} to " +
                    $"{partition.FirstBlock + partition.BlockCount - 1} lies outside the disk's {blocks} blocks.");
        }
        Overlaps(partitions);
        Overlaps([.. all.Where(partition => partition.Index <= PRIMARY_ENTRIES)]);
        return new MbrPartitionTable(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(DISK_SIGNATURE_AT)),
            [.. partitions.OrderBy(partition => partition.Index)]);
    }

    /// <summary>
    /// Writes an MBR with primary partitions to a disk, as a tool lays a removable drive out, then flushes and reads the
    /// table back as ReadAsync checks it. Block 0's boot code stays zero.
    /// </summary>
    /// <param name="disk">The disk, with blocks of at least 512 bytes.</param>
    /// <param name="diskSignature">The disk's 32-bit signature.</param>
    /// <param name="partitions">At most four primary partitions, by their index 1–4.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The table as read back.</returns>
    /// <exception cref="ArgumentException">A partition is no primary one, does not fit the disk or a 32-bit address, or
    /// overlaps another.</exception>
    public static async ValueTask<MbrPartitionTable> WriteAsync(IBlockStorage disk, uint diskSignature, IReadOnlyList<MbrPartition> partitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partitions);
        var size = disk.Info.LogicalBlockSize;
        var blocks = disk.Info.Length / size;
        if (size < RECORD_BYTES || partitions.Count > PRIMARY_ENTRIES)
            throw new ArgumentException($"A disk of blocks of {size} bytes takes at most four primary partitions.", nameof(partitions));
        var record = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(DISK_SIGNATURE_AT), diskSignature);
        foreach (var partition in partitions)
        {
            if (partition.Index is < 1 or > PRIMARY_ENTRIES || partition.Type == 0 || partition.Type == PROTECTIVE || IsExtended(partition.Type) ||
                partition.FirstBlock < 1 || partition.BlockCount < 1 || partition.FirstBlock + partition.BlockCount > blocks ||
                partition.FirstBlock + partition.BlockCount > uint.MaxValue ||
                partitions.Count(other => other.Index == partition.Index) > 1)
                throw new ArgumentException($"Partition {partition.Index} of type 0x{partition.Type:X2} on {partition.BlockCount} blocks from " +
                    $"{partition.FirstBlock} does not fit a disk of {blocks} blocks.", nameof(partitions));
            var entry = record.AsSpan(ENTRIES_AT + (partition.Index - 1) * ENTRY_BYTES, ENTRY_BYTES);
            entry[0] = partition.IsBootable ? (byte)0x80 : (byte)0x00;
            // CHS addresses beyond what they can hold, as tools write them for LBA disks.
            entry[1] = entry[5] = 0xFE;
            entry[2] = entry[3] = entry[6] = entry[7] = 0xFF;
            entry[4] = partition.Type;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)partition.FirstBlock);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)partition.BlockCount);
        }
        try
        {
            Overlaps(partitions);
        }
        catch (InvalidDataException overlap)
        {
            throw new ArgumentException(overlap.Message, nameof(partitions));
        }
        record[SIGNATURE_AT] = 0x55;
        record[SIGNATURE_AT + 1] = 0xAA;
        await disk.WriteAsync(0, record, cancellationToken: cancellationToken).ConfigureAwait(false);
        await disk.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(disk, cancellationToken).ConfigureAwait(false);
    }

    // Whether a block 0 holds an MBR table: the boot signature, active flags of 0x00 or 0x80 alone, and no FAT or exFAT
    // boot sector (which carries the signature too, with boot code where the entries would be).
    private static bool IsTable(byte[] record, out string problem)
    {
        if (record[SIGNATURE_AT] != 0x55 || record[SIGNATURE_AT + 1] != 0xAA)
        {
            problem = "no boot signature";
            return false;
        }
        if (record[0] is 0xEB or 0xE9 && (Text(record, 3, 8) == "EXFAT   " || Text(record, 54, 3) == "FAT" || Text(record, 82, 5) == "FAT32"))
        {
            problem = "it is a FAT or exFAT boot sector";
            return false;
        }
        for (var i = 0; i < PRIMARY_ENTRIES; i++)
        {
            var active = record[ENTRIES_AT + i * ENTRY_BYTES];
            if (active is not (0x00 or 0x80))
            {
                problem = $"entry {i + 1} has the active flag 0x{active:X2}";
                return false;
            }
        }
        problem = "";
        return true;
    }

    // The logical partitions an extended one chains: each extended boot record's first entry is a logical partition from
    // that record, its second the next record from the extended partition's start.
    private static async ValueTask<List<MbrPartition>> LogicalAsync(IBlockStorage disk, MbrPartition extended, CancellationToken cancellationToken)
    {
        var logical = new List<MbrPartition>();
        var seen = new HashSet<long>();
        var at = extended.FirstBlock;
        while (true)
        {
            if (!seen.Add(at) || logical.Count == MAX_LOGICAL)
                throw new InvalidDataException($"The extended partition's chain loops or holds more than {MAX_LOGICAL} records.");
            if (at < extended.FirstBlock || at >= extended.FirstBlock + extended.BlockCount)
                throw new InvalidDataException($"An extended boot record at block {at} lies outside the extended partition.");
            var record = await ReadBlockAsync(disk, at, cancellationToken).ConfigureAwait(false);
            if (record[SIGNATURE_AT] != 0x55 || record[SIGNATURE_AT + 1] != 0xAA)
                throw new InvalidDataException($"The extended boot record at block {at} has no boot signature.");
            if (Entry(record, 0, at, FIRST_LOGICAL + logical.Count) is { } partition)
            {
                if (IsExtended(partition.Type) || partition.FirstBlock + partition.BlockCount > extended.FirstBlock + extended.BlockCount)
                    throw new InvalidDataException($"The logical partition of the record at block {at} lies outside the extended partition.");
                logical.Add(partition);
            }
            if (Entry(record, 1, extended.FirstBlock, 0) is not { } next)
                return logical;
            at = next.FirstBlock;
        }
    }

    // An entry of a record, null when empty; its first block relative to the base given.
    private static MbrPartition? Entry(byte[] record, int index, long based, int number)
    {
        var entry = record.AsSpan(ENTRIES_AT + index * ENTRY_BYTES, ENTRY_BYTES);
        var type = entry[4];
        var first = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
        return type == 0 || count == 0 ? null : new MbrPartition(number, type, based + first, count, entry[0] == 0x80);
    }

    private static void Overlaps(IReadOnlyList<MbrPartition> partitions)
    {
        for (var i = 0; i < partitions.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                var (a, b) = (partitions[i], partitions[j]);
                if (a.FirstBlock < b.FirstBlock + b.BlockCount && b.FirstBlock < a.FirstBlock + a.BlockCount)
                    throw new InvalidDataException($"Partitions {b.Index} and {a.Index} overlap.");
            }
        }
    }

    private static bool IsExtended(byte type) => type is 0x05 or 0x0F or 0x85;

    private static string Text(byte[] record, int at, int length) => System.Text.Encoding.ASCII.GetString(record, at, length);

    private static async ValueTask<byte[]> ReadBlockAsync(IBlockStorage disk, long block, CancellationToken cancellationToken)
    {
        var bytes = new byte[disk.Info.LogicalBlockSize];
        await disk.ReadAsync(block * bytes.Length, bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The disk's 32-bit signature, which Windows names a disk by.
    /// </summary>
    public uint DiskSignature { get; }

    /// <summary>
    /// The partitions with a file system's place: the primary entries that are not extended and the logical partitions,
    /// in the order of their numbers.
    /// </summary>
    public IReadOnlyList<MbrPartition> Partitions { get; }

    #endregion
}
