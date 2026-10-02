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

    private const string BOOT_STORAGE_SOURCE = "src/Boot.Uefi/storage.c";

    #endregion

    #region Fields

    // Link order defines the image layout; fingerprints rely on it staying stable.
    private static readonly string[] KERNEL_SOURCES =
    [
        "src/Boot.Uefi/storage.c",
        "src/Kernel/package.c",
        "src/Kernel/storage.c",
        "src/Kernel/files.c",
        "src/Kernel.Arch.X64/user_files.c",
        "src/Kernel.Arch.X64/user_library.c",
        "src/Kernel.Arch.X64/user_library_readers.c",
        "src/Kernel.Arch.X64/user_library_tls.c",
        "src/Kernel.Arch.X64/user_library_lifecycle.c",
        "src/Kernel.Arch.X64/user_file_tests.c",
        "src/Kernel.Arch.X64/user_code.c",
        "src/Kernel/virtual_gap.c",
        "src/Kernel.Arch.X64/user_code_tests.c",
        "src/Kernel.Arch.X64/user_runtime_boot_tests.c",
        "src/Kernel.Arch.X64/user_exception.c",
        "src/Kernel.Arch.X64/user_runtime_unwind_tests.c",
        "src/Kernel.Arch.X64/user_stack_lease.c",
        "src/Kernel.Arch.X64/user_suspend_tests.c",
        "src/Kernel.Arch.X64/user_suspend.c",
        "src/Kernel.Arch.X64/user_thread_context.c",
        "src/Kernel.Arch.X64/user_cpu_context.c",
        "src/Kernel.Arch.X64/user_thread_name.c",
        "src/Kernel.Arch.X64/user_console.c",
        "src/Kernel.Arch.X64/user_apc.c",
        "src/Kernel.Arch.X64/user_objects.c",
        "src/Kernel.Arch.X64/user_reference.c",
        "src/Boot.Uefi/entropy.c",
        "src/Kernel/random.c",
        "src/Boot.Uefi/entry.c",
        "src/Boot.Uefi/image.c",
        "src/Kernel/kernel.c",
        "src/Kernel/memory.c",
        "src/Kernel/memory_tests.c",
        "src/Kernel.Arch.X64/platform.c",
        "src/Kernel.Arch.X64/clock.c",
        "src/Kernel.Arch.X64/cpu_cache.c",
        "src/Kernel.Arch.X64/cpu_cache_tests.c",
        "src/Kernel.Arch.X64/exceptions.c",
        "src/Kernel.Arch.X64/stacks.c",
        "src/Kernel.Arch.X64/paging.c",
        "src/Kernel.Arch.X64/scheduler.c",
        "src/Kernel/handles.c",
        "src/Kernel.Arch.X64/user_space.c",
        "src/Kernel.Arch.X64/user.c",
        "src/Kernel.Arch.X64/user_thread.c",
        "src/Kernel.Arch.X64/user_tls_tests.c",
        "src/Kernel.Arch.X64/user_dynamic_tls_tests.c",
        "src/Kernel.Arch.X64/user_process_exit_tests.c",
        "src/Kernel.Arch.X64/user_pal_tests.c",
        "src/Kernel.Arch.X64/user_pal_service_tests.c",
        "src/Kernel.Arch.X64/user_pal_background_tests.c",
        "src/Kernel.Arch.X64/user_pal_error_tests.c",
        "src/Kernel.Arch.X64/user_pal_module_tests.c",
        "src/Kernel.Arch.X64/user_pal_environment_tests.c",
        "src/Kernel.Arch.X64/user_runtime_config_tests.c",
        "src/Kernel.Arch.X64/user_tests.c",
        "src/Kernel.Arch.X64/user_memory_tests.c",
        "src/Kernel.Arch.X64/user_thread_tests.c",
        "src/Kernel/events.c",
        "src/Kernel.Arch.X64/user_wait.c",
        "src/Kernel.Arch.X64/user_pressure.c",
        "src/Kernel.Arch.X64/user_pressure_tests.c",
        "src/Kernel.Arch.X64/user_wait_tests.c",
        "src/Kernel.Arch.X64/user_wait_any_tests.c",
        "src/Kernel/pe.c",
        "src/Kernel/pe_imports.c",
        "src/Kernel/pe_exports.c",
        "src/Kernel.Arch.X64/user_image.c",
        "src/Kernel.Arch.X64/user_image_tests.c",
        "src/Kernel.Arch.X64/user_bootstrap_tests.c",
        "src/Kernel.Arch.X64/user_gc_tests.c"
    ];

    private static readonly string[] KERNEL_ASSEMBLY = ["entry", "context", "user_entry", "chkstk"];

    private static readonly Dictionary<string, string> SCENARIO_DEFINES = new()
    {
        ["invalid-boot-info"] = "WITOS_TEST_INVALID_BOOTINFO",
        ["overlapping-map"] = "WITOS_TEST_OVERLAPPING_MAP",
        ["breakpoint"] = "WITOS_TEST_BREAKPOINT",
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

    #region Build

    /// <summary>
    /// Builds the image of <paramref name="scenario"/> and returns the bootable FAT disk path.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="scenario">Scenario name; selects test defines and extra fixtures.</param>
    /// <param name="outputDirectory">Output directory; artifacts/x64/&lt;scenario&gt; when null.</param>
    /// <param name="fixedBuildId">Build id override; fingerprint builds use a fixed id so the
    /// Git revision string cannot shift image data between commits.</param>
    /// <returns>Path of the FAT disk image.</returns>
    public static async Task<string> BuildAsync(string root, string scenario, string? outputDirectory = null,
        string? fixedBuildId = null)
    {
        var msvc = await Toolchain.FindMsvcAsync(root);
        var output = outputDirectory ?? Path.Combine(root, "artifacts", "x64", scenario);
        Directory.CreateDirectory(output);
        var buildId = fixedBuildId ?? await BuildIdAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "build_info.h"), $"#define WITOS_BUILD_ID \"{buildId}\"\n",
            Encoding.ASCII);

        await UserImage.BuildAsync(root, output, msvc);
        if (scenario == "coreclr-memory")
        {
            await CoreClrMemoryImage.BuildAsync(root, output, msvc);
        }
        if (scenario == "runtime-config")
        {
            await RuntimeConfigProbe.BuildImageAsync(root, output, msvc);
        }
        if (scenario == "runtime-boot")
        {
            await EmbedRuntimeImageAsync(root, output);
        }

        var bootPackage = await BootPackage.BuildAsync(root, output, scenario == "coreclr-storage", scenario == "coreclr-memory");
        if (scenario == "coreclr-storage")
        {
            await CoreClrStorageImage.BuildAsync(root, output, msvc);
        }
        var objects = await CompileKernelAsync(root, output, msvc, scenario);
        var efi = await LinkKernelAsync(root, output, msvc, objects);

        var disk = Path.Combine(output, "WitOS-x64.img");
        FatImage.Create(disk, await File.ReadAllBytesAsync(efi), bootPackage);
        await File.WriteAllTextAsync(Path.Combine(output, "build.txt"),
            $"Build: {buildId}\nScenario: {scenario}\nCompiler: {msvc}\nQEMU: {Toolchain.QemuVersion}\n");
        Console.WriteLine($"Built {scenario}: {disk}");
        return disk;
    }

    private static async Task<List<string>> CompileKernelAsync(string root, string output, string msvc, string scenario)
    {
        var objects = new List<string>();
        SCENARIO_DEFINES.TryGetValue(scenario, out var define);
        foreach (var source in KERNEL_SOURCES)
        {
            var name = source == BOOT_STORAGE_SOURCE ? "boot_storage" : Path.GetFileNameWithoutExtension(source);
            var obj = Path.Combine(output, name + ".obj");
            objects.Add(obj);
            var arguments = new List<string>
            {
                "/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/Od", "/Zi",
                $"/I{Path.Combine(root, "src", "Kernel", "include")}", $"/I{Path.Combine(root, "tests", "User.X64")}",
                $"/I{output}", $"/Fo{obj}", $"/Fd{Path.Combine(output, "compiler.pdb")}"
            };
            if (define is not null)
            {
                arguments.Add($"/D{define}=1");
            }
            arguments.Add(Path.Combine(root, source));
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), arguments, root);
        }

        foreach (var assembly in KERNEL_ASSEMBLY)
        {
            var assemblyObject = Path.Combine(output, $"x64_{assembly}.obj");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
                ["/nologo", "/c", "/Zi", $"/Fo{assemblyObject}", Path.Combine(root, "src", "Kernel.Arch.X64", assembly + ".asm")],
                root);
            objects.Add(assemblyObject);
        }
        return objects;
    }

    private static async Task<string> LinkKernelAsync(string root, string output, string msvc, List<string> objects)
    {
        var efi = Path.Combine(output, "BOOTX64.EFI");
        var linkArgs = new List<string>
        {
            "/nologo", "/subsystem:efi_application", "/entry:efi_main", "/nodefaultlib", "/machine:x64",
            "/fixed:no", "/dynamicbase:no", "/incremental:no", "/debug:full", "/Brepro",
            $"/out:{efi}", $"/pdb:{Path.Combine(output, "WitOS.pdb")}", $"/map:{Path.Combine(output, "WitOS.map")}"
        };
        linkArgs.AddRange(objects);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), linkArgs, root);

        using var file = File.OpenRead(efi);
        using var pe = new PEReader(file);
        var header = pe.PEHeaders.PEHeader;
        if (header is null || (int)header.Subsystem != 10 ||
            pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || header.ImportTableDirectory.Size != 0)
        {
            throw new InvalidOperationException("Output must be an x64 EFI image with no imported OS/CRT functions.");
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
