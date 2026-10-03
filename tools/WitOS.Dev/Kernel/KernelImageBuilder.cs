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

    #endregion

    #region Fields

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
        var msvc = await architecture.FindMsvcAsync(root);
        var output = outputDirectory ?? Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        var buildId = fixedBuildId ?? await BuildIdAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "build_info.h"), $"#define WITOS_BUILD_ID \"{buildId}\"\n",
            Encoding.ASCII);

        // Every scenario kernel is a self-test kernel where the target has self-test layers; the release kernel never is.
        var selfTest = scenario != RELEASE_SCENARIO && target.SelfTestLayers.Length > 0;
        // User fixtures are x64 images; ARM64 user mode arrives with A2.
        if (selfTest && architecture == KernelArchitecture.X64)
        {
            await UserImage.BuildAsync(root, output, msvc);
        }
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
        var objects = await CompileKernelAsync(root, output, msvc, scenario, selfTest, target, architecture);
        var efi = await LinkKernelAsync(root, output, msvc, objects, architecture);
        if (!selfTest)
        {
            await RequireReleaseMapAsync(Path.Combine(output, "WitOS.map"));
        }

        var disk = Path.Combine(output, $"WitOS-{architecture.Name}.img");
        FatImage.Create(disk, await File.ReadAllBytesAsync(efi), bootPackage, architecture.EfiName);
        await File.WriteAllTextAsync(Path.Combine(output, "build.txt"),
            $"Build: {buildId}\nScenario: {scenario}\nCompiler: {msvc}\nQEMU: {Toolchain.QEMU_VERSION}\n");
        Console.WriteLine($"Built {scenario}: {disk}");
        return disk;
    }

    #endregion

    #region Tools

    private static async Task<List<string>> CompileKernelAsync(string root, string output, string msvc, string scenario,
        bool selfTest, KernelTarget target, KernelArchitecture architecture)
    {
        var layers = KernelManifest.ReadLayers(root, target, selfTest);
        SCENARIO_DEFINES.TryGetValue(scenario, out var define);
        var objects = new List<string>();
        foreach (var layer in layers)
        {
            foreach (var source in layer.Sources)
            {
                var obj = Path.Combine(output, $"{layer.Name}.{Path.GetFileNameWithoutExtension(source)}.obj");
                objects.Add(obj);
                var arguments = new List<string>
                {
                    "/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/Od", "/Zi"
                };
                arguments.AddRange(target.Includes.Concat(layer.Includes).Select(include => $"/I{Path.Combine(root, include)}"));
                arguments.AddRange([$"/I{output}", $"/Fo{obj}", $"/Fd{Path.Combine(output, "compiler.pdb")}"]);
                if (selfTest)
                {
                    arguments.Add("/DWITOS_SELFTEST=1");
                }
                arguments.AddRange((target.Defines ?? []).Select(symbol => $"/D{symbol}=1"));
                if (define is not null)
                {
                    arguments.Add($"/D{define}=1");
                }
                arguments.Add(Path.Combine(root, source));
                await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), arguments, root);
            }
        }
        foreach (var layer in layers)
        {
            foreach (var source in layer.Assembly)
            {
                var obj = Path.Combine(output, $"{layer.Name}.{Path.GetFileNameWithoutExtension(source)}.obj");
                string[] assemble = architecture.Assembler == "armasm64.exe"
                    ? ["-nologo", "-g", "-o", obj, Path.Combine(root, source)]
                    : ["/nologo", "/c", "/Zi", $"/Fo{obj}", Path.Combine(root, source)];
                await Processes.RequireSuccessAsync(Path.Combine(msvc, architecture.Assembler), assemble, root);
                objects.Add(obj);
            }
        }
        return objects;
    }

    private static async Task RequireReleaseMapAsync(string map)
    {
        var symbols = (await File.ReadAllLinesAsync(map)).Where(line => SELF_TEST_SYMBOL.IsMatch(line)).Take(5).ToList();
        if (symbols.Count > 0)
        {
            throw new InvalidOperationException("Release kernel links self-test code:\n" + string.Join("\n", symbols));
        }
    }

    private static async Task<string> LinkKernelAsync(string root, string output, string msvc, List<string> objects,
        KernelArchitecture architecture)
    {
        var efi = Path.Combine(output, architecture.EfiName);
        List<string> linkArgs =
        [
            "/nologo", "/subsystem:efi_application", "/entry:efi_main", "/nodefaultlib", $"/machine:{architecture.MsvcTarget}",
            "/fixed:no", .. architecture.LinkOptions, "/incremental:no", "/debug:full", "/Brepro",
            $"/out:{efi}", $"/pdb:{Path.Combine(output, "WitOS.pdb")}", $"/map:{Path.Combine(output, "WitOS.map")}"
        ];
        linkArgs.AddRange(objects);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), linkArgs, root);

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
