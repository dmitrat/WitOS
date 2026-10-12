using System;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// The checksums ext4 records: CRC-32C (Castagnoli, reflected 0x82F63B78) as the kernel's crc32c() computes it, a raw
/// update with no inversion before or after, the caller giving the seed; and CRC-16 (reflected 0xA001) of the older
/// group descriptor checksum (GDT_CSUM).
/// </summary>
internal static class Ext4Checksums
{
    #region Constants

    private static readonly uint[] CRC32C = Table32(0x82F63B78);
    private static readonly ushort[] CRC16 = Table16(0xA001);

    #endregion

    #region Functions

    /// <summary>
    /// A raw CRC-32C update.
    /// </summary>
    /// <param name="crc">The seed, or the checksum so far.</param>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The checksum so far.</returns>
    public static uint Crc32C(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            crc = CRC32C[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>
    /// A raw CRC-32C update with a little-endian 32-bit value.
    /// </summary>
    /// <param name="crc">The checksum so far.</param>
    /// <param name="value">The value.</param>
    /// <returns>The checksum so far.</returns>
    public static uint Crc32C(uint crc, uint value)
    {
        Span<byte> bytes = [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];
        return Crc32C(crc, bytes);
    }

    /// <summary>
    /// A raw CRC-16 update.
    /// </summary>
    /// <param name="crc">The seed, or the checksum so far.</param>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The checksum so far.</returns>
    public static ushort Crc16(ushort crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            crc = (ushort)(CRC16[(crc ^ value) & 0xFF] ^ (crc >> 8));
        return crc;
    }

    private static uint[] Table32(uint polynomial)
    {
        var table = new uint[256];
        for (var i = 0U; i < 256; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? crc >> 1 ^ polynomial : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    private static ushort[] Table16(ushort polynomial)
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (ushort)i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (ushort)(crc >> 1 ^ polynomial) : (ushort)(crc >> 1);
            table[i] = crc;
        }
        return table;
    }

    #endregion
}
