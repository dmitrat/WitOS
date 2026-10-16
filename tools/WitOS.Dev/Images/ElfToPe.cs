using System.Buffers.Binary;

namespace WitOS.Dev.Images;

/// <summary>
/// The RISC-V kernel image's container (plan step V1.1, the decision of V0): the pinned LLVM links no RISC-V PE/COFF,
/// and UEFI loads PE32+ alone, so the kernel and its loader are one position-independent ELF that this converts into a
/// PE32+ with a base relocation table, as EDK II's GenFw converts its own modules. Each loadable segment becomes a
/// section at its ELF address (relative to an image base of zero), the entry is the ELF's, and every relative
/// relocation becomes a 64-bit base relocation with its addend written where it belongs, so that the firmware's
/// relocation of the image to where it loads it is the ELF's own. The first page of the ELF, its headers, becomes the
/// PE headers; the loader's own check of them (src/Boot.Uefi/image.c) bounds what is written here.
/// </summary>
internal static class ElfToPe
{
    #region Constants

    private const int PAGE = 0x1000;

    private const int ELF_HEADER_SIZE = 64;
    private const int PROGRAM_HEADER_SIZE = 56;
    private const ushort ET_DYN = 3;
    private const uint PT_LOAD = 1;
    private const uint PT_DYNAMIC = 2;
    private const uint PF_X = 1, PF_W = 2, PF_R = 4;
    private const long DT_NULL = 0, DT_RELA = 7, DT_RELASZ = 8, DT_RELAENT = 9;
    private const uint R_RISCV_NONE = 0, R_RISCV_RELATIVE = 3;

    private const int PE_OFFSET = 0x40;
    private const int OPTIONAL_HEADER_SIZE = 240; // PE32+ with its 16 directories
    private const int SECTION_HEADER_SIZE = 40;
    private const ushort SUBSYSTEM_EFI_APPLICATION = 10;
    private const uint CHARACTERISTICS_CODE = 0x20, CHARACTERISTICS_INITIALIZED = 0x40, CHARACTERISTICS_UNINITIALIZED = 0x80;
    private const uint CHARACTERISTICS_DISCARDABLE = 0x02000000, CHARACTERISTICS_EXECUTE = 0x20000000, CHARACTERISTICS_READ = 0x40000000;
    private const uint CHARACTERISTICS_WRITE = 0x80000000;
    private const ushort RELOCATION_DIR64 = 10;

    #endregion

    #region Functions

    /// <summary>
    /// Converts the image.
    /// </summary>
    /// <param name="elf">The ELF: 64-bit little-endian, ET_DYN, every loadable segment page-aligned at or above the
    /// first page, relative relocations alone.</param>
    /// <param name="elfMachine">The ELF machine expected (243 for RISC-V).</param>
    /// <param name="peMachine">The PE machine written (0x5064 for RISC-V 64).</param>
    /// <returns>The PE32+ image.</returns>
    /// <exception cref="InvalidDataException">The ELF is not one the conversion serves.</exception>
    public static byte[] Convert(byte[] elf, ushort elfMachine, ushort peMachine)
    {
        var span = elf.AsSpan();
        if (span.Length < ELF_HEADER_SIZE || span[0] != 0x7F || span[1] != (byte)'E' || span[2] != (byte)'L' || span[3] != (byte)'F')
            throw new InvalidDataException("The kernel image is not an ELF file.");
        if (span[4] != 2 || span[5] != 1 || span[6] != 1)
            throw new InvalidDataException("The kernel image must be a 64-bit little-endian ELF of version 1.");
        var type = BinaryPrimitives.ReadUInt16LittleEndian(span[16..]);
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(span[18..]);
        if (type != ET_DYN || machine != elfMachine)
            throw new InvalidDataException($"The kernel image must be position-independent (ET_DYN) for machine {elfMachine}, not type {type} for {machine}.");
        var entry = BinaryPrimitives.ReadUInt64LittleEndian(span[24..]);
        var programHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(span[32..]);
        var programHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(span[54..]);
        var programHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(span[56..]);
        if (programHeaderSize != PROGRAM_HEADER_SIZE || programHeaderCount == 0 || programHeaderOffset > (ulong)span.Length ||
            (ulong)programHeaderCount * PROGRAM_HEADER_SIZE > (ulong)span.Length - programHeaderOffset)
            throw new InvalidDataException("The kernel image's program headers are missing or malformed.");

        var segments = new List<Segment>();
        (ulong Offset, ulong Size)? dynamic = null;
        for (var i = 0; i < programHeaderCount; i++)
        {
            var header = span.Slice((int)programHeaderOffset + i * PROGRAM_HEADER_SIZE, PROGRAM_HEADER_SIZE);
            var segmentType = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
            var address = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
            var segmentFileSize = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
            var memorySize = BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
            if (segmentType == PT_DYNAMIC)
            {
                dynamic = (offset, segmentFileSize);
                continue;
            }
            if (segmentType != PT_LOAD || memorySize == 0)
                continue;
            if (segmentFileSize > memorySize || offset > (ulong)span.Length || segmentFileSize > (ulong)span.Length - offset)
                throw new InvalidDataException($"The segment at 0x{address:X} has a file range beyond the image.");
            if ((flags & PF_R) == 0 || ((flags & PF_W) != 0 && (flags & PF_X) != 0))
                throw new InvalidDataException($"The segment at 0x{address:X} is not readable or is both writable and executable.");
            // The first page holds the ELF's headers in the file and the PE's in the image: a segment that starts there
            // gives the page up and keeps the rest.
            var start = address;
            var fileStart = offset;
            var fileEnd = offset + segmentFileSize;
            var end = address + memorySize;
            if (start < PAGE)
            {
                if ((offset & (PAGE - 1)) != (address & (PAGE - 1)))
                    throw new InvalidDataException($"The segment at 0x{address:X} is not congruent with its file offset.");
                fileStart += PAGE - start;
                start = PAGE;
                if (start >= end)
                    continue; // the headers alone
            }
            if ((start & (PAGE - 1)) != 0 || (fileStart & (PAGE - 1)) != 0)
                throw new InvalidDataException($"The segment at 0x{address:X} is not page-aligned.");
            var data = fileEnd > fileStart ? span.Slice((int)fileStart, (int)(fileEnd - fileStart)).ToArray() : [];
            segments.Add(new Segment(start, end - start, data, flags));
        }
        if (segments.Count == 0)
            throw new InvalidDataException("The kernel image has no loadable segment above its first page.");
        segments.Sort((a, b) => a.Address.CompareTo(b.Address));
        for (var i = 1; i < segments.Count; i++)
            if (segments[i].Address < segments[i - 1].Address + Pages(segments[i - 1].MemorySize))
                throw new InvalidDataException("The kernel image's segments overlap.");
        if (!segments.Any(segment => (segment.Flags & PF_X) != 0 && entry >= segment.Address && entry < segment.Address + segment.MemorySize))
            throw new InvalidDataException($"The entry 0x{entry:X} is outside every executable segment.");

        // The relocations: every one relative, applied for an image base of zero and listed for the firmware.
        var fixups = new SortedSet<ulong>();
        if (dynamic is { } table)
        {
            ulong relocations = 0, relocationBytes = 0, relocationSize = 24;
            for (var at = table.Offset; at + 16 <= table.Offset + table.Size && at + 16 <= (ulong)span.Length; at += 16)
            {
                var tag = BinaryPrimitives.ReadInt64LittleEndian(span[(int)at..]);
                var value = BinaryPrimitives.ReadUInt64LittleEndian(span[((int)at + 8)..]);
                if (tag == DT_NULL)
                    break;
                if (tag == DT_RELA)
                    relocations = value;
                else if (tag == DT_RELASZ)
                    relocationBytes = value;
                else if (tag == DT_RELAENT)
                    relocationSize = value;
            }
            if (relocationSize != 24)
                throw new InvalidDataException("The kernel image's relocation entries are not RELA entries of 24 bytes.");
            for (ulong at = 0; relocations != 0 && at < relocationBytes; at += relocationSize)
            {
                var relocation = ReadImage(segments, relocations + at, 24);
                var place = BinaryPrimitives.ReadUInt64LittleEndian(relocation);
                var info = BinaryPrimitives.ReadUInt64LittleEndian(relocation[8..]);
                var addend = BinaryPrimitives.ReadInt64LittleEndian(relocation[16..]);
                var kind = (uint)(info & 0xFFFFFFFF);
                if (kind == R_RISCV_NONE)
                    continue;
                if (kind != R_RISCV_RELATIVE || (info >> 32) != 0)
                    throw new InvalidDataException($"The kernel image needs a relocation of type {kind} with a symbol, which the firmware cannot apply; " +
                        "only relative relocations are served.");
                var segment = segments.FirstOrDefault(s => place >= s.Address && place + 8 <= s.Address + (ulong)s.Data.Length)
                    ?? throw new InvalidDataException($"The relocation at 0x{place:X} points outside the image's file data.");
                BinaryPrimitives.WriteInt64LittleEndian(segment.Data.AsSpan((int)(place - segment.Address)), addend);
                fixups.Add(place);
            }
        }

        // The layout: the headers' page, each segment's pages, the relocation section.
        var sections = new List<(string Name, ulong Rva, uint VirtualSize, byte[] Data, uint Characteristics)>();
        foreach (var segment in segments)
        {
            var execute = (segment.Flags & PF_X) != 0;
            var write = (segment.Flags & PF_W) != 0;
            var characteristics = CHARACTERISTICS_READ | (execute ? CHARACTERISTICS_CODE | CHARACTERISTICS_EXECUTE : CHARACTERISTICS_INITIALIZED) |
                (write ? CHARACTERISTICS_WRITE : 0);
            if (segment.Data.Length == 0)
                characteristics |= CHARACTERISTICS_UNINITIALIZED;
            sections.Add((execute ? ".text" : write ? ".data" : ".rodata", segment.Address, (uint)segment.MemorySize, segment.Data, characteristics));
        }
        // The relocation section follows the segments; an image without a fixup has none and an empty directory, which
        // the firmware's loader takes as nothing to relocate (a block of size zero would be a load error).
        var relocationData = RelocationBlocks(fixups);
        var relocationRva = Pages(sections[^1].Rva + sections[^1].VirtualSize);
        if (relocationData.Length > 0)
            sections.Add((".reloc", relocationRva, (uint)relocationData.Length, relocationData,
                CHARACTERISTICS_READ | CHARACTERISTICS_INITIALIZED | CHARACTERISTICS_DISCARDABLE));
        var imageSize = Pages(sections[^1].Rva + sections[^1].VirtualSize);
        var fileSize = PAGE + sections.Sum(section => (long)Pages((ulong)section.Data.Length));
        var image = new byte[fileSize];
        var output = image.AsSpan();

        // The DOS stub: MZ and the offset of the PE signature.
        output[0] = (byte)'M';
        output[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(output[0x3C..], PE_OFFSET);
        // The PE signature and the COFF header.
        BinaryPrimitives.WriteUInt32LittleEndian(output[PE_OFFSET..], 0x00004550);
        var coff = output[(PE_OFFSET + 4)..];
        BinaryPrimitives.WriteUInt16LittleEndian(coff, peMachine);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[2..], (ushort)sections.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(coff[4..], 0); // the time stamp: reproducible, as /Brepro leaves it
        BinaryPrimitives.WriteUInt32LittleEndian(coff[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(coff[12..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[16..], OPTIONAL_HEADER_SIZE);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[18..], 0x0022); // executable image, large address aware
        // The optional header, PE32+.
        var optional = output[(PE_OFFSET + 24)..];
        BinaryPrimitives.WriteUInt16LittleEndian(optional, 0x20B);
        var code = sections.Where(section => (section.Characteristics & CHARACTERISTICS_CODE) != 0).ToList();
        BinaryPrimitives.WriteUInt32LittleEndian(optional[4..], (uint)code.Sum(section => (long)Pages((ulong)section.Data.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(optional[8..],
            (uint)sections.Where(section => (section.Characteristics & CHARACTERISTICS_INITIALIZED) != 0).Sum(section => (long)Pages((ulong)section.Data.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(optional[12..],
            (uint)sections.Where(section => (section.Characteristics & CHARACTERISTICS_UNINITIALIZED) != 0).Sum(section => (long)section.VirtualSize));
        BinaryPrimitives.WriteUInt32LittleEndian(optional[16..], (uint)entry);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[20..], code.Count > 0 ? (uint)code[0].Rva : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(optional[24..], 0); // the image base: zero, every address an RVA
        BinaryPrimitives.WriteUInt32LittleEndian(optional[32..], PAGE); // section alignment
        BinaryPrimitives.WriteUInt32LittleEndian(optional[36..], PAGE); // file alignment
        BinaryPrimitives.WriteUInt16LittleEndian(optional[48..], 1); // the subsystem version UEFI asks for
        BinaryPrimitives.WriteUInt32LittleEndian(optional[56..], (uint)imageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[60..], PAGE); // the headers' size
        BinaryPrimitives.WriteUInt16LittleEndian(optional[68..], SUBSYSTEM_EFI_APPLICATION);
        BinaryPrimitives.WriteUInt16LittleEndian(optional[70..], 0x0160); // high entropy, dynamic base, NX compatible
        BinaryPrimitives.WriteUInt32LittleEndian(optional[108..], 16); // the directories
        if (relocationData.Length > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(optional[(112 + 5 * 8)..], (uint)relocationRva); // the base relocation table
            BinaryPrimitives.WriteUInt32LittleEndian(optional[(112 + 5 * 8 + 4)..], (uint)relocationData.Length);
        }
        // The section table and the sections.
        var headers = output[(PE_OFFSET + 24 + OPTIONAL_HEADER_SIZE)..];
        var fileOffset = (ulong)PAGE;
        for (var i = 0; i < sections.Count; i++)
        {
            var (name, rva, virtualSize, data, characteristics) = sections[i];
            var header = headers[(i * SECTION_HEADER_SIZE)..];
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], virtualSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)rva);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)Pages((ulong)data.Length));
            // A section without file data (uninitialized alone) still names the file position it would take, which the
            // loader's own check requires to lie within the headers' end and the image (src/Boot.Uefi/image.c).
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)fileOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header[36..], characteristics);
            data.CopyTo(output[(int)fileOffset..]);
            fileOffset += Pages((ulong)data.Length);
        }
        return image;
    }

    // The base relocation table: a block per page with the 64-bit entries of that page, each block's size a multiple
    // of four bytes.
    private static byte[] RelocationBlocks(SortedSet<ulong> fixups)
    {
        var blocks = new MemoryStream();
        foreach (var page in fixups.GroupBy(fixup => fixup & ~(ulong)(PAGE - 1)))
        {
            var entries = page.Select(fixup => (ushort)((RELOCATION_DIR64 << 12) | (int)(fixup - page.Key))).ToList();
            if (entries.Count % 2 == 1)
                entries.Add(0); // an absolute entry pads the block
            var block = new byte[8 + entries.Count * 2];
            BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)page.Key);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)block.Length);
            for (var i = 0; i < entries.Count; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8 + i * 2), entries[i]);
            blocks.Write(block);
        }
        return blocks.ToArray();
    }

    // Bytes of the image at an address, through the segment that holds them.
    private static ReadOnlySpan<byte> ReadImage(List<Segment> segments, ulong address, int size)
    {
        foreach (var segment in segments)
            if (address >= segment.Address && address + (ulong)size <= segment.Address + (ulong)segment.Data.Length)
                return segment.Data.AsSpan((int)(address - segment.Address), size);
        throw new InvalidDataException($"The address 0x{address:X} is outside the image's file data.");
    }

    private static ulong Pages(ulong bytes) => (bytes + PAGE - 1) & ~(ulong)(PAGE - 1);

    #endregion

    #region Nested Types

    private sealed record Segment(ulong Address, ulong MemorySize, byte[] Data, uint Flags);

    #endregion
}
