using System;
using System.Buffers.Binary;
using System.Text;

namespace OutWit.OS.Hardware.Nvme;

/// <summary>
/// The fields of an NVMe controller's Identify Controller data a host needs (NVM Express Base 1.4 §5.15.2.2, plan step
/// H3.2): its PCI vendor, serial number, model and firmware as space-padded ASCII, its largest transfer and its number of
/// namespaces, and whether it keeps a volatile write cache.
/// </summary>
/// <param name="Vendor">Bytes 0–1: the PCI vendor.</param>
/// <param name="Serial">Bytes 4–23.</param>
/// <param name="Model">Bytes 24–63.</param>
/// <param name="Firmware">Bytes 64–71.</param>
/// <param name="MaxTransferPages">Byte 77, MDTS: the largest transfer in pages of the controller's least page size, as a power
/// of two; zero for no limit.</param>
/// <param name="Namespaces">Bytes 516–519, NN: the largest namespace identifier.</param>
/// <param name="VolatileWriteCache">Byte 525 bit 0, VWC: a volatile write cache, which Flush empties.</param>
public sealed record NvmeControllerIdentity(ushort Vendor, string Serial, string Model, string Firmware, int MaxTransferPages, uint Namespaces,
    bool VolatileWriteCache)
{
    #region Constants

    /// <summary>
    /// Bytes of Identify data.
    /// </summary>
    public const int BYTES = 4096;

    #endregion

    #region Functions

    /// <summary>
    /// Reads Identify Controller data.
    /// </summary>
    /// <param name="data">The 4096 bytes.</param>
    /// <returns>The fields.</returns>
    /// <exception cref="FormatException">Fewer than 4096 bytes.</exception>
    public static NvmeControllerIdentity Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < BYTES)
            throw new FormatException($"Identify Controller data of {data.Length} bytes.");
        return new NvmeControllerIdentity(BinaryPrimitives.ReadUInt16LittleEndian(data), Text(data, 4, 20), Text(data, 24, 40), Text(data, 64, 8),
            data[77], BinaryPrimitives.ReadUInt32LittleEndian(data[516..]), (data[525] & 1) != 0);
    }

    /// <summary>
    /// A space-padded ASCII field.
    /// </summary>
    /// <param name="data">The data.</param>
    /// <param name="offset">The field's first byte.</param>
    /// <param name="length">Its bytes.</param>
    /// <returns>Its text, the padding dropped.</returns>
    internal static string Text(ReadOnlySpan<byte> data, int offset, int length) => Encoding.ASCII.GetString(data.Slice(offset, length)).Trim(' ', '\0');

    #endregion
}

/// <summary>
/// The fields of a namespace's Identify Namespace data a host needs (§5.15.2.1): its size in logical blocks and the
/// block of the LBA format in use, and whether it is write protected.
/// </summary>
/// <param name="Blocks">Bytes 0–7, NSZE.</param>
/// <param name="BlockBytes">Two to the LBADS of the LBA format FLBAS names (byte 26, bits 3–0; the formats at byte 128).</param>
/// <param name="MetadataBytes">That format's metadata bytes; the driver serves a namespace without them.</param>
/// <param name="WriteProtected">Byte 99 bit 0, NSATTR.</param>
public sealed record NvmeNamespaceIdentity(ulong Blocks, int BlockBytes, int MetadataBytes, bool WriteProtected)
{
    #region Functions

    /// <summary>
    /// Reads Identify Namespace data.
    /// </summary>
    /// <param name="data">The 4096 bytes.</param>
    /// <returns>The fields.</returns>
    /// <exception cref="FormatException">Fewer than 4096 bytes, an inactive namespace, or a block the driver cannot address.</exception>
    public static NvmeNamespaceIdentity Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < NvmeControllerIdentity.BYTES)
            throw new FormatException($"Identify Namespace data of {data.Length} bytes.");
        var blocks = BinaryPrimitives.ReadUInt64LittleEndian(data);
        if (blocks == 0)
            throw new FormatException("The namespace is not active: it has no block.");
        var format = BinaryPrimitives.ReadUInt32LittleEndian(data[(128 + (data[26] & 0xF) * 4)..]);
        var shift = (int)(format >> 16 & 0xFF);
        if (shift is < 9 or > 16)
            throw new FormatException($"A logical block of 2^{shift} bytes.");
        return new NvmeNamespaceIdentity(blocks, 1 << shift, (int)(format & 0xFFFF), (data[99] & 1) != 0);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The namespace's bytes.
    /// </summary>
    public long Bytes => (long)Blocks * BlockBytes;

    #endregion
}
