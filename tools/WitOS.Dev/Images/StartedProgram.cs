using System.Buffers.Binary;

namespace WitOS.Dev.Images;

/// <summary>
/// The build check of a program another process starts (plan step S5.2): a static position-independent ELF executable
/// that musl's dlstart.c relocates at its start. dlstart.c applies relative relocations and skips every other kind, so
/// the build refuses a program that would need any other: an interpreter, a needed library, text relocations or a
/// relocation that is not relative leave the program for ld.so (S5.3).
/// </summary>
internal static class StartedProgram
{
    #region Constants

    private const int ELF_HEADER_SIZE = 64;
    private const int PROGRAM_HEADER_SIZE = 56;
    private const int SECTION_HEADER_SIZE = 64;
    private const int RELA_SIZE = 24;
    private const int DYNAMIC_SIZE = 16;
    private const ushort ET_DYN = 3;
    private const uint PT_DYNAMIC = 2;
    private const uint PT_INTERP = 3;
    private const uint SHT_RELA = 4;
    private const uint SHT_DYNAMIC = 6;
    private const uint SHT_REL = 9;
    private const ulong DT_NEEDED = 1;
    private const ulong DT_TEXTREL = 22;
    private const ulong DT_FLAGS = 30;
    private const ulong DF_TEXTREL = 4;
    private const ushort EM_X86_64 = 62;
    private const ushort EM_AARCH64 = 183;
    private const uint R_X86_64_RELATIVE = 8;
    private const uint R_AARCH64_RELATIVE = 1027;

    #endregion

    #region Functions

    /// <summary>
    /// Requires a 64-bit little-endian ET_DYN of the machine with a dynamic section, no interpreter, no needed library,
    /// no text relocations and only relative relocations.
    /// </summary>
    /// <param name="bytes">The linked ELF file.</param>
    /// <param name="machine">Expected ELF machine.</param>
    /// <returns>The count of relative relocations.</returns>
    /// <exception cref="InvalidDataException">The file is not such a program.</exception>
    public static int Validate(byte[] bytes, ushort machine)
    {
        var span = bytes.AsSpan();
        if (span.Length < ELF_HEADER_SIZE || span[0] != 0x7F || span[1] != (byte)'E' || span[2] != (byte)'L' || span[3] != (byte)'F' ||
            span[4] != 2 || span[5] != 1 || span[6] != 1)
            throw new InvalidDataException("A started program must be a 64-bit little-endian ELF of version 1.");
        var type = BinaryPrimitives.ReadUInt16LittleEndian(span[16..]);
        var fileMachine = BinaryPrimitives.ReadUInt16LittleEndian(span[18..]);
        if (type != ET_DYN || fileMachine != machine)
            throw new InvalidDataException($"A started program must be position-independent (ET_DYN) for machine {machine}, not type {type} for {fileMachine}.");
        var relative = machine switch
        {
            EM_X86_64 => R_X86_64_RELATIVE,
            EM_AARCH64 => R_AARCH64_RELATIVE,
            _ => throw new InvalidDataException($"No relative relocation type is known for machine {machine}.")
        };
        var programHeaders = Table(span, BinaryPrimitives.ReadUInt64LittleEndian(span[32..]), BinaryPrimitives.ReadUInt16LittleEndian(span[54..]),
            BinaryPrimitives.ReadUInt16LittleEndian(span[56..]), PROGRAM_HEADER_SIZE);
        var dynamic = false;
        foreach (var header in programHeaders)
        {
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(span[header..]);
            if (kind == PT_INTERP)
                throw new InvalidDataException("A started program has no interpreter; a dynamic program is ld.so's (S5.3).");
            dynamic |= kind == PT_DYNAMIC;
        }
        if (!dynamic)
            throw new InvalidDataException("A started program needs its dynamic section: dlstart.c finds its relocations through it.");
        var sections = Table(span, BinaryPrimitives.ReadUInt64LittleEndian(span[40..]), BinaryPrimitives.ReadUInt16LittleEndian(span[58..]),
            BinaryPrimitives.ReadUInt16LittleEndian(span[60..]), SECTION_HEADER_SIZE);
        var count = 0;
        foreach (var section in sections)
        {
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(span[(section + 4)..]);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(span[(section + 24)..]);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(span[(section + 32)..]);
            if (kind == SHT_REL)
                throw new InvalidDataException("A started program has RELA relocations alone.");
            if (kind != SHT_RELA && kind != SHT_DYNAMIC)
                continue;
            var entrySize = kind == SHT_RELA ? RELA_SIZE : DYNAMIC_SIZE;
            foreach (var entry in Table(span, offset, entrySize, checked((int)(size / (ulong)entrySize)), entrySize))
            {
                if (kind == SHT_RELA)
                {
                    var relocation = (uint)BinaryPrimitives.ReadUInt64LittleEndian(span[(entry + 8)..]);
                    if (relocation != relative)
                        throw new InvalidDataException($"A started program has relative relocations alone, not type {relocation}.");
                    ++count;
                    continue;
                }
                var tag = BinaryPrimitives.ReadUInt64LittleEndian(span[entry..]);
                var value = BinaryPrimitives.ReadUInt64LittleEndian(span[(entry + 8)..]);
                if (tag == DT_NEEDED || tag == DT_TEXTREL || (tag == DT_FLAGS && (value & DF_TEXTREL) != 0))
                    throw new InvalidDataException("A started program needs no library and has no text relocations.");
            }
        }
        return count;
    }

    // The offsets of the entries of a table inside the file.
    private static List<int> Table(ReadOnlySpan<byte> span, ulong offset, int entrySize, int count, int expectedSize)
    {
        if (count > 0 && entrySize != expectedSize)
            throw new InvalidDataException($"An ELF table has entries of {entrySize} bytes, not {expectedSize}.");
        if (offset > (ulong)span.Length || (ulong)count * (ulong)expectedSize > (ulong)span.Length - offset)
            throw new InvalidDataException("An ELF table lies outside the file.");
        return Enumerable.Range(0, count).Select(index => checked((int)offset + index * expectedSize)).ToList();
    }

    #endregion
}
