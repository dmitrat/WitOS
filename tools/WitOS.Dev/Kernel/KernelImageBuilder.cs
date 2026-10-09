using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.CoreClr;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.NativeAot;

namespace WitOS.Dev.Kernel;

/// <summary>
/// Builds the x64 UEFI kernel image, its user fixtures and boot package for one scenario.
/// </summary>
internal static class KernelImageBuilder
{
    #region Constants

    /// <summary>
    /// Scenario of the release kernel: no self-tests, fixtures or WITOS_SELFTEST code.
    /// </summary>
    public const string RELEASE_SCENARIO = "release";

    /// <summary>
    /// The scenario whose root task is the first libc program (plan step S1.1).
    /// </summary>
    public const string LIBC_SCENARIO = "libc";

    /// <summary>
    /// The scenario that runs musl's libc-test (plan steps S1.3, S7.1): the system layer's root task, whose /bin/init
    /// starts every selected test in a process of its own, statically and dynamically linked.
    /// </summary>
    public const string LIBC_TEST_SCENARIO = "libc-test";

    /// <summary>
    /// The scenario whose root task runs the C++ exception scenarios and libc++ checks over the C++ runtime (plan step
    /// S4).
    /// </summary>
    public const string CXX_SCENARIO = "cxx";

    /// <summary>
    /// The scenario whose root task starts a static program of the boot package in a new process with libwitos's
    /// loader (plan step S5.2).
    /// </summary>
    public const string SPAWN_SCENARIO = "spawn";

    /// <summary>
    /// The scenario whose root task is the system layer's, the process manager, and whose /bin/init starts children
    /// with posix_spawn (plan step S6.1).
    /// </summary>
    public const string PROCESS_SCENARIO = "process";

    #endregion

    #region Fields

    /// <summary>
    /// Scenarios whose kernel is the release kernel: the release itself and the layer-2 scenarios (plan step T2.1b),
    /// which need no self-test and so none of the frozen line's MSVC fixtures, and build on every host.
    /// </summary>
    public static readonly string[] RELEASE_KERNEL_SCENARIOS =
        [RELEASE_SCENARIO, LIBC_SCENARIO, LIBC_TEST_SCENARIO, CXX_SCENARIO, SPAWN_SCENARIO, PROCESS_SCENARIO];

    // A release map must not name self-test code: test objects, self-test functions, fault triggers or workers.
    private static readonly Regex SELF_TEST_SYMBOL = new(
        @"self_test|selftest|_tests\.obj|wit_(?:x64|a64)_trigger_|wit_x64_worker\b|wit_worker_|fixture", RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> SCENARIO_DEFINES = new()
    {
        ["invalid-boot-info"] = "WITOS_TEST_INVALID_BOOTINFO",
        ["overlapping-map"] = "WITOS_TEST_OVERLAPPING_MAP",
        ["breakpoint"] = "WITOS_TEST_BREAKPOINT",
        ["undefined-instruction"] = "WITOS_TEST_UNDEFINED_INSTRUCTION",
        ["data-abort"] = "WITOS_TEST_DATA_ABORT",
        ["divide-error"] = "WITOS_TEST_DIVIDE_ERROR",
        ["invalid-opcode"] = "WITOS_TEST_INVALID_OPCODE",
        ["general-protection"] = "WITOS_TEST_GENERAL_PROTECTION",
        ["page-fault"] = "WITOS_TEST_PAGE_FAULT",
        ["double-fault"] = "WITOS_TEST_DOUBLE_FAULT",
        ["write-code"] = "WITOS_TEST_WRITE_CODE",
        ["execute-data"] = "WITOS_TEST_EXECUTE_DATA",
        ["guard-low"] = "WITOS_TEST_GUARD_LOW",
        ["guard-high"] = "WITOS_TEST_GUARD_HIGH",
        ["readonly-alias"] = "WITOS_TEST_READONLY_ALIAS",
        ["unmapped-alias"] = "WITOS_TEST_UNMAPPED_ALIAS",
        ["timeout"] = "WITOS_TEST_HANG",
        ["runtime-config"] = "WITOS_TEST_RUNTIME_CONFIG",
        ["runtime-boot"] = "WITOS_TEST_RUNTIME_BOOT",
        ["coreclr-memory"] = "WITOS_TEST_CORECLR_MEMORY",
        ["coreclr-storage"] = "WITOS_TEST_CORECLR_STORAGE"
    };

    #endregion

    #region Functions

    /// <summary>
    /// Builds the image of <paramref name="scenario"/> and returns the bootable FAT disk path.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="scenario">Scenario name; selects test defines and extra fixtures.</param>
    /// <param name="outputDirectory">Output directory; artifacts/&lt;architecture&gt;/&lt;scenario&gt; when null.</param>
    /// <param name="fixedBuildId">Build id override; fingerprint builds use a fixed id so the
    /// Git revision string cannot shift image data between commits.</param>
    /// <param name="architecture">Target architecture; x64 when null.</param>
    /// <returns>Path of the FAT disk image.</returns>
    public static async Task<string> BuildAsync(string root, string scenario, string? outputDirectory = null,
        string? fixedBuildId = null, KernelArchitecture? architecture = null)
    {
        architecture ??= KernelArchitecture.X64;
        var target = KernelManifest.ReadTarget(root, architecture.Name);
        // MSVC builds the frozen line's fixtures alone, found when a scenario needs them (plan step T2.1a): a host
        // without it builds the release kernel.
        string? found = null;
        async Task<string> Msvc() => found ??= await architecture.FindMsvcAsync(root);
        var output = outputDirectory ?? Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        var buildId = fixedBuildId ?? await BuildIdAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "build_info.h"), $"#define WITOS_BUILD_ID \"{buildId}\"\n",
            Encoding.ASCII);

        // A scenario kernel is a self-test kernel where the target has self-test layers, except the release kernel's
        // scenarios.
        var selfTest = !RELEASE_KERNEL_SCENARIOS.Contains(scenario) && target.SelfTestLayers.Length > 0;
        // Every x64 user fixture; ARM64 builds the fixtures that A2 has ported so far.
        if (selfTest && architecture == KernelArchitecture.X64)
        {
            await UserImage.BuildAsync(root, output, await Msvc());
        }
        else if (selfTest)
        {
            await UserImage.BuildArm64Async(root, output, await Msvc());
        }
        if (scenario == "coreclr-memory")
        {
            await CoreClrMemoryImage.BuildAsync(root, output, await Msvc());
        }
        if (scenario == "coreclr-storage")
        {
            // The .NET host over the delivered framework and application (P6.4.k1): the fixture boots as /dotnet.
            await CoreClrMemoryImage.BuildHostRuntimeAsync(root, output, await Msvc(),
                await CoreClrMemoryImage.BuildSupportAsync(root, output, await Msvc()));
        }
        if (scenario == "runtime-config")
        {
            await RuntimeConfigProbe.BuildImageAsync(root, output, await Msvc());
        }
        if (scenario == "runtime-boot")
        {
            await EmbedRuntimeImageAsync(root, output);
        }

        // The programs a root task starts from the package (S5.2).
        var programs = scenario == SPAWN_SCENARIO ? await Substrate.LibWitos.BuildProgramsAsync(root, output, architecture)
            : scenario == PROCESS_SCENARIO ? await Substrate.LibWitos.BuildProcessProgramsAsync(root, output, architecture)
            : scenario == LIBC_TEST_SCENARIO ? await Substrate.LibcTestSuite.BuildProgramsAsync(root, output, architecture)
            : [];
        var bootPackage = await BootPackage.BuildAsync(root, output, scenario == "coreclr-storage", scenario == "coreclr-memory",
            scenario is LIBC_SCENARIO or SPAWN_SCENARIO or PROCESS_SCENARIO, programs);
        // The root task's flat image (K4): every kernel, release or self-test, starts it from the boot disk.
        var rootTask = scenario == LIBC_SCENARIO ? await Substrate.MuslLibc.BuildRootAsync(root, output, architecture, "libc_hello.c")
            : scenario == LIBC_TEST_SCENARIO ? await Substrate.LibWitos.BuildRootTaskAsync(root, output, architecture)
            : scenario == CXX_SCENARIO ? await Substrate.LlvmRuntimes.BuildRootAsync(root, output, architecture)
            : scenario == SPAWN_SCENARIO ? await Substrate.LibWitos.BuildRootAsync(root, output, architecture)
            : scenario == PROCESS_SCENARIO ? await Substrate.LibWitos.BuildRootTaskAsync(root, output, architecture)
            : await UserImage.BuildRootAsync(root, output, architecture);
        if (scenario == "coreclr-storage")
        {
            await CoreClrStorageImage.BuildAsync(root, output, await Msvc());
        }
        var objects = await CompileKernelAsync(root, output, scenario, selfTest, target, architecture);
        var efi = await LinkKernelAsync(root, output, objects, architecture);
        if (!selfTest)
        {
            await RequireReleaseMapAsync(Path.Combine(output, "WitOS.map"));
        }

        var disk = Path.Combine(output, $"WitOS-{architecture.Name}.img");
        FatImage.Create(disk, await File.ReadAllBytesAsync(efi), bootPackage, architecture.EfiName, await File.ReadAllBytesAsync(rootTask));
        await File.WriteAllTextAsync(Path.Combine(output, "build.txt"),
            $"Build: {buildId}\nScenario: {scenario}\nKernel compiler: {KernelCompiler(architecture)}\nFixtures: {found ?? "none"}\n" +
            $"QEMU: {Toolchain.QEMU_VERSION}\n");
        Console.WriteLine($"Built {scenario}: {disk}");
        return disk;
    }

    #endregion

    #region Tools

    // The kernel on clang (plan step T3): C17 for the architecture's kernel triple, freestanding with no header but the
    // kernel's own, the compiler's helpers in assembly (compiler.S), no optimization as MSVC's /Od was, CodeView debug
    // records for the linker's PDB; the assembly is GNU syntax through clang's integrated assembler.
    private static async Task<List<string>> CompileKernelAsync(string root, string output, string scenario, bool selfTest,
        KernelTarget target, KernelArchitecture architecture)
    {
        Toolchain.RequireClang(root);
        var layers = KernelManifest.ReadLayers(root, target, selfTest);
        SCENARIO_DEFINES.TryGetValue(scenario, out var define);
        var objects = new List<string>();
        foreach (var layer in layers)
        {
            List<string> options =
            [
                $"--target={architecture.KernelTriple}", "-c", "-ffreestanding", "-nostdinc", "-fno-stack-protector", "-O0", "-g",
                "-gcodeview", "-Wall", "-Wextra", "-Werror"
            ];
            options.AddRange(target.Includes.Concat(layer.Includes).Select(include => "-I" + Path.Combine(root, include)));
            options.Add("-I" + output);
            if (selfTest)
            {
                options.Add("-DWITOS_SELFTEST=1");
            }
            options.AddRange((target.Defines ?? []).Select(symbol => $"-D{symbol}=1"));
            if (define is not null)
            {
                options.Add($"-D{define}=1");
            }
            foreach (var source in layer.Sources.Concat(layer.Assembly))
            {
                var obj = Path.Combine(output, $"{layer.Name}.{Path.GetFileNameWithoutExtension(source)}.obj");
                objects.Add(obj);
                string[] language = source.EndsWith(".c", StringComparison.Ordinal) ? ["-std=c17"] : [];
                await Processes.RequireSuccessAsync(Toolchain.Clang(root), [.. options, .. language, Path.Combine(root, source), "-o", obj], root);
            }
        }
        return objects;
    }

    private static string KernelCompiler(KernelArchitecture architecture) =>
        $"clang {Toolchain.LLVM_VERSION} for {architecture.KernelTriple}, lld-link";

    private static async Task RequireReleaseMapAsync(string map)
    {
        var symbols = (await File.ReadAllLinesAsync(map)).Where(line => SELF_TEST_SYMBOL.IsMatch(line)).Take(5).ToList();
        if (symbols.Count > 0)
        {
            throw new InvalidOperationException("Release kernel links self-test code:\n" + string.Join("\n", symbols));
        }
    }

    private static async Task<string> LinkKernelAsync(string root, string output, List<string> objects,
        KernelArchitecture architecture)
    {
        var efi = Path.Combine(output, architecture.EfiName);
        // /pdbaltpath:%_PDB% records the PDB's name alone, so that an image does not depend on the directory or the
        // host it was built on (plan step T2.1a).
        List<string> linkArgs =
        [
            "/nologo", "/subsystem:efi_application", "/entry:efi_main", "/nodefaultlib", $"/machine:{architecture.MsvcTarget}",
            "/fixed:no", .. architecture.LinkOptions, "/incremental:no", "/debug:full", "/Brepro", "/pdbaltpath:%_PDB%",
            $"/out:{efi}", $"/pdb:{Path.Combine(output, "WitOS.pdb")}", $"/map:{Path.Combine(output, "WitOS.map")}"
        ];
        linkArgs.AddRange(objects);
        // The pinned lld-link takes link.exe's options (plan step T3).
        await Processes.RequireSuccessAsync(Toolchain.LldLink(root), linkArgs, root);

        using var file = File.OpenRead(efi);
        using var pe = new PEReader(file);
        var header = pe.PEHeaders.PEHeader;
        if (header is null || (int)header.Subsystem != 10 ||
            pe.PEHeaders.CoffHeader.Machine != architecture.Machine || header.ImportTableDirectory.Size != 0)
        {
            throw new InvalidOperationException($"Output must be an {architecture.Name} EFI image with no imported OS/CRT functions.");
        }
        return efi;
    }

    // The runtime-boot kernel embeds the hash-verified prepared runtime image.
    private static async Task EmbedRuntimeImageAsync(string root, string output)
    {
        var driver = Path.Combine(root, "artifacts/runtime-readiness/guest-driver/WitOS.NativeAotBoot.pe");
        var bytes = await File.ReadAllBytesAsync(driver);
        var evidenceText = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(driver)!, "image.json"));
        using (var evidence = JsonDocument.Parse(evidenceText))
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (hash != evidence.RootElement.GetProperty("sha256").GetString())
            {
                throw new InvalidDataException("Prepared runtime image hash changed; rebuild runtime-boot.");
            }
            var reportRva = evidence.RootElement.GetProperty("abruptReportRva").GetUInt32();
            await File.WriteAllTextAsync(Path.Combine(output, "runtime_report.h"),
                $"#define WIT_RUNTIME_ABRUPT_REPORT_RVA {reportRva}U\n", Encoding.ASCII);
            await File.WriteAllTextAsync(Path.Combine(output, "runtime-input.json"), JsonSerializer.Serialize(new
            {
                file = driver,
                sha256 = hash,
                fileBytes = bytes.Length,
                buildEvidence = evidence.RootElement.Clone()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        await File.WriteAllBytesAsync(Path.Combine(output, "runtime-image.pe"), bytes);
        var header = new StringBuilder("static const unsigned char wit_runtime_boot_image[] = {\n");
        for (var i = 0; i < bytes.Length; i += 16)
        {
            header.AppendLine("    " + string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        }
        header.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, "runtime_boot_image.h"), header.ToString(), Encoding.ASCII);
    }

    private static async Task<string> BuildIdAsync(string root)
    {
        // Trust only this explicitly selected workspace; do not change global Git settings.
        string[] prefix = ["-c", $"safe.directory={root.Replace('\\', '/')}"];
        var commit = await Processes.RunAsync("git", [.. prefix, "rev-parse", "--short=12", "HEAD"], root);
        var id = commit.Output.Trim();
        if (commit.ExitCode != 0 || !Regex.IsMatch(id, "^[0-9a-f]{7,40}$"))
        {
            return "uncommitted";
        }
        var state = await Processes.RunAsync("git", [.. prefix, "status", "--porcelain"], root);
        if (state.ExitCode != 0 || state.TimedOut)
        {
            throw new InvalidOperationException("Could not determine Git worktree state for build identification.");
        }
        return id + (state.Output.Length == 0 ? "" : "-dirty");
    }

    #endregion
}
