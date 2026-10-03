using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// Import descriptor parser on guarded inputs and real linker-built DLL graphs, compared with Windows (hosted).
/// </summary>
[TestFixture]
public sealed class PeImportsTests
{
    #region Functions

    [Test]
    public Task NativeImportDescriptorsTest() => RunAsync(TestEnvironment.Root, TestEnvironment.Scratch());

    [Test]
    [Category(TestCategories.PE_IMPORTS_ASAN)]
    [Explicit("Needs the pinned LLVM toolchain")]
    public Task NativeImportDescriptorsAsanTest() => RunAsync(TestEnvironment.Root, TestEnvironment.Scratch(), true);

    #endregion

    #region Tools

    private static async Task RunAsync(string root, string output, bool sanitize = false)
    {
        if (sanitize)
            await NativeCoverage.PrepareAsync(root);
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var provider = await NativeLibraryImage.BuildAsync(root, output, msvc);
        var fixtures = await NativeLibraryImage.BuildDependenciesAsync(root, output, msvc);
        var tlsFixture = await NativeTlsLibraryImage.BuildAsync(root, output, msvc);
        var exe = Path.Combine(output, "pe-imports.exe");
        await Processes.RequireSuccessAsync(sanitize ? Path.Combine(NativeCoverage.DirectoryPath(root), "bin/clang-cl.exe") : Path.Combine(msvc, "cl.exe"), ["/nologo","/MD","/TC","/std:c17","/W4","/WX",sanitize?"/O1":"/O2",..(sanitize?new[]{"/Zi","/clang:-fsanitize=address","-fuse-ld=lld"}:Array.Empty<string>()),
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"um"),"/I"+Path.Combine(sdk,"Include",version,"shared"),"/I"+Path.Combine(root,"src/Runtime.Native"),"/I"+Path.Combine(root,"src/Kernel/include"),
            "/Fo"+output+"/","/Fe"+exe,Path.Combine(root,"src/Kernel/pe.c"),Path.Combine(root,"src/Kernel/pe_exports.c"),Path.Combine(root,"src/Kernel/pe_imports.c"),Path.Combine(root,"tests/WitOS.Dev.Tests/Native/PeImports.c"),Path.Combine(root,"tests/WitOS.Dev.Tests/Native/HostIdentity.c"),
            "/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"ucrt/x64"),
            "/LIBPATH:"+Path.Combine(sdk,"Lib",version,"um/x64"),"kernel32.lib"], root);
        var run = await Processes.RunAsync(exe, [fixtures["dependent.dll"], fixtures["CycleA.dll"], provider, fixtures["init.dll"], fixtures["initparent.dll"], fixtures["initfail.dll"], fixtures["initparentfail.dll"], tlsFixture], output, 30, sanitize ? new Dictionary<string, string>
        {
            ["PATH"] = Path.Combine(NativeCoverage.DirectoryPath(root), "lib/clang/20/lib/windows") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
        } : null);
        await File.WriteAllTextAsync(Path.Combine(output, "pe-imports.log"), run.Output + run.Error);
        if (run.TimedOut || run.ExitCode != 0 || !run.Output.StartsWith("PASS: ", StringComparison.Ordinal))
            throw new InvalidDataException("PE imports cases failed: " + run.Output + run.Error);
        var mappingName = "Local\\WitOS.DllShutdown." + Guid.NewGuid().ToString("N");
        using (var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(mappingName, 4096))
        using (var view = mapping.CreateViewAccessor())
        {
            var shutdown = await Processes.RunAsync(exe, ["--shutdown-child", provider, fixtures["initparent.dll"], mappingName], output, 30, sanitize ? new Dictionary<string, string>
            {
                ["PATH"] = Path.Combine(NativeCoverage.DirectoryPath(root), "lib/clang/20/lib/windows") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            } : null);
            await File.WriteAllTextAsync(Path.Combine(output, "dll-shutdown-reference.log"), shutdown.Output + shutdown.Error);
            if (shutdown.TimedOut || shutdown.ExitCode != 0 || view.ReadUInt64(0) != 7311584)
                throw new InvalidDataException($"Windows DLL process shutdown mismatch: exit={shutdown.ExitCode}, timeout={shutdown.TimedOut}, trace={view.ReadUInt64(0)}");
        }
        Console.WriteLine("PASS: actual Windows process shutdown DLL order and nonnull reserved argument (shared-memory witness)");
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(output, "pe-imports.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            fullImageValidation = true,
            guestExecuted = false,
            addressSanitizer = sanitize,
            executableSha256 = Hash(exe),
            sourceSha256 = new[] { "src/Kernel/pe.c", "src/Kernel/pe_exports.c", "src/Kernel/pe_imports.c", "src/Kernel/include/witos/pe.h", "src/Kernel/include/witos/pe_imports.h", "tests/WitOS.Dev.Tests/Native/PeImports.c" }.ToDictionary(file => file, file => Hash(Path.Combine(root, file)))
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.Write(run.Output);
    }

    #endregion
}
