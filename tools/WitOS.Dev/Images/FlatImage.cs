using System.Reflection.PortableExecutable;
using System.Text;

namespace WitOS.Dev.Images;

/// <summary>
/// Converts a separately linked fixed native PE into the flat image of the root task (witos/flat.h, plan step
/// K4): a 64-byte header, at most four 32-byte segments and their page-aligned bytes, no names, imports or
/// relocations. The segments are the PE's sections at their linked addresses with their protections.
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

    #endregion

    #region Functions

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

        var dataStart = (HEADER_SIZE + SEGMENTS * SEGMENT_SIZE + PAGE - 1) / PAGE * PAGE;
        var flat = new MemoryStream();
        var writer = new BinaryWriter(flat);
        writer.Write(MAGIC);
        writer.Write(1u);
        writer.Write((uint)HEADER_SIZE);
        writer.Write(headerInfo.ImageBase + (uint)headerInfo.AddressOfEntryPoint);
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
        var payload = flat.ToArray();
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
