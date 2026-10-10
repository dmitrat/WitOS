using WitOS.Dev.Kernel;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;

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
        "src/Kernel/include/witos/user_abi_frozen.h",
        "src/Kernel/include/witos/wait_objects.h",
        "src/Kernel/include/witos/thread_info.h",
        "src/Kernel/include/witos/thread_reference.h",
        "src/Kernel/include/witos/process.h",
        "src/Kernel/include/witos/processor.h",
        "src/Kernel/include/witos/thread_context.h",
        "src/Kernel/include/witos/exception.h",
        "src/Kernel/include/witos/cpu_context_info.h",
        "src/Kernel/include/witos/channels.h",
        "src/Kernel/include/witos/memory_object.h",
        "src/Kernel/include/witos/device.h",
        "src/Kernel/include/witos/dma.h",
        "src/Kernel/include/witos/root.h",
        "src/Kernel/include/witos/flat.h",
        "src/Kernel/include/witos/user_layout.h",
        "src/Kernel/include/witos/limits.h",
        "tests/User/protocol.h"
    ];

    // The root task fixture's sources in tests/User, linked in this order.
    private static readonly string[] ROOT_SOURCES = ["root.c", "root_mechanisms.c"];

    // The mechanism fixtures in C (plan step K8.3), one source in tests/User/Fixtures for both ISAs: the source, the
    // fixture's name, the C array the kernel self-test embeds and the header that holds it.
    private static readonly (string Source, string Name, string Symbol, string Header)[] CLANG_FIXTURES =
    [
        ("channels", "ChannelFixture", "wit_user_channel_image", "user_channel_image.h"),
        ("exceptions", "ExceptionFixture", "wit_user_exception_image", "user_exception_image.h"),
        ("memory_objects", "MemoryObjectFixture", "wit_user_memory_object_image", "user_memory_object_image.h"),
        ("devices", "DeviceFixture", "wit_user_device_image", "user_device_image.h"),
        ("interrupts", "InterruptFixture", "wit_user_interrupt_image", "user_interrupt_image.h"),
        ("virtio", "VirtioFixture", "wit_user_virtio_image", "user_virtio_image.h"),
        ("threads2", "Thread2Fixture", "wit_user_thread2_image", "user_thread2_image.h"),
        ("processes", "ProcessFixture", "wit_user_process_image", "user_process_image.h"),
        ("processors", "ProcessorFixture", "wit_user_processor_image", "user_processor_image.h")
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
        var constants = await PrepareAbiAsync(root, output);
        await BuildFixtureAsync(root, output, msvc, constants, "entry", "UserFixture", "wit_user_test_image", "user_image.h");
        await BuildFixtureAsync(root, output, msvc, constants, "threads", "ThreadFixture", "wit_user_thread_image", "user_thread_image.h");
        await BuildFixtureAsync(root, output, msvc, constants, "waits", "WaitFixture", "wit_user_wait_image", "user_wait_image.h");
        await BuildClangFixturesAsync(root, output, KernelArchitecture.X64, constants);
        await UserPeImage.BuildAsync(root, output, msvc, constants);
        await UserBootstrapImage.BuildAsync(root, output, msvc);
        await UserTlsImage.BuildAsync(root, output, msvc);
    }

    /// <summary>
    /// Generates user_abi.inc, the ABI constants for x64 assembly.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <returns>The ABI constants.</returns>
    public static async Task<Dictionary<string, ulong>> PrepareAbiAsync(string root, string output)
    {
        var constants = await ReadConstantsAsync(root);
        var includes = string.Join("\n", constants.Select(item => $"{item.Key} EQU 0{item.Value:X}h")) + "\n";
        await File.WriteAllTextAsync(Path.Combine(output, "user_abi.inc"), includes, Encoding.ASCII);
        return constants;
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
        await BuildClangFixturesAsync(root, output, KernelArchitecture.Arm64, constants);
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
                @"^#define\s+(WIT_[A-Z0-9_]+)\s+(0x[0-9A-Fa-f]+|[0-9]+)(?:ULL|U)?\s*(?:/\*.*\*/)?\s*$", RegexOptions.Multiline))
            {
                var literal = match.Groups[2].Value;
                var value = literal.StartsWith("0x", StringComparison.Ordinal)
                    ? ulong.Parse(literal[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : ulong.Parse(literal, CultureInfo.InvariantCulture);
                constants.Add(match.Groups[1].Value, value);
            }
        }
        // The frozen line's names are aliases of the kernel's constants (user_abi_frozen.h).
        foreach (var header in ABI_HEADERS)
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, header));
            foreach (Match match in Regex.Matches(source, @"^#define\s+(WIT_[A-Z0-9_]+)\s+(WIT_[A-Z0-9_]+)\s*$", RegexOptions.Multiline))
            {
                if (constants.TryGetValue(match.Groups[2].Value, out var value))
                {
                    constants.Add(match.Groups[1].Value, value);
                }
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

    // A mechanism fixture of tests/User/Fixtures built by the pinned clang and lld (plan step K8.3): one executable
    // segment at WIT_USER_CODE whose first byte is the entry, within the code window wit_user_create maps (up to the
    // startup block at WIT_USER_INFO), embedded as a C array.
    private static async Task BuildClangFixturesAsync(string root, string output, KernelArchitecture architecture,
        Dictionary<string, ulong> constants)
    {
        var code = constants["WIT_USER_CODE"];
        var window = constants["WIT_USER_INFO"] - code;
        var executable = (uint)(constants["WIT_MEMORY_READ"] | constants["WIT_MEMORY_EXECUTE"]);
        foreach (var (source, name, symbol, header) in CLANG_FIXTURES)
        {
            var obj = Path.Combine(output, name + ".o");
            var image = Path.Combine(output, name + ".elf");
            // Optimized for size: a fixture lives in the code window below the startup block.
            await CompileFreestandingAsync(root, architecture, Path.Combine(root, "tests", "User", "Fixtures", source + ".c"), obj,
                "-Os", "-I", Path.Combine(root, "tests", "User"));
            await Processes.RequireSuccessAsync(Toolchain.Lld(root),
            [
                "-o", image, "-static", "--no-dynamic-linker", "--build-id=none", "-z", "max-page-size=4096", "-z", "norelro",
                "--gc-sections", "-T", Path.Combine(root, "tests", "User", "Fixtures", "fixture.ld"), obj
            ], root);
            var (entry, segments) = FlatImage.ParseElf(await File.ReadAllBytesAsync(image), architecture.ElfMachine);
            if (segments.Count != 1 || segments[0].Address != code || entry != code || segments[0].Protection != executable ||
                segments[0].Data.Length == 0 || segments[0].MemorySize > window)
                throw new InvalidDataException(
                    $"{name} must be executable code at WIT_USER_CODE within the code window that starts with its entry, and no data.");
            await EmbedAsync(output, segments[0].Data, name, symbol, header);
        }
    }

    // A freestanding layer-2 compilation by the pinned clang for the architecture's triple: no libc and no runtime, the
    // ABI-1 transport of the sysroot and the kernel's ABI headers, and the options given, which come last.
    private static Task CompileFreestandingAsync(string root, KernelArchitecture architecture, string source, string obj,
        params string[] options) =>
        Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            $"--target={architecture.Triple}", "-std=c11", "-O2", "-ffreestanding", "-fno-builtin", "-nostdlib", "-nostdlibinc",
            "-fPIE", "-fno-plt", "-fno-stack-protector", "-fno-asynchronous-unwind-tables", "-fno-unwind-tables",
            "-Wall", "-Wextra", "-Werror", .. architecture.ClangOptions,
            "-I", Path.Combine(root, "src", "Sysroot", "include"), "-I", Path.Combine(root, "src", "Kernel", "include"),
            .. options, "-c", source, "-o", obj
        ], root);

    private static Task LinkFixtureAsync(string root, string msvc, Dictionary<string, ulong> constants,
        string machine, string[] options, string obj, string image, string baseKey = "WIT_USER_BASE") =>
        Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
        [
            "/nologo", "/subsystem:native", "/entry:wit_user_start", "/nodefaultlib", $"/machine:{machine}",
            .. options, "/incremental:no", "/Brepro",
            $"/base:0x{constants[baseKey]:X}", $"/out:{image}", obj
        ], root);

    /// <summary>
    /// Builds the root task fixture of the architecture as a flat image (plan steps K4 and T1): its C sources
    /// (tests/User/root.c and, since K8.2, root_mechanisms.c) compiled by the pinned clang for the architecture's
    /// triple against the sysroot's ABI-1
    /// transport header and the kernel's ABI headers, with no libc and no runtime, linked by lld as a static ELF at
    /// the component's image window (tests/User/root.ld) and converted by <see cref="FlatImage"/>; also embedded
    /// as a C array for the kernel self-test. Built for every scenario, since the release kernel starts it.
    /// </summary>
    /// <returns>Path of the flat image to place on the boot disk.</returns>
    public static async Task<string> BuildRootAsync(string root, string output, KernelArchitecture architecture)
    {
        var image = Path.Combine(output, "RootFixture.elf");
        var objects = new List<string>();
        foreach (var source in ROOT_SOURCES)
        {
            var obj = Path.Combine(output, "RootFixture." + Path.GetFileNameWithoutExtension(source) + ".o");
            await CompileFreestandingAsync(root, architecture, Path.Combine(root, "tests", "User", source), obj);
            objects.Add(obj);
        }
        await Processes.RequireSuccessAsync(Toolchain.Lld(root),
        [
            "-o", image, "-static", "--no-dynamic-linker", "--build-id=none", "-z", "max-page-size=4096", "-z", "norelro",
            "--gc-sections", "-T", Path.Combine(root, "tests", "User", "root.ld"), .. objects
        ], root);
        return await FlatImage.FromElfAsync(output, architecture.ElfMachine, image, "RootFixture", "wit_user_root_image", "user_root_image.h");
    }

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

        await EmbedAsync(output, bytes.AsSpan(code.PointerToRawData, code.VirtualSize).ToArray(), name, symbol, header);
    }

    // Writes a fixture's code as the C array the kernel self-test embeds.
    private static async Task EmbedAsync(string output, byte[] payload, string name, string symbol, string header)
    {
        var generated = new StringBuilder("/* Generated from the separately linked user fixture; do not edit. */\nstatic const unsigned char " + symbol + "[] = {\n");
        for (var index = 0; index < payload.Length; index += 16)
            generated.AppendLine("    " + string.Join(", ", payload.Skip(index).Take(16).Select(value => $"0x{value:X2}")) + ",");
        generated.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, header), generated.ToString(), Encoding.ASCII);
        Console.WriteLine($"{name}: {payload.Length} bytes of separately linked native code.");
    }

    #endregion
}
