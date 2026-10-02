using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.NativeAot.Acceptance;
using WitOS.Dev.NativeAot.References;
using WitOS.Dev.Pe;

namespace WitOS.Dev.NativeAot;

internal static class RuntimeReadiness
{
    #region Functions

    public static async Task RunAsync(string root, string msvc, string nativeSdk)
    {
        const string project = "experiments/NativeAotBoot";
        var output = Path.Combine(root, "artifacts", "runtime-readiness");
        var reference = Path.Combine(output, "reference");
        Directory.CreateDirectory(reference);
        var publish = await Processes.RunAsync("dotnet",
            ["publish", Path.Combine(root, project, "NativeAotBoot.csproj"), "-c", "Release", "-r", "win-x64",
                "-o", reference, "--packages", Path.Combine(root, ".tools", "nuget"),
                "--configfile", Path.Combine(root, project, "NuGet.Config"), "-p:RestoreLockedMode=true",
                "-p:NativeIntermediateOutputPath=" + Path.Combine(reference, "native") + "/",
                "-p:NativeOutputPath=" + Path.Combine(reference, "link") + "/"], root, 600, Toolchain.NativeAotEnvironment());
        await File.WriteAllTextAsync(Path.Combine(output, "publish.log"), publish.Output + publish.Error);
        if (publish.TimedOut || publish.ExitCode != 0)
            throw new InvalidOperationException("Minimal NativeAOT publish failed; see runtime-readiness/publish.log.");
        var pin = RuntimeExperiment.ReadLock(root);
        var packages = RuntimeExperiment.VerifyPublishedPackages(root, pin);
        var compilerArgs = await File.ReadAllLinesAsync(Path.Combine(reference, "native", "NativeAotBoot.ilc.rsp"));
        string[] required = ["--runtimeknob:System.GC.Concurrent=false", "--runtimeknob:System.GC.LargePages=false", "--runtimeknob:System.GC.Server=false", "--targetos:win", "--targetarch:x64", "--instruction-set:x86-64", "--initassembly:System.Private.CoreLib"];
        if (required.Any(a => !compilerArgs.Contains(a)) || compilerArgs.Contains("--nativelib") || compilerArgs.Contains("--guard:cf") ||
            compilerArgs.Any(a => a.StartsWith("--systemmodule", StringComparison.Ordinal)) ||
            !compilerArgs.Any(a => a.StartsWith("-r:", StringComparison.Ordinal) && a.EndsWith("System.Private.CoreLib.dll", StringComparison.Ordinal)))
            throw new InvalidDataException("Minimal workload must use normal executable bootstrap and standard CoreLib.");
        var managed = Path.Combine(reference, "native", "NativeAotBoot.obj");
        var coff = NativeObject.Inspect(managed, collectReferences: true);
        if (!coff.Sections.Any(s => s.Name.StartsWith(".managedcode", StringComparison.Ordinal)) ||
            !coff.UndefinedExternals.Any(s => s.StartsWith("RhpNew", StringComparison.Ordinal)))
            throw new InvalidDataException("Minimal workload lost actual managed code/allocation dependencies.");
        var executable = Path.Combine(reference, "NativeAotBoot.exe");
        await RuntimeUnwindReference.ValidateImageAsync(root, executable);
        var hosted = await Processes.RunAsync(executable, [], reference, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "hosted.log"), hosted.Output + hosted.Error + $"\nExit code: {hosted.ExitCode}\n");
        if (!RuntimeBootProtocol.ValidateHosted(hosted.Output, hosted.ExitCode, hosted.TimedOut) || !string.IsNullOrEmpty(hosted.Error))
            throw new InvalidOperationException("Hosted combined GC/EH/finalization/thread semantic workload failed.");
        Console.WriteLine("[READINESS-PASS] Four combined GC/EH/finalization/thread cycles with exact semantic reports (HOSTED Windows).");
        var module = NativeModule.Inspect(executable);
        var imports = NativeImports.Inspect(executable);
        if (imports.HasClrHeader || module.Tls is null || module.UnwindEntries == 0 || imports.DirectImports.Length == 0)
            throw new InvalidDataException("Minimal Windows reference lost expected native runtime metadata.");

        var captured = (await File.ReadAllLinesAsync(Path.Combine(reference, "native", "native-libraries.txt")))
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(Path.GetFullPath).ToArray();
        if (!captured.Any(p => Path.GetFileName(p).Equals("bootstrapper.obj", StringComparison.OrdinalIgnoreCase)) ||
            captured.Any(p => Path.GetFileName(p).Equals("bootstrapperdll.obj", StringComparison.OrdinalIgnoreCase)) ||
            !captured.Any(p => Path.GetFileName(p) == "Runtime.WorkstationGC.lib"))
            throw new InvalidDataException("Minimal workload must select real executable/workstation bootstrap.");
        var libraries = captured.Select(p => Path.Combine(nativeSdk, Path.GetFileName(p))).ToArray();
        if (libraries.Any(p => !File.Exists(p)))
            throw new InvalidDataException("Missing source-built minimal runtime input.");
        var exitCall = Constant(root, "src/Kernel/include/witos/user_abi.h", "WIT_CALL_EXIT");
        await File.WriteAllTextAsync(Path.Combine(output, "user_abi.inc"), $"WIT_CALL_EXIT EQU {exitCall}\n", Encoding.ASCII);
        var transport = Path.Combine(output, "native_transport.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/DWITOS_NATIVE_TRANSPORT_ONLY", "/I" + output, "/Fo" + transport,
                Path.Combine(root, "src", "Kernel.Arch.X64", "native_start.asm")], root);
        if (NativeObject.Inspect(transport).UndefinedExternals.Contains("wit_native_main"))
            throw new InvalidDataException("Transport-only object unexpectedly requires a fixture entrypoint.");
        var tls = Path.Combine(output, "tls_metadata.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/O1", "/Fo" + tls,
                "/I" + Path.Combine(root, "src", "Kernel", "include"), Path.Combine(root, "src", "System.Native", "tls_metadata.c")], root);
        var image = Path.Combine(output, "unlinked-native-entry.exe");
        if (File.Exists(image))
            File.Delete(image);
        // wmain is the actual pinned bootstrapper entry: it references RhInitialize,
        // RhRegisterOSModule, InitializeModules and the ILC-generated managed Main.
        // This roots real startup without inventing runtime bodies or importing Windows.
        string[] linkArgs = ["/nologo", "/subsystem:native", "/entry:wmain", "/nodefaultlib", "/machine:x64",
            "/incremental:no", "/opt:ref", "/include:_tls_used", "/merge:.CRT=.rdata", "/out:" + image,
            managed, transport, tls, .. libraries];
        await File.WriteAllLinesAsync(Path.Combine(output, "link-arguments.txt"), linkArgs);
        var link = await Processes.RunAsync(Path.Combine(msvc, "link.exe"), linkArgs, root, 120);
        var log = link.Output + link.Error;
        await File.WriteAllTextAsync(Path.Combine(output, "strict-link.log"), log);
        var unresolved = Regex.Matches(log, @"error LNK(?:2001|2019): unresolved external symbol (.+?)(?: referenced in function .*|\r?$)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var errors = Regex.Matches(log, @"error (LNK\d+):").Select(m => m.Groups[1].Value).ToArray();
        if (link.TimedOut || link.ExitCode != 0 || !File.Exists(image) || unresolved.Length != 0 || errors.Length != 0)
            throw new InvalidOperationException("Strict native startup link is incomplete; see runtime-readiness/strict-link.log.");
        var diagnosticModule = NativeModule.Inspect(image);
        var diagnosticImports = NativeImports.Inspect(image);
        if (diagnosticImports.HasClrHeader || diagnosticImports.DirectImports.Length != 0 || diagnosticImports.DelayImportDirectorySize != 0 ||
           diagnosticModule.Tls is null || diagnosticModule.UnwindEntries == 0)
            throw new InvalidDataException("Linked startup diagnostic lost native TLS/unwind metadata or retained OS imports.");
        await RuntimePlatformBoundary.WriteAsync(root, managed, coff, log, unresolved, compilerArgs);
        var guestDriver = await RuntimeGuestDriver.BuildAsync(root, msvc, output, managed, tls, libraries);
        var imageLimit = Constant(root, "src/Kernel/include/witos/pe.h", "WIT_PE_MAX_IMAGE_SIZE");
        var unwindLimit = Constant(root, "src/Kernel/include/witos/pe.h", "WIT_PE_MAX_UNWIND_ENTRIES");
        var pageLimit = Constant(root, "src/Kernel.Arch.X64/user_layout.h", "WIT_USER_PAGE_CAPACITY");
        var handleLimit = Constant(root, "src/Kernel/include/witos/handles.h", "WIT_HANDLE_CAPACITY");
        var runtimeUnwindLimit = Constant(root, "src/Kernel/include/witos/pe.h", "WIT_PE_RUNTIME_UNWIND_ENTRIES");
        var evidence = new
        {
            pin.RuntimeVersion,
            pin.RuntimeCommit,
            guestManagedExecution = false,
            guestImageLinked = true,
            diagnosticImageLinked = true,
            guestDriver = new { file = guestDriver, sha256 = Hash(guestDriver), executed = false },
            diagnosticImage = new { file = image, sha256 = Hash(image), module = diagnosticModule, imports = diagnosticImports },
            hostedPassed = true,
            hostedSemanticAcceptance = true,
            hostedLogSha256 = Hash(Path.Combine(output, "hosted.log")),
            hostedExitCode = hosted.ExitCode,
            packages,
            module,
            imports,
            managedObjectSha256 = Hash(managed),
            referenceImageSha256 = Hash(executable),
            startupRoot = "wmain",
            artificialRuntimeRoots = Array.Empty<string>(),
            strictLinkExitCode = link.ExitCode,
            unresolved,
            limits = new { imageLimit, unwindLimit, runtimeUnwindLimit, pageLimit, handleLimit, pageSize = 4096 },
            referenceImagePages = (module.ImageBytes + 4095) / 4096,
            inputs = libraries.Concat([transport, tls]).Select(p => new { file = p, sha256 = Hash(p) }),
            localSources = new[] { project + "/Program.cs", project + "/ExceptionProbe.cs", project + "/FinalizationProbe.cs", project + "/GuestReport.cs", project + "/ManagedThreadProbe.cs", project + "/StackOverflowProbe.cs", project + "/ThreadQuotaProbe.cs", project + "/FaultProbe.cs", project + "/MemoryFailureProbe.cs", project + "/NativeAotBoot.csproj", project + "/packages.lock.json",
                "src/Kernel.Arch.X64/native_start.asm", "src/System.Native/tls_metadata.c", "src/Kernel/include/witos/pe.h",
                "src/Kernel/include/witos/user_abi.h", "src/Kernel/include/witos/handles.h", "src/Kernel.Arch.X64/user_layout.h" }
                .Select(p => new { file = p, sha256 = Hash(Path.Combine(root, p)) }),
            scope = "Hosted standard-CoreLib executable plus strictly linked source-built wmain diagnostic image. wmain is a dependency root, not a valid WitOS startup thunk. The separate guest handoff driver links image/environment publication, GS/TLS/initializer entry and orderly shutdown; its execution and resource budgets still require guest acceptance. Reference image sizes are not final guest requirements."
        };
        await File.WriteAllTextAsync(Path.Combine(output, "readiness.json"), JsonSerializer.Serialize(evidence,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var summary = new StringBuilder("# Minimal runtime startup readiness\n\nGuest .NET has not executed.\n\n");
        summary.AppendLine("Hosted standard-CoreLib allocation/GC workload passed (exit 42). The source-built wmain diagnostic image links with zero unresolved symbols and no OS imports. It has not executed in the guest.");
        summary.AppendLine($"\nWitOS diagnostic: {diagnosticModule.ImageBytes} mapped bytes, {diagnosticModule.UnwindEntries} unwind entries; TLS template {diagnosticModule.Tls!.TemplateBytes} bytes plus {diagnosticModule.Tls.ZeroFillBytes} zero-fill bytes. The entry still requires a real guest handoff driver.");
        summary.AppendLine($"\nWindows reference: {module.ImageBytes} mapped bytes ({(module.ImageBytes + 4095) / 4096} pages), {module.UnwindEntries} unwind entries. Current guest limits: {imageLimit} image bytes, {pageLimit} total owned pages, {unwindLimit} plain unwind entries. Reference sizes do not establish final guest resource requirements.");
        summary.AppendLine($"\nSeparate guest handoff driver: {guestDriver}. It links strictly but has not executed; loader/resource and runtime/GC acceptance remain open.\n\n## Unresolved startup dependencies\n");
        foreach (var symbol in unresolved)
            summary.AppendLine("- `" + symbol + "`");
        await File.WriteAllTextAsync(Path.Combine(output, "readiness.md"), summary.ToString());
        Console.WriteLine($"[READINESS-PASS] Real wmain startup boundary: {unresolved.Length} unresolved symbols; transport/TLS metadata supplied, no OS imports linked.");
        Console.WriteLine($"Minimal Windows reference: {module.ImageBytes} mapped bytes, {module.UnwindEntries} unwind entries. Reports: {output}");
    }

    #endregion

    #region Tools

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static uint Constant(string root, string file, string name)
    {
        var matches = Regex.Matches(File.ReadAllText(Path.Combine(root, file)),
            @"^#define\s+" + Regex.Escape(name) + @"\s+([0-9]+)U\s*$", RegexOptions.Multiline);
        if (matches.Count != 1)
            throw new InvalidDataException("Missing/ambiguous readiness limit: " + name);
        return uint.Parse(matches[0].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    #endregion
}
