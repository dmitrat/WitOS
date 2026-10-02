using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev;

internal static class NativeCoverage
{
    internal const string Version = Toolchain.LlvmVersion;
    internal static string DirectoryPath(string root) => Path.Combine(root, ".tools", "llvm-" + Version);

    internal static async Task PrepareAsync(string root)
    {
        var installer = await Toolchain.RequireLlvmInstallerAsync(root);
        // Always extract verified bytes: cached installed executables are not a source pin.
        await Processes.RequireSuccessAsync(Toolchain.SevenZip(),
            ["x", installer, "-o" + DirectoryPath(root), "-y", "-bso0", "-bsp0"], root);
    }

    internal static async Task FuzzAsync(string root, string output)
    {
        await PrepareAsync(root);
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var exe = Path.Combine(output, "pe-fuzzer.exe");
        await Processes.RequireSuccessAsync(Path.Combine(DirectoryPath(root), "bin/clang-cl.exe"),
            ["/nologo", "/MD", "/TC", "/std:c17", "/O1", "/Zi", "/W4", "/WX", "/clang:-fsanitize=fuzzer,address", "-fuse-ld=lld",
             "/I"+Path.Combine(vc,"include"), "/I"+Path.Combine(sdk,"Include",version,"ucrt"),
             "/I"+Path.Combine(root,"src/Kernel/include"), "/Fo"+output+"/", "/Fe"+exe,
             Path.Combine(root,"tests/WitOS.Dev.Tests/PeFuzzer.c"), Path.Combine(root,"src/Kernel/pe.c"), Path.Combine(root,"src/Kernel/pe_imports.c"),Path.Combine(root,"src/Kernel/pe_exports.c"), "/link",
             "/LIBPATH:"+Path.Combine(vc,"lib/x64"), "/LIBPATH:"+Path.Combine(sdk,"Lib",version,"ucrt/x64"),
             "/LIBPATH:"+Path.Combine(sdk,"Lib",version,"um/x64")], root);
        var corpus = Path.Combine(output, "fuzz-corpus");
        Directory.CreateDirectory(corpus);
        var image = Path.Combine(root, "artifacts/runtime-readiness/guest-driver/WitOS.NativeAotBoot.pe");
        File.Copy(image, Path.Combine(corpus, "valid.pe"));
        var library = await NativeLibraryImage.BuildAsync(root, output, msvc);
        File.Copy(library, Path.Combine(corpus, "library.dll"));
        var dependencies = await NativeLibraryImage.BuildDependenciesAsync(root, output, msvc);
        var tls = await NativeTlsLibraryImage.BuildAsync(root, output, msvc);
        File.Copy(tls, Path.Combine(corpus, "statictls.dll"));
        foreach (var dependency in dependencies)
            File.Copy(dependency.Value, Path.Combine(corpus, dependency.Key));
        await File.WriteAllBytesAsync(Path.Combine(corpus, "header.bin"), (await File.ReadAllBytesAsync(image))[..64]);
        string[] arguments = [corpus, "-runs=500", "-seed=1462848041", "-max_len=1048576", "-timeout=5", "-rss_limit_mb=512", "-artifact_prefix=" + output + "/"];
        var run = await Processes.RunAsync(exe, arguments, output, 120, new Dictionary<string, string>
        {
            ["PATH"] = Path.Combine(DirectoryPath(root), "lib/clang/20/lib/windows") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
        });
        await File.WriteAllTextAsync(Path.Combine(output, "native-fuzz.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut || !run.Error.Contains("Done 500 runs", StringComparison.Ordinal))
            throw new InvalidDataException("Native fuzz smoke failed; see " + output);
        await File.WriteAllTextAsync(Path.Combine(output, "native-fuzz.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            llvmVersion = Version,
            installerSha256 = Toolchain.LlvmInstallerSha256,
            runs = 500,
            seed = 1462848041,
            profiles = new[] { "runtime-full", "library-runtime-unwind", "library-imports", "library-static-tls" },
            librarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(library))).ToLowerInvariant(),
            imageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image))).ToLowerInvariant(),
            sources = new[] { "src/Kernel/pe.c", "src/Kernel/pe_imports.c", "src/Kernel/include/witos/pe_imports.h", "src/Kernel/pe_exports.c", "src/Kernel/include/witos/unwind_metadata.h", "tests/WitOS.Dev.Tests/PeFuzzer.c" }
                .Select(file => new { file, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, file)))).ToLowerInvariant() })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: 500 bounded libFuzzer/ASan parser runs; this is a smoke budget, not exhaustive fuzzing.");
    }

    internal static async Task ReportAsync(string root, string output, string executable)
    {
        var bin = Path.Combine(DirectoryPath(root), "bin");
        var profile = Path.Combine(output, "pe-corpus.profdata");
        await Processes.RequireSuccessAsync(Path.Combine(bin, "llvm-profdata.exe"),
            ["merge", "-sparse", Path.Combine(output, "pe-corpus.profraw"), "-o", profile], root);
        var sources = new[] { "src/Kernel/pe.c", "src/Kernel/include/witos/unwind_metadata.h" }.Select(p => Path.Combine(root, p)).ToArray();
        var result = await Processes.RunAsync(Path.Combine(bin, "llvm-cov.exe"),
            ["export", executable, "-instr-profile=" + profile, "-summary-only", .. sources], root);
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidDataException("LLVM coverage export failed: " + result.Error);
        await File.WriteAllTextAsync(Path.Combine(output, "native-coverage.json"), result.Output);
        using var json = JsonDocument.Parse(result.Output);
        foreach (var file in json.RootElement.GetProperty("data")[0].GetProperty("files").EnumerateArray())
        {
            var branch = file.GetProperty("summary").GetProperty("branches");
            Console.WriteLine($"COVERAGE: {Path.GetFileName(file.GetProperty("filename").GetString())}: branches {branch.GetProperty("covered")}/{branch.GetProperty("count")} ({branch.GetProperty("percent")}%)");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "native-coverage-profile.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            llvmVersion = Version,
            installerSha256 = Toolchain.LlvmInstallerSha256,
            addressSanitizer = true,
            cases = 555,
            structuralVerdictCases = 26,
            profile = "clang-cl /O0, LLVM branch instrumentation + ASan; actual common kernel parser and metadata header",
            executableSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable))).ToLowerInvariant(),
            sources = sources.Select(file => new { file, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant() })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
