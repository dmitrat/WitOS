using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// Bounded libFuzzer/ASan smoke run of the PE parsers (hosted).
/// </summary>
[TestFixture]
public sealed class PeFuzzTests
{
    #region Functions

    [Test]
    [Category(TestCategories.PE_FUZZ)]
    [Explicit("Needs the pinned LLVM toolchain and the runtime-source image")]
    public Task PeFuzzSmokeTest() => RunAsync(TestEnvironment.Root, TestEnvironment.Scratch());

    #endregion

    #region Tools

    private static async Task RunAsync(string root, string output)
    {
        await NativeCoverage.PrepareAsync(root);
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var exe = Path.Combine(output, "pe-fuzzer.exe");
        await Processes.RequireSuccessAsync(Path.Combine(NativeCoverage.DirectoryPath(root), "bin/clang-cl.exe"),
            ["/nologo", "/MD", "/TC", "/std:c17", "/O1", "/Zi", "/W4", "/WX", "/clang:-fsanitize=fuzzer,address", "-fuse-ld=lld",
             "/I"+Path.Combine(vc,"include"), "/I"+Path.Combine(sdk,"Include",version,"ucrt"),
             "/I"+Path.Combine(root,"src/Kernel/include"), "/Fo"+output+"/", "/Fe"+exe,
             Path.Combine(root,"tests/WitOS.Dev.Tests/Native/PeFuzzer.c"), Path.Combine(root,"src/Kernel/pe.c"), Path.Combine(root,"src/Kernel/pe_imports.c"),Path.Combine(root,"src/Kernel/pe_exports.c"), "/link",
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
            ["PATH"] = Path.Combine(NativeCoverage.DirectoryPath(root), "lib/clang/20/lib/windows") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
        });
        await File.WriteAllTextAsync(Path.Combine(output, "native-fuzz.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut || !run.Error.Contains("Done 500 runs", StringComparison.Ordinal))
            throw new InvalidDataException("Native fuzz smoke failed; see " + output);
        await File.WriteAllTextAsync(Path.Combine(output, "native-fuzz.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            llvmVersion = NativeCoverage.VERSION,
            installerSha256 = Toolchain.LLVM_INSTALLER_SHA256,
            runs = 500,
            seed = 1462848041,
            profiles = new[] { "runtime-full", "library-runtime-unwind", "library-imports", "library-static-tls" },
            librarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(library))).ToLowerInvariant(),
            imageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image))).ToLowerInvariant(),
            sources = new[] { "src/Kernel/pe.c", "src/Kernel/pe_imports.c", "src/Kernel/include/witos/pe_imports.h", "src/Kernel/pe_exports.c", "src/Kernel/include/witos/unwind_metadata.h", "tests/WitOS.Dev.Tests/Native/PeFuzzer.c" }
                .Select(file => new { file, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, file)))).ToLowerInvariant() })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: 500 bounded libFuzzer/ASan parser runs; this is a smoke budget, not exhaustive fuzzing.");
    }

    #endregion
}
