using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware;

/// <summary>
/// The kernel's device descriptor table (witos/device.h, RFC 0011 §7.7): one page the platform filled at boot, read
/// through its memory object. Every field is little-endian and fixed-size. The reader validates the table instead of
/// trusting it (RFC 0007 §137).
/// </summary>
public static class DeviceTable
{
    #region Constants

    /// <summary>
    /// The version of the table's header.
    /// </summary>
    public const uint VERSION = 1;

    /// <summary>
    /// The size of the table's header.
    /// </summary>
    public const int HEADER_SIZE = 16;

    /// <summary>
    /// The size of one descriptor.
    /// </summary>
    public const int DESCRIPTOR_SIZE = 232;

    /// <summary>
    /// The bytes of the table: one page.
    /// </summary>
    public const int TABLE_BYTES = 4096;

    private const int REGIONS = 7;
    private const int LINES = 2;
    private const int REGION_SIZE = 24;
    private const int LINE_SIZE = 8;
    private const int REGIONS_OFFSET = 32;
    private const int LINES_OFFSET = REGIONS_OFFSET + REGIONS * REGION_SIZE;

    #endregion

    #region Functions

    /// <summary>
    /// Maps the table, reads its descriptors and releases the mapping.
    /// </summary>
    /// <param name="table">The table's memory object, with MAP.</param>
    /// <returns>The descriptors, in the table's order.</returns>
    /// <exception cref="CapabilityException">The table could not be mapped.</exception>
    /// <exception cref="FormatException">The table is not one this reader knows.</exception>
    public static IReadOnlyList<DeviceDescriptor> Read(KernelHandle table)
    {
        using var mapping = MemoryMapping.Map(table, 0, TABLE_BYTES, MemoryMapping.READ);
        return Parse(mapping.Bytes);
    }

    /// <summary>
    /// Reads the descriptors of a table's bytes.
    /// </summary>
    /// <param name="bytes">The table: its header, then its descriptors.</param>
    /// <returns>The descriptors, in the table's order.</returns>
    /// <exception cref="FormatException">The header, the count or a descriptor is not valid.</exception>
    public static IReadOnlyList<DeviceDescriptor> Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HEADER_SIZE)
            throw new FormatException("The device table is shorter than its header.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var descriptorSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (version != VERSION || size != HEADER_SIZE || descriptorSize != DESCRIPTOR_SIZE)
            throw new FormatException($"The device table has version {version}, header {size} and descriptors of {descriptorSize} bytes.");
        if (count > (ulong)(bytes.Length - HEADER_SIZE) / DESCRIPTOR_SIZE)
            throw new FormatException($"The device table counts {count} descriptors beyond its {bytes.Length} bytes.");
        var descriptors = new List<DeviceDescriptor>((int)count);
        for (var i = 0; i < (int)count; i++)
            descriptors.Add(ParseDescriptor(i, bytes.Slice(HEADER_SIZE + i * DESCRIPTOR_SIZE, DESCRIPTOR_SIZE)));
        return descriptors;
    }

    /// <summary>
    /// Reads one descriptor's bytes, as the table holds them and the driver protocol carries them (witos/driver.h).
    /// </summary>
    /// <param name="index">The descriptor's index in the table.</param>
    /// <param name="bytes">DESCRIPTOR_SIZE bytes.</param>
    /// <returns>The descriptor, with its bytes.</returns>
    /// <exception cref="FormatException">The bytes are not one descriptor, or it lists more regions or lines than one holds.</exception>
    public static DeviceDescriptor ParseDescriptor(int index, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != DESCRIPTOR_SIZE)
            throw new FormatException($"A descriptor has {DESCRIPTOR_SIZE} bytes, not {bytes.Length}.");
        var regionCount = Word(bytes, 24);
        var lineCount = Word(bytes, 28);
        if (regionCount > REGIONS || lineCount > LINES)
            throw new FormatException($"Descriptor {index} lists {regionCount} regions and {lineCount} lines.");
        var regions = new List<DeviceRegion>((int)regionCount);
        for (var i = 0; i < (int)regionCount; i++)
        {
            var region = bytes.Slice(REGIONS_OFFSET + i * REGION_SIZE, REGION_SIZE);
            regions.Add(new DeviceRegion(BinaryPrimitives.ReadUInt64LittleEndian(region),
                BinaryPrimitives.ReadUInt64LittleEndian(region[8..]), BinaryPrimitives.ReadUInt32LittleEndian(region[16..])));
        }
        var lines = new List<DeviceLine>((int)lineCount);
        for (var i = 0; i < (int)lineCount; i++)
        {
            var line = bytes.Slice(LINES_OFFSET + i * LINE_SIZE, LINE_SIZE);
            lines.Add(new DeviceLine(BinaryPrimitives.ReadUInt32LittleEndian(line), BinaryPrimitives.ReadUInt32LittleEndian(line[4..])));
        }
        return new DeviceDescriptor(index, Word(bytes, 0), Word(bytes, 4),
            [Word(bytes, 8), Word(bytes, 12), Word(bytes, 16), Word(bytes, 20)], regions, lines)
        {
            Bytes = bytes.ToArray()
        };
    }

    private static uint Word(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    #endregion
}
