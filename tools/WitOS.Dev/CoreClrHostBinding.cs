using System.Text.Json;
using System.Text.Json.Nodes;
namespace WitOS.Dev;

internal static class CoreClrHostBinding
{
    internal static async Task<object> RunAsync(string root, string source, string binaries, RuntimeExperiment.SourceLock pin, string attempt)
    {
        var pointerPath = Path.Combine(root, "artifacts/coreclr-source/current-run.json");
        using var pointer = JsonDocument.Parse(await File.ReadAllTextAsync(pointerPath));
        var success = pointer.RootElement.GetProperty("lastSuccess");
        var accepted = success.GetProperty("file").GetString()!;
        if (CoreClrHostReference.Hash(accepted) != success.GetProperty("sha256").GetString())
            throw new InvalidDataException("CoreCLR reference acceptance hash mismatch.");
        using var runtime = JsonDocument.Parse(await File.ReadAllTextAsync(accepted));
        var record = runtime.RootElement;
        if (record.GetProperty("RuntimeCommit").GetString() != pin.RuntimeCommit || record.GetProperty("PackageCommit").GetString() != pin.PackageCommit || !record.GetProperty("hostOnly").GetBoolean())
            throw new InvalidDataException("CoreCLR reference does not match this host profile.");
        var kit = Path.Combine(attempt, "dotnet");
        var fxr = Path.Combine(kit, "host/fxr", pin.RuntimeVersion);
        var framework = Path.Combine(kit, "shared/Microsoft.NETCore.App", pin.RuntimeVersion);
        Directory.CreateDirectory(fxr);
        Directory.CreateDirectory(framework);
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet/shared/Microsoft.NETCore.App", pin.RuntimeVersion);
        var corelib = Path.Combine(installed, "System.Private.CoreLib.dll");
        if (CoreClrHostReference.Hash(corelib) != record.GetProperty("corelibSha256").GetString())
            throw new InvalidDataException("Framework CoreLib changed since the pinned source reference.");
        foreach (var file in Directory.EnumerateFiles(installed))
            File.Copy(file, Path.Combine(framework, Path.GetFileName(file)));
        foreach (var name in new[] { "coreclr.dll", "clrjit.dll" })
        {
            var item = record.GetProperty("inventory").EnumerateArray().Single(item => Path.GetFileName(item.GetProperty("file").GetString()) == name);
            var file = item.GetProperty("file").GetString()!;
            if (CoreClrHostReference.Hash(file) != item.GetProperty("sha256").GetString())
                throw new InvalidDataException("CoreCLR/JIT snapshot changed: " + name);
            File.Copy(file, Path.Combine(framework, name), true);
        }
        File.Copy(Path.Combine(binaries, "dotnet.exe"), Path.Combine(kit, "dotnet.exe"));
        File.Copy(Path.Combine(binaries, "hostfxr.dll"), Path.Combine(fxr, "hostfxr.dll"));
        File.Copy(Path.Combine(binaries, "hostpolicy.dll"), Path.Combine(framework, "hostpolicy.dll"), true);
        var app = Path.Combine(attempt, "application");
        var build = await Processes.RunAsync("dotnet", ["build", Path.Combine(root, "experiments/CoreClrHost/Probe/HostBindingProbe.csproj"), "--configuration", "Release", "--output", app], root, 120);
        await File.WriteAllTextAsync(Path.Combine(attempt, "application-build.log"), build.Output + build.Error);
        if (build.TimedOut || build.ExitCode != 0)
            throw new InvalidOperationException("Hosting IL probe build failed: " + build.Output + build.Error);
        var errors = await File.ReadAllTextAsync(Path.Combine(source, "src/native/corehost/error_codes.h"));
        int Error(string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(errors, @"\b" + name + @"\s*=\s*0x([0-9a-fA-F]+)");
            if (!match.Success)
                throw new InvalidDataException("Pinned host status missing: " + name);
            return unchecked((int)Convert.ToUInt32(match.Groups[1].Value, 16));
        }
        var expectations = new (string Name, int Code)[]{("default-patch",42),("exact",42),
            ("disable-rollforward",Error("FrameworkMissingFailure")),("missing-framework",Error("FrameworkMissingFailure")),
            ("malformed-config",Error("InvalidConfigFile")),("malformed-deps",Error("ResolverInitFailure")),
            ("missing-dependency",unchecked((int)0xE0434352U)),("missing-dependency-probing",Error("ResolverResolveFailure"))};
        var reports = new List<object>();
        foreach (var test in expectations)
        {
            var directory = Path.Combine(attempt, "cases", test.Name);
            Directory.CreateDirectory(directory);
            foreach (var name in new[] { "HostBindingProbe.dll", "HostBindingDependency.dll", "HostBindingProbe.deps.json", "HostBindingProbe.runtimeconfig.json" })
                if (!test.Name.StartsWith("missing-dependency", StringComparison.Ordinal) || name != "HostBindingDependency.dll")
                    File.Copy(Path.Combine(app, name), Path.Combine(directory, name));
            var configPath = Path.Combine(directory, "HostBindingProbe.runtimeconfig.json");
            var config = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!;
            var options = config["runtimeOptions"]!;
            if (test.Name == "exact")
            { options["framework"]!["version"] = pin.RuntimeVersion; options["rollForward"] = "Disable"; }
            if (test.Name == "disable-rollforward")
                options["rollForward"] = "Disable";
            if (test.Name == "missing-framework")
                options["framework"]!["version"] = "99.0.0";
            if (test.Name == "missing-dependency-probing")
            {
                var probing = Path.Combine(directory, "probe");
                Directory.CreateDirectory(probing);
                options["additionalProbingPaths"] = new JsonArray(probing);
            }
            await File.WriteAllTextAsync(configPath, test.Name == "malformed-config" ? "[ invalid JSON ]" : config.ToJsonString());
            if (test.Name == "malformed-deps")
                await File.WriteAllTextAsync(Path.Combine(directory, "HostBindingProbe.deps.json"), "[ invalid JSON ]");
            var trace = Path.Combine(directory, "host.trace.log");
            var programData = Path.Combine(attempt, "program-data");
            Directory.CreateDirectory(programData);
            var env = new Dictionary<string, string>
            {
                ["ProgramData"] = programData,
                ["DOTNET_ROOT"] = kit,
                ["DOTNET_ROOT_X64"] = kit,
                ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
                ["DOTNET_ROLL_FORWARD"] = "",
                ["DOTNET_ADDITIONAL_DEPS"] = "",
                ["DOTNET_SHARED_STORE"] = "",
                ["DOTNET_STARTUP_HOOKS"] = "",
                ["DOTNET_ReadyToRun"] = "0",
                ["COMPlus_ReadyToRun"] = "0",
                ["DOTNET_HOST_TRACE"] = "1",
                ["DOTNET_HOST_TRACEFILE"] = trace,
                ["WITOS_HOST_REFERENCE"] = kit,
                ["WITOS_HOST_VERSION"] = pin.RuntimeVersion
            };
            var application = Path.Combine(directory, "HostBindingProbe.dll");
            var result = await Processes.RunAsync(Path.Combine(kit, "dotnet.exe"), [application], directory, 60, env);
            var log = Path.Combine(directory, "result.log");
            await File.WriteAllTextAsync(log, result.Output + result.Error);
            if (result.TimedOut || result.ExitCode != test.Code)
                throw new InvalidDataException($"Host case {test.Name}: expected {test.Code:X8}, actual {result.ExitCode:X8}, timeout={result.TimedOut}. See {log}");
            if (test.Code == 42)
            {
                foreach (var marker in new[] { "DependencyFromDeps", "ActualJit", "SourceModule:dotnet.exe", "SourceModule:hostfxr.dll", "SourceModule:hostpolicy.dll", "SourceModule:coreclr.dll", "SourceModule:clrjit.dll" })
                    if (result.Output.Split("[HOST-BINDING-PASS] " + marker, StringSplitOptions.None).Length != 2)
                        throw new InvalidDataException("Missing/duplicate host proof: " + marker);
                if (!result.Output.Contains("[HOST-BINDING-SUCCESS]", StringComparison.Ordinal))
                    throw new InvalidDataException("Hosting probe did not finish.");
            }
            else if (result.Output.Contains("[HOST-BINDING-", StringComparison.Ordinal))
                throw new InvalidDataException("Negative host case unexpectedly completed a managed assertion.");
            if (CoreClrHostReference.Hash(application) != CoreClrHostReference.Hash(Path.Combine(app, "HostBindingProbe.dll")))
                throw new InvalidDataException("Portable assembly bytes changed between cases.");
            if (!File.Exists(trace) || new FileInfo(trace).Length == 0)
                throw new InvalidDataException("Upstream host trace missing.");
            if (test.Name == "missing-dependency")
            {
                var details = result.Output + result.Error;
                var hostTrace = await File.ReadAllTextAsync(trace);
                if (!details.Contains("System.IO.FileNotFoundException", StringComparison.Ordinal) || !details.Contains("HostBindingDependency, Version=1.0.0.0", StringComparison.Ordinal) ||
                    !hostTrace.Contains("Skipped file existence check:", StringComparison.Ordinal) || !hostTrace.Contains("CoreCLR path =", StringComparison.Ordinal))
                    throw new InvalidDataException("Missing project dependency failed at an unexpected boundary.");
            }
            reports.Add(new
            {
                test.Name,
                result.ExitCode,
                result.TimedOut,
                log,
                logSha256 = CoreClrHostReference.Hash(log),
                trace,
                traceSha256 = CoreClrHostReference.Hash(trace),
                configSha256 = CoreClrHostReference.Hash(configPath),
                depsSha256 = CoreClrHostReference.Hash(Path.Combine(directory, "HostBindingProbe.deps.json"))
            });
            Console.WriteLine($"[HOST-BINDING-REFERENCE-PASS] {test.Name}: {result.ExitCode:X8}");
        }
        return new
        {
            reports,
            kit,
            framework,
            coreClrAcceptance = accepted,
            coreClrAcceptanceSha256 = CoreClrHostReference.Hash(accepted),
            managedAssemblySha256 = CoreClrHostReference.Hash(Path.Combine(app, "HostBindingProbe.dll")),
            dependencySha256 = CoreClrHostReference.Hash(Path.Combine(app, "HostBindingDependency.dll")),
            frameworkFiles = Directory.EnumerateFiles(framework).Order(StringComparer.Ordinal).Select(file => new { file, bytes = new FileInfo(file).Length, sha256 = CoreClrHostReference.Hash(file) }),
            actualModules = new[] { Path.Combine(kit, "dotnet.exe"), Path.Combine(fxr, "hostfxr.dll"), Path.Combine(framework, "hostpolicy.dll"), Path.Combine(framework, "coreclr.dll"), Path.Combine(framework, "clrjit.dll") }
                .Select(file => new { file, sha256 = CoreClrHostReference.Hash(file) })
        };
    }
}
