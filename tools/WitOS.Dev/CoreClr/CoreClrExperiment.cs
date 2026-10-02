using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.NativeAot;
using WitOS.Dev.NativeAot.Acceptance;
using WitOS.Dev.Pe;

namespace WitOS.Dev.CoreClr;

internal static class CoreClrExperiment
{
    private const string PROFILE = "coreclr-reference";
    private static readonly JsonSerializerOptions JSON = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static Task RunAsync(string root)
        => RuntimeBootAttempt.RunInDirectoryAsync(Path.Combine(root, "artifacts/coreclr-source"), "coreclr-source",
            attempt => BuildAsync(root, attempt));

    private static async Task BuildAsync(string root, RuntimeBootAttempt attempt)
    {
        var output = Path.Combine(root, "artifacts/coreclr-source");
        var legacy = Path.Combine(output, "reference.json");
        if (File.Exists(legacy))
            File.Move(legacy, Path.Combine(attempt.RunDirectory, "previous-reference.json"));
        var pin = RuntimeExperiment.ReadLock(root);
        var profilePath = Path.Combine(root, "experiments/CoreClrProbe/profile.json");
        using var profile = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath));
        var contract = profile.RootElement;
        if (contract.GetProperty("schemaVersion").GetInt32() != 1 || contract.GetProperty("runtimeVersion").GetString() != pin.RuntimeVersion ||
            contract.GetProperty("runtimeCommit").GetString() != pin.RuntimeCommit || contract.GetProperty("packageCommit").GetString() != pin.PackageCommit)
            throw new InvalidDataException("CoreCLR profile and upstream package/source pin differ.");
        var reference = contract.GetProperty("reference");
        if (reference.GetProperty("os").GetString() != "windows" || reference.GetProperty("architecture").GetString() != "x64" ||
            reference.GetProperty("configuration").GetString() != "Release" || reference.GetProperty("outputProfile").GetString() != PROFILE ||
            reference.GetProperty("readyToRunExecution").GetBoolean() ||
            !reference.GetProperty("components").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "runtime", "jit" }))
            throw new InvalidDataException("Unsupported CoreCLR reference build profile.");
        var source = await RuntimeSourceBuild.PrepareSourceAsync(root, pin);
        Directory.CreateDirectory(output);
        var script = Path.Combine(output, "build-reference.cmd");
        string Quote(string value)
        {
            if (value.Any(c => c > 127) || value.IndexOfAny(['\"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n']) >= 0)
                throw new InvalidDataException("CoreCLR source build requires an ASCII path without command-shell metacharacters.");
            return '\"' + value + '\"';
        }
        await File.WriteAllTextAsync(script, "@echo off\r\nsetlocal\r\nset \"NumberOfCores=4\"\r\nset \"CMAKE_BUILD_PARALLEL_LEVEL=4\"\r\ncall " +
            Quote(Path.Combine(source, "src/coreclr/build-runtime.cmd")) +
            " -x64 -release -component runtime -component jit -subdir " + PROFILE + "\r\nexit /b %errorlevel%\r\n", Encoding.ASCII);
        Console.WriteLine("Building pinned CoreCLR/JIT WINDOWS REFERENCE; this does not execute in the guest.");
        var build = await Processes.RunAsync("cmd.exe", ["/d", "/c", script], root, 1800);
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), build.Output + build.Error);
        if (build.TimedOut || build.ExitCode != 0)
            throw new InvalidOperationException($"CoreCLR reference build failed (exit={build.ExitCode}, timeout={build.TimedOut}); see artifacts/coreclr-source/build.log.");
        var status = await Processes.RunAsync("git", ["-c", $"safe.directory={source.Replace('\\', '/')}", "status", "--porcelain", "--untracked-files=normal"], source);
        if (status.TimedOut || status.ExitCode != 0 || !string.IsNullOrWhiteSpace(status.Output))
            throw new InvalidDataException("Upstream CoreCLR source tree changed during build.");
        var msvc = await Toolchain.FindMsvcAsync(root);
        var binaries = Path.Combine(source, "artifacts/bin/coreclr", "windows.x64.Release", PROFILE);
        // Locate only within this profile, never the NativeAOT install or host runtime.
        if (!Directory.Exists(binaries))
            throw new DirectoryNotFoundException("CoreCLR profile output missing: " + binaries);
        var inventory = new List<object>();
        foreach (var name in new[] { "coreclr.dll", "clrjit.dll", "corerun.exe" })
        {
            var file = Path.Combine(binaries, name);
            if (!File.Exists(file))
                throw new FileNotFoundException("CoreCLR reference binary missing.", file);
            var imports = await Processes.RunAsync(Path.Combine(msvc, "dumpbin.exe"), ["/nologo", "/imports", file], root);
            if (imports.TimedOut || imports.ExitCode != 0)
                throw new InvalidDataException("CoreCLR import inventory failed: " + name);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".imports.txt"), imports.Output);
            var pe = NativeImports.Inspect(file);
            if (pe.HasClrHeader)
                throw new InvalidDataException("Expected native CoreCLR/JIT/host binary.");
            inventory.Add(new { file = attempt.Snapshot(file), sourceFile = file, sha256 = Hash(file), bytes = new FileInfo(file).Length, pe.DirectImports, pe.DelayImports });
        }
        var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet/shared/Microsoft.NETCore.App", pin.RuntimeVersion);
        var corelib = Path.Combine(framework, "System.Private.CoreLib.dll");
        var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(corelib).ProductVersion;
        if (product is null || !product.StartsWith(pin.RuntimeVersion + "-", StringComparison.Ordinal) || !product.EndsWith("+" + pin.PackageCommit, StringComparison.Ordinal))
            throw new InvalidDataException("Installed standard CoreLib does not match the pinned package VMR.");
        var probe = Path.Combine(root, "experiments/CoreClrProbe/CoreClrProbe.csproj");
        var app = Path.Combine(output, "portable");
        await Processes.RequireSuccessAsync("dotnet", ["build", probe, "--configuration", "Release", "--output", app], root);
        var assembly = Path.Combine(app, "CoreClrProbe.dll");
        var run = await Processes.RunAsync(Path.Combine(binaries, "corerun.exe"), [assembly], root, 120,
            new Dictionary<string, string>
            {
                ["CORE_ROOT"] = binaries,
                ["CORE_LIBRARIES"] = framework,
                ["WITOS_CORECLR_REFERENCE"] = binaries,
                ["DOTNET_ReadyToRun"] = "0"
            });
        await File.WriteAllTextAsync(Path.Combine(output, "hosted.log"), run.Output + run.Error + $"\nExit code: {run.ExitCode}\n");
        string[] expected = ["JitRuntimeIdentity", "DynamicMethodExecution", "RuntimeGenericReflection", "GcAndFinalization", "ExceptionsAndRoots",
            "ThreadPoolTaskAndGc", "AssemblyLoadContextFromStream", "SourceModule:coreclr.dll", "SourceModule:clrjit.dll"];
        var lines = run.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (run.TimedOut || run.ExitCode != 42 || run.Error.Length != 0 || !lines.SequenceEqual(expected.Select(name => "[CORECLR-PASS] " + name)))
            throw new InvalidDataException("Source-built CoreCLR/JIT hosted semantic probe failed; see artifacts/coreclr-source/hosted.log.");
        var capturedAssembly = attempt.Snapshot(assembly);
        var capturedCorelib = attempt.Snapshot(corelib);
        var capturedProfile = attempt.Snapshot(profilePath);
        var report = new
        {
            hostOnly = true,
            guestExecuted = false,
            profile = PROFILE,
            pin.RuntimeVersion,
            pin.RuntimeCommit,
            pin.PackageCommit,
            upstreamClean = true,
            buildEntry = "src/coreclr/build-runtime.cmd -x64 -release -component runtime -component jit",
            binaries,
            inventory,
            profileSha256 = Hash(capturedProfile),
            capturedProfile,
            capturedAssembly,
            capturedCorelib,
            guestContract = contract.GetProperty("guest"),
            hostedPassed = true,
            readyToRunDisabled = true,
            managedAssemblySha256 = Hash(assembly),
            corelibSha256 = Hash(corelib),
            corelibProductVersion = product,
            sources = new[] { "src/coreclr/components.cmake", "src/coreclr/dlls/mscoree/coreclr/CMakeLists.txt", "src/coreclr/jit/CMakeLists.txt" }
                .Select(path => new { path, worktreeSha256 = Hash(Path.Combine(source, path)) })
        };
        await CoreClrBoundary.WriteAsync(root, output, inventory.Select(value => JsonSerializer.SerializeToElement(value, JSON)).ToArray());
        // Reuse the Q1 commit/recovery protocol for hosted evidence too.
        // reference.json is a per-run diagnostic; acceptance/current-run are authoritative.
        await File.WriteAllTextAsync(Path.Combine(attempt.RunDirectory, "reference.json"), JsonSerializer.Serialize(report, JSON));
        foreach (var file in new[] { "build.log", "hosted.log", "platform-boundary.json", "platform-boundary.md" })
            attempt.Snapshot(Path.Combine(output, file));
        attempt.Publish(report);
        Console.WriteLine("[CORECLR-REFERENCE-BUILD-PASS] Actual upstream CoreCLR/JIT/host binaries and direct/delay imports inventoried. Guest port remains pending.");
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
