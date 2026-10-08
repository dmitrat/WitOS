using System.Buffers.Binary;
using WitOS.Dev.Images;

namespace WitOS.Dev.Tests.Images;

/// <summary>
/// The build check of a started program (plan step S5.2): a static position-independent executable whose relocations
/// musl's dlstart.c applies whole, so everything that needs more than relative relocations is refused.
/// </summary>
[TestFixture]
public sealed class StartedProgramTests
{
    #region Constants

    private const ushort EM_X86_64 = 62;
    private const ushort EM_AARCH64 = 183;
    private const ushort ET_EXEC = 2;
    private const ushort ET_DYN = 3;
    private const uint PT_LOAD = 1;
    private const uint PT_DYNAMIC = 2;
    private const uint PT_INTERP = 3;
    private const uint SHT_RELA = 4;
    private const uint SHT_DYNAMIC = 6;
    private const uint SHT_REL = 9;
    private const uint R_X86_64_GLOB_DAT = 6;
    private const uint R_X86_64_RELATIVE = 8;
    private const uint R_AARCH64_RELATIVE = 1027;
    private const ulong DT_NEEDED = 1;
    private const ulong DT_TEXTREL = 22;
    private const ulong DT_FLAGS = 30;

    #endregion

    #region Functions

    [Test]
    public void RelativeRelocationsAreAcceptedTest()
    {
        Assert.That(StartedProgram.Validate(Elf(ET_DYN, EM_X86_64, [PT_LOAD, PT_DYNAMIC], [R_X86_64_RELATIVE, R_X86_64_RELATIVE], []),
            EM_X86_64), Is.EqualTo(2));
        Assert.That(StartedProgram.Validate(Elf(ET_DYN, EM_AARCH64, [PT_LOAD, PT_DYNAMIC], [R_AARCH64_RELATIVE], [(DT_FLAGS, 8UL)]),
            EM_AARCH64), Is.EqualTo(1), "ARM64's relative type, and flags without DF_TEXTREL");
    }

    [Test]
    public void ProgramsNeedingMoreAreRefusedTest()
    {
        void Refused(byte[] elf, ushort machine, string what) =>
            Assert.That(() => StartedProgram.Validate(elf, machine), Throws.TypeOf<InvalidDataException>(), what);

        Refused(Elf(ET_EXEC, EM_X86_64, [PT_LOAD, PT_DYNAMIC], [], []), EM_X86_64, "A fixed executable accepted");
        Refused(Elf(ET_DYN, EM_AARCH64, [PT_LOAD, PT_DYNAMIC], [], []), EM_X86_64, "A foreign machine accepted");
        Refused(Elf(ET_DYN, EM_X86_64, [PT_LOAD, PT_DYNAMIC, PT_INTERP], [], []), EM_X86_64, "An interpreter accepted");
        Refused(Elf(ET_DYN, EM_X86_64, [PT_LOAD], [], []), EM_X86_64, "A program without its dynamic section accepted");
        Refused(Elf(ET_DYN, EM_X86_64, [PT_LOAD, PT_DYNAMIC], [R_X86_64_RELATIVE, R_X86_64_GLOB_DAT], []), EM_X86_64,
            "A symbol relocation accepted");
        Refused(Elf(ET_DYN, EM_AARCH64, [PT_LOAD, PT_DYNAMIC], [R_X86_64_RELATIVE], []), EM_AARCH64,
            "Another machine's relative type accepted");
        Refused(Elf(ET_DYN, EM_X86_64, [PT_LOAD, PT_DYNAMIC], [], [(DT_NEEDED, 1UL)]), EM_X86_64, "A needed library accepted");
        Refused(Elf(ET_DYN, EM_X86_64, [PT_LOAD, PT_DYNAMIC], [], [(DT_TEXTREL, 0UL)]), EM_X86_64, "Text relocations accepted");
        Refused(Elf(ET_DYN, EM_X86_64, [PT_LOAD, PT_DYNAMIC], [], [(DT_FLAGS, 4UL)]), EM_X86_64, "DF_TEXTREL accepted");
        Refused(Elf(ET_DYN, EM_X86_64, [PT_LOAD, PT_DYNAMIC], [], [], rel: true), EM_X86_64, "REL relocations accepted");
        Refused([0x7F, (byte)'E', (byte)'L', (byte)'F'], EM_X86_64, "A truncated header accepted");
    }

    // An ELF file of the program header types, one RELA section of the relocation types (or a REL section) and one
    // dynamic section of the entries, ending with DT_NULL.
    private static byte[] Elf(ushort type, ushort machine, uint[] segments, uint[] relocations, (ulong Tag, ulong Value)[] dynamic,
        bool rel = false)
    {
        const int header = 64, programHeader = 56, rela = 24, entry = 16, sectionHeader = 64;
        var relocationsAt = header + segments.Length * programHeader;
        var dynamicAt = relocationsAt + relocations.Length * rela;
        var dynamicBytes = (dynamic.Length + 1) * entry;
        var sectionsAt = dynamicAt + dynamicBytes;
        var bytes = new byte[sectionsAt + 3 * sectionHeader];
        var span = bytes.AsSpan();
        new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1 }.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], machine);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], header);
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..], (ulong)sectionsAt);
        BinaryPrimitives.WriteUInt16LittleEndian(span[52..], header);
        BinaryPrimitives.WriteUInt16LittleEndian(span[54..], programHeader);
        BinaryPrimitives.WriteUInt16LittleEndian(span[56..], (ushort)segments.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[58..], sectionHeader);
        BinaryPrimitives.WriteUInt16LittleEndian(span[60..], 3);
        for (var i = 0; i < segments.Length; ++i)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[(header + i * programHeader)..], segments[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(header + i * programHeader + 4)..], 4);
        }
        for (var i = 0; i < relocations.Length; ++i)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span[(relocationsAt + i * rela)..], (ulong)(0x1000 + 8 * i));
            BinaryPrimitives.WriteUInt64LittleEndian(span[(relocationsAt + i * rela + 8)..], relocations[i]);
        }
        for (var i = 0; i < dynamic.Length; ++i)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span[(dynamicAt + i * entry)..], dynamic[i].Tag);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(dynamicAt + i * entry + 8)..], dynamic[i].Value);
        }
        // Section 0 is the null section; then the relocations and the dynamic section.
        void Section(int index, uint kind, int offset, int size, int entrySize)
        {
            var at = sectionsAt + index * sectionHeader;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 4), kind);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 24), (ulong)offset);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 32), (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 56), (ulong)entrySize);
        }
        Section(1, rel ? SHT_REL : SHT_RELA, relocationsAt, relocations.Length * rela, rela);
        Section(2, SHT_DYNAMIC, dynamicAt, dynamicBytes, entry);
        return bytes;
    }

    #endregion
}
