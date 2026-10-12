using System;

namespace OutWit.OS.Storage;

/// <summary>
/// CRC-32 of IEEE 802.3 (reflected polynomial 0xEDB88320, initial and final value all ones), as the GUID partition table
/// checks its header and its entries with it (UEFI 2.10, §5.3).
/// </summary>
internal static class Crc32
{
    #region Fields

    private static readonly uint[] TABLE = Build();

    #endregion

    #region Functions

    /// <summary>
    /// The CRC-32 of the bytes.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The checksum.</returns>
    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
            crc = TABLE[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    #endregion
}
