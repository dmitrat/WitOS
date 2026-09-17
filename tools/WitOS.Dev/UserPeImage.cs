using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace WitOS.Dev;

internal static class UserPeImage
{
    public static async Task BuildAsync(string root, string output, string msvc, Dictionary<string, ulong> constants)
    {
        var obj = Path.Combine(output, "PeFixture.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", $"/I{output}", $"/Fo{obj}", Path.Combine(root, "tests", "User.X64", "image.asm")], root);
        var generated = new StringBuilder("/* Full PE files for guest parsing; generated, do not edit. */\n");
        var symbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var fixedImage in new[] { false, true })
        {
            var name = fixedImage ? "PeFixedFixture" : "PeFixture";
            var image = Path.Combine(output, name + ".pe");
            var map = Path.Combine(output, name + ".map");
            var preferredBase = fixedImage ? constants["WIT_USER_IMAGE_BASE"] : 0x180000000UL;
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
                ["/nologo", "/subsystem:native", "/entry:wit_pe_start", "/nodefaultlib", "/machine:x64",
                    fixedImage ? "/fixed" : "/fixed:no", fixedImage ? "/dynamicbase:no" : "/dynamicbase",
                    "/incremental:no", "/Brepro", "/section:USERDATA,RW", $"/base:0x{preferredBase:X}", $"/out:{image}", $"/map:{map}", obj], root);
            var bytes = await File.ReadAllBytesAsync(image);
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            var h = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("PE fixture header missing.");
            var sections = pe.PEHeaders.SectionHeaders;
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || h.Magic != PEMagic.PE32Plus ||
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
                if (!match.Success) throw new InvalidDataException($"Missing test symbol {label}.");
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
}
