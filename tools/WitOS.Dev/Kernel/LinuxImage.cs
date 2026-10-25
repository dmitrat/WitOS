using System.Buffers.Binary;

namespace WitOS.Dev.Kernel;

/// <summary>
/// The kernel image as the Linux arm64 boot protocol takes it (plan step H6, ADR 0030; Linux's
/// Documentation/arch/arm64/booting.rst): a bootloader copies the file to memory and enters its first byte, so the
/// PE image is laid out flat, every section at its address, and the protocol's header written over the DOS header.
/// The header's first instruction, a NOP whose bytes are also "MZ", keeps the file a PE image, whose header offset the
/// protocol's last header word names as Linux's own images do; its second branches to the boot layer's entry,
/// <see cref="ENTRY"/>. The file alignment becomes the section alignment, so that the flat file stays a valid PE image
/// the UEFI firmware loads as well.
/// </summary>
internal static class LinuxImage
{
    #region Constants

    /// <summary>
    /// The boot layer's entry (src/Boot.Linux/entry.S).
    /// </summary>
    public const string ENTRY = "wit_linux_entry";

    /// <summary>
    /// The protocol's magic, "ARM\x64", at offset 56.
    /// </summary>
    public const uint MAGIC = 0x644D5241;

    /// <summary>
    /// The header's first instruction: ccmp x18, #0, #0xd, pl, a NOP whose first bytes are "MZ".
    /// </summary>
    public const uint MZ_NOP = 0xFA405A4D;

    /// <summary>
    /// The header's flags: a little-endian kernel, 4 KiB pages (bits 1–2 of 1) and an image placed anywhere in memory
    /// (bit 3).
    /// </summary>
    public const ulong FLAGS = 1 << 1 | 1 << 3;

    private const int PAGE = 4096;

    #endregion

    #region Functions

    /// <summary>
    /// Lays a PE image out flat and writes the protocol's header.
    /// </summary>
    /// <param name="pe">The kernel's PE image.</param>
    /// <param name="entry">The relative address of <see cref="ENTRY"/>.</param>
    /// <returns>The image.</returns>
    /// <exception cref="InvalidDataException">The PE image is not one the entry can relocate or the header can branch from.</exception>
    public static byte[] FromPe(byte[] pe, uint entry)
    {
        if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z')
            throw new InvalidDataException("The kernel is no PE image.");
        var lfanew = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        if (lfanew < 0x40 || lfanew > PAGE - 248 || lfanew % 8 != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(lfanew)) != 0x00004550)
            throw new InvalidDataException("The kernel is no PE image whose header the entry reads 8-aligned.");
        var optional = lfanew + 24;
        if (BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(optional)) != 0x20B)
            throw new InvalidDataException("The kernel is no PE32+ image.");
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(lfanew + 6));
        var optionalBytes = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(lfanew + 20));
        var sectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(optional + 32));
        var imageBytes = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(optional + 56));
        var headerBytes = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(optional + 60));
        var sections = optional + optionalBytes;
        if (sectionAlignment != PAGE || imageBytes % PAGE != 0 || headerBytes > PAGE || headerBytes > pe.Length || sections + sectionCount * 40 > headerBytes)
            throw new InvalidDataException("The kernel's sections are not page-aligned after one page of headers.");
        if (entry % 4 != 0 || entry < PAGE || entry >= imageBytes)
            throw new InvalidDataException($"The entry at {entry:x} is no instruction of the image.");

        var image = new byte[imageBytes];
        pe.AsSpan(0, (int)headerBytes).CopyTo(image);
        for (var i = 0; i < sectionCount; i++)
        {
            var header = sections + i * 40;
            var virtualBytes = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(header + 8));
            var address = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(header + 12));
            var rawBytes = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(header + 16));
            var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(header + 20));
            var span = (uint)Math.Min(Align(Math.Max(virtualBytes, rawBytes)), imageBytes - address);
            if (address % PAGE != 0 || address < PAGE || address >= imageBytes || rawOffset + (ulong)Math.Min(rawBytes, span) > (ulong)pe.Length)
                throw new InvalidDataException($"Section {i} lies outside the image.");
            pe.AsSpan((int)rawOffset, (int)Math.Min(rawBytes, span)).CopyTo(image.AsSpan((int)address));
            // In the flat file a section's bytes are where it is loaded.
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(header + 16), span);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(header + 20), address);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 36), PAGE); // FileAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 60), PAGE); // SizeOfHeaders, in the file's alignment
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 64), 0);    // CheckSum
        // The debug directory's records name their bytes by file offset too.
        var debugAddress = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 112 + 6 * 8));
        var debugBytes = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 112 + 6 * 8 + 4));
        if (debugAddress != 0 && debugAddress + (ulong)debugBytes <= imageBytes)
        {
            for (var at = debugAddress; at + 28 <= debugAddress + debugBytes; at += 28)
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)at + 24), BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)at + 20)));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(image, MZ_NOP);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 0x14000000 | ((entry - 4) >> 2 & 0x03FFFFFF)); // b entry
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(8), 0);                  // text_offset
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(16), imageBytes);        // image_size
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(24), FLAGS);
        image.AsSpan(32, 24).Clear();                                                  // res2–res4
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(56), MAGIC);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(60), lfanew);
        return image;
    }

    /// <summary>
    /// The relative address of the entry, from the kernel's link map: its Rva+Base less the image's preferred base.
    /// </summary>
    /// <param name="map">The link map's lines.</param>
    /// <param name="pe">The kernel's PE image, whose optional header names the preferred base.</param>
    /// <returns>The relative address.</returns>
    /// <exception cref="InvalidDataException">The map names no entry.</exception>
    public static uint EntryRva(IEnumerable<string> map, byte[] pe)
    {
        var lfanew = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        var preferred = BinaryPrimitives.ReadUInt64LittleEndian(pe.AsSpan(lfanew + 24 + 24));
        foreach (var line in map)
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 3 && words[1] == ENTRY && ulong.TryParse(words[2], System.Globalization.NumberStyles.HexNumber, null, out var address) &&
                address >= preferred)
                return (uint)(address - preferred);
        }
        throw new InvalidDataException($"The kernel's link map names no {ENTRY}.");
    }

    private static uint Align(uint value) => (value + PAGE - 1) & ~(uint)(PAGE - 1);

    #endregion
}
