using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.NativeAot;
using WitOS.Dev.NativeAot.Acceptance;
using WitOS.Dev.Pe;
namespace WitOS.Dev.CoreClr;

internal static class CoreClrHostReference
{
    internal static Task RunAsync(string root) => RuntimeBootAttempt.RunInDirectoryAsync(
        Path.Combine(root, "artifacts/coreclr-host-reference"), "coreclr-host", attempt => RunAsync(root, attempt));

    private static async Task RunAsync(string root, RuntimeBootAttempt attempt)
    {
        var pin = RuntimeExperiment.ReadLock(root);
        var source = await RuntimeSourceBuild.PrepareSourceAsync(root, pin);
        var gitArgs = new[] { "-c", "safe.directory=" + Forward(source), "-C", source };
        await RequireAsync("git", [.. gitArgs, "sparse-checkout", "add", "src/libraries/System.Runtime.InteropServices/gen"], root, 600);
        var output = Path.Combine(root, "artifacts/coreclr-host-reference");
        var artifact = Path.Combine(output, "artifacts");
        var generated = Path.Combine(artifact, "obj");
        var build = Path.Combine(generated, "win-x64.Release/corehost");
        var resources = Path.Combine(generated, "win-x64.Release/hostResourceFiles");
        var install = Path.Combine(artifact, "bin/win-x64.Release");
        var environment = new Dictionary<string, string> { { "NUGET_PACKAGES", Path.Combine(root, ".tools/host-nuget") } };
        var project = Path.Combine(source, "src/native/corehost/corehost.proj");
        string[] properties = ["-nologo","-p:Configuration=Release","-p:TargetArchitecture=x64","-p:TargetOS=windows",
            "-p:GenerateNativeVersionInfo=true","-p:VersionPrefix="+pin.RuntimeVersion,"-p:VersionSuffix=",
            "-p:SourceRevisionId="+pin.RuntimeCommit,"-p:ArtifactsDir="+Forward(artifact)+"/","-p:EnableSourceControlManagerQueries=false"];
        Console.WriteLine("Building upstream dotnet/hostfxr/hostpolicy WINDOWS REFERENCE. Guest hosting remains unimplemented.");
        await RequireAsync("dotnet", ["msbuild", project, "-restore", "-t:GenerateRuntimeVersionFile", .. properties], source, 600, environment);
        foreach (var component in new[] { "dotnet", "hostfxr", "hostpolicy", "comhost", "ijwhost", "nethost" })
            await RequireAsync("dotnet", ["msbuild",project,"-t:GenerateNativeVersionFile",..properties,
                "-p:NativeVersionFile="+Path.Combine(resources,component,"version_info.h"),"-p:AssemblyName="+component], source, 120, environment);
        var hook = Path.Combine(root, "experiments/CoreClrHost/version-includes.cmake");
        var script = Path.Combine(output, "build-reference.cmd");
        var configure = new[]{"-DCLI_CMAKE_PKG_RID=win-x64","-DCLI_CMAKE_FALLBACK_OS=win10","-DCLI_CMAKE_COMMIT_HASH="+pin.RuntimeCommit,
            "-DCLI_CMAKE_RESOURCE_DIR="+Forward(resources),"-DCMAKE_BUILD_TYPE=Release","-DCMAKE_EXPORT_COMPILE_COMMANDS=ON",
            "-DCMAKE_PROJECT_corehost_INCLUDE="+Forward(hook),"-DWITOS_HOST_VERSION_DIR="+Forward(generated)};
        var commands = new[]{"@echo off","setlocal","set CMAKE_BUILD_PARALLEL_LEVEL=4","set __Ninja=1",
            "set __CMakeBinDir="+Forward(install),
            "call "+Quote(Path.Combine(source,"eng/native/init-vs-env.cmd"))+" x64","if errorlevel 1 exit /b 1",
            "call "+Quote(Path.Combine(source,"eng/native/gen-buildsys.cmd"))+" "+Quote(Path.Combine(source,"src/native/corehost"))+" "+Quote(build)+" %VisualStudioVersion% x64 windows "+string.Join(' ',configure.Select(Quote)),
            "if errorlevel 1 exit /b 1","cmake --build "+Quote(build)+" --target install --config Release --parallel 4","exit /b %errorlevel%"};
        await File.WriteAllLinesAsync(script, commands, Encoding.ASCII);
        var result = await Processes.RunAsync("cmd.exe", ["/d", "/c", script], root, 1200);
        var log = Path.Combine(output, "build.log");
        await File.WriteAllTextAsync(log, result.Output + result.Error);
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidOperationException("Native host reference build failed; see " + log);
        var clean = await Processes.RunAsync("git", [.. gitArgs, "status", "--porcelain", "--untracked-files=normal"], root);
        if (clean.ExitCode != 0 || clean.TimedOut || !string.IsNullOrWhiteSpace(clean.Output))
            throw new InvalidDataException("Hosting source tree changed during build.");
        var binaries = Path.Combine(install, "corehost");
        var inventory = new List<object>();
        foreach (var name in new[] { "dotnet.exe", "hostfxr.dll", "hostpolicy.dll" })
        {
            var file = Path.Combine(binaries, name);
            var info = NativeImports.Inspect(file);
            if (info.HasClrHeader)
                throw new InvalidDataException("Expected genuine native hosting image: " + name);
            inventory.Add(new { file = attempt.Snapshot(file), sha256 = Hash(file), info.DirectImports, info.DelayImports });
        }
        await CoreClrBoundary.WriteAsync(root, output, inventory.Select(item => JsonSerializer.SerializeToElement(item, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ToArray());
        var cases = await CoreClrHostBinding.RunAsync(root, source, binaries, pin, attempt.RunDirectory);
        foreach (var file in new[]{log,script,hook,Path.Combine(build,"compile_commands.json"),Path.Combine(build,"CMakeCache.txt"),
            Path.Combine(generated,"runtime_version.h"),Path.Combine(generated,"_version.h"),Path.Combine(output,"platform-boundary.json"),Path.Combine(output,"platform-boundary.md")})
            attempt.Snapshot(file);
        var versions = new List<object>();
        foreach (var component in new[] { "dotnet", "hostfxr", "hostpolicy", "comhost", "ijwhost", "nethost" })
        {
            var copy = Path.Combine(attempt.RunDirectory, component + ".version_info.h");
            File.Copy(Path.Combine(resources, component, "version_info.h"), copy);
            versions.Add(new { file = copy, sha256 = Hash(copy) });
        }
        attempt.Publish(new
        {
            hostOnly = true,
            guestExecuted = false,
            pin.RuntimeVersion,
            pin.RuntimeCommit,
            pin.PackageCommit,
            inventory,
            versions,
            cases,
            versionPolicy = "Upstream MSBuild generated resources; VersionPrefix explicitly matches the package pin. Arcade local file-version sentinel retained; these are not Microsoft release binaries.",
            compileCommandsSha256 = Hash(Path.Combine(build, "compile_commands.json")),
            upstreamClean = true,
            sourceInputs = new[]{"tools/WitOS.Dev/CoreClr/CoreClrHostReference.cs","tools/WitOS.Dev/CoreClr/CoreClrHostBinding.cs","experiments/CoreClrHost/version-includes.cmake",
                "experiments/CoreClrHost/Probe/HostBindingProbe.csproj","experiments/CoreClrHost/Probe/Program.cs","experiments/CoreClrHost/Probe/packages.lock.json",
                "experiments/CoreClrHost/Dependency/HostBindingDependency.csproj","experiments/CoreClrHost/Dependency/Dependency.cs","experiments/CoreClrHost/Dependency/packages.lock.json"}
                .Select(path => new { path, sha256 = Hash(Path.Combine(root, path)) })
        });
        Console.WriteLine("[CORECLR-HOST-REFERENCE-PASS] Source-built host and standard runtimeconfig/deps binding; WINDOWS ONLY.");
    }
    private static async Task RequireAsync(string executable, string[] arguments, string directory, int seconds, IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = await Processes.RunAsync(executable, arguments, directory, seconds, environment);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException(executable + " failed: " + result.Output + result.Error);
    }
    internal static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    private static string Forward(string value) => value.Replace((char)92, '/');
    private static string Quote(string value)
    {
        if (value.Any(c => c < 32 || c > 126 || c == 34 || "%!^&|<>$;".Contains(c)))
            throw new InvalidDataException("Host reference build requires ASCII paths without shell/CMake metacharacters.");
        return ((char)34) + value + (char)34;
    }
}
