using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.NativeAot.References;
using WitOS.Dev.Pe;

namespace WitOS.Dev.NativeAot;

/// <summary>
/// Builds the full upstream native runtime libraries and verifies the WitOS source overlay.
/// </summary>
internal static class RuntimeSourceBuild
{
    #region Fields

    private static readonly JsonSerializerOptions JSON = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly string[] CASES = ["FirstExportInitialization", "AllocationGcAndExceptions", "TlsOnNativeThreads", "RepeatEntry"];

    #endregion

    #region Functions

    /// <summary>
    /// Builds the reference and WitOS-overlay native libraries and checks unresolved port requirements.
    /// </summary>
    /// <param name="root">Repository root.</param>
    public static async Task RunAsync(string root)
    {
        // Refresh the real ILC object, native host and locked-package evidence.
        await RuntimeTargetExperiment.RunAsync(root);
        var pin = RuntimeExperiment.ReadLock(root);
        var output = Path.Combine(root, "artifacts", "runtime-source");
        Directory.CreateDirectory(output);
        var source = await RuntimeSourceCheckout.PrepareAsync(root, pin);
        await RuntimeConfigProbe.PrepareAsync(root, pin);
        await RuntimeUnwindReference.PrepareAsync(root);
        var tree = (await RuntimeSourceCheckout.GitAsync(source, ["rev-parse", "HEAD^{tree}"])).Trim();
        var msvc = await Toolchain.FindMsvcAsync(root);
        Console.WriteLine("Building upstream native libraries from source; Windows reference plus incomplete WitOS archive. No guest managed execution.");
        var reference = await RuntimeSourceBuildProfile.BuildAsync(root, source, output, msvc, "reference", overlay: false);
        var ported = await RuntimeSourceBuildProfile.BuildAsync(root, source, output, msvc, "witos", overlay: true);
        await RuntimeSourceCheckout.RequireCleanAsync(source);

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
            pin.RuntimeVersion,
            pin.RuntimeCommit,
            upstreamTree = tree,
            backend = RuntimePortImage.BACKEND,
            upstreamWorkingTreeClean = true,
            upstreamWorkingTreePatched = false,
            rhConfigAllocationChecksPatched = true,
            startupExitRegistrationChecked = true,
            allocHeapLockCleanupCorrected = true,
            threadStoreTlsDiscoveryAdapted = true,
            nativeRuntimeSourceBuilt = true,
            managedCompilerAndCoreLibFromLockedPackages = true,
            guestRuntimePorted = false,
            guestManagedExecution = false,
            referenceHostPassedCases = CASES,
            referenceHost,
            reference = new { reference.ArchiveSha256, members = reference.Members, compileUnits = reference.Commands.Length, minipal = reference.Minipal },
            ported = new { ported.ArchiveSha256, members = ported.Members, compileUnits = ported.Commands.Length, minipal = ported.Minipal },
            sourceOverlay = new[] { "artifacts/runtime-config/source/gc.witos.cpp", "artifacts/runtime-config/source/gcwks.witos.cpp", "artifacts/runtime-config/source/gchelpers.witos.cpp", "artifacts/runtime-config/source/finalizerhelpers.witos.cpp", "artifacts/runtime-config/source/gcenv.ee.witos.cpp", "src/Kernel/include/witos/handles.h", "src/Runtime.NativeAot/pal_hijack.witos.cpp", "src/Runtime.NativeAot/hijack_evidence.witos.h", "src/Runtime.NativeAot/pal_attach.witos.cpp", "src/System.Native/exception_classification.h", "src/Kernel.Arch.X64/native_exception_x64.cpp", "src/Kernel.Arch.X64/gp_reference_fixture.asm", "tests/Runtime.NativeAot/gp_reference.cpp", "src/Runtime.NativeAot/security_handler.witos.h", "src/Runtime.NativeAot/seh_security.witos.cpp", "tests/Runtime.NativeAot/seh_gs_frame.cpp", "tests/User.X64/runtime_seh.cpp", "src/Runtime.NativeAot/seh_scope.witos.cpp", "src/Runtime.NativeAot/seh_scope.witos.h", "src/Runtime.NativeAot/seh_validation.witos.cpp", "src/Runtime.NativeAot/seh_validation.witos.h", "tests/Runtime.NativeAot/seh_reference.cpp", "tests/User.X64/runtime_failfast.cpp", "src/Kernel/include/witos/fatal_info.h", "src/Runtime.NativeAot/failfast_exception.witos.cpp", "tests/User.X64/runtime_raise.cpp", "src/Kernel/include/witos/exception.h", "src/Kernel.Arch.X64/user_exception.c", "tests/User.X64/runtime_vectored.cpp", "src/Runtime.NativeAot/context_conversion.witos.h", "src/Runtime.NativeAot/native_exception.witos.cpp", "src/Kernel.Arch.X64/native_exception.asm", "src/Kernel/include/witos/exception.h", "src/Kernel.Arch.X64/user_exception.c", "src/Kernel.Arch.X64/entry.asm", "tests/User.X64/runtime_exception.cpp", "src/Kernel.Arch.X64/user_runtime_exception_fixture.asm", "src/Kernel.Arch.X64/user_runtime_unwind_tests.c", "src/Kernel/pe.c", "src/Kernel/include/witos/pe.h", "tests/User.X64/runtime_unwind.cpp", "tests/User.X64/runtime_unwind_protected.cpp", "tests/User.X64/runtime_unwind_entry.cpp", "src/Kernel.Arch.X64/user_runtime_unwind_fixture.asm", "src/Kernel/include/witos/unwind_metadata.h", "src/Runtime.NativeAot/unwind_guest.witos.cpp", "src/Runtime.NativeAot/unwind_checked.witos.cpp", "src/Runtime.NativeAot/unwind_checked.witos.h", "src/Runtime.NativeAot/unwind_environment.witos.h", "src/Runtime.NativeAot/unwind_validation.witos.cpp", "src/Runtime.NativeAot/unwind_validation.witos.h", "src/Kernel.Arch.X64/native_unwind.asm", "artifacts/runtime-unwind/unwinder.checked.cpp", "artifacts/runtime-unwind/unwinder.h", "artifacts/runtime-unwind/baseunwinder.h", "artifacts/runtime-unwind/win64unwind.h", "src/Runtime.NativeAot/unwind_scope.witos.cpp", "src/Runtime.NativeAot/unwind_scope.witos.h", "tests/User.X64/runtime_stack_lease.cpp", "src/Kernel/include/witos/stack_lease.h", "src/Kernel.Arch.X64/user_stack_lease.c", "src/Runtime.NativeAot/pal_context.witos.cpp", "tests/User.X64/runtime_context_set.cpp", "tests/User.X64/runtime_context_set_entry.cpp", "src/Kernel.Arch.X64/user_runtime_context_set_fixture.asm", "src/Kernel.Arch.X64/user_suspend.c", "src/Kernel.Arch.X64/user_suspend_tests.c", "src/Runtime.NativeAot/native_suspend.witos.cpp", "src/Kernel.Arch.X64/native_suspend.asm", "tests/User.X64/runtime_suspend.cpp", "src/Kernel.Arch.X64/user_runtime_suspend_fixture.asm", "tests/Runtime.NativeAot/suspend_reference.cpp", "tools/WitOS.Dev/NativeAot/References/RuntimeSuspendReference.cs", "src/Kernel.Arch.X64/user_thread_context.c", "src/Kernel/include/witos/thread_context.h", "tests/User.X64/runtime_context_capture.cpp", "src/Kernel.Arch.X64/user_runtime_capture_fixture.asm", "src/Runtime.NativeAot/pal_context_storage.witos.cpp", "src/Kernel.Arch.X64/user_cpu_context.c", "src/Kernel/include/witos/cpu_context_info.h", "tests/User.X64/runtime_context_storage.cpp", "src/Kernel.Arch.X64/user_runtime_context_fixture.asm", "artifacts/runtime-config/include/NativeContext.h", "src/Runtime.NativeAot/gc_policy.witos.cpp", "src/Kernel.Arch.X64/gc_policy.asm", "src/Kernel.Arch.X64/exceptions.c", "tools/WitOS.Dev/NativeAot/RuntimeGcPolicy.cs", "tests/User.X64/runtime_gc_policy.cpp", "src/Runtime.NativeAot/native_com.witos.cpp", "src/Runtime.NativeAot/com_counter.witos.h", "src/Kernel.Arch.X64/native_com.asm", "tests/User.X64/runtime_com.cpp", "tests/User.X64/runtime_com_entry.cpp", "src/Kernel.Arch.X64/user_runtime_com_fixture.asm", "tests/Runtime.NativeAot/com_reference.cpp", "tools/WitOS.Dev/NativeAot/References/RuntimeComReference.cs", "src/Runtime.NativeAot/native_diagnostics.witos.cpp", "src/Runtime.NativeAot/native_heap.witos.h", "src/Kernel.Arch.X64/native_diagnostics.asm", "tests/User.X64/runtime_diagnostics.cpp", "src/Kernel.Arch.X64/user_runtime_diagnostics_fixture.asm", "src/Runtime.NativeAot/pal_thread_name.witos.cpp", "src/Kernel.Arch.X64/user_thread_name.c", "src/Kernel/include/witos/thread_name.h", "tests/User.X64/runtime_thread_names.cpp", "src/Runtime.NativeAot/native_module.witos.cpp", "src/Kernel.Arch.X64/native_module.asm", "src/Kernel/include/witos/image_info.h", "src/Kernel.Arch.X64/user.c", "src/System.Native/image.c", "tests/User.X64/runtime_module_names.cpp", "src/Kernel.Arch.X64/user_runtime_module_fixture.asm", "src/Runtime.NativeAot/native_encoding.witos.cpp", "src/Runtime.NativeAot/native_encoding.witos.h", "src/Kernel.Arch.X64/native_encoding.asm", "tests/User.X64/runtime_encoding.cpp", "tests/Runtime.NativeAot/encoding_reference.cpp", "tools/WitOS.Dev/NativeAot/References/RuntimeEncodingReference.cs", "src/Runtime.NativeAot/native_console.witos.cpp", "src/Runtime.NativeAot/native_processor.witos.cpp", "src/Kernel.Arch.X64/native_console.asm", "src/Kernel.Arch.X64/native_processor.asm", "src/Kernel.Arch.X64/user_console.c", "src/Kernel/include/witos/console_info.h", "src/Runtime.NativeAot/native_wait.witos.cpp", "src/Kernel.Arch.X64/native_wait.asm", "src/Kernel.Arch.X64/user_apc.c", "src/Kernel.Arch.X64/user_objects.c", "src/Kernel/include/witos/wait_objects.h", "src/Runtime.NativeAot/native_thread_handles.witos.cpp", "src/Runtime.NativeAot/native_thread_create.witos.cpp", "src/Kernel.Arch.X64/native_thread_create.asm", "src/Kernel.Arch.X64/native_thread_handles.asm", "src/Kernel.Arch.X64/user_reference.c", "src/Kernel/include/witos/thread_reference.h", "src/Runtime.NativeAot/native_services.witos.cpp", "src/Kernel.Arch.X64/native_services.asm", "src/Runtime.NativeAot/native_memory.witos.cpp", "src/Kernel.Arch.X64/native_memory.asm", "tools/WitOS.Dev/NativeAot/References/RuntimeSecurityReference.cs", "tests/Runtime.NativeAot/security_frame.cpp", "tests/Runtime.NativeAot/security_reference.cpp", "src/Runtime.NativeAot/native_random.witos.cpp", "src/Kernel.Arch.X64/native_random.asm", "src/Kernel/random.c", "src/Kernel/include/witos/random.h", "src/Boot.Uefi/entropy.c", "src/Boot.Uefi/uefi.h", "src/Kernel/include/witos/boot.h", "src/Runtime.NativeAot/security_cookie.witos.cpp", "src/Runtime.NativeAot/security_handler.witos.cpp", "src/Kernel.Arch.X64/security_cookie.asm", "src/System.Native/native_security.h", "src/System.Native/diagnostics.h", "src/Runtime.NativeAot/native_format.witos.cpp", "src/Runtime.NativeAot/format_fixed.witos.cpp", "src/Runtime.NativeAot/format_fixed.witos.h", "src/Kernel.Arch.X64/native_format.asm", "tools/WitOS.Dev/NativeAot/References/RuntimeFormattingReference.cs", "tests/Runtime.NativeAot/format_reference.cpp", "src/Runtime.NativeAot/math.lock.json", "src/Runtime.NativeAot/math_bits.witos.h", "src/Runtime.NativeAot/native_math.witos.cpp", "artifacts/runtime-config/source/log.openlibm.c", "tools/WitOS.Dev/NativeAot/NativeMathSources.cs", "src/Runtime.NativeAot/gc_affinity.witos.cpp", "src/Runtime.NativeAot/runtime-overlay.cmake", "src/Runtime.NativeAot/config-probe/CMakeLists.txt", "src/Runtime.NativeAot/gcenv.witos.cpp",
                "src/Runtime.NativeAot/gcenv.witos.h", "src/Runtime.NativeAot/gc_events.witos.cpp", "src/Runtime.NativeAot/gc_time.witos.cpp",
                "src/Runtime.NativeAot/minipal_time.witos.cpp", "src/Runtime.NativeAot/minipal_time.witos.h", "src/Runtime.NativeAot/mutex.witos.cpp", "src/Runtime.NativeAot/crst.witos.cpp", "src/Runtime.NativeAot/native_new.witos.cpp", "src/Runtime.NativeAot/crt_config.witos.cpp", "src/Runtime.NativeAot/crt_memory.witos.c", "src/Runtime.NativeAot/crt_exit.witos.cpp", "src/System.Native/native_process.h", "src/System.Native/library_lifecycle.c", "src/System.Native/library_lifecycle.h", "src/Kernel/include/witos/library.h", "artifacts/runtime-config/source/rhconfig.witos.cpp", "artifacts/runtime-config/source/startup.witos.cpp", "artifacts/runtime-config/source/allocheap.witos.cpp", "artifacts/runtime-config/source/threadstore.witos.cpp", "artifacts/runtime-config/source/thread.witos.cpp", "artifacts/runtime-config/source/debugheader.witos.cpp", "artifacts/runtime-config/include/stressLog.h", "tools/WitOS.Dev/NativeAot/RuntimeStartupSources.cs", "src/Runtime.NativeAot/tls.witos.cpp", "src/Runtime.NativeAot/pal.witos.cpp", "src/Runtime.NativeAot/pal_init.witos.cpp", "src/Runtime.NativeAot/pal_memory.witos.cpp", "src/Runtime.NativeAot/pal_events.witos.cpp", "src/Runtime.NativeAot/pal_threads.witos.cpp", "src/System.Native/thread.c", "src/System.Native/image.c", "src/System.Native/image.h", "src/Runtime.NativeAot/pal_module.witos.cpp", "src/Runtime.NativeAot/pal_environment.witos.cpp", "src/Runtime.NativeAot/pal_environment.witos.h", "src/System.Native/error.h", "src/Runtime.NativeAot/pal_error.witos.cpp", "src/Kernel.Arch.X64/minipal_cpu.witos.cpp", "src/Kernel.Arch.X64/minipal_cpu.witos.h", "src/Kernel.Arch.X64/chkstk.asm", "src/Runtime.NativeAot/fatal.witos.cpp", "src/System.Native/diagnostics.h", "src/Runtime.NativeAot/native_clock.witos.cpp", "src/Kernel.Arch.X64/native_clock.asm", "src/Kernel.Arch.X64/native_error.asm", "src/Kernel.Arch.X64/native_environment.asm", "src/Runtime.NativeAot/pal.witos.h", "src/System.Native/tls.h", "src/System.Native/bootstrap.h", "src/Kernel/include/witos/types.h",
                "src/Kernel/include/witos/user_abi.h", "src/Kernel/include/witos/code_memory.h", "src/Kernel/include/witos/file_io.h", "src/Kernel/include/witos/storage_query.h", "src/Kernel/include/witos/thread_info.h", "src/Kernel/include/witos/image_info.h", "src/Kernel/include/witos/memory_info.h" }
                .Select(p => new { path = p, sha256 = Hash(Path.Combine(root, p)) }),
            referenceInputs = referenceInputs.Select(p => new { file = Path.GetFileName(p), sha256 = Hash(p) }),
            portedInputs = portedInputs.Select(p => new { file = Path.GetFileName(p), sha256 = Hash(p) }),
            boundary,
            missingGroups = groups,
            scope = "Entire upstream nativeaot CMake component built twice. Workstation GC environment, Release Crst and aotminipal mutex sources are replaced in the WitOS archives; remaining Windows PAL/CRT/TLS dependencies and unsupported GC methods are intentionally unresolved. This is not a runnable guest runtime or a complete .NET source build."
        };
        await File.WriteAllTextAsync(Path.Combine(output, "source-build-report.json"), JsonSerializer.Serialize(report, JSON));
        await File.WriteAllTextAsync(Path.Combine(output, "missing-platform.md"),
            "# Source-built NativeAOT port boundary\n\nNo guest runtime executed. Strict link failed as expected.\n\n" +
            string.Join("\n\n", groups.Select(g => $"## {g.Key} ({g.Value.Length})\n\n" + string.Join("\n", g.Value.Select(v => "- `" + v + "`")))) + "\n");
        Console.WriteLine($"[SOURCE-PASS] Full native archive: {ported.Members.Length} members; native adapter objects verified byte-for-byte.");
        Console.WriteLine($"[SOURCE-PASS] aotminipal archive: {ported.Minipal.Members.Length} members; mutex/time adapters and upstream PRNG object verified byte-for-byte.");
        Console.WriteLine($"[SOURCE-PASS] Windows source-built reference: {CASES.Length} execution groups.");
        Console.WriteLine($"[SOURCE-PASS] Strict WitOS link boundary: {boundary.Unresolved.Length} unresolved symbols, including {groups["gc-environment"].Length} GC environment requirements.");
        Console.WriteLine($"Reports: {output}");
        await RuntimeReadiness.RunAsync(root, msvc, ported.Sdk);
    }

    #endregion

    #region Tools

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
                if (!Path.IsPathRooted(value))
                    continue;
                var index = Array.FindIndex(publishedInputs, p => p.Equals(Path.GetFullPath(value), StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    continue;
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
            CASES.Any(c => !run.Output.Contains("[TARGET-PASS] " + c, StringComparison.Ordinal)))
            throw new InvalidOperationException("Source-built Windows runtime reference failed native-host execution.");
        Console.Write(run.Output);
        return new
        {
            imageSha256 = Hash(dll),
            imageBytes = new FileInfo(dll).Length,
            nativeInputsReplaced = replacements,
            imports = NativeImports.Inspect(dll)
        };
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    #endregion
}
