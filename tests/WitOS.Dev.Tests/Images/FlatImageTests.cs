using System.Buffers.Binary;
using WitOS.Dev.Images;

namespace WitOS.Dev.Tests.Images;

/// <summary>
/// The ELF side of the flat root task image (plan step T1): a static executable's loadable segments become the
/// flat segments with their protections, and everything the kernel's flat format cannot carry is refused whole.
/// </summary>
[TestFixture]
public sealed class FlatImageTests
{
    #region Constants

    private const ushort EM_X86_64 = 62;
    private const ushort ET_EXEC = 2;
    private const ushort ET_DYN = 3;
    private const uint PT_LOAD = 1;
    private const uint PT_INTERP = 3;
    private const uint PT_TLS = 7;
    private const uint PT_GNU_STACK = 0x6474E551;
    private const ulong BASE = 0x0000008000100000UL;
    private const int PAGE = 4096;

    #endregion

    #region Functions

    [Test]
    public void LoadableSegmentsBecomeFlatSegmentsTest()
    {
        var code = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        var data = new byte[] { 1, 2, 3 };
        var elf = Elf(ET_EXEC, EM_X86_64, BASE + 16,
            (PT_LOAD, 5u, BASE, code, 100UL), (PT_LOAD, 6u, BASE + PAGE, data, 4200UL), (PT_GNU_STACK, 6u, 0UL, [], 0UL));

        var (entry, segments) = FlatImage.ParseElf(elf, EM_X86_64);

        Assert.That(entry, Is.EqualTo(BASE + 16));
        Assert.That(segments, Has.Count.EqualTo(2));
        Assert.That(segments[0], Is.EqualTo((BASE, code, (uint)PAGE, 5u)));
        Assert.That(segments[1].Address, Is.EqualTo(BASE + PAGE));
        Assert.That(segments[1].Data, Is.EqualTo(data));
        Assert.That(segments[1].MemorySize, Is.EqualTo(2u * PAGE), "Memory size is rounded to pages");
        Assert.That(segments[1].Protection, Is.EqualTo(3u), "Read and write");

        var flat = FlatImage.Build(entry, segments);
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(flat), Is.EqualTo(0x3154414C46544957UL), "Magic");
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(flat.AsSpan(16)), Is.EqualTo(BASE + 16), "Entry");
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(flat.AsSpan(24)), Is.EqualTo(2u), "Segment count");
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(flat.AsSpan(64 + 8)), Is.EqualTo((ulong)PAGE), "First segment's file offset");
        Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(flat.AsSpan(64 + 32 + 8)), Is.EqualTo(2UL * PAGE), "Second segment's file offset");
        Assert.That(flat.AsSpan(PAGE, 100).ToArray(), Is.EqualTo(code), "Code bytes");
        Assert.That(flat.AsSpan(2 * PAGE, 3).ToArray(), Is.EqualTo(data), "Data bytes");
        Assert.That(flat, Has.Length.EqualTo(3 * PAGE));
    }

    [Test]
    public void UnacceptableExecutablesAreRefusedTest()
    {
        var code = new byte[32];
        Assert.That(() => FlatImage.ParseElf(Elf(ET_DYN, EM_X86_64, BASE, (PT_LOAD, 5u, BASE, code, 32UL)), EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "Position-independent executable accepted");
        Assert.That(() => FlatImage.ParseElf(Elf(ET_EXEC, 183, BASE, (PT_LOAD, 5u, BASE, code, 32UL)), EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "Foreign machine accepted");
        Assert.That(() => FlatImage.ParseElf(Elf(ET_EXEC, EM_X86_64, BASE, (PT_LOAD, 5u, BASE, code, 32UL),
                (PT_INTERP, 4u, BASE + PAGE, [1], 1UL)), EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "Interpreter accepted");
        Assert.That(() => FlatImage.ParseElf(Elf(ET_EXEC, EM_X86_64, BASE, (PT_LOAD, 5u, BASE, code, 32UL),
                (PT_TLS, 4u, BASE + PAGE, [1], 1UL)), EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "TLS segment accepted");
        Assert.That(() => FlatImage.ParseElf(Elf(ET_EXEC, EM_X86_64, BASE, (PT_LOAD, 7u, BASE, code, 32UL)), EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "Writable executable segment accepted");
        Assert.That(() => FlatImage.ParseElf(Elf(ET_EXEC, EM_X86_64, BASE, (PT_LOAD, 1u, BASE, code, 32UL)), EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "Unreadable segment accepted");
        Assert.That(() => FlatImage.ParseElf(Elf(ET_EXEC, EM_X86_64, BASE + 16, (PT_LOAD, 5u, BASE + 16, code, 32UL)), EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "Unaligned segment accepted");
        Assert.That(() => FlatImage.ParseElf(Elf(ET_EXEC, EM_X86_64, BASE + PAGE, (PT_LOAD, 5u, BASE, code, 32UL)), EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "Entry outside executable code accepted");
        Assert.That(() => FlatImage.ParseElf(Elf(ET_EXEC, EM_X86_64, BASE, (PT_LOAD, 5u, BASE, code, 16UL)), EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "File size beyond memory size accepted");
        Assert.That(() => FlatImage.ParseElf(Elf(ET_EXEC, EM_X86_64, BASE, (PT_LOAD, 5u, BASE, code, 32UL), (PT_LOAD, 4u, BASE + PAGE, code, 32UL),
                (PT_LOAD, 4u, BASE + 2 * PAGE, code, 32UL), (PT_LOAD, 4u, BASE + 3 * PAGE, code, 32UL),
                (PT_LOAD, 4u, BASE + 4 * PAGE, code, 32UL)), EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "Five segments accepted");
        Assert.That(() => FlatImage.ParseElf(Elf(ET_EXEC, EM_X86_64, BASE, (PT_GNU_STACK, 6u, 0UL, [], 0UL)), EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "No loadable segment accepted");
        Assert.That(() => FlatImage.ParseElf([0x7F, (byte)'E', (byte)'L', (byte)'F'], EM_X86_64),
            Throws.TypeOf<InvalidDataException>(), "Truncated header accepted");
    }

    #endregion

    #region Tools

    // A minimal ELF64 little-endian file: the header, the program headers and each segment's bytes on its own page.
    private static byte[] Elf(ushort type, ushort machine, ulong entry,
        params (uint Type, uint Flags, ulong Address, byte[] Data, ulong MemorySize)[] segments)
    {
        var file = new byte[PAGE * (1 + segments.Length)];
        file[0] = 0x7F;
        file[1] = (byte)'E';
        file[2] = (byte)'L';
        file[3] = (byte)'F';
        file[4] = 2; // ELFCLASS64
        file[5] = 1; // little-endian
        file[6] = 1; // version
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(16), type);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(18), machine);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(24), entry);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(56), (ushort)segments.Length);
        for (var i = 0; i < segments.Length; i++)
        {
            var (segmentType, flags, address, data, memorySize) = segments[i];
            var header = file.AsSpan(64 + i * 56, 56);
            var offset = (ulong)PAGE * (ulong)(1 + i);
            BinaryPrimitives.WriteUInt32LittleEndian(header, segmentType);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], flags);
            BinaryPrimitives.WriteUInt64LittleEndian(header[8..], offset);
            BinaryPrimitives.WriteUInt64LittleEndian(header[16..], address);
            BinaryPrimitives.WriteUInt64LittleEndian(header[24..], address);
            BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(header[40..], memorySize);
            BinaryPrimitives.WriteUInt64LittleEndian(header[48..], PAGE);
            data.CopyTo(file.AsSpan((int)offset));
        }
        return file;
    }

    #endregion
}
