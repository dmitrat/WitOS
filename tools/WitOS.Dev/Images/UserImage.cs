using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.NativeAot;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the ABI include files and every ring-3 test fixture embedded in the kernel test image.
/// </summary>
internal static class UserImage
{
    #region Constants

    private static readonly string[] ABI_HEADERS =
    [
        "src/Kernel/include/witos/user_abi.h",
        "src/Kernel/include/witos/user_layout.h",
        "tests/User/protocol.h"
    ];

    #endregion

    #region Functions

    /// <summary>
    /// Generates the ABI include files and builds every user fixture.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    public static async Task BuildAsync(string root, string output, string msvc)
    {
        var constants = await ReadConstantsAsync(root);
        var includes = string.Join("\n", constants.Select(item => $"{item.Key} EQU 0{item.Value:X}h")) + "\n";
        await File.WriteAllTextAsync(Path.Combine(output, "user_abi.inc"), includes, Encoding.ASCII);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", $"/I{output}", $"/Fo{Path.Combine(output, "native_error.obj")}",
                Path.Combine(root, "src", "Runtime.Pal.Win32", "X64", "native_error.asm")], root);
        await BuildFixtureAsync(root, output, msvc, constants, "entry", "UserFixture", "wit_user_test_image", "user_image.h");
        await BuildFixtureAsync(root, output, msvc, constants, "threads", "ThreadFixture", "wit_user_thread_image", "user_thread_image.h");
        await BuildFixtureAsync(root, output, msvc, constants, "waits", "WaitFixture", "wit_user_wait_image", "user_wait_image.h");
        await BuildFixtureAsync(root, output, msvc, constants, "coreclr_memory", "CoreClrMemoryFixture", "wit_coreclr_memory_image", "coreclr_memory_image.h");
        await UserPeImage.BuildAsync(root, output, msvc, constants);
        await UserBootstrapImage.BuildAsync(root, output, msvc);
        await RuntimePortImage.BuildAsync(root, output, msvc);
        await UserTlsImage.BuildAsync(root, output, msvc);
        await UserDynamicTlsImage.BuildAsync(root, output, msvc);
        await UserPalImage.BuildAsync(root, output, msvc);
        await PalFixtureImage.BuildAsync(root, output, msvc, "pal-background");
        await PalFixtureImage.BuildAsync(root, output, msvc, "pal-module");
        await PalFixtureImage.BuildAsync(root, output, msvc, "pal-environment");
        await PalFixtureImage.BuildAsync(root, output, msvc, "process-exit");
    }

    /// <summary>
    /// Generates the ABI header for ARM64 assembly and builds the ARM64 user fixtures.
    /// </summary>
    /// <remarks>
    /// armasm64 limits EQU to 32 bits, so the C preprocessor expands the ABI constants into each fixture before
    /// assembly, as the Windows SDK does for ARM64 assembly.
    /// </remarks>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC ARM64 cross tools.</param>
    public static async Task BuildArm64Async(string root, string output, string msvc)
    {
        var constants = await ReadConstantsAsync(root);
        var defines = string.Join("\n", constants.Select(item => $"#define {item.Key} 0x{item.Value:X}")) + "\n";
        await File.WriteAllTextAsync(Path.Combine(output, "user_abi_a64.h"), defines, Encoding.ASCII);
        await BuildArm64FixtureAsync(root, output, msvc, constants, "entry", "UserFixture", "wit_user_test_image", "user_image.h");
        await BuildArm64FixtureAsync(root, output, msvc, constants, "threads", "ThreadFixture", "wit_user_thread_image", "user_thread_image.h");
        await BuildArm64FixtureAsync(root, output, msvc, constants, "waits", "WaitFixture", "wit_user_wait_image", "user_wait_image.h");
        await UserPeImage.BuildArm64Async(root, output, msvc, constants);
    }

    #endregion

    #region Tools

    private static async Task<Dictionary<string, ulong>> ReadConstantsAsync(string root)
    {
        var constants = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var header in ABI_HEADERS)
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
        return constants;
    }

    private static async Task BuildFixtureAsync(string root, string output, string msvc,
        Dictionary<string, ulong> constants, string source, string name, string symbol, string header)
    {
        var obj = Path.Combine(output, name + ".obj");
        var image = Path.Combine(output, name + ".pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", $"/I{output}", $"/Fo{obj}", Path.Combine(root, "tests", "User.X64", source + ".asm")], root);
        await LinkFixtureAsync(root, msvc, constants, "x64", ["/fixed", "/dynamicbase:no"], obj, image);
        await EmbedFixtureAsync(output, constants, Machine.Amd64, image, name, symbol, header);
    }

    private static async Task BuildArm64FixtureAsync(string root, string output, string msvc,
        Dictionary<string, ulong> constants, string source, string name, string symbol, string header)
    {
        var preprocessed = Path.Combine(output, name + ".asm");
        var obj = Path.Combine(output, name + ".obj");
        var image = Path.Combine(output, name + ".pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo", "/EP", "/P", $"/Fi{preprocessed}", $"/I{output}", "/Tc",
                Path.Combine(root, "tests", "User.A64", source + ".asm")], root);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "armasm64.exe"),
            ["-nologo", "-o", obj, preprocessed], root);
        // ARM64 images are always relocatable; the fixture holds no absolute address, so it has no relocations.
        await LinkFixtureAsync(root, msvc, constants, "arm64", [], obj, image);
        await EmbedFixtureAsync(output, constants, Machine.Arm64, image, name, symbol, header);
    }

    private static Task LinkFixtureAsync(string root, string msvc, Dictionary<string, ulong> constants,
        string machine, string[] options, string obj, string image) =>
        Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
        [
            "/nologo", "/subsystem:native", "/entry:wit_user_start", "/nodefaultlib", $"/machine:{machine}",
            .. options, "/incremental:no", "/Brepro",
            $"/base:0x{constants["WIT_USER_BASE"]:X}", $"/out:{image}", obj
        ], root);

    // Checks that the fixture is one fixed page of RX code at WIT_USER_CODE and embeds that page as a C array.
    private static async Task EmbedFixtureAsync(string output, Dictionary<string, ulong> constants, Machine machine,
        string image, string name, string symbol, string header)
    {
        var bytes = await File.ReadAllBytesAsync(image);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var headerInfo = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("User PE header missing.");
        var code = pe.PEHeaders.SectionHeaders.Single(section => section.Name == ".text");
        if (pe.PEHeaders.CoffHeader.Machine != machine || pe.PEHeaders.CorHeader is not null ||
            headerInfo.Subsystem != Subsystem.Native ||
            headerInfo.ImageBase != constants["WIT_USER_BASE"] ||
            headerInfo.ImageBase + (uint)headerInfo.AddressOfEntryPoint != constants["WIT_USER_CODE"] ||
            headerInfo.AddressOfEntryPoint != code.VirtualAddress ||
            headerInfo.ImportTableDirectory.Size != 0 || headerInfo.BaseRelocationTableDirectory.Size != 0 ||
            code.VirtualSize <= 0 || code.VirtualSize > 4096 || code.VirtualSize > code.SizeOfRawData ||
            !code.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute) ||
            !code.SectionCharacteristics.HasFlag(SectionCharacteristics.MemRead) ||
            code.SectionCharacteristics.HasFlag(SectionCharacteristics.MemWrite))
            throw new InvalidDataException(
                $"User fixture must be a fixed {machine} native image with one-page RX code and no imports/relocations.");

        var payload = bytes.AsSpan(code.PointerToRawData, code.VirtualSize).ToArray();
        var generated = new StringBuilder("/* Generated from the separately linked user fixture; do not edit. */\nstatic const unsigned char " + symbol + "[] = {\n");
        for (var index = 0; index < payload.Length; index += 16)
            generated.AppendLine("    " + string.Join(", ", payload.Skip(index).Take(16).Select(value => $"0x{value:X2}")) + ",");
        generated.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, header), generated.ToString(), Encoding.ASCII);
        Console.WriteLine($"{name}: {payload.Length} bytes of separately linked native code.");
    }

    #endregion
}
