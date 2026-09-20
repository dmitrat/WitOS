using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WitOS.Dev;

internal static class RuntimeSourceBuild
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Cases = ["FirstExportInitialization", "AllocationGcAndExceptions", "TlsOnNativeThreads", "RepeatEntry"];
    private sealed record CompileCommand(string Directory, string Command, string File, string Output);
    private sealed record ArchiveEvidence(string Sha256, string[] Members, int CompileUnits);
    private sealed record SourceBuild(string Sdk, string[] Members, CompileCommand[] Commands, string ArchiveSha256, ArchiveEvidence Minipal);

    public static async Task RunAsync(string root)
    {
        // Refresh the real ILC object, native host and locked-package evidence.
        await RuntimeTargetExperiment.RunAsync(root);
        var pin = RuntimeExperiment.ReadLock(root);
        var output = Path.Combine(root, "artifacts", "runtime-source");
        Directory.CreateDirectory(output);
        var source = await PrepareSourceAsync(root, pin);
        var tree = (await GitAsync(source, ["rev-parse", "HEAD^{tree}"])).Trim();
        var msvc = await Toolchain.FindMsvcAsync(root);
        Console.WriteLine("Building upstream native libraries from source; Windows reference plus incomplete WitOS archive. No guest managed execution.");
        var reference = await BuildAsync(root, source, output, msvc, "reference", overlay: false);
        var ported = await BuildAsync(root, source, output, msvc, "witos", overlay: true);
        await RequireCleanAsync(source);

        var target = Path.Combine(root, "artifacts", "runtime-target");
        var publishedInputs = (await File.ReadAllLinesAsync(Path.Combine(target, "shared", "native", "native-libraries.txt")))
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).ToArray();
        string[] Inputs(string sdk) => publishedInputs.Select(p => Path.Combine(sdk, Path.GetFileName(p))).ToArray();
        var referenceInputs = Inputs(reference.Sdk);
        var portedInputs = Inputs(ported.Sdk);
        if (referenceInputs.Concat(portedInputs).Any(p => !File.Exists(p)))
            throw new InvalidDataException("Source build did not produce every captured NativeAOT native input.");
        var referenceHost = await RunReferenceAsync(root, output, target, msvc, publishedInputs, referenceInputs);
        var boundary = await RuntimeTargetExperiment.LinkBoundaryAsync(msvc, output,
            Path.Combine(target, "static", "NativeAotTarget.lib"), portedInputs, "witos-without-platform");
        string[] implemented = ["VirtualReset@", "VirtualReserve@", "VirtualCommit@", "VirtualDecommit@", "VirtualRelease@", "SupportsWriteWatch@", "Initialize@", "Shutdown@", "GetTotalProcessorCount@", "GetPhysicalMemoryLimit@", "GetVirtualMemoryLimit@", "GetVirtualMemoryMaxAddress@", "GetMemoryStatus@", "CanEnableGCCPUGroups@", "CanEnableGCNumaAware@", "YieldThread@", "Sleep@", "QueryPerformanceCounter@", "QueryPerformanceFrequency@", "GetLowPrecisionTimeStamp@"];
        string[] replacedMutexSymbols = ["minipal_mutex_init", "minipal_mutex_destroy", "minipal_mutex_enter", "minipal_mutex_leave",
            "__imp_InitializeCriticalSection", "__imp_DeleteCriticalSection", "__imp_EnterCriticalSection", "__imp_LeaveCriticalSection"];
        string[] palImplemented = ["PalGetCurrentOSThreadId", "PalGetMaximumStackBounds", "PalGetCurrentProcessId", "PalGetProcessCpuCount",
            "PalVirtualAlloc", "PalVirtualFree", "PalVirtualProtect", "PalCreateEventW", "PalSetEvent", "PalResetEvent",
            "PalWaitForSingleObjectEx", "PalCloseHandle", "PalSleep", "PalSwitchToThread", "PalStartBackgroundGCThread", "PalStartFinalizerThread", "PalStartEventPipeHelperThread", "PalGetModuleHandleFromPointer", "PalGetModuleBounds", "PalGetEnvironmentVariable", "PalCopyTCharAsChar"];
        if (boundary.Unresolved.Any(s => s is "GetEnvironmentVariableW" or "__imp_GetEnvironmentVariableW" or "GetLastError" or "SetLastError" or "__imp_GetLastError" or "__imp_SetLastError") ||
            boundary.Unresolved.Any(s => palImplemented.Any(p => s.Contains(p, StringComparison.Ordinal))) ||
            !boundary.Unresolved.Any(s => s.Contains("PalAttachThread", StringComparison.Ordinal)) ||
            !boundary.Unresolved.Any(s => s.Contains("?PalInit@@", StringComparison.Ordinal)) ||
            boundary.Unresolved.Any(s => s is "__dyn_tls_init" or "__dyn_tls_on_demand_init" or "__tls_guard" or "__tlregdtor") ||
            boundary.Unresolved.Any(s => s.Contains("operator new", StringComparison.Ordinal) || s.Contains("operator delete", StringComparison.Ordinal) || s.Contains("?nothrow@std@@", StringComparison.Ordinal)) ||
            boundary.Unresolved.Any(s => replacedMutexSymbols.Contains(s, StringComparer.Ordinal)) ||
            !boundary.Unresolved.Any(s => s.Contains("FlushProcessWriteBuffers@GCToOSInterface", StringComparison.Ordinal)) ||
            boundary.Unresolved.Any(s => s.Contains("GCEvent", StringComparison.Ordinal)) ||
            !boundary.Unresolved.Contains("wit_native_call") ||
            !boundary.Unresolved.Contains("_tls_index") ||
            boundary.Unresolved.Contains("RhpReversePInvoke") ||
            boundary.Unresolved.Any(s => implemented.Any(name => s.Contains(name + "GCToOSInterface", StringComparison.Ordinal))))
            throw new InvalidDataException("Source archive link did not expose the expected implemented/missing adapter boundary.");
        var groups = new Dictionary<string, string[]>
        {
            ["gc-environment"] = boundary.Unresolved.Where(s => s.Contains("GCToOSInterface", StringComparison.Ordinal) || s.Contains("GCEvent", StringComparison.Ordinal)).ToArray(),
            ["nativeaot-pal"] = boundary.Unresolved.Where(s => s.Contains("?Pal", StringComparison.Ordinal)).ToArray(),
            ["witos-transport"] = boundary.Unresolved.Where(s => s.StartsWith("wit_native_", StringComparison.Ordinal)).ToArray(),
            ["remaining-platform-runtime"] = boundary.Unresolved.Where(s => !s.Contains("GCToOSInterface", StringComparison.Ordinal) && !s.Contains("GCEvent", StringComparison.Ordinal) && !s.StartsWith("wit_native_", StringComparison.Ordinal) && !s.Contains("?Pal", StringComparison.Ordinal)).ToArray()
        };
        var report = new
        {
            pin.RuntimeVersion, pin.RuntimeCommit, upstreamTree = tree, backend = RuntimePortImage.Backend,
            upstreamWorkingTreeClean = true, upstreamFilesPatched = false,
            nativeRuntimeSourceBuilt = true, managedCompilerAndCoreLibFromLockedPackages = true,
            guestRuntimePorted = false, guestManagedExecution = false,
            referenceHostPassedCases = Cases, referenceHost,
            reference = new { reference.ArchiveSha256, members = reference.Members, compileUnits = reference.Commands.Length, minipal = reference.Minipal },
            ported = new { ported.ArchiveSha256, members = ported.Members, compileUnits = ported.Commands.Length, minipal = ported.Minipal },
            sourceOverlay = new[] { "src/Runtime.NativeAot/runtime-overlay.cmake", "src/Runtime.NativeAot/gcenv.witos.cpp",
                "src/Runtime.NativeAot/gcenv.witos.h", "src/Runtime.NativeAot/gc_events.witos.cpp", "src/Runtime.NativeAot/gc_time.witos.cpp",
                "src/Runtime.NativeAot/mutex.witos.cpp", "src/Runtime.NativeAot/crst.witos.cpp", "src/Runtime.NativeAot/native_new.witos.cpp", "src/Runtime.NativeAot/tls.witos.cpp", "src/Runtime.NativeAot/pal.witos.cpp", "src/Runtime.NativeAot/pal_memory.witos.cpp", "src/Runtime.NativeAot/pal_events.witos.cpp", "src/Runtime.NativeAot/pal_threads.witos.cpp", "src/System.Native/thread.c", "src/System.Native/image.c", "src/System.Native/image.h", "src/Runtime.NativeAot/pal_module.witos.cpp", "src/Runtime.NativeAot/pal_environment.witos.cpp", "src/Runtime.NativeAot/pal_environment.witos.h", "src/System.Native/error.h", "src/Runtime.NativeAot/pal_error.witos.cpp", "src/Kernel.Arch.X64/native_error.asm", "src/Kernel.Arch.X64/native_environment.asm", "src/Runtime.NativeAot/pal.witos.h", "src/System.Native/tls.h", "src/System.Native/bootstrap.h", "src/Kernel/include/witos/types.h",
                "src/Kernel/include/witos/user_abi.h", "src/Kernel/include/witos/thread_info.h", "src/Kernel/include/witos/image_info.h", "src/Kernel/include/witos/memory_info.h" }
                .Select(p => new { path = p, sha256 = Hash(Path.Combine(root, p)) }),
            referenceInputs = referenceInputs.Select(p => new { file = Path.GetFileName(p), sha256 = Hash(p) }),
            portedInputs = portedInputs.Select(p => new { file = Path.GetFileName(p), sha256 = Hash(p) }),
            boundary, missingGroups = groups,
            scope = "Entire upstream nativeaot CMake component built twice. Workstation GC environment, Release Crst and aotminipal mutex sources are replaced in the WitOS archives; remaining Windows PAL/CRT/TLS dependencies and unsupported GC methods are intentionally unresolved. This is not a runnable guest runtime or a complete .NET source build."
        };
        await File.WriteAllTextAsync(Path.Combine(output, "source-build-report.json"), JsonSerializer.Serialize(report, Json));
        await File.WriteAllTextAsync(Path.Combine(output, "missing-platform.md"),
            "# Source-built NativeAOT port boundary\n\nNo guest runtime executed. Strict link failed as expected.\n\n" +
            string.Join("\n\n", groups.Select(g => $"## {g.Key} ({g.Value.Length})\n\n" + string.Join("\n", g.Value.Select(v => "- `" + v + "`")))) + "\n");
        Console.WriteLine($"[SOURCE-PASS] Full native archive: {ported.Members.Length} members; native adapter objects verified byte-for-byte.");
        Console.WriteLine($"[SOURCE-PASS] aotminipal archive: {ported.Minipal.Members.Length} members; mutex adapter object verified byte-for-byte.");
        Console.WriteLine($"[SOURCE-PASS] Windows source-built reference: {Cases.Length} execution groups.");
        Console.WriteLine($"[SOURCE-PASS] Strict WitOS link boundary: {boundary.Unresolved.Length} unresolved symbols, including {groups["gc-environment"].Length} GC environment requirements.");
        Console.WriteLine($"Reports: {output}");
    }

    private static async Task<string> PrepareSourceAsync(string root, RuntimeExperiment.SourceLock pin)
    {
        var source = Path.Combine(root, ".tools", "upstream", $"runtime-{pin.RuntimeVersion}");
        if (!Directory.Exists(Path.Combine(source, ".git")))
        {
            if (Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any())
                throw new InvalidOperationException("Runtime source directory exists without Git metadata; refusing to overwrite it.");
            Directory.CreateDirectory(source);
            await GitAsync(source, ["init"]);
            await GitAsync(source, ["remote", "add", "origin", pin.RuntimeRepository + ".git"]);
            await GitAsync(source, ["fetch", "--depth=1", "--filter=blob:none", "origin", pin.RuntimeCommit], 600);
            await GitAsync(source, ["sparse-checkout", "init", "--cone"]);
            await GitAsync(source, ["sparse-checkout", "set", "eng", "src/coreclr", "src/native"]);
            await GitAsync(source, ["checkout", "--detach", pin.RuntimeCommit], 600);
        }
        var revision = (await GitAsync(source, ["rev-parse", "HEAD"])).Trim();
        var remote = (await GitAsync(source, ["remote", "get-url", "origin"])).Trim();
        if (revision != pin.RuntimeCommit || (remote.TrimEnd('/') != pin.RuntimeRepository && remote.TrimEnd('/') != pin.RuntimeRepository + ".git"))
            throw new InvalidDataException("Existing runtime checkout differs from the pinned repository/commit; it was not changed.");
        await RequireCleanAsync(source);
        await GitAsync(source, ["sparse-checkout", "add", "eng", "src/coreclr", "src/native"], 600);
        await RequireCleanAsync(source);
        return source;
    }

    private static async Task RequireCleanAsync(string source)
    {
        if (!string.IsNullOrWhiteSpace(await GitAsync(source, ["status", "--porcelain", "--untracked-files=normal"])))
            throw new InvalidDataException("Runtime source checkout has local changes; refusing to build or overwrite them.");
    }

    private static async Task<string> GitAsync(string source, string[] args, int timeout = 60)
    {
        var result = await Processes.RunAsync("git", ["-c", $"safe.directory={source.Replace('\\', '/')}", .. args], source, timeout);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"Runtime Git {args[0]} failed. {result.Output}\n{result.Error}");
        return result.Output;
    }

    private static string BatchPath(string path)
    {
        // cmd.exe is needed only for the upstream .cmd build entry. Keep its
        // generated script free of user-controlled shell metacharacters.
        if (path.Any(c => c > 127) || path.IndexOfAny(['"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n']) >= 0)
            throw new InvalidOperationException("Native source build currently requires ASCII paths without command-shell metacharacters.");
        return '"' + path + '"';
    }

    private static async Task<SourceBuild> BuildAsync(string root, string source, string output, string msvc, string profile, bool overlay)
    {
        var script = Path.Combine(output, "build-" + profile + ".cmd");
        var hook = Path.Combine(root, "src", "Runtime.NativeAot", "runtime-overlay.cmake").Replace('\\', '/');
        var extra = overlay ? " -cmakeargs " + BatchPath("-DCMAKE_PROJECT_CoreCLR_INCLUDE=" + hook) : "";
        await File.WriteAllTextAsync(script, "@echo off\r\nsetlocal\r\nset \"NumberOfCores=4\"\r\nset \"CMAKE_BUILD_PARALLEL_LEVEL=4\"\r\ncall " +
            BatchPath(Path.Combine(source, "src", "coreclr", "build-runtime.cmd")) +
            " -x64 -release -component nativeaot -subdir " + profile + extra + "\r\nexit /b %errorlevel%\r\n", Encoding.ASCII);
        Console.WriteLine($"Building source profile {profile} (up to four parallel native jobs)...");
        var build = await Processes.RunAsync("cmd.exe", ["/d", "/c", script], root, 900);
        await File.WriteAllTextAsync(Path.Combine(output, profile + "-build.log"), build.Output + build.Error);
        if (build.TimedOut || build.ExitCode != 0)
            throw new InvalidOperationException($"Source profile {profile} failed (exit={build.ExitCode}, timeout={build.TimedOut}). See artifacts/runtime-source/{profile}-build.log.");
        var sdk = Path.Combine(source, "artifacts", "bin", "coreclr", "windows.x64.Release", profile, "aotsdk");
        var archive = Path.Combine(sdk, "Runtime.WorkstationGC.lib");
        var listing = await Processes.RunAsync(Path.Combine(msvc, "lib.exe"), ["/nologo", "/list", archive], root);
        if (listing.ExitCode != 0 || listing.TimedOut) throw new InvalidDataException("Cannot inspect source-built runtime archive.");
        await File.WriteAllTextAsync(Path.Combine(output, profile + "-members.txt"), listing.Output);
        var members = listing.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var obj = Path.Combine(source, "artifacts", "obj", "coreclr", "windows.x64.Release", profile);
        var allCommands = JsonSerializer.Deserialize<CompileCommand[]>(await File.ReadAllTextAsync(Path.Combine(obj, "compile_commands.json")), Json)
            ?? throw new InvalidDataException("Missing native compile commands.");
        static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
        var commands = allCommands.Where(c => Normalize(c.Output).Contains("/Runtime.WorkstationGC.dir/", StringComparison.Ordinal)).ToArray();
        var paths = commands.Select(c => Normalize(c.File)).ToArray();
        string[] required = ["/gc/gcwks.cpp", "/gc/gchandletable.cpp", "/Runtime/startup.cpp", "/Runtime/thread.cpp", "/Runtime/TypeManager.cpp"];
        if (required.Any(s => !paths.Any(p => p.EndsWith(s, StringComparison.Ordinal))) ||
            paths.Count(p => p.EndsWith("/gc/windows/gcenv.windows.cpp", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            paths.Count(p => p.EndsWith("/gcenv.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0) ||
            paths.Count(p => p.EndsWith("/Runtime/Crst.cpp", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            paths.Count(p => p.EndsWith("/crst.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0) ||
            members.Count(m => Normalize(m).EndsWith("/Crst.cpp.obj", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            members.Count(m => Normalize(m).EndsWith("/crst.witos.cpp.obj", StringComparison.Ordinal)) != (overlay ? 1 : 0) || members.Length < 50)
            throw new InvalidDataException("Native source build did not compile the expected full runtime and selected GC/Crst adapters.");
        foreach (var file in new[] { "PalCommon.cpp", "PalMinWin.cpp" })
            if (paths.Count(p => p.EndsWith("/windows/" + file, StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
                members.Count(m => Normalize(m).EndsWith("/" + file + ".obj", StringComparison.Ordinal)) != (overlay ? 0 : 1))
                throw new InvalidDataException("Runtime archive contains the wrong platform PAL source.");
        if (paths.Count(p => p.EndsWith("/pal.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Runtime archive did not compile the selected WitOS PAL.");
        await File.WriteAllTextAsync(Path.Combine(output, profile + "-compile-commands.json"), JsonSerializer.Serialize(commands, Json));

        var minipalArchive = Path.Combine(sdk, "aotminipal.lib");
        var minipalListing = await Processes.RunAsync(Path.Combine(msvc, "lib.exe"), ["/nologo", "/list", minipalArchive], root);
        if (minipalListing.ExitCode != 0 || minipalListing.TimedOut) throw new InvalidDataException("Cannot inspect source-built aotminipal archive.");
        await File.WriteAllTextAsync(Path.Combine(output, profile + "-minipal-members.txt"), minipalListing.Output);
        var minipalMembers = minipalListing.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var minipalCommands = allCommands.Where(c => Normalize(c.Output).Contains("/aotminipal.dir/", StringComparison.Ordinal)).ToArray();
        if (minipalCommands.Count(c => Normalize(c.File).EndsWith("/minipal/mutex.c", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            minipalCommands.Count(c => Normalize(c.File).EndsWith("/mutex.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0) ||
            minipalMembers.Count(m => Normalize(m).EndsWith("/mutex.c.obj", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            minipalMembers.Count(m => Normalize(m).EndsWith("/mutex.witos.cpp.obj", StringComparison.Ordinal)) != (overlay ? 1 : 0) ||
            minipalMembers.Length < 2)
            throw new InvalidDataException("Source build did not compile the selected minipal mutex implementation.");
        await File.WriteAllTextAsync(Path.Combine(output, profile + "-minipal-compile-commands.json"), JsonSerializer.Serialize(minipalCommands, Json));
        if (overlay)
        {
            foreach (var file in new[] { "/gcenv.witos.cpp", "/gc_events.witos.cpp", "/gc_time.witos.cpp", "/crst.witos.cpp", "/native_new.witos.cpp", "/tls.witos.cpp", "/pal.witos.cpp", "/pal_memory.witos.cpp", "/pal_events.witos.cpp", "/pal_threads.witos.cpp", "/System.Native/thread.c", "/System.Native/image.c", "/pal_module.witos.cpp", "/pal_environment.witos.cpp", "/pal_error.witos.cpp", "/native_error.asm", "/native_environment.asm" })
            {
                var adapter = commands.Single(c => Normalize(c.File).EndsWith(file, StringComparison.Ordinal));
                NativeObject.VerifyArchive(archive, Path.GetFullPath(adapter.Output, adapter.Directory));
            }
            var mutex = minipalCommands.Single(c => Normalize(c.File).EndsWith("/mutex.witos.cpp", StringComparison.Ordinal));
            NativeObject.VerifyArchive(minipalArchive, Path.GetFullPath(mutex.Output, mutex.Directory));
        }
        return new(sdk, members, commands, Hash(archive), new(Hash(minipalArchive), minipalMembers, minipalCommands.Length));
    }

    private static async Task<object> RunReferenceAsync(string root, string output, string target, string msvc,
        string[] publishedInputs, string[] sourceInputs)
    {
        var hostDirectory = Path.Combine(output, "reference-host");
        Directory.CreateDirectory(hostDirectory);
        var dll = Path.Combine(hostDirectory, "NativeAotTarget.dll");
        var lines = await File.ReadAllLinesAsync(Path.Combine(target, "shared", "native", "link.rsp"));
        var replacements = 0;
        var seen = new HashSet<int>();
        var outputs = 0;
        for (var i = 0; i < lines.Length; ++i)
        {
            if (lines[i].StartsWith("/OUT:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "/OUT:\"" + dll + "\"";
                ++outputs;
            }
            else if (lines[i].StartsWith('"') && lines[i].EndsWith('"'))
            {
                var value = lines[i][1..^1];
                if (!Path.IsPathRooted(value)) continue;
                var index = Array.FindIndex(publishedInputs, p => p.Equals(Path.GetFullPath(value), StringComparison.OrdinalIgnoreCase));
                if (index < 0) continue;
                lines[i] = '"' + sourceInputs[index] + '"';
                seen.Add(index);
                ++replacements;
            }
        }
        if (outputs != 1 || seen.Count != publishedInputs.Length || replacements != publishedInputs.Length || lines.Any(l => l.Contains("/FORCE", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Could not replace every published native input with its source-built counterpart.");
        var rsp = Path.Combine(hostDirectory, "link.rsp");
        await File.WriteAllLinesAsync(rsp, lines);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["@" + rsp], root);
        File.Copy(Path.Combine(target, "shared", "native_host.exe"), Path.Combine(hostDirectory, "native_host.exe"), overwrite: true);
        var run = await Processes.RunAsync(Path.Combine(hostDirectory, "native_host.exe"), [], hostDirectory, 60);
        await File.WriteAllTextAsync(Path.Combine(output, "reference-host.log"), run.Output + run.Error);
        if (run.TimedOut || run.ExitCode != 0 || run.Output.Contains("[TARGET-FAIL]", StringComparison.Ordinal) ||
            !run.Output.Contains("[TARGET-SUCCESS]", StringComparison.Ordinal) ||
            Cases.Any(c => !run.Output.Contains("[TARGET-PASS] " + c, StringComparison.Ordinal)))
            throw new InvalidOperationException("Source-built Windows runtime reference failed native-host execution.");
        Console.Write(run.Output);
        return new { imageSha256 = Hash(dll), imageBytes = new FileInfo(dll).Length,
            nativeInputsReplaced = replacements, imports = NativeImports.Inspect(dll) };
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
