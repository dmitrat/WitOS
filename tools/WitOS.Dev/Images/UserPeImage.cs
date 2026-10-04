using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the relocatable and fixed PE fixtures that the guest PE loader must parse.
/// </summary>
internal static class UserPeImage
{
    #region Constants

    // PE layout offsets: COFF characteristics after the signature, DllCharacteristics and the base relocation
    // directory in the PE32+ optional header.
    private const int COFF_CHARACTERISTICS = 22;
    private const int OPTIONAL_HEADER = 24;
    private const int DLL_CHARACTERISTICS = 70;
    private const int RELOCATION_DIRECTORY = 112 + 5 * 8;
    private const ushort RELOCS_STRIPPED = 1;
    private const ushort DYNAMIC_BASE = 0x40;

    #endregion

    #region Functions

    /// <summary>
    /// Links the relocatable and fixed PE fixtures and generates their embedding header.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <param name="constants">User ABI constants by name.</param>
    public static async Task BuildAsync(string root, string output, string msvc, Dictionary<string, ulong> constants)
    {
        var obj = Path.Combine(output, "PeFixture.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", $"/I{output}", $"/Fo{obj}", Path.Combine(root, "tests", "User.X64", "image.asm")], root);
        await LinkAndEmbedAsync(root, output, msvc, constants, obj, "x64", Machine.Amd64, stripFixed: false);
    }

    /// <summary>
    /// Links the ARM64 PE fixtures and generates their embedding header.
    /// </summary>
    /// <remarks>
    /// The ARM64 linker refuses /FIXED. The fixed fixture is therefore the same object linked at its load address
    /// with the relocation directory removed afterwards: a valid PE that only loads at its preferred base.
    /// </remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory with the generated ARM64 ABI header.</param>
    /// <param name="msvc">Directory of the MSVC ARM64 cross tools.</param>
    /// <param name="constants">User ABI constants by name.</param>
    public static async Task BuildArm64Async(string root, string output, string msvc, Dictionary<string, ulong> constants)
    {
        var preprocessed = Path.Combine(output, "PeFixture.asm");
        var obj = Path.Combine(output, "PeFixture.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo", "/EP", "/P", $"/Fi{preprocessed}", $"/I{output}", "/Tc",
                Path.Combine(root, "tests", "User.A64", "image.asm")], root);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "armasm64.exe"), ["-nologo", "-o", obj, preprocessed], root);
        await LinkAndEmbedAsync(root, output, msvc, constants, obj, "arm64", Machine.Arm64, stripFixed: true);
    }

    #endregion

    #region Tools

    private static async Task LinkAndEmbedAsync(string root, string output, string msvc,
        Dictionary<string, ulong> constants, string obj, string linkMachine, Machine machine, bool stripFixed)
    {
        var generated = new StringBuilder("/* Full PE files for guest parsing; generated, do not edit. */\n");
        var symbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var fixedImage in new[] { false, true })
        {
            var name = fixedImage ? "PeFixedFixture" : "PeFixture";
            var image = Path.Combine(output, name + ".pe");
            var map = Path.Combine(output, name + ".map");
            var preferredBase = fixedImage ? constants["WIT_USER_IMAGE_BASE"] : 0x180000000UL;
            var linkFixed = fixedImage && !stripFixed;
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
                ["/nologo", "/subsystem:native", "/entry:wit_pe_start", "/nodefaultlib", $"/machine:{linkMachine}",
                    linkFixed ? "/fixed" : "/fixed:no", linkFixed ? "/dynamicbase:no" : "/dynamicbase",
                    "/incremental:no", "/Brepro", "/section:USERDATA,RW", $"/base:0x{preferredBase:X}", $"/out:{image}", $"/map:{map}", obj], root);
            var bytes = await File.ReadAllBytesAsync(image);
            if (fixedImage && stripFixed)
            {
                StripRelocations(bytes);
                await File.WriteAllBytesAsync(image, bytes);
            }
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            var h = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("PE fixture header missing.");
            var sections = pe.PEHeaders.SectionHeaders;
            if (pe.PEHeaders.CoffHeader.Machine != machine || h.Magic != PEMagic.PE32Plus ||
                h.Subsystem != Subsystem.Native || h.ImageBase != preferredBase ||
                pe.PEHeaders.CorHeader is not null || h.ImportTableDirectory.Size != 0 ||
                h.ThreadLocalStorageTableDirectory.Size != 0 || h.ExceptionTableDirectory.Size != 0 ||
                (h.BaseRelocationTableDirectory.Size == 0) != fixedImage ||
                !sections.Any(s => s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute)) ||
                !sections.Any(s => s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemWrite) &&
                    s.VirtualSize > s.SizeOfRawData + 4096) ||
                sections.Any(s => s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemWrite) &&
                    s.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute)))
                throw new InvalidDataException("PE fixture did not exercise the expected guest loading profile.");
            var mapText = await File.ReadAllTextAsync(map);
            foreach (var label in new[] { "data_pointer", "written", "split_pointer", "readonly_pointer", "bss_data" })
            {
                var match = Regex.Match(mapText,
                    @"^\s*[0-9A-Fa-f]+:[0-9A-Fa-f]+\s+" + label + @"\s+([0-9A-Fa-f]+)\s", RegexOptions.Multiline);
                if (!match.Success)
                    throw new InvalidDataException($"Missing test symbol {label}.");
                var address = ulong.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (address < preferredBase || address - preferredBase >= (ulong)h.SizeOfImage)
                    throw new InvalidDataException("Test symbol escaped the image.");
                var rva = address - preferredBase;
                if (symbols.TryGetValue(label, out var previous) && previous != rva)
                    throw new InvalidDataException("Fixed and relocatable fixture layouts differ.");
                symbols[label] = rva;
            }
            if ((symbols["split_pointer"] & 4095) != 4092)
                throw new InvalidDataException("DIR64 fixture must actually cross a page boundary.");
            var symbol = fixedImage ? "wit_pe_fixed_image" : "wit_pe_test_image";
            generated.AppendLine($"static const unsigned char {symbol}[] = {{");
            for (var i = 0; i < bytes.Length; i += 16)
                generated.AppendLine("    " + string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
            generated.AppendLine("};");
            Console.WriteLine($"{name}: {bytes.Length} file bytes, {h.SizeOfImage} mapped bytes; full image embedded.");
        }
        foreach (var symbol in symbols)
            generated.AppendLine($"#define WIT_PE_TEST_{symbol.Key.ToUpperInvariant()}_RVA 0x{symbol.Value:X}U");
        await File.WriteAllTextAsync(Path.Combine(output, "pe_image.h"), generated.ToString(), Encoding.ASCII);
    }

    // Turns an image linked at its load address into a fixed one: no relocation directory, relocations stripped,
    // no dynamic base. The relocation section stays as unused read-only data.
    private static void StripRelocations(byte[] bytes)
    {
        var nt = BitConverter.ToInt32(bytes, 60);
        var optional = nt + OPTIONAL_HEADER;
        var characteristics = BitConverter.ToUInt16(bytes, nt + COFF_CHARACTERISTICS);
        BitConverter.GetBytes((ushort)(characteristics | RELOCS_STRIPPED)).CopyTo(bytes, nt + COFF_CHARACTERISTICS);
        var dll = BitConverter.ToUInt16(bytes, optional + DLL_CHARACTERISTICS);
        BitConverter.GetBytes((ushort)(dll & ~DYNAMIC_BASE)).CopyTo(bytes, optional + DLL_CHARACTERISTICS);
        Array.Clear(bytes, optional + RELOCATION_DIRECTORY, 8);
    }

    #endregion
}
