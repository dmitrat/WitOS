using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the flat image of the root task (witos/flat.h, plan step K4): a 64-byte header, at most four 32-byte
/// segments and their page-aligned bytes, no names, imports or relocations. The segments come from a static ELF
/// executable's loadable segments (plan step T1, the clang and lld build of layer 2) or from a fixed native PE's
/// sections at their linked addresses with their protections.
/// </summary>
internal static class FlatImage
{
    #region Constants

    private const ulong MAGIC = 0x3154414C46544957UL; // "WITFLAT1"
    private const int HEADER_SIZE = 64;
    private const int SEGMENT_SIZE = 32;
    private const int SEGMENTS = 4;
    private const int PAGE = 4096;
    private const uint READ = 1;
    private const uint WRITE = 2;
    private const uint EXECUTE = 4;

    private const int ELF_HEADER_SIZE = 64;
    private const int ELF_PROGRAM_HEADER_SIZE = 56;
    private const ushort ET_EXEC = 2;
    private const uint PT_LOAD = 1;
    private const uint PT_DYNAMIC = 2;
    private const uint PT_INTERP = 3;
    private const uint PT_TLS = 7;
    private const uint PF_X = 1;
    private const uint PF_W = 2;
    private const uint PF_R = 4;

    #endregion

    #region Functions

    /// <summary>
    /// Builds the flat image of a static ELF executable and writes it beside the ELF; also embeds it as a C array so
    /// the kernel self-test can start the root task without the boot disk. The executable must be a 64-bit
    /// little-endian ET_EXEC of the machine with one to four loadable segments at page-aligned addresses, each
    /// readable and not both writable and executable, no interpreter, no dynamic section and no TLS segment, and its
    /// entry inside an executable segment.
    /// </summary>
    /// <param name="output">Artifact directory.</param>
    /// <param name="machine">Expected ELF machine (e_machine).</param>
    /// <param name="image">Path of the linked ELF.</param>
    /// <param name="name">Fixture name, for the flat file and the report.</param>
    /// <param name="symbol">C array symbol.</param>
    /// <param name="header">Generated header file name.</param>
    /// <returns>Path of the flat image.</returns>
    public static async Task<string> FromElfAsync(string output, ushort machine, string image, string name,
        string symbol, string header)
    {
        var bytes = await File.ReadAllBytesAsync(image);
        var (entry, segments) = ParseElf(bytes, machine);
        return await WriteAsync(output, name, symbol, header, entry, segments);
    }

    /// <summary>
    /// Reads the loadable segments of a static ELF executable; see <see cref="FromElfAsync"/> for the rules.
    /// </summary>
    /// <param name="bytes">The ELF file.</param>
    /// <param name="machine">Expected ELF machine.</param>
    /// <returns>The entry address and the segments.</returns>
    /// <exception cref="InvalidDataException">The file is not an acceptable root task image.</exception>
    public static (ulong Entry, List<(ulong Address, byte[] Data, uint MemorySize, uint Protection)> Segments) ParseElf(
        byte[] bytes, ushort machine)
    {
        var span = bytes.AsSpan();
        if (span.Length < ELF_HEADER_SIZE || span[0] != 0x7F || span[1] != (byte)'E' || span[2] != (byte)'L' || span[3] != (byte)'F')
            throw new InvalidDataException("Root task is not an ELF file.");
        if (span[4] != 2 || span[5] != 1 || span[6] != 1)
            throw new InvalidDataException("Root task must be a 64-bit little-endian ELF of version 1.");
        var type = BinaryPrimitives.ReadUInt16LittleEndian(span[16..]);
        var fileMachine = BinaryPrimitives.ReadUInt16LittleEndian(span[18..]);
        if (type != ET_EXEC || fileMachine != machine)
            throw new InvalidDataException($"Root task must be a static executable (ET_EXEC) for machine {machine}, not type {type} for {fileMachine}.");
        var entry = BinaryPrimitives.ReadUInt64LittleEndian(span[24..]);
        var programHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(span[32..]);
        var programHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(span[54..]);
        var programHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(span[56..]);
        if (programHeaderSize != ELF_PROGRAM_HEADER_SIZE || programHeaderCount == 0 ||
            programHeaderOffset > (ulong)span.Length ||
            (ulong)programHeaderCount * ELF_PROGRAM_HEADER_SIZE > (ulong)span.Length - programHeaderOffset)
            throw new InvalidDataException("Root task program headers are missing or malformed.");

        var segments = new List<(ulong Address, byte[] Data, uint MemorySize, uint Protection)>();
        for (var i = 0; i < programHeaderCount; i++)
        {
            var phdr = span.Slice((int)programHeaderOffset + i * ELF_PROGRAM_HEADER_SIZE, ELF_PROGRAM_HEADER_SIZE);
            var segmentType = BinaryPrimitives.ReadUInt32LittleEndian(phdr);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(phdr[4..]);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(phdr[8..]);
            var address = BinaryPrimitives.ReadUInt64LittleEndian(phdr[16..]);
            var fileSize = BinaryPrimitives.ReadUInt64LittleEndian(phdr[32..]);
            var memorySize = BinaryPrimitives.ReadUInt64LittleEndian(phdr[40..]);
            if (segmentType is PT_DYNAMIC or PT_INTERP or PT_TLS)
                throw new InvalidDataException("Root task must be static: no interpreter, dynamic section or TLS segment.");
            if (segmentType != PT_LOAD || memorySize == 0)
                continue;
            var execute = (flags & PF_X) != 0;
            var write = (flags & PF_W) != 0;
            if ((flags & PF_R) == 0 || (execute && write))
                throw new InvalidDataException($"Root task segment at 0x{address:X} is not readable or is both writable and executable.");
            if ((address & (PAGE - 1)) != 0)
                throw new InvalidDataException($"Root task segment at 0x{address:X} is not page-aligned.");
            if (fileSize > memorySize || offset > (ulong)span.Length || fileSize > (ulong)span.Length - offset ||
                memorySize > uint.MaxValue - PAGE)
                throw new InvalidDataException($"Root task segment at 0x{address:X} has a file range beyond the image or an impossible size.");
            var data = span.Slice((int)offset, (int)fileSize).ToArray();
            var pages = (uint)((memorySize + PAGE - 1) / PAGE * PAGE);
            segments.Add((address, data, pages, READ | (write ? WRITE : 0) | (execute ? EXECUTE : 0)));
        }
        if (segments.Count is 0 or > SEGMENTS)
            throw new InvalidDataException($"Root task must have one to {SEGMENTS} loadable segments, not {segments.Count}.");
        if (!segments.Any(segment => (segment.Protection & EXECUTE) != 0 && entry >= segment.Address &&
                entry - segment.Address < segment.MemorySize))
            throw new InvalidDataException($"Root task entry 0x{entry:X} is outside every executable segment.");
        return (entry, segments);
    }

    /// <summary>
    /// Builds the flat image of a fixed native PE and writes it beside the PE; also embeds it as a C array so the
    /// kernel self-test can start the root task without the boot disk.
    /// </summary>
    /// <param name="output">Artifact directory.</param>
    /// <param name="machine">Expected PE machine.</param>
    /// <param name="image">Path of the linked PE.</param>
    /// <param name="name">Fixture name, for the flat file and the report.</param>
    /// <param name="symbol">C array symbol.</param>
    /// <param name="header">Generated header file name.</param>
    /// <returns>Path of the flat image.</returns>
    public static async Task<string> FromPeAsync(string output, Machine machine, string image, string name,
        string symbol, string header)
    {
        var bytes = await File.ReadAllBytesAsync(image);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var headerInfo = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("Root task PE header missing.");
        if (pe.PEHeaders.CoffHeader.Machine != machine || pe.PEHeaders.CorHeader is not null ||
            headerInfo.Subsystem != Subsystem.Native || headerInfo.ImportTableDirectory.Size != 0 ||
            headerInfo.BaseRelocationTableDirectory.Size != 0)
            throw new InvalidDataException($"Root task must be a fixed {machine} native image without imports or relocations.");
        var sections = pe.PEHeaders.SectionHeaders.Where(section => section.VirtualSize > 0).ToList();
        if (sections.Count is 0 or > SEGMENTS)
            throw new InvalidDataException($"Root task must have one to {SEGMENTS} sections, not {sections.Count}.");

        var segments = new List<(ulong Address, byte[] Data, uint MemorySize, uint Protection)>();
        foreach (var section in sections)
        {
            var characteristics = section.SectionCharacteristics;
            var execute = characteristics.HasFlag(SectionCharacteristics.MemExecute);
            var write = characteristics.HasFlag(SectionCharacteristics.MemWrite);
            if (!characteristics.HasFlag(SectionCharacteristics.MemRead) || (execute && write))
                throw new InvalidDataException($"Root task section {section.Name} is not readable or is both writable and executable.");
            var fileSize = Math.Min(section.VirtualSize, section.SizeOfRawData);
            var data = section.SizeOfRawData == 0 ? [] : bytes.AsSpan(section.PointerToRawData, fileSize).ToArray();
            var memorySize = (uint)((section.VirtualSize + PAGE - 1) / PAGE * PAGE);
            segments.Add((headerInfo.ImageBase + (ulong)section.VirtualAddress, data, memorySize,
                READ | (write ? WRITE : 0) | (execute ? EXECUTE : 0)));
        }
        return await WriteAsync(output, name, symbol, header, headerInfo.ImageBase + (uint)headerInfo.AddressOfEntryPoint, segments);
    }

    /// <summary>
    /// Lays out a flat image: the header, the segment table and every segment's bytes padded to pages.
    /// </summary>
    /// <param name="entry">Entry address.</param>
    /// <param name="segments">Segments in order.</param>
    /// <returns>The image bytes.</returns>
    public static byte[] Build(ulong entry, IReadOnlyList<(ulong Address, byte[] Data, uint MemorySize, uint Protection)> segments)
    {
        var dataStart = (HEADER_SIZE + SEGMENTS * SEGMENT_SIZE + PAGE - 1) / PAGE * PAGE;
        var flat = new MemoryStream();
        var writer = new BinaryWriter(flat);
        writer.Write(MAGIC);
        writer.Write(1u);
        writer.Write((uint)HEADER_SIZE);
        writer.Write(entry);
        writer.Write((uint)segments.Count);
        writer.Write(0u);
        for (var i = 0; i < 4; i++)
            writer.Write(0UL);
        var offset = (ulong)dataStart;
        foreach (var (address, data, memorySize, protection) in segments)
        {
            writer.Write(address);
            writer.Write(offset);
            writer.Write((uint)data.Length);
            writer.Write(memorySize);
            writer.Write(protection);
            writer.Write(0u);
            offset += (ulong)((data.Length + PAGE - 1) / PAGE * PAGE);
        }
        for (var i = segments.Count; i < SEGMENTS; i++)
            for (var k = 0; k < SEGMENT_SIZE; k += 8)
                writer.Write(0UL);
        flat.SetLength(dataStart);
        flat.Position = dataStart;
        foreach (var (_, data, _, _) in segments)
        {
            writer.Write(data);
            var padded = (data.Length + PAGE - 1) / PAGE * PAGE;
            flat.SetLength(flat.Position + (padded - data.Length));
            flat.Position = flat.Length;
        }
        return flat.ToArray();
    }

    #endregion

    #region Tools

    private static async Task<string> WriteAsync(string output, string name, string symbol, string header, ulong entry,
        IReadOnlyList<(ulong Address, byte[] Data, uint MemorySize, uint Protection)> segments)
    {
        var payload = Build(entry, segments);
        var flatPath = Path.Combine(output, name + ".flat");
        await File.WriteAllBytesAsync(flatPath, payload);

        var generated = new StringBuilder("/* Generated from the separately linked root task; do not edit. */\nstatic const unsigned char " + symbol + "[] = {\n");
        for (var index = 0; index < payload.Length; index += 16)
            generated.AppendLine("    " + string.Join(", ", payload.Skip(index).Take(16).Select(value => $"0x{value:X2}")) + ",");
        generated.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, header), generated.ToString(), Encoding.ASCII);
        Console.WriteLine($"{name}: flat image of {segments.Count} segment(s), {payload.Length} bytes.");
        return flatPath;
    }

    #endregion
}
