using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.Storage;

/// <summary>
/// A disk image with a GUID partition table, as the tests need one: a protective MBR, the primary header and entries,
/// and the backup entries and header at the end, each with its CRC-32.
/// </summary>
internal static class GptImage
{
    #region Constants

    public const int ENTRIES = 128;
    public const int ENTRY_SIZE = 128;

    #endregion

    #region Functions

    /// <summary>
    /// The image of a disk of the blocks given with the partitions, each a first and last block and a name.
    /// </summary>
    public static byte[] Create(int blockSize, long blocks, Guid disk, IReadOnlyList<(Guid Type, Guid Id, long First, long Last, string Name)> partitions)
    {
        var image = new byte[blocks * blockSize];
        // The protective MBR: one partition of type 0xEE over the disk, and the boot signature.
        image[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(446 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(446 + 12), (uint)Math.Min(blocks - 1, uint.MaxValue));
        image[510] = 0x55;
        image[511] = 0xAA;
        var entries = new byte[ENTRIES * ENTRY_SIZE];
        for (var i = 0; i < partitions.Count; i++)
        {
            var entry = entries.AsSpan(i * ENTRY_SIZE, ENTRY_SIZE);
            partitions[i].Type.TryWriteBytes(entry);
            partitions[i].Id.TryWriteBytes(entry[16..]);
            BinaryPrimitives.WriteInt64LittleEndian(entry[32..], partitions[i].First);
            BinaryPrimitives.WriteInt64LittleEndian(entry[40..], partitions[i].Last);
            Encoding.Unicode.GetBytes(partitions[i].Name).CopyTo(entry[56..]);
        }
        var entryBlocks = ENTRIES * ENTRY_SIZE / blockSize;
        var firstUsable = 2 + entryBlocks;
        var lastUsable = blocks - 2 - entryBlocks;
        entries.CopyTo(image.AsSpan(2 * blockSize));
        entries.CopyTo(image.AsSpan((int)((blocks - 1 - entryBlocks) * blockSize)));
        WriteHeader(image, blockSize, 1, blocks - 1, firstUsable, lastUsable, disk, 2, entries);
        WriteHeader(image, blockSize, blocks - 1, 1, firstUsable, lastUsable, disk, blocks - 1 - entryBlocks, entries);
        return image;
    }

    private static void WriteHeader(byte[] image, int blockSize, long mine, long alternate, long firstUsable, long lastUsable, Guid disk,
        long entriesAt, byte[] entries)
    {
        var header = image.AsSpan((int)(mine * blockSize), GptPartitionTable.HEADER_SIZE);
        BinaryPrimitives.WriteUInt64LittleEndian(header, GptPartitionTable.SIGNATURE);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], GptPartitionTable.REVISION);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], GptPartitionTable.HEADER_SIZE);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], mine);
        BinaryPrimitives.WriteInt64LittleEndian(header[32..], alternate);
        BinaryPrimitives.WriteInt64LittleEndian(header[40..], firstUsable);
        BinaryPrimitives.WriteInt64LittleEndian(header[48..], lastUsable);
        disk.TryWriteBytes(header[56..]);
        BinaryPrimitives.WriteInt64LittleEndian(header[72..], entriesAt);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], ENTRIES);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], ENTRY_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(header[88..], Crc32.Compute(entries));
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32.Compute(header));
    }

    #endregion
}
