using System;
using System.Buffers.Binary;
using System.Text;

namespace OutWit.OS.Hardware.Ahci;

/// <summary>
/// The words of an ATA device's IDENTIFY DEVICE data a host needs (ACS-3 §7.12.7, plan step H3.1): its model, serial
/// number and firmware as strings whose words hold their characters byte-swapped, its 48-bit capacity (or the 28-bit one
/// of an older device), its logical and physical sectors, and its write cache with the flush that empties it.
/// </summary>
/// <param name="Model">Words 27–46.</param>
/// <param name="Serial">Words 10–19.</param>
/// <param name="Firmware">Words 23–26.</param>
/// <param name="Lba48">Word 83 bit 10: the 48-bit address feature set.</param>
/// <param name="Sectors">The logical sectors: words 100–103 with 48-bit addresses, words 60–61 otherwise.</param>
/// <param name="LogicalSectorBytes">Words 117–118 where word 106 says the logical sector is longer than 256 words, else 512.</param>
/// <param name="PhysicalSectorBytes">The logical sector times two to the power word 106 bits 3–0 names, where its bit 13 says so.</param>
/// <param name="WriteCache">Words 82 and 85 bit 5: a volatile write cache, supported and enabled.</param>
/// <param name="FlushExt">Word 83 bit 13: FLUSH CACHE EXT.</param>
public sealed record AtaIdentify(string Model, string Serial, string Firmware, bool Lba48, ulong Sectors, int LogicalSectorBytes,
    int PhysicalSectorBytes, bool WriteCache, bool FlushExt)
{
    #region Constants

    /// <summary>
    /// Bytes of IDENTIFY DEVICE data.
    /// </summary>
    public const int BYTES = 512;

    #endregion

    #region Functions

    /// <summary>
    /// Reads IDENTIFY DEVICE data.
    /// </summary>
    /// <param name="data">The 512 bytes.</param>
    /// <returns>The fields.</returns>
    /// <exception cref="FormatException">Fewer than 512 bytes, or a device that reports no sector.</exception>
    public static AtaIdentify Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < BYTES)
            throw new FormatException($"IDENTIFY DEVICE data of {data.Length} bytes.");
        var lba48 = (Word(data, 83) & 0xC000) == 0x4000 && (Word(data, 83) & 1 << 10) != 0;
        var sectors = lba48
            ? Word(data, 100) | (ulong)Word(data, 101) << 16 | (ulong)Word(data, 102) << 32 | (ulong)Word(data, 103) << 48
            : Word(data, 60) | (ulong)Word(data, 61) << 16;
        if (sectors == 0)
            throw new FormatException("The device reports no sector.");
        var sizes = Word(data, 106);
        var valid = (sizes & 0xC000) == 0x4000;
        var logical = valid && (sizes & 1 << 12) != 0 ? (int)((Word(data, 117) | (uint)Word(data, 118) << 16) * 2) : 512;
        if (logical < 512 || logical % 512 != 0 || logical > 64 * 1024)
            throw new FormatException($"A logical sector of {logical} bytes.");
        var physical = valid && (sizes & 1 << 13) != 0 ? logical << (sizes & 0xF) : logical;
        var writeCache = (Word(data, 82) & 1 << 5) != 0 && (Word(data, 85) & 1 << 5) != 0;
        var flushExt = (Word(data, 83) & 0xC000) == 0x4000 && (Word(data, 83) & 1 << 13) != 0;
        return new AtaIdentify(Text(data, 27, 20), Text(data, 10, 10), Text(data, 23, 4), lba48, sectors, logical, physical, writeCache, flushExt);
    }

    private static ushort Word(ReadOnlySpan<byte> data, int index) => BinaryPrimitives.ReadUInt16LittleEndian(data[(index * 2)..]);

    // An IDENTIFY string: each word holds two characters, the first in its high byte; trailing spaces dropped.
    private static string Text(ReadOnlySpan<byte> data, int word, int words)
    {
        var text = new StringBuilder(words * 2);
        for (var i = 0; i < words; i++)
        {
            var at = (word + i) * 2;
            text.Append((char)data[at + 1]).Append((char)data[at]);
        }
        return text.ToString().Trim(' ', '\0');
    }

    #endregion

    #region Properties

    /// <summary>
    /// The device's bytes.
    /// </summary>
    public long Bytes => (long)Sectors * LogicalSectorBytes;

    #endregion
}
