using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WitOS.Dev;

internal static class RuntimeTargetExperiment
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string Project = "experiments/NativeAotTarget";
    private static readonly string[] Exports = ["witos_target_probe", "witos_target_version"];
    private static readonly string[] Cases = ["FirstExportInitialization", "AllocationGcAndExceptions", "TlsOnNativeThreads", "RepeatEntry"];

    public static async Task RunAsync(string root)
    {
        await RuntimeExperiment.AuditAsync(root);
        var pin = RuntimeExperiment.ReadLock(root);
        var output = Path.Combine(root, "artifacts", "runtime-target");
        Directory.CreateDirectory(output);
        var msvc = await Toolchain.FindMsvcAsync(root);
        var packageRoot = Path.Combine(root, ".tools", "nuget");
        var nativeSdk = Path.GetFullPath(Path.Combine(packageRoot,
            "microsoft.netcore.app.runtime.nativeaot.win-x64", pin.RuntimeVersion, "runtimes", "win-x64", "native"));
        Console.WriteLine("NativeAOT target/bootstrap evidence on Windows; no guest runtime is claimed.");

        var staticDirectory = await PublishAsync(root, output, "Static");
        var sharedDirectory = await PublishAsync(root, output, "Shared");
        var verifiedPackages = RuntimeExperiment.VerifyPublishedPackages(root, pin);
        var obj = Path.Combine(staticDirectory, "native", "NativeAotTarget.obj");
        var archive = Path.Combine(staticDirectory, "NativeAotTarget.lib");
        var dll = Path.Combine(sharedDirectory, "NativeAotTarget.dll");
        NativeObject.VerifyArchive(archive, obj);
        var coff = NativeObject.Inspect(obj);
        if (!Exports.SequenceEqual(coff.DefinedExports) ||
            !coff.UndefinedExternals.Contains("RhpReversePInvoke") || !coff.UndefinedExternals.Contains("_tls_index") ||
            !coff.Sections.Any(section => section.Name.StartsWith(".managedcode", StringComparison.Ordinal)) ||
            coff.RelocationKinds.Count == 0)
            throw new InvalidDataException("NativeAOT object does not match the declared experiment.");
        var arguments = await File.ReadAllLinesAsync(Path.Combine(staticDirectory, "native", "NativeAotTarget.ilc.rsp"));
        string[] required = ["--targetos:win", "--targetarch:x64", "--nativelib", "--instruction-set:x86-64", "--initassembly:System.Private.CoreLib"];
        if (required.Any(value => !arguments.Contains(value)) ||
            arguments.Any(value => value.StartsWith("--systemmodule", StringComparison.Ordinal)) ||
            !arguments.Any(value => value.StartsWith("-r:", StringComparison.Ordinal) &&
                value.EndsWith("System.Private.CoreLib.dll", StringComparison.Ordinal)))
            throw new InvalidDataException("Compiler target or standard CoreLib profile changed.");

        var imports = NativeImports.Inspect(dll);
        var module = NativeModule.Inspect(dll);
        if (imports.HasClrHeader || imports.DirectImports.Length == 0 || module.Tls is null ||
            module.Tls.TemplateBytes == 0 || module.Tls.CallbackRvas.Length == 0 ||
            module.UnwindEntries == 0 || module.BaseRelocations.GetValueOrDefault("DIR64") == 0)
            throw new InvalidDataException("NativeAOT module lacks expected platform/TLS/unwind evidence.");
        VerifyMalformedInputs(output, obj, archive, dll);

        var kernel32 = Toolchain.FindWindowsSdkLibrary("kernel32.lib");
        var hostObject = Path.Combine(sharedDirectory, "native_host.obj");
        var host = Path.Combine(sharedDirectory, "native_host.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/O2",
                "/Fo" + hostObject, Path.Combine(root, Project, "native_host.c")], root);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo", "/entry:host_main", "/subsystem:console", "/nodefaultlib", "/machine:x64",
                "/incremental:no", "/Brepro", "/out:" + host, hostObject, kernel32], root);
        var hostImports = NativeImports.Inspect(host);
        if (hostImports.HasClrHeader || hostImports.DirectImports.Length != 1 ||
            !hostImports.DirectImports[0].Library.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The native bootstrap host must depend only on Kernel32.");
        var execution = await Processes.RunAsync(host, [], sharedDirectory, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "native-host.stdout.log"), execution.Output);
        await File.WriteAllTextAsync(Path.Combine(output, "native-host.stderr.log"), execution.Error);
        Console.Write(execution.Output);
        if (execution.TimedOut || execution.ExitCode != 0 ||
            !execution.Output.Contains("[TARGET-SUCCESS]", StringComparison.Ordinal) ||
            execution.Output.Contains("[TARGET-FAIL]", StringComparison.Ordinal) ||
            Cases.Any(test => !execution.Output.Contains($"[TARGET-PASS] {test}", StringComparison.Ordinal)))
            throw new InvalidOperationException($"Native host failed: exit={execution.ExitCode}, timeout={execution.TimedOut}.");

        var libraries = await File.ReadAllLinesAsync(Path.Combine(sharedDirectory, "native", "native-libraries.txt"));
        libraries = libraries.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Path.GetFullPath).ToArray();
        if (libraries.Length == 0 || libraries.Any(path =>
                !path.StartsWith(nativeSdk + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path)) || !libraries.Any(path => Path.GetFileName(path) == "Runtime.WorkstationGC.lib"))
            throw new InvalidDataException("Captured native inputs escaped the pinned runtime package.");
        var withoutRuntime = await LinkBoundaryAsync(msvc, output, archive, [], "without-runtime");
        var withoutPlatform = await LinkBoundaryAsync(msvc, output, archive, libraries, "without-platform");
        if (!withoutRuntime.Unresolved.Contains("RhpReversePInvoke") ||
            withoutPlatform.Unresolved.Contains("RhpReversePInvoke") ||
            !withoutPlatform.Unresolved.Any(name => name.Contains("VirtualAlloc", StringComparison.Ordinal)) ||
            !withoutPlatform.Unresolved.Contains("_tls_index"))
            throw new InvalidDataException("Link boundary no longer exposes the expected runtime/platform separation.");

        var sdkInputs = new List<object>();
        foreach (var path in libraries)
            sdkInputs.Add(new { file = Path.GetFileName(path), sha256 = Hash(path) });
        var blockers = new[]
        {
            "Unmodified Windows native runtime still requires OS/CRT implementations; strict link is unresolved.",
            "Windows TLS template/index/callback semantics differ from the current WitOS raw FS TLS block.",
            "PE base relocations, module registration and x64 unwind/hardware-fault integration need a guest loader/adapter.",
            "The full runtime/GC needs larger image, stack and commitment limits than the controlled one-page fixtures.",
            "The x86-64 ILC baseline does not by itself certify every prebuilt runtime-library instruction path."
        };
        var report = new
        {
            hostOnly = true, guestRuntimePorted = false,
            candidateCallingConvention = "Microsoft x64", candidateObjectFormat = "AMD64 COFF / PE32+",
            runtimeBackendSelected = true,
            runtimeBackend = RuntimePortImage.Backend,
            fullGuestRuntimeSourceBuild = false,
            pin.RuntimeVersion, pin.RuntimeCommit, pin.PackageCommit, verifiedPackages,
            compilerArguments = required,
            objectSha256 = Hash(obj), staticArchiveSha256 = Hash(archive), moduleSha256 = Hash(dll),
            nativeHostSha256 = Hash(host), nativeHostImports = hostImports,
            passedCases = Cases, archiveContainsExactObject = true,
            objectFile = coff, image = module, imports, nativeSdkInputs = sdkInputs,
            withoutRuntime, withoutPlatform, guestBlockers = blockers,
            scope = "Selected exports and explicit bootstrap roots. Link-undefined symbols, PE imports and executed calls are different sets; no dummy definitions or /FORCE linking."
        };
        await File.WriteAllTextAsync(Path.Combine(output, "target-report.json"), JsonSerializer.Serialize(report, Json));
        var summary = new StringBuilder("# NativeAOT target/bootstrap evidence\n\n");
        summary.AppendLine("HOSTED Windows only. The actual upstream runtime is initialized from a C executable with no CoreCLR.");
        summary.AppendLine();
        summary.AppendLine($"Runtime {pin.RuntimeVersion}; source {pin.RuntimeCommit}.");
        summary.AppendLine($"COFF: {coff.SectionCount} sections, {coff.UndefinedExternals.Length} undefined external symbols.");
        summary.AppendLine($"Strict link without runtime: {withoutRuntime.Unresolved.Length} unresolved symbols.");
        summary.AppendLine($"Strict link with runtime but without OS/CRT: {withoutPlatform.Unresolved.Length} unresolved symbols.");
        summary.AppendLine($"PE: {module.ImageBytes} mapped bytes; TLS template {module.Tls.TemplateBytes} bytes, {module.Tls.CallbackRvas.Length} callback(s); {module.UnwindEntries} unwind entries.");
        summary.AppendLine($"Direct PE imports: {imports.DirectImports.Sum(item => item.Symbols.Length)} symbols in {imports.DirectImports.Length} libraries.");
        summary.AppendLine();
        summary.AppendLine("Candidate format/calling convention: PE32+ and Microsoft x64. This does not select or implement a Windows compatibility personality.");
        summary.AppendLine();
        foreach (var blocker in blockers) summary.AppendLine("- " + blocker);
        summary.AppendLine();
        summary.AppendLine(report.scope);
        await File.WriteAllTextAsync(Path.Combine(output, "target-report.md"), summary.ToString());
        Console.WriteLine($"COFF verified: {coff.SectionCount} sections; static archive contains the exact ILC object.");
        Console.WriteLine($"Expected strict-link failures: {withoutRuntime.Unresolved.Length} / {withoutPlatform.Unresolved.Length} unresolved symbols.");
        Console.WriteLine($"Reports: {output}");
    }

    private static async Task<string> PublishAsync(string root, string output, string kind)
    {
        var directory = Path.Combine(output, kind.ToLowerInvariant());
        Directory.CreateDirectory(directory);
        var result = await Processes.RunAsync("dotnet",
        [
            "publish", Path.Combine(root, Project, "NativeAotTarget.csproj"), "-c", "Release",
            "-r", "win-x64", "-o", directory, "--packages", Path.Combine(root, ".tools", "nuget"),
            "--configfile", Path.Combine(root, Project, "NuGet.Config"), "-p:RestoreLockedMode=true",
            "-p:NativeLib=" + kind, "-p:NativeIntermediateOutputPath=" + Path.Combine(directory, "native") + "/",
            "-p:NativeOutputPath=" + Path.Combine(directory, "link") + "/"
        ], root, 600);
        await File.WriteAllTextAsync(Path.Combine(directory, "publish.log"), result.Output + result.Error);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"NativeAOT {kind} publish failed. {result.Output}\n{result.Error}");
        return directory;
    }

    private sealed record LinkEvidence(string[] Roots, string[] Inputs, int ExitCode, string[] Unresolved);

    private static async Task<LinkEvidence> LinkBoundaryAsync(string msvc, string output, string archive,
        string[] libraries, string name)
    {
        var image = Path.Combine(output, name + ".dll");
        if (File.Exists(image)) File.Delete(image);
        string[] roots = libraries.Length == 0 ? [] : ["RhInitialize", "RhRegisterOSModule", "InitializeModules"];
        var result = await Processes.RunAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo", "/dll", "/noentry", "/nodefaultlib", "/machine:x64", "/incremental:no", "/opt:ref",
                .. Exports.Select(value => "/export:" + value), .. roots.Select(value => "/include:" + value),
                "/out:" + image, archive, .. libraries], output, 120);
        var log = result.Output + result.Error;
        await File.WriteAllTextAsync(Path.Combine(output, name + ".log"), log);
        var unresolved = Regex.Matches(log, @"error LNK(?:2001|2019): unresolved external symbol (.+?)(?: referenced in function .*|\r?$)", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var fatal = Regex.Matches(log, @"fatal error (LNK\d+):");
        if (result.TimedOut || result.ExitCode == 0 || File.Exists(image) || unresolved.Length == 0 ||
            fatal.Count == 0 || fatal.Any(match => match.Groups[1].Value != "LNK1120") ||
            Regex.Matches(log, @"(?<!fatal )error (LNK\d+):").Any(match => match.Groups[1].Value is not ("LNK2001" or "LNK2019" or "LNK1120")))
            throw new InvalidOperationException($"Unexpected strict-link outcome ({name}). See {name}.log.");
        return new([.. Exports, .. roots], [Path.GetFileName(archive), .. libraries.Select(path => Path.GetFileName(path)!)],
            result.ExitCode, unresolved);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void VerifyMalformedInputs(string output, string obj, string archive, string dll)
    {
        var directory = Path.Combine(output, "malformed");
        Directory.CreateDirectory(directory);
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or BadImageFormatException or OverflowException) { return; }
            throw new InvalidOperationException("Malformed native artifact was accepted.");
        }
        var badObject = Path.Combine(directory, "truncated.obj");
        File.WriteAllBytes(badObject, File.ReadAllBytes(obj)[..19]);
        Reject(() => NativeObject.Inspect(badObject));
        Reject(() => NativeObject.VerifyArchive(archive, badObject));
        var badImage = Path.Combine(directory, "truncated.dll");
        File.WriteAllBytes(badImage, File.ReadAllBytes(dll)[..32]);
        Reject(() => NativeModule.Inspect(badImage));
    }
}
