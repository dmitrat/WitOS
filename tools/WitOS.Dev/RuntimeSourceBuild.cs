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
        await RuntimeConfigProbe.PrepareAsync(root, pin);
        await RuntimeUnwindReference.PrepareAsync(root);
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
        if (!publishedInputs.Any(p => Path.GetFileName(p) == "standalonegc-disabled.lib") ||
            publishedInputs.Any(p => Path.GetFileName(p) == "standalonegc-enabled.lib"))
            throw new InvalidDataException("Startup probe requires the built-in GC selection profile.");
        var referenceInputs = Inputs(reference.Sdk);
        var portedInputs = Inputs(ported.Sdk);
        if (referenceInputs.Concat(portedInputs).Any(p => !File.Exists(p)))
            throw new InvalidDataException("Source build did not produce every captured NativeAOT native input.");
        if (ported.Commands.Any(c => c.Command.Contains("/guard:cf", StringComparison.OrdinalIgnoreCase) || c.Command.Contains("/guard:ehcont", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("WitOS native archive retained Windows CFG/EH-continuation instrumentation.");
        if (!reference.Commands.Any(c => c.Command.Contains("/guard:cf", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Windows reference lost its upstream CFG profile.");
        await RuntimeFormattingReference.RunAsync(root, msvc);
        await RuntimeEncodingReference.RunAsync(root, msvc);
        await RuntimeComReference.RunAsync(root, msvc);
        await RuntimeSuspendReference.RunAsync(root, msvc);
        await RuntimeUnwindReference.RunAsync(root, msvc);
        await RuntimeExceptionReference.RunAsync(root, msvc);
        await RuntimeGpReference.RunAsync(root, msvc);
        await RuntimeFailFastReference.RunAsync(root, msvc);
        await RuntimeSehReference.RunAsync(root, msvc);
        await RuntimeSecurityReference.RunAsync(root, msvc);
        var referenceHost = await RunReferenceAsync(root, output, target, msvc, publishedInputs, referenceInputs);
        var boundary = await RuntimeTargetExperiment.LinkBoundaryAsync(msvc, output,
            Path.Combine(target, "static", "NativeAotTarget.lib"), portedInputs, "witos-without-platform");
        if (boundary.Unresolved.Any(s => s.Contains("wit_native_", StringComparison.Ordinal) && !s.StartsWith("wit_native_", StringComparison.Ordinal)))
            throw new InvalidDataException("Native transport must retain C linkage, not unresolved C++-decorated declarations.");
        string[] implemented = ["ParseGCHeapAffinitizeRangesEntry@", "VirtualReset@", "VirtualReserve@", "VirtualCommit@", "VirtualDecommit@", "VirtualRelease@", "SupportsWriteWatch@", "Initialize@", "Shutdown@", "GetTotalProcessorCount@", "GetPhysicalMemoryLimit@", "GetVirtualMemoryLimit@", "GetVirtualMemoryMaxAddress@", "GetMemoryStatus@", "CanEnableGCCPUGroups@", "CanEnableGCNumaAware@", "YieldThread@", "Sleep@", "QueryPerformanceCounter@", "QueryPerformanceFrequency@", "GetLowPrecisionTimeStamp@"];
        string[] replacedMutexSymbols = ["minipal_mutex_init", "minipal_mutex_destroy", "minipal_mutex_enter", "minipal_mutex_leave",
            "__imp_InitializeCriticalSection", "__imp_DeleteCriticalSection", "__imp_EnterCriticalSection", "__imp_LeaveCriticalSection"];
        string[] palImplemented = ["PalPrintFatalError", "PalInitComAndFlsSlot", "PalAttachThread", "PalHijack", "PalGetCurrentOSThreadId", "PalGetMaximumStackBounds", "PalGetCurrentProcessId", "PalGetProcessCpuCount",
            "PalVirtualAlloc", "PalVirtualFree", "PalVirtualProtect", "PalCreateEventW", "PalSetEvent", "PalResetEvent",
            "PalWaitForSingleObjectEx", "PalCompatibleWaitAny", "PalCreateLowMemoryResourceNotification", "PalCloseHandle", "PalSleep", "PalSwitchToThread", "PalStartBackgroundGCThread", "PalStartFinalizerThread", "PalStartEventPipeHelperThread", "PalGetModuleHandleFromPointer", "PalGetModuleBounds", "PalGetCompleteThreadContext", "PalSetThreadContext", "PalRestoreContext", "GetSSP", "SetSSP", "PalAllocateCompleteOSContext", "PalAreShadowStacksEnabled", "PalGetHijackTarget", "PopulateControlSegmentRegisters", "PalGetModuleFileName", "PalSetCurrentThreadName", "PalGetEnvironmentVariable", "PalCopyTCharAsChar", "?PalInit@@"];
        if (boundary.Unresolved.Any(s => s is "SuspendThread" or "ResumeThread" or "__imp_SuspendThread" or "__imp_ResumeThread" or "CoInitializeEx" or "CoUninitialize" or "CoGetApartmentType" or "FormatMessageW" or "LocalFree" or "RegisterEventSourceW" or "DeregisterEventSource" or "ReportEventW" or "__imp_IsDebuggerPresent" or "GetModuleFileNameW" or "GetModuleHandleW" or "GetProcAddress" or "__imp_GetModuleFileNameW" or "__imp_GetModuleHandleW" or "__imp_GetProcAddress" or "MultiByteToWideChar" or "WideCharToMultiByte" or "__imp_MultiByteToWideChar" or "__imp_WideCharToMultiByte" or "WriteFile" or "GetStdHandle" or "GetConsoleOutputCP" or "GetCurrentProcessorNumberEx" or "CreateEventExW" or "SetEvent" or "WaitForMultipleObjectsEx" or "GetCurrentThreadId" or "__imp_GetCurrentThreadId" or "GetCurrentProcess" or "GetCurrentThread" or "DuplicateHandle" or "GetThreadPriority" or "CloseHandle" or "__imp_CloseHandle" or "Sleep" or "__imp_Sleep" or "VirtualAlloc" or "VirtualFree" or "__imp_VirtualAlloc" or "__imp_VirtualFree" or "BCryptGenRandom" or "__imp_BCryptGenRandom" or "__security_cookie" or "__security_check_cookie" or "__GSHandlerCheck" or "__stdio_common_vsnprintf_s" or "log" or "_fltused" or "__guard_dispatch_icall_fptr" or "_purecall" or "__report_rangecheckfailure" or "QueryPerformanceCounter" or "QueryPerformanceFrequency" or "GetTickCount64" or "__imp_QueryPerformanceCounter" or "__imp_QueryPerformanceFrequency" or "__imp_GetTickCount64" or "__imp_GetEnabledXStateFeatures" or "__chkstk" or "memset" or "memmove" or "memcmp" or "strcpy" or "strstr" or "strtoul" or "atexit" or "__imp_atexit" or "strlen" or "memcpy" or "strcmp" or "_stricmp" or "strtoull" or "_errno" or "__imp__errno" or "GetEnvironmentVariableW" or "__imp_GetEnvironmentVariableW" or "GetLastError" or "SetLastError" or "__imp_GetLastError" or "__imp_SetLastError") ||
            boundary.Unresolved.Any(s => palImplemented.Any(p => s.Contains(p, StringComparison.Ordinal))) ||
            boundary.Unresolved.Any(s => s is "__dyn_tls_init" or "__dyn_tls_on_demand_init" or "__tls_guard" or "__tlregdtor") ||
            boundary.Unresolved.Any(s => s.Contains("operator new", StringComparison.Ordinal) || s.Contains("operator delete", StringComparison.Ordinal) || s.Contains("?nothrow@std@@", StringComparison.Ordinal)) ||
            boundary.Unresolved.Any(s => replacedMutexSymbols.Contains(s, StringComparer.Ordinal)) ||
            boundary.Unresolved.Any(s => s.Contains("FlushProcessWriteBuffers", StringComparison.Ordinal)) ||
            boundary.Unresolved.Any(s => s.Contains("GetCacheSizePerLogicalCpu@GCToOSInterface", StringComparison.Ordinal)) ||
            boundary.Unresolved.Any(s => s.Contains("GCToOSInterface", StringComparison.Ordinal)) ||
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
            upstreamWorkingTreeClean = true, upstreamWorkingTreePatched = false, rhConfigAllocationChecksPatched = true, startupExitRegistrationChecked = true, allocHeapLockCleanupCorrected = true, threadStoreTlsDiscoveryAdapted = true,
            nativeRuntimeSourceBuilt = true, managedCompilerAndCoreLibFromLockedPackages = true,
            guestRuntimePorted = false, guestManagedExecution = false,
            referenceHostPassedCases = Cases, referenceHost,
            reference = new { reference.ArchiveSha256, members = reference.Members, compileUnits = reference.Commands.Length, minipal = reference.Minipal },
            ported = new { ported.ArchiveSha256, members = ported.Members, compileUnits = ported.Commands.Length, minipal = ported.Minipal },
            sourceOverlay = new[] { "artifacts/runtime-config/source/gc.witos.cpp", "artifacts/runtime-config/source/gcwks.witos.cpp", "artifacts/runtime-config/source/gchelpers.witos.cpp", "artifacts/runtime-config/source/finalizerhelpers.witos.cpp", "artifacts/runtime-config/source/gcenv.ee.witos.cpp", "src/Kernel/include/witos/handles.h", "src/Runtime.NativeAot/pal_hijack.witos.cpp", "src/Runtime.NativeAot/hijack_evidence.witos.h", "src/Runtime.NativeAot/pal_attach.witos.cpp", "src/System.Native/exception_classification.h", "src/Kernel.Arch.X64/native_exception_x64.cpp", "src/Kernel.Arch.X64/gp_reference_fixture.asm", "tests/Runtime.NativeAot/gp_reference.cpp", "src/Runtime.NativeAot/security_handler.witos.h", "src/Runtime.NativeAot/seh_security.witos.cpp", "tests/Runtime.NativeAot/seh_gs_frame.cpp", "tests/User.X64/runtime_seh.cpp", "src/Runtime.NativeAot/seh_scope.witos.cpp", "src/Runtime.NativeAot/seh_scope.witos.h", "src/Runtime.NativeAot/seh_validation.witos.cpp", "src/Runtime.NativeAot/seh_validation.witos.h", "tests/Runtime.NativeAot/seh_reference.cpp", "tests/User.X64/runtime_failfast.cpp", "src/Kernel/include/witos/fatal_info.h", "src/Runtime.NativeAot/failfast_exception.witos.cpp", "tests/User.X64/runtime_raise.cpp", "src/Kernel/include/witos/exception.h", "src/Kernel.Arch.X64/user_exception.c", "tests/User.X64/runtime_vectored.cpp", "src/Runtime.NativeAot/context_conversion.witos.h", "src/Runtime.NativeAot/native_exception.witos.cpp", "src/Kernel.Arch.X64/native_exception.asm", "src/Kernel/include/witos/exception.h", "src/Kernel.Arch.X64/user_exception.c", "src/Kernel.Arch.X64/entry.asm", "tests/User.X64/runtime_exception.cpp", "src/Kernel.Arch.X64/user_runtime_exception_fixture.asm", "src/Kernel.Arch.X64/user_runtime_unwind_tests.c", "src/Kernel/pe.c", "src/Kernel/include/witos/pe.h", "tests/User.X64/runtime_unwind.cpp", "tests/User.X64/runtime_unwind_protected.cpp", "tests/User.X64/runtime_unwind_entry.cpp", "src/Kernel.Arch.X64/user_runtime_unwind_fixture.asm", "src/Kernel/include/witos/unwind_metadata.h", "src/Runtime.NativeAot/unwind_guest.witos.cpp", "src/Runtime.NativeAot/unwind_checked.witos.cpp", "src/Runtime.NativeAot/unwind_checked.witos.h", "src/Runtime.NativeAot/unwind_environment.witos.h", "src/Runtime.NativeAot/unwind_validation.witos.cpp", "src/Runtime.NativeAot/unwind_validation.witos.h", "src/Kernel.Arch.X64/native_unwind.asm", "artifacts/runtime-unwind/unwinder.checked.cpp", "artifacts/runtime-unwind/unwinder.h", "artifacts/runtime-unwind/baseunwinder.h", "artifacts/runtime-unwind/win64unwind.h", "src/Runtime.NativeAot/unwind_scope.witos.cpp", "src/Runtime.NativeAot/unwind_scope.witos.h", "tests/User.X64/runtime_stack_lease.cpp", "src/Kernel/include/witos/stack_lease.h", "src/Kernel.Arch.X64/user_stack_lease.c", "src/Runtime.NativeAot/pal_context.witos.cpp", "tests/User.X64/runtime_context_set.cpp", "tests/User.X64/runtime_context_set_entry.cpp", "src/Kernel.Arch.X64/user_runtime_context_set_fixture.asm", "src/Kernel.Arch.X64/user_suspend.c", "src/Kernel.Arch.X64/user_suspend_tests.c", "src/Runtime.NativeAot/native_suspend.witos.cpp", "src/Kernel.Arch.X64/native_suspend.asm", "tests/User.X64/runtime_suspend.cpp", "src/Kernel.Arch.X64/user_runtime_suspend_fixture.asm", "tests/Runtime.NativeAot/suspend_reference.cpp", "tools/WitOS.Dev/RuntimeSuspendReference.cs", "src/Kernel.Arch.X64/user_thread_context.c", "src/Kernel/include/witos/thread_context.h", "tests/User.X64/runtime_context_capture.cpp", "src/Kernel.Arch.X64/user_runtime_capture_fixture.asm", "src/Runtime.NativeAot/pal_context_storage.witos.cpp", "src/Kernel.Arch.X64/user_cpu_context.c", "src/Kernel/include/witos/cpu_context_info.h", "tests/User.X64/runtime_context_storage.cpp", "src/Kernel.Arch.X64/user_runtime_context_fixture.asm", "artifacts/runtime-config/include/NativeContext.h", "src/Runtime.NativeAot/gc_policy.witos.cpp", "src/Kernel.Arch.X64/gc_policy.asm", "src/Kernel.Arch.X64/exceptions.c", "tools/WitOS.Dev/RuntimeGcPolicy.cs", "tests/User.X64/runtime_gc_policy.cpp", "src/Runtime.NativeAot/native_com.witos.cpp", "src/Runtime.NativeAot/com_counter.witos.h", "src/Kernel.Arch.X64/native_com.asm", "tests/User.X64/runtime_com.cpp", "tests/User.X64/runtime_com_entry.cpp", "src/Kernel.Arch.X64/user_runtime_com_fixture.asm", "tests/Runtime.NativeAot/com_reference.cpp", "tools/WitOS.Dev/RuntimeComReference.cs", "src/Runtime.NativeAot/native_diagnostics.witos.cpp", "src/Runtime.NativeAot/native_heap.witos.h", "src/Kernel.Arch.X64/native_diagnostics.asm", "tests/User.X64/runtime_diagnostics.cpp", "src/Kernel.Arch.X64/user_runtime_diagnostics_fixture.asm", "src/Runtime.NativeAot/pal_thread_name.witos.cpp", "src/Kernel.Arch.X64/user_thread_name.c", "src/Kernel/include/witos/thread_name.h", "tests/User.X64/runtime_thread_names.cpp", "src/Runtime.NativeAot/native_module.witos.cpp", "src/Kernel.Arch.X64/native_module.asm", "src/Kernel/include/witos/image_info.h", "src/Kernel.Arch.X64/user.c", "src/System.Native/image.c", "tests/User.X64/runtime_module_names.cpp", "src/Kernel.Arch.X64/user_runtime_module_fixture.asm", "src/Runtime.NativeAot/native_encoding.witos.cpp", "src/Runtime.NativeAot/native_encoding.witos.h", "src/Kernel.Arch.X64/native_encoding.asm", "tests/User.X64/runtime_encoding.cpp", "tests/Runtime.NativeAot/encoding_reference.cpp", "tools/WitOS.Dev/RuntimeEncodingReference.cs", "src/Runtime.NativeAot/native_console.witos.cpp", "src/Runtime.NativeAot/native_processor.witos.cpp", "src/Kernel.Arch.X64/native_console.asm", "src/Kernel.Arch.X64/native_processor.asm", "src/Kernel.Arch.X64/user_console.c", "src/Kernel/include/witos/console_info.h", "src/Runtime.NativeAot/native_wait.witos.cpp", "src/Kernel.Arch.X64/native_wait.asm", "src/Kernel.Arch.X64/user_apc.c", "src/Kernel.Arch.X64/user_objects.c", "src/Kernel/include/witos/wait_objects.h", "src/Runtime.NativeAot/native_thread_handles.witos.cpp", "src/Runtime.NativeAot/native_thread_create.witos.cpp", "src/Kernel.Arch.X64/native_thread_create.asm", "src/Kernel.Arch.X64/native_thread_handles.asm", "src/Kernel.Arch.X64/user_reference.c", "src/Kernel/include/witos/thread_reference.h", "src/Runtime.NativeAot/native_services.witos.cpp", "src/Kernel.Arch.X64/native_services.asm", "src/Runtime.NativeAot/native_memory.witos.cpp", "src/Kernel.Arch.X64/native_memory.asm", "tools/WitOS.Dev/RuntimeSecurityReference.cs", "tests/Runtime.NativeAot/security_frame.cpp", "tests/Runtime.NativeAot/security_reference.cpp", "src/Runtime.NativeAot/native_random.witos.cpp", "src/Kernel.Arch.X64/native_random.asm", "src/Kernel/random.c", "src/Kernel/include/witos/random.h", "src/Boot.Uefi/entropy.c", "src/Boot.Uefi/uefi.h", "src/Kernel/include/witos/boot.h", "src/Runtime.NativeAot/security_cookie.witos.cpp", "src/Runtime.NativeAot/security_handler.witos.cpp", "src/Kernel.Arch.X64/security_cookie.asm", "src/System.Native/native_security.h", "src/System.Native/diagnostics.h", "src/Runtime.NativeAot/native_format.witos.cpp", "src/Runtime.NativeAot/format_fixed.witos.cpp", "src/Runtime.NativeAot/format_fixed.witos.h", "src/Kernel.Arch.X64/native_format.asm", "tools/WitOS.Dev/RuntimeFormattingReference.cs", "tests/Runtime.NativeAot/format_reference.cpp", "src/Runtime.NativeAot/math.lock.json", "src/Runtime.NativeAot/math_bits.witos.h", "src/Runtime.NativeAot/native_math.witos.cpp", "artifacts/runtime-config/source/log.openlibm.c", "tools/WitOS.Dev/NativeMathSources.cs", "src/Runtime.NativeAot/gc_affinity.witos.cpp", "src/Runtime.NativeAot/runtime-overlay.cmake", "src/Runtime.NativeAot/config-probe/CMakeLists.txt", "src/Runtime.NativeAot/gcenv.witos.cpp",
                "src/Runtime.NativeAot/gcenv.witos.h", "src/Runtime.NativeAot/gc_events.witos.cpp", "src/Runtime.NativeAot/gc_time.witos.cpp",
                "src/Runtime.NativeAot/minipal_time.witos.cpp", "src/Runtime.NativeAot/minipal_time.witos.h", "src/Runtime.NativeAot/mutex.witos.cpp", "src/Runtime.NativeAot/crst.witos.cpp", "src/Runtime.NativeAot/native_new.witos.cpp", "src/Runtime.NativeAot/crt_config.witos.cpp", "src/Runtime.NativeAot/crt_memory.witos.c", "src/Runtime.NativeAot/crt_exit.witos.cpp", "src/System.Native/native_process.h", "src/System.Native/library_lifecycle.c", "src/System.Native/library_lifecycle.h", "src/Kernel/include/witos/library.h", "artifacts/runtime-config/source/rhconfig.witos.cpp", "artifacts/runtime-config/source/startup.witos.cpp", "artifacts/runtime-config/source/allocheap.witos.cpp", "artifacts/runtime-config/source/threadstore.witos.cpp", "artifacts/runtime-config/source/thread.witos.cpp", "artifacts/runtime-config/source/debugheader.witos.cpp", "artifacts/runtime-config/include/stressLog.h", "tools/WitOS.Dev/RuntimeStartupSources.cs", "src/Runtime.NativeAot/tls.witos.cpp", "src/Runtime.NativeAot/pal.witos.cpp", "src/Runtime.NativeAot/pal_init.witos.cpp", "src/Runtime.NativeAot/pal_memory.witos.cpp", "src/Runtime.NativeAot/pal_events.witos.cpp", "src/Runtime.NativeAot/pal_threads.witos.cpp", "src/System.Native/thread.c", "src/System.Native/image.c", "src/System.Native/image.h", "src/Runtime.NativeAot/pal_module.witos.cpp", "src/Runtime.NativeAot/pal_environment.witos.cpp", "src/Runtime.NativeAot/pal_environment.witos.h", "src/System.Native/error.h", "src/Runtime.NativeAot/pal_error.witos.cpp", "src/Kernel.Arch.X64/minipal_cpu.witos.cpp", "src/Kernel.Arch.X64/minipal_cpu.witos.h", "src/Kernel.Arch.X64/chkstk.asm", "src/Runtime.NativeAot/fatal.witos.cpp", "src/System.Native/diagnostics.h", "src/Runtime.NativeAot/native_clock.witos.cpp", "src/Kernel.Arch.X64/native_clock.asm", "src/Kernel.Arch.X64/native_error.asm", "src/Kernel.Arch.X64/native_environment.asm", "src/Runtime.NativeAot/pal.witos.h", "src/System.Native/tls.h", "src/System.Native/bootstrap.h", "src/Kernel/include/witos/types.h",
                "src/Kernel/include/witos/user_abi.h", "src/Kernel/include/witos/code_memory.h", "src/Kernel/include/witos/file_io.h", "src/Kernel/include/witos/storage_query.h", "src/Kernel/include/witos/thread_info.h", "src/Kernel/include/witos/image_info.h", "src/Kernel/include/witos/memory_info.h" }
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
        Console.WriteLine($"[SOURCE-PASS] aotminipal archive: {ported.Minipal.Members.Length} members; mutex/time adapters and upstream PRNG object verified byte-for-byte.");
        Console.WriteLine($"[SOURCE-PASS] Windows source-built reference: {Cases.Length} execution groups.");
        Console.WriteLine($"[SOURCE-PASS] Strict WitOS link boundary: {boundary.Unresolved.Length} unresolved symbols, including {groups["gc-environment"].Length} GC environment requirements.");
        Console.WriteLine($"Reports: {output}");
        await RuntimeReadiness.RunAsync(root, msvc, ported.Sdk);
    }

    internal static async Task<string> PrepareSourceAsync(string root, RuntimeExperiment.SourceLock pin)
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
            paths.Count(p => p.EndsWith("/System.Native/library_lifecycle.c", StringComparison.Ordinal)) != (overlay ? 1 : 0))
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
        if (minipalCommands.Count(c => Normalize(c.File).EndsWith("/minipal/time.c", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            minipalCommands.Count(c => Normalize(c.File).EndsWith("/minipal_time.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0) ||
            minipalMembers.Count(m => Normalize(m).EndsWith("/time.c.obj", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            minipalMembers.Count(m => Normalize(m).EndsWith("/minipal_time.witos.cpp.obj", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Source build did not select the expected minipal time implementation.");
        if (minipalCommands.Count(c => Normalize(c.File).EndsWith("/minipal/cpufeatures.c", StringComparison.Ordinal)) != (overlay ? 0 : 1) ||
            minipalCommands.Count(c => Normalize(c.File).EndsWith("/minipal_cpu.witos.cpp", StringComparison.Ordinal)) != (overlay ? 1 : 0))
            throw new InvalidDataException("Incorrect minipal CPU backend.");
        await File.WriteAllTextAsync(Path.Combine(output, profile + "-minipal-compile-commands.json"), JsonSerializer.Serialize(minipalCommands, Json));
        if (overlay)
        {
            await RuntimeGcPolicy.RunAsync(root, source, obj, archive);
            await RuntimeGcLayoutReference.RunAsync(root,msvc);
            foreach(var name in new[]{"unwind_scope.witos.cpp","unwind_guest.witos.cpp","unwind_checked.witos.cpp","unwind_validation.witos.cpp","unwinder.checked.cpp","native_unwind.asm","native_exception.witos.cpp","native_exception.asm","seh_validation.witos.cpp","seh_scope.witos.cpp","seh_security.witos.cpp","native_exception_x64.cpp"}) {
                var command=commands.Single(c=>Normalize(c.File).EndsWith("/"+name,StringComparison.Ordinal));
                if(command.Command.Contains("/GS-",StringComparison.Ordinal))throw new InvalidDataException("Production unwinder lost GS protection: "+name);
                NativeObject.VerifyArchive(archive,Path.GetFullPath(command.Output,command.Directory));
            }
            var hijack=commands.Single(c=>Normalize(c.File).EndsWith("/pal_hijack.witos.cpp",StringComparison.Ordinal));
            NativeObject.VerifyArchive(archive,Path.GetFullPath(hijack.Output,hijack.Directory));
            var hijackSymbols=NativeObject.Inspect(Path.GetFullPath(hijack.Output,hijack.Directory));
            if(hijack.Command.Contains("/GS-",StringComparison.Ordinal)||
               !hijackSymbols.UndefinedExternals.Any(s=>s.Contains("HijackCallback",StringComparison.Ordinal))||
               !hijackSymbols.UndefinedExternals.Any(s=>s.Contains("PalGetCompleteThreadContext",StringComparison.Ordinal)))
                throw new InvalidDataException("PAL hijack lost actual runtime callback/context dependencies.");
            var attachment=commands.Single(c=>Normalize(c.File).EndsWith("/pal_attach.witos.cpp",StringComparison.Ordinal));
            var attachmentSymbols=NativeObject.Inspect(Path.GetFullPath(attachment.Output,attachment.Directory));
            if(attachment.Command.Contains("/GS-",StringComparison.Ordinal)||
               !attachmentSymbols.UndefinedExternals.Any(s=>s.Contains("RuntimeThreadShutdown",StringComparison.Ordinal))||
               !attachmentSymbols.UndefinedExternals.Contains("wit_native_thread_on_exit"))
                throw new InvalidDataException("PAL attachment lost production protection or actual runtime exit dependencies.");
            var failfast=commands.Single(c=>Normalize(c.File).EndsWith("/failfast_exception.witos.cpp",StringComparison.Ordinal));
            if(!failfast.Command.Contains("/GS-",StringComparison.Ordinal)||!failfast.Command.Contains("/Od",StringComparison.Ordinal))throw new InvalidDataException("Fail-fast must not recursively require GS/optimized unwind state.");
            NativeObject.VerifyArchive(archive,Path.GetFullPath(failfast.Output,failfast.Directory));
            var exceptionBinding=commands.Single(c=>Normalize(c.File).EndsWith("/native_exception.asm",StringComparison.Ordinal));
            var exceptionSymbols=NativeObject.Inspect(Path.GetFullPath(exceptionBinding.Output,exceptionBinding.Directory),collectReferences:true,includeDefinedReferences:true);
            var exceptionReferences=exceptionSymbols.ExternalReferences??throw new InvalidDataException("Missing VEH binding references.");
            if(!exceptionSymbols.Sections.Any(s=>s.Name==".rdata"&&(s.Characteristics&0xE0000000U)==0x40000000U))throw new InvalidDataException("VEH import slots are not readonly.");
            if(!exceptionReferences.Any(r=>r.ContainingSymbol=="__imp_RaiseException"&&r.Target=="RaiseException"&&r.Section==".rdata"&&r.Kind==1))
                throw new InvalidDataException("RaiseException import is not a readonly alias of the real capture entry.");
            if(!exceptionReferences.Any(r=>r.ContainingSymbol=="__imp_RaiseFailFastException"&&r.Target=="RaiseFailFastException"&&r.Section==".rdata"&&r.Kind==1))
                throw new InvalidDataException("RaiseFailFastException lost readonly common entry binding.");
            foreach(var (name,target) in new[]{("AddVectoredExceptionHandler","wit_native_add_vectored_exception_handler"),("RemoveVectoredExceptionHandler","wit_native_remove_vectored_exception_handler")})
                if(!exceptionReferences.Any(r=>r.ContainingSymbol=="__imp_"+name&&r.Target==name&&r.Section==".rdata"&&r.Kind==1)||
                   !exceptionReferences.Any(r=>r.ContainingSymbol==name&&r.Target==target&&(r.Section==".text"||r.Section.StartsWith(".text$",StringComparison.Ordinal))&&r.Kind==4))
                    throw new InvalidDataException("VEH direct/import transport lost its actual common target: "+name);
            var unwindBinding=commands.Single(c=>Normalize(c.File).EndsWith("/native_unwind.asm",StringComparison.Ordinal));
            var unwindBindingPath=Path.GetFullPath(unwindBinding.Output,unwindBinding.Directory);
            var unwindSymbols=NativeObject.Inspect(unwindBindingPath,collectReferences:true,includeDefinedReferences:true);
            var unwindReferences=unwindSymbols.ExternalReferences??throw new InvalidDataException("Missing unwinder COFF relocation evidence.");
            if(!unwindSymbols.Sections.Any(s=>s.Name==".rdata"&&(s.Characteristics&0xE0000000U)==0x40000000U)||
                !unwindReferences.Any(r=>r.ContainingSymbol=="__imp_RtlVirtualUnwind"&&r.Target=="RtlVirtualUnwind"&&r.Section==".rdata"&&r.Offset==0&&r.Kind==1)||
                !unwindReferences.Any(r=>r.ContainingSymbol=="RtlVirtualUnwind"&&r.Target=="wit_native_rtl_virtual_unwind"&&(r.Section==".text"||r.Section.StartsWith(".text$",StringComparison.Ordinal))&&r.Offset==1&&r.Kind==4))
                throw new InvalidDataException("Unwinder direct/import binding lost its readonly alias or real tail-call target.");
            var unwindObjects=commands.Where(c=>new[]{"unwind_scope.witos.cpp","unwind_guest.witos.cpp","unwind_checked.witos.cpp","unwind_validation.witos.cpp","unwinder.checked.cpp","native_unwind.asm"}.Contains(Path.GetFileName(c.File)))
                .Select(c=>{var file=Path.GetFullPath(c.Output,c.Directory);return new{file,sha256=Hash(file),NativeObject.Inspect(file).UndefinedExternals};}).ToArray();
            await File.WriteAllTextAsync(Path.Combine(output,"witos-unwind-objects.json"),JsonSerializer.Serialize(new {guestUnwinderExecuted=false,archiveSha256=Hash(archive),objects=unwindObjects,exceptionObjects=commands.Where(c=>new[]{"native_exception.witos.cpp","native_exception.asm","failfast_exception.witos.cpp","seh_scope.witos.cpp","seh_validation.witos.cpp","seh_security.witos.cpp","native_exception_x64.cpp"}.Contains(Path.GetFileName(c.File)))
                .Select(c=>{var file=Path.GetFullPath(c.Output,c.Directory);return new{file,sha256=Hash(file)};}).ToArray(),contextObjects=commands.Where(c=>new[]{"pal_context.witos.cpp","pal_context_storage.witos.cpp"}.Contains(Path.GetFileName(c.File)))
                .Select(c=>{var file=Path.GetFullPath(c.Output,c.Directory);return new{file,sha256=Hash(file)};}).ToArray()},Json));
            if(commands.Single(c=>Normalize(c.File).EndsWith("/pal_context.witos.cpp",StringComparison.Ordinal)).Command.Contains("/GS-",StringComparison.Ordinal))throw new InvalidDataException("Production PAL context lost GS protection.");
            var contextStorage=commands.Single(c=>Normalize(c.File).EndsWith("/pal_context_storage.witos.cpp",StringComparison.Ordinal));
            if(contextStorage.Command.Contains("/GS-",StringComparison.Ordinal))
                throw new InvalidDataException("Production context storage lost compiler GS protection.");
            foreach (var file in new[] { "/pal_attach.witos.cpp", "/pal_context.witos.cpp", "/native_suspend.witos.cpp", "/native_suspend.asm", "/pal_context_storage.witos.cpp", "/gc_policy.witos.cpp", "/gc_policy.asm", "/native_com.witos.cpp", "/native_com.asm", "/native_diagnostics.witos.cpp", "/native_diagnostics.asm", "/pal_thread_name.witos.cpp", "/native_module.witos.cpp", "/native_module.asm", "/native_encoding.witos.cpp", "/native_encoding.asm", "/native_console.witos.cpp", "/native_processor.witos.cpp", "/native_console.asm", "/native_processor.asm", "/native_wait.witos.cpp", "/native_wait.asm", "/native_thread_handles.witos.cpp", "/native_thread_handles.asm", "/native_services.witos.cpp", "/native_services.asm", "/native_memory.witos.cpp", "/native_memory.asm", "/native_random.witos.cpp", "/native_random.asm", "/security_cookie.witos.cpp", "/security_handler.witos.cpp", "/security_cookie.asm", "/native_format.witos.cpp", "/format_fixed.witos.cpp", "/native_format.asm", "/native_math.witos.cpp", "/log.openlibm.c", "/gc_affinity.witos.cpp", "/gcenv.witos.cpp", "/gc_events.witos.cpp", "/gc_time.witos.cpp", "/crst.witos.cpp", "/native_new.witos.cpp", "/crt_config.witos.cpp", "/crt_memory.witos.c", "/crt_exit.witos.cpp", "/System.Native/library_lifecycle.c", "/rhconfig.witos.cpp", "/startup.witos.cpp", "/allocheap.witos.cpp", "/threadstore.witos.cpp", "/thread.witos.cpp", "/debugheader.witos.cpp", "/tls.witos.cpp", "/pal.witos.cpp", "/pal_init.witos.cpp", "/pal_memory.witos.cpp", "/pal_events.witos.cpp", "/pal_threads.witos.cpp", "/System.Native/thread.c", "/System.Native/image.c", "/pal_module.witos.cpp", "/pal_environment.witos.cpp", "/pal_error.witos.cpp", "/chkstk.asm", "/fatal.witos.cpp", "/native_clock.witos.cpp", "/native_clock.asm", "/native_error.asm", "/native_environment.asm" })
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
            foreach (var numeric in new[] { math, log }) {
                var fp = System.Text.RegularExpressions.Regex.Matches(numeric.Command, @"/fp:(strict|precise|fast)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (fp.Count == 0 || !fp[^1].Groups[1].Value.Equals("strict", StringComparison.OrdinalIgnoreCase) || !numeric.Command.Contains("/Od", StringComparison.Ordinal))
                    throw new InvalidDataException("Native log requires strict FP and the validated plain-unwind profile.");
            }
            var affinity = commands.Single(c => Normalize(c.File).EndsWith("/gc_affinity.witos.cpp", StringComparison.Ordinal));
            var fatal = commands.Single(c => Normalize(c.File).EndsWith("/fatal.witos.cpp", StringComparison.Ordinal));
            var security = new NativePlatformObjects(NativePlatformObjects.Sources.Select(name =>
            {
                var compile = commands.Single(c => Normalize(c.File).EndsWith("/" + name, StringComparison.Ordinal));
                return KeyValuePair.Create(name, Path.GetFullPath(compile.Output, compile.Directory));
            }));
            await RuntimeConfigProbe.VerifyArchiveAsync(root, obj, msvc, minipalArchive, Path.GetFullPath(memory.Output, memory.Directory),
                Path.GetFullPath(stack.Output, stack.Directory), Path.GetFullPath(clock.Output, clock.Directory), Path.GetFullPath(clockBinding.Output, clockBinding.Directory), Path.GetFullPath(fatal.Output, fatal.Directory), Path.GetFullPath(affinity.Output, affinity.Directory), Path.GetFullPath(math.Output, math.Directory), Path.GetFullPath(log.Output, log.Directory), security);
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
