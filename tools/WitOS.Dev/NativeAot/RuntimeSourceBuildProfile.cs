using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.NativeAot.References;
using WitOS.Dev.Pe;

namespace WitOS.Dev.NativeAot;

/// <summary>
/// Builds one profile of the upstream native runtime: the Windows reference or the WitOS overlay.
/// </summary>
internal static class RuntimeSourceBuildProfile
{
    #region Fields

    private static readonly JsonSerializerOptions JSON = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    #endregion

    #region Functions

    /// <summary>
    /// Runs the upstream native build for one profile and records its compile commands, archives and objects.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="source">Runtime source tree.</param>
    /// <param name="output">Report directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <param name="profile">Profile name: reference or witos.</param>
    /// <param name="overlay">Whether to apply the WitOS source overlay.</param>
    /// <returns>Recorded build outcome.</returns>
    internal static async Task<RuntimeSourceBuildResult> BuildAsync(string root, string source, string output, string msvc, string profile, bool overlay)
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
        if (listing.ExitCode != 0 || listing.TimedOut)
            throw new InvalidDataException("Cannot inspect source-built runtime archive.");
        await File.WriteAllTextAsync(Path.Combine(output, profile + "-members.txt"), listing.Output);
        var members = listing.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var obj = Path.Combine(source, "artifacts", "obj", "coreclr", "windows.x64.Release", profile);
        var allCommands = JsonSerializer.Deserialize<RuntimeSourceBuildCommand[]>(await File.ReadAllTextAsync(Path.Combine(obj, "compile_commands.json")), JSON)
            ?? throw new InvalidDataException("Missing native compile commands.");
        static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
        var commands = allCommands.Where(c => Normalize(c.Output).Contains("/Runtime.WorkstationGC.dir/", StringComparison.Ordinal)).ToArray();
        var paths = commands.Select(c => Normalize(c.File)).ToArray();
        if (commands.Any(c => (c.Command.Contains("-DNO_STRESS_LOG", StringComparison.Ordinal) ||
            c.Command.Contains("/DNO_STRESS_LOG", StringComparison.Ordinal)) != overlay))
            throw new InvalidDataException("Runtime archive contains the wrong stress-log profile.");
        string[] required = [overlay ? "/gcwks.witos.cpp" : "/gc/gcwks.cpp", "/gc/gchandletable.cpp", overlay ? "/startup.witos.cpp" : "/Runtime/startup.cpp", overlay ? "/thread.witos.cpp" : "/Runtime/thread.cpp", overlay ? "/threadstore.witos.cpp" : "/Runtime/threadstore.cpp", "/Runtime/TypeManager.cpp"];
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
        if (paths.Count(p => p.EndsWith("/pal.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0) ||
            paths.Count(p => p.EndsWith("/pal_init.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Runtime archive did not compile the selected WitOS PAL.");
        if (paths.Count(p => p.EndsWith("/Runtime/startup.cpp", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            paths.Count(p => p.EndsWith("/startup.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0) ||
            paths.Count(p => p.EndsWith("/crt_exit.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0) ||
            paths.Count(p => p.EndsWith("/Runtime.Native/library_lifecycle.c", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Runtime archive contains the wrong startup/exit registration policy.");
        if (paths.Count(p => p.EndsWith("/Runtime/DebugHeader.cpp", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            paths.Count(p => p.EndsWith("/debugheader.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Runtime archive contains the wrong diagnostic metadata profile.");
        if (paths.Count(p => p.EndsWith("/Runtime/thread.cpp", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            paths.Count(p => p.EndsWith("/thread.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Runtime archive contains the wrong Thread OS-handle policy.");
        if (paths.Count(p => p.EndsWith("/Runtime/threadstore.cpp", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            paths.Count(p => p.EndsWith("/threadstore.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Runtime archive contains the wrong ThreadStore TLS policy.");
        if (paths.Count(p => p.EndsWith("/Runtime/allocheap.cpp", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            paths.Count(p => p.EndsWith("/allocheap.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Runtime archive contains the wrong AllocHeap lifecycle policy.");
        if (paths.Count(p => p.EndsWith("/Runtime/RhConfig.cpp", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            paths.Count(p => p.EndsWith("/rhconfig.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Runtime archive contains the wrong RhConfig allocation policy.");
        await File.WriteAllTextAsync(Path.Combine(output, profile + "-compile-commands.json"), JsonSerializer.Serialize(commands, JSON));

        var minipalArchive = Path.Combine(sdk, "aotminipal.lib");
        var minipalListing = await Processes.RunAsync(Path.Combine(msvc, "lib.exe"), ["/nologo", "/list", minipalArchive], root);
        if (minipalListing.ExitCode != 0 || minipalListing.TimedOut)
            throw new InvalidDataException("Cannot inspect source-built aotminipal archive.");
        await File.WriteAllTextAsync(Path.Combine(output, profile + "-minipal-members.txt"), minipalListing.Output);
        var minipalMembers = minipalListing.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var minipalCommands = allCommands.Where(c => Normalize(c.Output).Contains("/aotminipal.dir/", StringComparison.Ordinal)).ToArray();
        if (minipalCommands.Count(c => Normalize(c.File).EndsWith("/minipal/mutex.c", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            minipalCommands.Count(c => Normalize(c.File).EndsWith("/mutex.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0) ||
            minipalMembers.Count(m => Normalize(m).EndsWith("/mutex.c.obj", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            minipalMembers.Count(m => Normalize(m).EndsWith("/mutex.witos.cpp.obj", StringComparison.Ordinal)) != (overlay ? 1 : 0) ||
            minipalMembers.Length < 2)
            throw new InvalidDataException("Source build did not compile the selected minipal mutex implementation.");
        if (minipalCommands.Count(c => Normalize(c.File).EndsWith("/minipal/time.c", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            minipalCommands.Count(c => Normalize(c.File).EndsWith("/minipal_time.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0) ||
            minipalMembers.Count(m => Normalize(m).EndsWith("/time.c.obj", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            minipalMembers.Count(m => Normalize(m).EndsWith("/minipal_time.witos.cpp.obj", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Source build did not select the expected minipal time implementation.");
        if (minipalCommands.Count(c => Normalize(c.File).EndsWith("/minipal/cpufeatures.c", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            minipalCommands.Count(c => Normalize(c.File).EndsWith("/minipal_cpu.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Incorrect minipal CPU backend.");
        await File.WriteAllTextAsync(Path.Combine(output, profile + "-minipal-compile-commands.json"), JsonSerializer.Serialize(minipalCommands, JSON));
        if (overlay)
        {
            await RuntimeGcPolicy.RunAsync(root, source, obj, archive);
            await RuntimeGcLayoutReference.RunAsync(root, msvc);
            foreach (var name in new[] { "unwind_scope.witos.cpp", "unwind_guest.witos.cpp", "unwind_checked.witos.cpp", "unwind_validation.witos.cpp", "unwinder.checked.cpp", "native_unwind.asm", "native_exception.witos.cpp", "native_exception.asm", "seh_validation.witos.cpp", "seh_scope.witos.cpp", "seh_security.witos.cpp", "native_exception_x64.cpp" })
            {
                var command = commands.Single(c => Normalize(c.File).EndsWith("/" + name, StringComparison.Ordinal));
                if (command.Command.Contains("/GS-", StringComparison.Ordinal))
                    throw new InvalidDataException("Production unwinder lost GS protection: " + name);
                NativeObject.VerifyArchive(archive, Path.GetFullPath(command.Output, command.Directory));
            }
            var hijack = commands.Single(c => Normalize(c.File).EndsWith("/pal_hijack.witos.cpp", StringComparison.Ordinal));
            NativeObject.VerifyArchive(archive, Path.GetFullPath(hijack.Output, hijack.Directory));
            var hijackSymbols = NativeObject.Inspect(Path.GetFullPath(hijack.Output, hijack.Directory));
            if (hijack.Command.Contains("/GS-", StringComparison.Ordinal) ||
               !hijackSymbols.UndefinedExternals.Any(s => s.Contains("HijackCallback", StringComparison.Ordinal)) ||
               !hijackSymbols.UndefinedExternals.Any(s => s.Contains("PalGetCompleteThreadContext", StringComparison.Ordinal)))
                throw new InvalidDataException("PAL hijack lost actual runtime callback/context dependencies.");
            var attachment = commands.Single(c => Normalize(c.File).EndsWith("/pal_attach.witos.cpp", StringComparison.Ordinal));
            var attachmentSymbols = NativeObject.Inspect(Path.GetFullPath(attachment.Output, attachment.Directory));
            if (attachment.Command.Contains("/GS-", StringComparison.Ordinal) ||
               !attachmentSymbols.UndefinedExternals.Any(s => s.Contains("RuntimeThreadShutdown", StringComparison.Ordinal)) ||
               !attachmentSymbols.UndefinedExternals.Contains("wit_native_thread_on_exit"))
                throw new InvalidDataException("PAL attachment lost production protection or actual runtime exit dependencies.");
            var failfast = commands.Single(c => Normalize(c.File).EndsWith("/failfast_exception.witos.cpp", StringComparison.Ordinal));
            if (!failfast.Command.Contains("/GS-", StringComparison.Ordinal) || !failfast.Command.Contains("/Od", StringComparison.Ordinal))
                throw new InvalidDataException("Fail-fast must not recursively require GS/optimized unwind state.");
            NativeObject.VerifyArchive(archive, Path.GetFullPath(failfast.Output, failfast.Directory));
            var exceptionBinding = commands.Single(c => Normalize(c.File).EndsWith("/native_exception.asm", StringComparison.Ordinal));
            var exceptionSymbols = NativeObject.Inspect(Path.GetFullPath(exceptionBinding.Output, exceptionBinding.Directory), collectReferences: true, includeDefinedReferences: true);
            var exceptionReferences = exceptionSymbols.ExternalReferences ?? throw new InvalidDataException("Missing VEH binding references.");
            if (!exceptionSymbols.Sections.Any(s => s.Name == ".rdata" && (s.Characteristics & 0xE0000000U) == 0x40000000U))
                throw new InvalidDataException("VEH import slots are not readonly.");
            if (!exceptionReferences.Any(r => r.ContainingSymbol == "__imp_RaiseException" && r.Target == "RaiseException" && r.Section == ".rdata" && r.Kind == 1))
                throw new InvalidDataException("RaiseException import is not a readonly alias of the real capture entry.");
            if (!exceptionReferences.Any(r => r.ContainingSymbol == "__imp_RaiseFailFastException" && r.Target == "RaiseFailFastException" && r.Section == ".rdata" && r.Kind == 1))
                throw new InvalidDataException("RaiseFailFastException lost readonly common entry binding.");
            foreach (var (name, target) in new[] { ("AddVectoredExceptionHandler", "wit_native_add_vectored_exception_handler"), ("RemoveVectoredExceptionHandler", "wit_native_remove_vectored_exception_handler") })
                if (!exceptionReferences.Any(r => r.ContainingSymbol == "__imp_" + name && r.Target == name && r.Section == ".rdata" && r.Kind == 1) ||
                   !exceptionReferences.Any(r => r.ContainingSymbol == name && r.Target == target && (r.Section == ".text" || r.Section.StartsWith(".text$", StringComparison.Ordinal)) && r.Kind == 4))
                    throw new InvalidDataException("VEH direct/import transport lost its actual common target: " + name);
            var unwindBinding = commands.Single(c => Normalize(c.File).EndsWith("/native_unwind.asm", StringComparison.Ordinal));
            var unwindBindingPath = Path.GetFullPath(unwindBinding.Output, unwindBinding.Directory);
            var unwindSymbols = NativeObject.Inspect(unwindBindingPath, collectReferences: true, includeDefinedReferences: true);
            var unwindReferences = unwindSymbols.ExternalReferences ?? throw new InvalidDataException("Missing unwinder COFF relocation evidence.");
            if (!unwindSymbols.Sections.Any(s => s.Name == ".rdata" && (s.Characteristics & 0xE0000000U) == 0x40000000U) ||
                !unwindReferences.Any(r => r.ContainingSymbol == "__imp_RtlVirtualUnwind" && r.Target == "RtlVirtualUnwind" && r.Section == ".rdata" && r.Offset == 0 && r.Kind == 1) ||
                !unwindReferences.Any(r => r.ContainingSymbol == "RtlVirtualUnwind" && r.Target == "wit_native_rtl_virtual_unwind" && (r.Section == ".text" || r.Section.StartsWith(".text$", StringComparison.Ordinal)) && r.Offset == 1 && r.Kind == 4))
                throw new InvalidDataException("Unwinder direct/import binding lost its readonly alias or real tail-call target.");
            var unwindObjects = commands.Where(c => new[] { "unwind_scope.witos.cpp", "unwind_guest.witos.cpp", "unwind_checked.witos.cpp", "unwind_validation.witos.cpp", "unwinder.checked.cpp", "native_unwind.asm" }.Contains(Path.GetFileName(c.File)))
                .Select(c => { var file = Path.GetFullPath(c.Output, c.Directory); return new { file, sha256 = Hash(file), NativeObject.Inspect(file).UndefinedExternals }; }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(output, "witos-unwind-objects.json"), JsonSerializer.Serialize(new
            {
                guestUnwinderExecuted = false,
                archiveSha256 = Hash(archive),
                objects = unwindObjects,
                exceptionObjects = commands.Where(c => new[] { "native_exception.witos.cpp", "native_exception.asm", "failfast_exception.witos.cpp", "seh_scope.witos.cpp", "seh_validation.witos.cpp", "seh_security.witos.cpp", "native_exception_x64.cpp" }.Contains(Path.GetFileName(c.File)))
                .Select(c => { var file = Path.GetFullPath(c.Output, c.Directory); return new { file, sha256 = Hash(file) }; }).ToArray(),
                contextObjects = commands.Where(c => new[] { "pal_context.witos.cpp", "pal_context_storage.witos.cpp" }.Contains(Path.GetFileName(c.File)))
                .Select(c => { var file = Path.GetFullPath(c.Output, c.Directory); return new { file, sha256 = Hash(file) }; }).ToArray()
            }, JSON));
            if (commands.Single(c => Normalize(c.File).EndsWith("/pal_context.witos.cpp", StringComparison.Ordinal)).Command.Contains("/GS-", StringComparison.Ordinal))
                throw new InvalidDataException("Production PAL context lost GS protection.");
            var contextStorage = commands.Single(c => Normalize(c.File).EndsWith("/pal_context_storage.witos.cpp", StringComparison.Ordinal));
            if (contextStorage.Command.Contains("/GS-", StringComparison.Ordinal))
                throw new InvalidDataException("Production context storage lost compiler GS protection.");
            foreach (var file in new[] { "/pal_attach.witos.cpp", "/pal_context.witos.cpp", "/native_suspend.witos.cpp", "/native_suspend.asm", "/pal_context_storage.witos.cpp", "/gc_policy.witos.cpp", "/gc_policy.asm", "/native_com.witos.cpp", "/native_com.asm", "/native_diagnostics.witos.cpp", "/native_diagnostics.asm", "/pal_thread_name.witos.cpp", "/native_module.witos.cpp", "/native_module.asm", "/native_encoding.witos.cpp", "/native_encoding.asm", "/native_console.witos.cpp", "/native_processor.witos.cpp", "/native_console.asm", "/native_processor.asm", "/native_wait.witos.cpp", "/native_wait.asm", "/native_thread_handles.witos.cpp", "/native_thread_handles.asm", "/native_services.witos.cpp", "/native_services.asm", "/native_memory.witos.cpp", "/native_memory.asm", "/native_random.witos.cpp", "/native_random.asm", "/security_cookie.witos.cpp", "/security_handler.witos.cpp", "/security_cookie.asm", "/native_format.witos.cpp", "/format_fixed.witos.cpp", "/native_format.asm", "/native_math.witos.cpp", "/log.openlibm.c", "/gc_affinity.witos.cpp", "/gcenv.witos.cpp", "/gc_events.witos.cpp", "/gc_time.witos.cpp", "/crst.witos.cpp", "/native_new.witos.cpp", "/crt_config.witos.cpp", "/crt_memory.witos.c", "/crt_exit.witos.cpp", "/Runtime.Native/library_lifecycle.c", "/rhconfig.witos.cpp", "/startup.witos.cpp", "/allocheap.witos.cpp", "/threadstore.witos.cpp", "/thread.witos.cpp", "/debugheader.witos.cpp", "/tls.witos.cpp", "/pal.witos.cpp", "/pal_init.witos.cpp", "/pal_memory.witos.cpp", "/pal_events.witos.cpp", "/pal_threads.witos.cpp", "/Runtime.Native/thread.c", "/Runtime.Native/image.c", "/pal_module.witos.cpp", "/pal_environment.witos.cpp", "/pal_error.witos.cpp", "/chkstk.asm", "/fatal.witos.cpp", "/native_clock.witos.cpp", "/native_clock.asm", "/native_error.asm", "/native_environment.asm" })
            {
                var adapter = commands.Single(c => Normalize(c.File).EndsWith(file, StringComparison.Ordinal));
                NativeObject.VerifyArchive(archive, Path.GetFullPath(adapter.Output, adapter.Directory));
            }
            var mutex = minipalCommands.Single(c => Normalize(c.File).EndsWith("/mutex.witos.cpp", StringComparison.Ordinal));
            NativeObject.VerifyArchive(minipalArchive, Path.GetFullPath(mutex.Output, mutex.Directory));
            var time = minipalCommands.Single(c => Normalize(c.File).EndsWith("/minipal_time.witos.cpp", StringComparison.Ordinal));
            if (!time.Command.Contains("/Od", StringComparison.Ordinal))
                throw new InvalidDataException("Minipal time must retain the current plain-unwind bootstrap profile.");
            NativeObject.VerifyArchive(minipalArchive, Path.GetFullPath(time.Output, time.Directory));
            var cpu = minipalCommands.Single(c => Normalize(c.File).EndsWith("/minipal_cpu.witos.cpp", StringComparison.Ordinal));
            NativeObject.VerifyArchive(minipalArchive, Path.GetFullPath(cpu.Output, cpu.Directory));
            var random = minipalCommands.Single(c => Normalize(c.File).EndsWith("/minipal/xoshiro128pp.c", StringComparison.Ordinal));
            NativeObject.VerifyArchive(minipalArchive, Path.GetFullPath(random.Output, random.Directory));
            var memory = commands.Single(c => Normalize(c.File).EndsWith("/crt_memory.witos.c", StringComparison.Ordinal));
            var stack = commands.Single(c => Normalize(c.File).EndsWith("/chkstk.asm", StringComparison.Ordinal));
            var clock = commands.Single(c => Normalize(c.File).EndsWith("/native_clock.witos.cpp", StringComparison.Ordinal));
            var clockBinding = commands.Single(c => Normalize(c.File).EndsWith("/native_clock.asm", StringComparison.Ordinal));
            var math = commands.Single(c => Normalize(c.File).EndsWith("/native_math.witos.cpp", StringComparison.Ordinal));
            var log = commands.Single(c => Normalize(c.File).EndsWith("/log.openlibm.c", StringComparison.Ordinal));
            foreach (var numeric in new[] { math, log })
            {
                var fp = System.Text.RegularExpressions.Regex.Matches(numeric.Command, @"/fp:(strict|precise|fast)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (fp.Count == 0 || !fp[^1].Groups[1].Value.Equals("strict", StringComparison.OrdinalIgnoreCase) || !numeric.Command.Contains("/Od", StringComparison.Ordinal))
                    throw new InvalidDataException("Native log requires strict FP and the validated plain-unwind profile.");
            }
            var affinity = commands.Single(c => Normalize(c.File).EndsWith("/gc_affinity.witos.cpp", StringComparison.Ordinal));
            var fatal = commands.Single(c => Normalize(c.File).EndsWith("/fatal.witos.cpp", StringComparison.Ordinal));
            var security = new NativePlatformObjects(NativePlatformObjects.SOURCES.Select(name =>
            {
                var compile = commands.Single(c => Normalize(c.File).EndsWith("/" + name, StringComparison.Ordinal));
                return KeyValuePair.Create(name, Path.GetFullPath(compile.Output, compile.Directory));
            }));
            await RuntimeConfigProbe.VerifyArchiveAsync(root, obj, msvc, minipalArchive, Path.GetFullPath(memory.Output, memory.Directory),
                Path.GetFullPath(stack.Output, stack.Directory), Path.GetFullPath(clock.Output, clock.Directory), Path.GetFullPath(clockBinding.Output, clockBinding.Directory), Path.GetFullPath(fatal.Output, fatal.Directory), Path.GetFullPath(affinity.Output, affinity.Directory), Path.GetFullPath(math.Output, math.Directory), Path.GetFullPath(log.Output, log.Directory), security);
        }
        return new(sdk, members, commands, Hash(archive), new(Hash(minipalArchive), minipalMembers, minipalCommands.Length));
    }

    #endregion

    #region Tools

    private static string BatchPath(string path)
    {
        // cmd.exe is needed only for the upstream .cmd build entry. Keep its
        // generated script free of user-controlled shell metacharacters.
        if (path.Any(c => c > 127) || path.IndexOfAny(['"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n']) >= 0)
            throw new InvalidOperationException("Native source build currently requires ASCII paths without command-shell metacharacters.");
        return '"' + path + '"';
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    #endregion
}
