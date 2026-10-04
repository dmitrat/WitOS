using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.NativeAot;
using WitOS.Dev.NativeAot.Acceptance;
namespace WitOS.Dev.CoreClr;

/// <summary>
/// Verifies the pinned hosting PAL file contracts against a hosted model of the WitOS file calls.
/// </summary>
internal static class CoreClrHostFilePal
{
    #region Functions

    /// <summary>
    /// Runs the host file PAL verification as one recorded attempt.
    /// </summary>
    /// <param name="root">Repository root.</param>
    internal static Task RunAsync(string root) => RuntimeBootAttempt.RunInDirectoryAsync(Path.Combine(root, "artifacts/coreclr-host-file-pal"), "coreclr-host-files", attempt => RunAsync(root, attempt));

    /// <summary>
    /// Fetches the pinned hosting PAL sources and generates their WitOS configuration header.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <returns>Path of the prepared PAL source.</returns>
    internal static async Task<string> PrepareAsync(string root, string output)
    {
        Directory.CreateDirectory(output);
        var pin = RuntimeExperiment.ReadLock(root);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "experiments/CoreClrHost/host-files.lock.json")));
        var item = contract.RootElement;
        if (item.GetProperty("runtimeCommit").GetString() != pin.RuntimeCommit)
            throw new InvalidDataException("Host PAL header pin mismatch.");
        using var client = new HttpClient();
        var raw = await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools/runtime-audit"), "runtime", pin.RuntimeCommit,
            item.GetProperty("path").GetString()!, item.GetProperty("sha256").GetString()!);
        var header = (await File.ReadAllTextAsync(raw)).Replace("\r\n", "\n");
        await File.WriteAllTextAsync(Path.Combine(output, "pal.h"),
            UpstreamPatches.Apply(root, "runtime", item.GetProperty("path").GetString()!, "pal.h", header));
        var configuration = item.GetProperty("configuration");
        var template = await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools/runtime-audit"), "runtime", pin.RuntimeCommit,
            configuration.GetProperty("path").GetString()!, configuration.GetProperty("sha256").GetString()!);
        var config = (await File.ReadAllTextAsync(template)).Replace("#cmakedefine CLR_SINGLE_FILE_HOST_ONLY", "/* CLR_SINGLE_FILE_HOST_ONLY is not enabled. */", StringComparison.Ordinal)
            .Replace("@CLI_CMAKE_PKG_RID@", "witos-x64", StringComparison.Ordinal).Replace("@CLI_CMAKE_COMMIT_HASH@", pin.RuntimeCommit, StringComparison.Ordinal)
            .Replace("@CLI_CMAKE_FALLBACK_OS@", "witos", StringComparison.Ordinal).Replace("@CLR_CMAKE_TARGET_OS@", "witos", StringComparison.Ordinal).Replace("@CLR_CMAKE_TARGET_ARCH@", "x64", StringComparison.Ordinal);
        if (config.Contains('@') || config.Contains("#cmakedefine", StringComparison.Ordinal))
            throw new InvalidDataException("Host PAL configure template changed.");
        await File.WriteAllTextAsync(Path.Combine(output, "configure.h"), config);
        return raw;
    }

    #endregion

    #region Tools

    private static async Task RunAsync(string root, RuntimeBootAttempt attempt)
    {
        var legacy = Path.Combine(root, "artifacts/coreclr-host-file-pal/reference.json");
        if (File.Exists(legacy))
            File.Move(legacy, Path.Combine(attempt.RunDirectory, "previous-reference.json"));
        var output = Path.Combine(attempt.RunDirectory, "build");
        Directory.CreateDirectory(output);
        var raw = await PrepareAsync(root, output);
        var configure = Path.Combine(output, "configure.h");
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var dll = await NativeLibraryImage.BuildAsync(root, output, msvc);
        var objects = new List<string>();
        foreach (var source in new[] { "src/Runtime.Native/library.c", "src/Runtime.Native/library_lifecycle.c", "src/Runtime.CoreClr/host_library.witos.cpp", "src/Runtime.CoreClr/host_library_discovery.witos.cpp", "tests/Runtime.NativeAot/host_library_reference.c", "tests/Runtime.NativeAot/host_library_allocation.cpp", "src/Runtime.Native/directory.c", "src/Runtime.Native/path.c", "src/Runtime.Native/current_directory.c", "src/Runtime.Native/file.c", "src/Runtime.Native/file_view.c", "tests/WitOS.Dev.Tests/Native/FileViewFaults.c", "src/Runtime.CoreClr/host_files.witos.cpp", "src/Runtime.CoreClr/host_paths.witos.cpp", "src/Runtime.CoreClr/host_directory.witos.cpp", "src/Runtime.CoreClr/host_environment.witos.cpp", "tests/Runtime.NativeAot/host_environment_reference.cpp", "tests/Runtime.NativeAot/host_file_pal.cpp" })
        {
            var cpp = source.EndsWith(".cpp", StringComparison.Ordinal);
            var obj = Path.Combine(output, Path.GetFileName(source) + ".obj");
            objects.Add(obj);
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/c",cpp?"/TP":"/TC",cpp?"/std:c++17":"/std:c17","/MD","/EHsc","/O2","/GS-","/Zl","/W4","/WX","/DNDEBUG","/DWITOS_HOST_FILES","/DFILE_VIEW_PAL_TEST","/DHOST_LIBRARY_PAL_TEST",
                "/I"+output,"/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"um"),"/I"+Path.Combine(sdk,"Include",version,"shared"),
                "/I"+Path.Combine(root,"src/Runtime.Native"),"/I"+Path.Combine(root,"src/Kernel/include"),"/Fo"+obj,Path.Combine(root,source)], root);
        }
        var exe = Path.Combine(output, "host-file-pal.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/out:" + exe, "/LIBPATH:" + Path.Combine(vc, "lib/x64"), "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "ucrt/x64"), "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "um/x64"), .. objects, "msvcrt.lib", "vcruntime.lib", "ucrt.lib", "msvcprt.lib", "kernel32.lib"], root);
        var run = await Processes.RunAsync(exe, [dll], root, 30);
        await File.WriteAllTextAsync(Path.Combine(output, "reference.log"), run.Output + run.Error);
        if (run.ExitCode != 0 || run.TimedOut || !run.Output.Contains("PASS: actual corehost PAL file signatures", StringComparison.Ordinal))
            throw new InvalidDataException("Host PAL file contract failed: " + run.ExitCode + " " + run.Output + run.Error);
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var report = new
        {
            hostOnly = true,
            guestHostExecuted = false,
            headerSha256 = Hash(raw),
            correctedHeaderSha256 = Hash(Path.Combine(output, "pal.h")),
            patch = UpstreamPatches.Describe(root, "runtime", "pal.h"),
            configureSha256 = Hash(configure),
            libraryFixtureSha256 = Hash(dll),
            objects = objects.Select(file => new { file, sha256 = Hash(file) }),
            exeSha256 = Hash(exe),
            scope = "Actual PAL file/path/directory/environment/library signatures with real C++ objects; controlled file syscall model and actual Windows environment reference, not guest C++ runtime closure"
        };
        File.Copy(raw, Path.Combine(attempt.RunDirectory, "pal.upstream.h"));
        attempt.Snapshot(Path.Combine(root, "experiments/CoreClrHost/host-files.lock.json"));
        await File.WriteAllTextAsync(Path.Combine(attempt.RunDirectory, "reference.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        attempt.Publish(report);
        Console.Write(run.Output);
    }

    #endregion
}
