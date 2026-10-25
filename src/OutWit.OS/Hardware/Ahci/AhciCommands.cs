using System;
using System.Buffers.Binary;

namespace OutWit.OS.Hardware.Ahci;

/// <summary>
/// What an AHCI port's command slot holds (AHCI 1.3.1 §4.2.2–§4.2.3, plan step H3.1): the command header in the command
/// list, and the command table it names — the command FIS, a Register Host to Device FIS of SATA 3.x §10.5.6 carrying
/// an ATA command (ACS-3), then the Physical Region Descriptor Table, one entry per physical segment of the data.
/// </summary>
public static class AhciCommands
{
    #region Constants

    /// <summary>
    /// Bytes of a command header; a command list holds 32.
    /// </summary>
    public const int HEADER_BYTES = 32;

    /// <summary>
    /// Bytes of a command list: 32 headers, aligned to 1 KiB.
    /// </summary>
    public const int LIST_BYTES = 32 * HEADER_BYTES;

    /// <summary>
    /// Bytes of the received FIS area, aligned to 256.
    /// </summary>
    public const int RECEIVED_FIS_BYTES = 256;

    /// <summary>
    /// The offset of a command table's PRDT, after the command FIS, the ATAPI command and the reserved bytes.
    /// </summary>
    public const int PRDT_OFFSET = 0x80;

    /// <summary>
    /// Bytes of a PRDT entry.
    /// </summary>
    public const int PRDT_ENTRY_BYTES = 16;

    /// <summary>
    /// The most bytes one PRDT entry carries: its byte count is 22 bits, less one.
    /// </summary>
    public const int PRDT_ENTRY_LIMIT = 4 * 1024 * 1024;

    /// <summary>
    /// Bytes of a Register Host to Device FIS, five dwords.
    /// </summary>
    public const int REGISTER_FIS_BYTES = 20;

    /// <summary>
    /// The most sectors one 48-bit command transfers: a count of zero.
    /// </summary>
    public const int MAX_SECTORS = 65536;

    /// <summary>
    /// ATA IDENTIFY DEVICE: 512 bytes of the device's description.
    /// </summary>
    public const byte IDENTIFY_DEVICE = 0xEC;

    /// <summary>
    /// ATA READ DMA EXT: sectors at a 48-bit address.
    /// </summary>
    public const byte READ_DMA_EXT = 0x25;

    /// <summary>
    /// ATA WRITE DMA EXT.
    /// </summary>
    public const byte WRITE_DMA_EXT = 0x35;

    /// <summary>
    /// ATA FLUSH CACHE EXT: the volatile cache written to the medium.
    /// </summary>
    public const byte FLUSH_CACHE_EXT = 0xEA;

    /// <summary>
    /// ATA FLUSH CACHE, for a device without the 48-bit feature set's flush.
    /// </summary>
    public const byte FLUSH_CACHE = 0xE7;

    /// <summary>
    /// The FIS type of a Register Host to Device FIS.
    /// </summary>
    private const byte REGISTER_HOST_TO_DEVICE = 0x27;

    /// <summary>
    /// The FIS's C bit: the FIS updates the Command register.
    /// </summary>
    private const byte COMMAND_BIT = 0x80;

    /// <summary>
    /// The Device register's LBA bit, which every 48-bit command sets.
    /// </summary>
    private const byte LBA_MODE = 0x40;

    #endregion

    #region Functions

    /// <summary>
    /// Writes the Register Host to Device FIS of an ATA command at the start of a command table.
    /// </summary>
    /// <param name="table">The command table.</param>
    /// <param name="command">The ATA command.</param>
    /// <param name="lba">The first sector, 48 bits.</param>
    /// <param name="count">The sectors, 1 to 65536 (written as zero for 65536), or zero for a command without data.</param>
    public static void WriteRegisterFis(Span<byte> table, byte command, ulong lba, int count)
    {
        if (count is < 0 or > MAX_SECTORS)
            throw new ArgumentOutOfRangeException(nameof(count), $"A command of {count} sectors.");
        if (lba >> 48 != 0)
            throw new ArgumentOutOfRangeException(nameof(lba), $"Sector {lba} lies beyond 48 bits.");
        var fis = table[..REGISTER_FIS_BYTES];
        fis.Clear();
        fis[0] = REGISTER_HOST_TO_DEVICE;
        fis[1] = COMMAND_BIT;
        fis[2] = command;
        fis[4] = (byte)lba;
        fis[5] = (byte)(lba >> 8);
        fis[6] = (byte)(lba >> 16);
        fis[7] = command == IDENTIFY_DEVICE ? (byte)0 : LBA_MODE;
        fis[8] = (byte)(lba >> 24);
        fis[9] = (byte)(lba >> 32);
        fis[10] = (byte)(lba >> 40);
        BinaryPrimitives.WriteUInt16LittleEndian(fis[12..], (ushort)count); // 65536 wraps to zero, as ATA reads it
    }

    /// <summary>
    /// Writes a command header: the command FIS's length, the direction, the PRDT's entries and the table's address.
    /// </summary>
    /// <param name="list">The command list.</param>
    /// <param name="slot">The command slot, 0 to 31.</param>
    /// <param name="write">Whether the device reads the data (a write to the disk).</param>
    /// <param name="entries">The PRDT's entries.</param>
    /// <param name="table">The command table's physical address, aligned to 128.</param>
    public static void WriteHeader(Span<byte> list, int slot, bool write, int entries, ulong table)
    {
        if ((table & 0x7F) != 0)
            throw new ArgumentException($"A command table at {table:x} is not aligned to 128.", nameof(table));
        var header = list.Slice(slot * HEADER_BYTES, HEADER_BYTES);
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(REGISTER_FIS_BYTES / 4 | (write ? 1 << 6 : 0) | entries << 16));
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], table);
    }

    /// <summary>
    /// The bytes the device transferred for a command header's last command (PRDBC).
    /// </summary>
    /// <param name="list">The command list.</param>
    /// <param name="slot">The command slot.</param>
    /// <returns>The bytes.</returns>
    public static uint TransferredBytes(ReadOnlySpan<byte> list, int slot) => BinaryPrimitives.ReadUInt32LittleEndian(list[(slot * HEADER_BYTES + 4)..]);

    /// <summary>
    /// Writes a PRDT entry: a physical segment of the data.
    /// </summary>
    /// <param name="table">The command table.</param>
    /// <param name="entry">The entry's index.</param>
    /// <param name="address">The segment's physical address, even.</param>
    /// <param name="bytes">Its bytes, even, at most <see cref="PRDT_ENTRY_LIMIT"/>.</param>
    public static void WritePrdtEntry(Span<byte> table, int entry, ulong address, int bytes)
    {
        if ((address & 1) != 0 || (bytes & 1) != 0 || bytes is <= 0 or > PRDT_ENTRY_LIMIT)
            throw new ArgumentException($"A PRDT entry of {bytes} bytes at {address:x}.", nameof(bytes));
        var at = table.Slice(PRDT_OFFSET + entry * PRDT_ENTRY_BYTES, PRDT_ENTRY_BYTES);
        BinaryPrimitives.WriteUInt64LittleEndian(at, address);
        BinaryPrimitives.WriteUInt32LittleEndian(at[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(at[12..], (uint)(bytes - 1)); // no interrupt of its own: the command's end interrupts
    }

    /// <summary>
    /// The bytes of a command table with a PRDT of so many entries.
    /// </summary>
    /// <param name="entries">The entries.</param>
    /// <returns>The bytes.</returns>
    public static int TableBytes(int entries) => PRDT_OFFSET + entries * PRDT_ENTRY_BYTES;

    #endregion
}
