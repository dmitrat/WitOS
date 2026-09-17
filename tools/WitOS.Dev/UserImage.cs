using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace WitOS.Dev;

internal static class UserImage
{
    public static async Task BuildAsync(string root, string output, string msvc)
    {
        var constants = new Dictionary<string, ulong>(StringComparer.Ordinal);
        string[] headers =
        [
            "src/Kernel/include/witos/user_abi.h",
            "src/Kernel.Arch.X64/user_layout.h",
            "tests/User.X64/protocol.h"
        ];
        foreach (var header in headers)
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, header));
            foreach (Match match in Regex.Matches(source,
                @"^#define\s+(WIT_[A-Z0-9_]+)\s+(0x[0-9A-Fa-f]+|[0-9]+)(?:ULL|U)?\s*$", RegexOptions.Multiline))
            {
                var literal = match.Groups[2].Value;
                var value = literal.StartsWith("0x", StringComparison.Ordinal)
                    ? ulong.Parse(literal[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : ulong.Parse(literal, CultureInfo.InvariantCulture);
                constants.Add(match.Groups[1].Value, value);
            }
        }
        var includes = string.Join("\n", constants.Select(item => $"{item.Key} EQU 0{item.Value:X}h")) + "\n";
        await File.WriteAllTextAsync(Path.Combine(output, "user_abi.inc"), includes, Encoding.ASCII);
        await BuildFixtureAsync(root, output, msvc, constants, "entry", "UserFixture", "wit_user_test_image", "user_image.h");
        await BuildFixtureAsync(root, output, msvc, constants, "threads", "ThreadFixture", "wit_user_thread_image", "user_thread_image.h");
        await BuildFixtureAsync(root, output, msvc, constants, "waits", "WaitFixture", "wit_user_wait_image", "user_wait_image.h");
    }

    private static async Task BuildFixtureAsync(string root, string output, string msvc,
        Dictionary<string, ulong> constants, string source, string name, string symbol, string header)
    {
        var obj = Path.Combine(output, name + ".obj");
        var image = Path.Combine(output, name + ".pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", $"/I{output}", $"/Fo{obj}", Path.Combine(root, "tests", "User.X64", source + ".asm")], root);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
        [
            "/nologo", "/subsystem:native", "/entry:wit_user_start", "/nodefaultlib", "/machine:x64",
            "/fixed", "/dynamicbase:no", "/incremental:no", "/Brepro",
            $"/base:0x{constants["WIT_USER_BASE"]:X}", $"/out:{image}", obj
        ], root);

        var bytes = await File.ReadAllBytesAsync(image);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var headerInfo = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("User PE header missing.");
        var code = pe.PEHeaders.SectionHeaders.Single(section => section.Name == ".text");
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.CorHeader is not null ||
            headerInfo.Subsystem != Subsystem.Native ||
            headerInfo.ImageBase != constants["WIT_USER_BASE"] ||
            headerInfo.ImageBase + (uint)headerInfo.AddressOfEntryPoint != constants["WIT_USER_CODE"] ||
            headerInfo.AddressOfEntryPoint != code.VirtualAddress ||
            headerInfo.ImportTableDirectory.Size != 0 || headerInfo.BaseRelocationTableDirectory.Size != 0 ||
            code.VirtualSize <= 0 || code.VirtualSize > 4096 || code.VirtualSize > code.SizeOfRawData ||
            !code.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute) ||
            !code.SectionCharacteristics.HasFlag(SectionCharacteristics.MemRead) ||
            code.SectionCharacteristics.HasFlag(SectionCharacteristics.MemWrite))
            throw new InvalidDataException("User fixture must be a fixed x64 native image with one-page RX code and no imports/relocations.");

        var payload = bytes.AsSpan(code.PointerToRawData, code.VirtualSize).ToArray();
        var generated = new StringBuilder("/* Generated from the separately linked user fixture; do not edit. */\nstatic const unsigned char " + symbol + "[] = {\n");
        for (var index = 0; index < payload.Length; index += 16)
            generated.AppendLine("    " + string.Join(", ", payload.Skip(index).Take(16).Select(value => $"0x{value:X2}")) + ",");
        generated.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, header), generated.ToString(), Encoding.ASCII);
        Console.WriteLine($"{name}: {payload.Length} bytes of separately linked native code.");
    }
}
