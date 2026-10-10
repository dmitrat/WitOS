using WitOS.Dev.Kernel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds the ring-3 test fixtures a self-test kernel embeds and the root task fixture.
/// </summary>
internal static class UserImage
{
    #region Constants

    // The headers whose constants place a fixture: the code window (user_layout.h) and the protections (user_abi.h).
    private static readonly string[] ABI_HEADERS =
    [
        "src/Kernel/include/witos/user_abi.h",
        "src/Kernel/include/witos/user_layout.h"
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
        ("waits", "WaitFixture", "wit_user_wait_image", "user_wait_image.h"),
        ("entry", "UserFixture", "wit_user_test_image", "user_image.h"),
        ("threads", "ThreadFixture", "wit_user_thread_image", "user_thread_image.h"),
        ("processors", "ProcessorFixture", "wit_user_processor_image", "user_processor_image.h")
    ];

    #endregion

    #region Functions

    /// <summary>
    /// Builds the mechanism fixtures of the architecture (plan step K8.3): each source of tests/User/Fixtures compiled
    /// by the pinned clang, linked by lld at WIT_USER_CODE and embedded as the C array its kernel self-test includes.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="architecture">Target architecture.</param>
    public static async Task BuildFixturesAsync(string root, string output, KernelArchitecture architecture) =>
        await BuildClangFixturesAsync(root, output, architecture, await ReadConstantsAsync(root));

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
        return constants;
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
