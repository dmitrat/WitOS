using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WitOS.Dev;

internal static class RuntimeConfigProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    public static async Task PrepareAsync(string root, RuntimeExperiment.SourceLock pin)
    {
        var output = Path.Combine(root, "artifacts", "runtime-config", "source");
        Directory.CreateDirectory(output);
        await NativeMathSources.PrepareAsync(root, generate: true);
        await RuntimeStartupSources.PrepareAsync(root, pin);
        using(var contextClient=new HttpClient { Timeout=TimeSpan.FromMinutes(2) }) {
            var header=pin.Sources.Single(s=>s.Path=="src/coreclr/nativeaot/Runtime/windows/NativeContext.h");
            var verified=await RuntimeExperiment.FetchAsync(contextClient,Path.Combine(root,".tools/runtime-audit"),"runtime",pin.RuntimeCommit,header.Path,header.Sha256);
            File.Copy(verified,Path.Combine(root,"artifacts/runtime-config/include/NativeContext.h"),overwrite:true);
        }
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        async Task<string> Read(string path)
        {
            var item = pin.Sources.Single(s => s.Path == path);
            var file = await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools", "runtime-audit"),
                "runtime", pin.RuntimeCommit, path, item.Sha256);
            return (await File.ReadAllTextAsync(file)).Replace("\r\n", "\n");
        }
        static string ReplaceOne(string source, string before, string after)
        {
            var first = source.IndexOf(before, StringComparison.Ordinal);
            if (first < 0 || source.IndexOf(before, first + before.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Pinned runtime correction anchor changed.");
            return source.Replace(before, after, StringComparison.Ordinal);
        }
        static string Slice(string source, string first, string next)
        {
            var start = source.IndexOf(first, StringComparison.Ordinal);
            var end = source.IndexOf(next, StringComparison.Ordinal);
            if (start < 0 || end <= start || source.IndexOf(first, start + first.Length, StringComparison.Ordinal) >= 0 ||
                source.IndexOf(next, end + next.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Pinned GC configuration slice anchors changed.");
            return source[start..end];
        }
        const string allocPath = "src/coreclr/nativeaot/Runtime/allocheap.cpp";
        const string dispatchPath = "src/coreclr/runtime/CachedInterfaceDispatch.cpp";
        const string dispatchAotPath = "src/coreclr/nativeaot/Runtime/CachedInterfaceDispatch_Aot.cpp";
        var alloc = ReplaceOne(await Read(allocPath), "        delete pCur;\n    }\n}",
            "        delete pCur;\n    }\n    m_lock.Destroy();\n}");
        await File.WriteAllTextAsync(Path.Combine(output, "allocheap.witos.cpp"), alloc);
        var dispatch = await Read(dispatchPath);
        if (dispatch.Split("static CrstStatic g_sListLock;", StringSplitOptions.None).Length != 2)
            throw new InvalidDataException("Pinned interface dispatch lock declaration changed.");
        var dispatchPrefix = dispatch[..dispatch.IndexOf("#include", StringComparison.Ordinal)] +
            "#include \"common.h\"\n#include <minipal/mutex.h>\n#include \"CachedInterfaceDispatchPal.h\"\n#include \"CachedInterfaceDispatch.h\"\nstatic CrstStatic g_sListLock;\n";
        await File.WriteAllTextAsync(Path.Combine(output, "dispatch.shared.slice.cpp"), dispatchPrefix +
            Slice(dispatch, "bool InterfaceDispatch_Initialize()", "PCODE InterfaceDispatch_UpdateDispatchCellCache("));
        var dispatchAot = await Read(dispatchAotPath);
        var dispatchEnd = dispatchAot.IndexOf("FCIMPL4(PCODE, RhpUpdateDispatchCellCache", StringComparison.Ordinal);
        if (dispatchEnd < 0) throw new InvalidDataException("Pinned AOT dispatch prefix changed.");
        await File.WriteAllTextAsync(Path.Combine(output, "dispatch.aot.slice.cpp"), dispatchAot[..dispatchEnd]);
        const string startupPath = "src/coreclr/nativeaot/Runtime/startup.cpp";
        var startup = ReplaceOne(await Read(startupPath), "    atexit(&OnProcessExit);",
            "    if (atexit(&OnProcessExit) != 0) return false;");
        startup = ReplaceOne(startup, "    AddVectoredExceptionHandler(1, RhpVectoredExceptionHandler);", "    if (AddVectoredExceptionHandler(1, RhpVectoredExceptionHandler) == NULL) return false;");
        foreach(var condition in new[]{"InterfaceDispatch_Initialize()","RestrictedCallouts::Initialize()","RuntimeInstance::Initialize(hPalInstance)","InitializeGC()","DetectCPUFeatures()","PalInit()","InitDLL(PalGetModuleHandleFromPointer((void*)&RhInitialize))"})
            startup=ReplaceOne(startup,"    if (!"+condition+")\n        return false;",
                "    if (!"+condition+") { PalPrintFatalError(\"WitOS startup failure: "+condition+"\\n\"); return false; }");
        await File.WriteAllTextAsync(Path.Combine(output, "startup.witos.cpp"), startup);
        var gcHelpers=await Read("src/coreclr/nativeaot/Runtime/GCHelpers.cpp");
        foreach(var condition in new[]{"RhInitializeFinalization()","GCHandleUtilities::GetGCHandleManager()->Initialize()","RhWaitForFinalizerThreadStart()"})
            gcHelpers=ReplaceOne(gcHelpers,"    if (!"+condition+")\n        return false;",
                "    if (!"+condition+") { PalPrintFatalError(\"WitOS GC startup failure: "+condition+"\\n\"); return false; }");
        foreach(var call in new[]{"HRESULT hr = GCHeapUtilities::InitializeGC();","hr = g_pGCHeap->Initialize();"})
            gcHelpers=ReplaceOne(gcHelpers,"    "+call+"\n    if (FAILED(hr))\n        return false;",
                "    "+call+"\n    if (FAILED(hr)) { PalPrintFatalError(\"WitOS GC startup failure: "+call+"\\n\"); return false; }");
        await File.WriteAllTextAsync(Path.Combine(output,"gchelpers.witos.cpp"),gcHelpers);
        var finalizers=await Read("src/coreclr/nativeaot/Runtime/FinalizerHelpers.cpp");
        foreach(var condition in new[]{"g_FinalizerEvent.CreateAutoEventNoThrow(false)","g_FinalizerDoneEvent.CreateManualEventNoThrow(false)","PalStartFinalizerThread(FinalizerStart, (void*)g_FinalizerEvent.GetOSEvent())"})
            finalizers=ReplaceOne(finalizers,"    if (!"+condition+")\n        return false;",
                "    if (!"+condition+") { PalPrintFatalError(\"WitOS finalizer startup failure: "+condition+"\\n\"); return false; }");
        finalizers=ReplaceOne(finalizers,"    if (!g_ComAndFlsInitSucceeded)\n        return 0;",
            "    if (!g_ComAndFlsInitSucceeded) { PalPrintFatalError(\"WitOS finalizer PAL init failed\\n\"); return 0; }");
        await File.WriteAllTextAsync(Path.Combine(output,"finalizerhelpers.witos.cpp"),finalizers);
        var collector=await Read("src/coreclr/gc/gc.cpp");
        collector=ReplaceOne(collector,"        if ((element != total_bookkeeping_elements) && (sizes[element] != 0))",
            "        // Preserve a page boundary for the commit clamp even when the mark array is empty.\n"+
            "        if ((element == total_bookkeeping_elements)\n#ifdef BACKGROUND_GC\n            || (element == mark_array_element)\n#endif\n            || (sizes[element] != 0))");
        await File.WriteAllTextAsync(Path.Combine(output,"gc.witos.cpp"),collector);
        var workstation=ReplaceOne(await Read("src/coreclr/gc/gcwks.cpp"),"#include \"gc.cpp\"","#include \"gc.witos.cpp\"");
        await File.WriteAllTextAsync(Path.Combine(output,"gcwks.witos.cpp"),workstation);
        const string rhPath = "src/coreclr/nativeaot/Runtime/RhConfig.cpp";
        const string gcPath = "src/coreclr/gc/gcconfig.cpp";
        const string eePath = "src/coreclr/nativeaot/Runtime/gcenv.ee.cpp";
        var rh = await Read(rhPath);
        rh = ReplaceOne(rh, "        *value = PalCopyTCharAsChar(buffer);\n        return true;",
            "        char* converted = PalCopyTCharAsChar(buffer);\n        if (!converted) return false;\n        *value = converted;\n        return true;");
        rh = ReplaceOne(rh, "    NewArrayHolder<TCHAR> newBuffer {new (nothrow) TCHAR[bufferLen]};",
            "    NewArrayHolder<TCHAR> newBuffer {new (nothrow) TCHAR[bufferLen]};\n    if (newBuffer == nullptr) return false;");
        rh = ReplaceOne(rh, "    if (actualLen >= bufferLen)", "    if (actualLen == 0 || actualLen >= bufferLen)");
        rh = ReplaceOne(rh, "    *value = PalCopyTCharAsChar(newBuffer);",
            "    char* converted = PalCopyTCharAsChar(newBuffer);\n    if (!converted) return false;\n    *value = converted;");
        await File.WriteAllTextAsync(Path.Combine(output, "rhconfig.witos.cpp"), rh);
        var gc = await Read(gcPath);
        var cut = gc.IndexOf("// Parse an integer index or range", StringComparison.Ordinal);
        if (cut < 0 || !gc[..cut].Contains("void GCConfig::Initialize()", StringComparison.Ordinal))
            throw new InvalidDataException("GCConfig prefix boundary changed.");
        await File.WriteAllTextAsync(Path.Combine(output, "gcconfig.slice.cpp"), gc[..cut]);
        // Actual unchanged parser bodies; full runtime already compiles gcconfig.cpp.
        var headersEnd = gc.IndexOf("#define BOOL_CONFIG", StringComparison.Ordinal);
        if (headersEnd < 0 || !gc[cut..].Contains("bool ParseGCHeapAffinitizeRanges(", StringComparison.Ordinal))
            throw new InvalidDataException("GC affinity parser boundaries changed.");
        await File.WriteAllTextAsync(Path.Combine(output, "gcaffinity.slice.cpp"), gc[..headersEnd] + gc[cut..]);
        var ee = await Read(eePath);
        var selected = ee[..ee.IndexOf("#include", StringComparison.Ordinal)] +
            "#include \"common.h\"\n#include \"gcenv.h\"\n#include \"gcenv.ee.h\"\n#include \"RhConfig.h\"\n\n" +
            Slice(ee, "bool GCToEEInterface::GetBooleanConfigValue(", "void GCToEEInterface::LogErrorToHost(") +
            Slice(ee, "bool GCToEEInterface::GetStringConfigValue(", "void GCToEEInterface::TriggerClientBridgeProcessing(");
        await File.WriteAllTextAsync(Path.Combine(output, "gcenv.config.slice.cpp"), selected);
        var diagnosticEe=ReplaceOne(ee,"void GCToEEInterface::LogErrorToHost(const char *message)\n{\n}",
            "void GCToEEInterface::LogErrorToHost(const char *message)\n{\n    PalPrintFatalError(message);\n    PalPrintFatalError(\"\\n\");\n}");
        await File.WriteAllTextAsync(Path.Combine(output,"gcenv.ee.witos.cpp"),diagnosticEe);
        await File.WriteAllTextAsync(Path.Combine(output, "provenance.json"), JsonSerializer.Serialize(new
        {
            pin.RuntimeCommit,
            scope = "Full pinned GC preserves page alignment of the empty mark-array boundary and total bookkeeping extent; GCHelpers/FinalizerHelpers add failure-only diagnostics and GCToEE LogErrorToHost uses bounded console output. Whole RhConfig with explicit allocation-failure checks; unchanged GCConfig prefix and affinity parser bodies and four unchanged GCToEE configuration methods. Startup checks atexit and vectored-handler registration failure; whole AllocHeap destroys its Crst; selected dispatch initialization/allocation method bodies remain unchanged; full dispatch dependencies retained outside this probe. No collector/lifecycle substitutes.",
            inputs = pin.Sources.Where(s => s.Path == rhPath || s.Path == gcPath || s.Path == eePath || s.Path == startupPath || s.Path == allocPath || s.Path == dispatchPath || s.Path == dispatchAotPath || s.Path == "src/coreclr/nativeaot/Runtime/allocheap.h" || s.Path == "src/coreclr/nativeaot/Runtime/GCHelpers.cpp" || s.Path == "src/coreclr/nativeaot/Runtime/FinalizerHelpers.cpp" || s.Path == "src/coreclr/gc/gc.cpp" || s.Path == "src/coreclr/gc/gcwks.cpp"),
            generated = new[] { "gc.witos.cpp", "gcwks.witos.cpp", "gcenv.ee.witos.cpp", "gchelpers.witos.cpp", "finalizerhelpers.witos.cpp", "allocheap.witos.cpp", "dispatch.shared.slice.cpp", "dispatch.aot.slice.cpp", "startup.witos.cpp", "rhconfig.witos.cpp", "gcconfig.slice.cpp", "gcaffinity.slice.cpp", "gcenv.config.slice.cpp" }
                .Select(p => new { file = p, sha256 = Hash(Path.Combine(output, p)) })
        }, Json));
    }

    public static async Task VerifyArchiveAsync(string root, string obj, string msvc, string minipalArchive, string memoryObject, string stackObject, string clockObject, string clockBinding, string fatalObject, string affinityObject, string mathObject, string logObject, NativePlatformObjects securityObjects)
    {
        var output = Path.Combine(root, "artifacts", "runtime-config");
        var archive = Path.Combine(obj, "witos-config", "WitOS.ConfigProbe.lib");
        var listing = await Processes.RunAsync(Path.Combine(msvc, "lib.exe"), ["/nologo", "/list", archive], root);
        if (listing.ExitCode != 0 || listing.TimedOut) throw new InvalidDataException("Configuration archive listing failed.");
        var members = listing.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var all = JsonSerializer.Deserialize<JsonElement[]>(await File.ReadAllTextAsync(Path.Combine(obj, "compile_commands.json")), Json)!;
        var commands = all.Where(c => c.GetProperty("output").GetString()!.Replace('\\', '/').Contains("/WitOS.ConfigProbe.dir/", StringComparison.Ordinal)).ToArray();
        string[] names = ["rhconfig.witos.cpp", "gcconfig.slice.cpp", "gcaffinity.slice.cpp", "gcenv.config.slice.cpp", "runtime_config.cpp", "pal_init.witos.cpp", "allocheap.witos.cpp", "dispatch.shared.slice.cpp", "dispatch.aot.slice.cpp", "runtime_allocator.cpp", "startup.objects.slice.cpp", "runtime_instance.cpp", "runtime_barrier.cpp", "runtime_time.cpp", "runtime_crt.cpp", "runtime_stack.cpp", "runtime_cpu.cpp", "runtime_clock.cpp", "runtime_fatal.cpp", "runtime_affinity.cpp", "runtime_math.cpp", "native_format.witos.cpp", "format_fixed.witos.cpp", "runtime_format.cpp", "runtime_security.cpp", "runtime_random.cpp", "runtime_memory.cpp", "runtime_services.cpp", "runtime_thread_references.cpp", "runtime_object_wait.cpp", "runtime_console.cpp", "runtime_encoding.cpp", "runtime_module_names.cpp", "runtime_thread_names.cpp", "runtime_diagnostics.cpp", "runtime_com.cpp", "runtime_gc_policy.cpp", "runtime_context_storage.cpp", "runtime_context_capture.cpp", "runtime_suspend.cpp", "runtime_context_set.cpp", "runtime_stack_lease.cpp", "runtime_unwind.cpp", "runtime_exception.cpp", "runtime_vectored.cpp", "runtime_raise.cpp", "runtime_failfast.cpp", "runtime_seh.cpp", "unwind_scope.witos.cpp", "pal_context.witos.cpp", "pal_context_storage.witos.cpp"];
        if (members.Length != names.Length || commands.Length != names.Length)
            throw new InvalidDataException("Configuration probe must contain exactly the selected source objects.");
        foreach (var name in names)
        {
            var command = commands.Single(c => Path.GetFileName(c.GetProperty("file").GetString()!) == name);
            var compile = command.GetProperty("command").GetString()!;
            if (!compile.Contains("-DNO_STRESS_LOG", StringComparison.Ordinal) && !compile.Contains("/DNO_STRESS_LOG", StringComparison.Ordinal))
                throw new InvalidDataException("Configuration probe must match the WitOS NO_STRESS_LOG profile.");
            NativeObject.VerifyArchive(archive, Path.GetFullPath(command.GetProperty("output").GetString()!, command.GetProperty("directory").GetString()!));
        }
        if(!commands.Single(c=>Path.GetFileName(c.GetProperty("file").GetString()!)=="unwind_scope.witos.cpp").GetProperty("command").GetString()!.Contains("/GS-",StringComparison.Ordinal))
            throw new InvalidDataException("Unwind scope probe must remain explicitly distinct from the protected production object until guest handler execution.");
        var contextStorageCommand=commands.Single(c=>Path.GetFileName(c.GetProperty("file").GetString()!)=="pal_context_storage.witos.cpp");
        if(!contextStorageCommand.GetProperty("command").GetString()!.Contains("/GS-",StringComparison.Ordinal))
            throw new InvalidDataException("Context storage probe profile must be explicitly distinct from protected production code.");
        if(!commands.Single(c=>Path.GetFileName(c.GetProperty("file").GetString()!)=="pal_context.witos.cpp").GetProperty("command").GetString()!.Contains("/GS-",StringComparison.Ordinal))throw new InvalidDataException("PAL context probe profile missing.");
        var stackCommand = commands.Single(c => Path.GetFileName(c.GetProperty("file").GetString()!) == "runtime_stack.cpp");
        if (!stackCommand.GetProperty("command").GetString()!.Contains("/Gs4096", StringComparison.Ordinal))
            throw new InvalidDataException("Compiler stack-probe threshold missing.");
        var stackSymbols = await Processes.RunAsync(Path.Combine(msvc, "dumpbin.exe"),
            ["/symbols", Path.GetFullPath(stackCommand.GetProperty("output").GetString()!, stackCommand.GetProperty("directory").GetString()!)], root);
        if (stackSymbols.ExitCode != 0 || stackSymbols.TimedOut || !System.Text.RegularExpressions.Regex.IsMatch(stackSymbols.Output, @"UNDEF[^\r\n]*\b__chkstk\b"))
            throw new InvalidDataException("Compiler did not emit the real __chkstk dependency.");
        File.Copy(stackObject, Path.Combine(output, "chkstk.obj"), overwrite: true);
        var platformManifest = securityObjects.CopyTo(output);
        File.Copy(mathObject, Path.Combine(output, "native_math.witos.obj"), overwrite: true);
        File.Copy(logObject, Path.Combine(output, "log.openlibm.obj"), overwrite: true);
        File.Copy(affinityObject, Path.Combine(output, "gc_affinity.witos.obj"), overwrite: true);
        File.Copy(fatalObject, Path.Combine(output, "fatal.witos.obj"), overwrite: true);
        File.Copy(clockObject, Path.Combine(output, "native_clock.witos.obj"), overwrite: true);
        File.Copy(clockBinding, Path.Combine(output, "native_clock.obj"), overwrite: true);
        File.Copy(archive, Path.Combine(output, "WitOS.ConfigProbe.lib"), overwrite: true);
        File.Copy(minipalArchive, Path.Combine(output, "WitOS.Minipal.lib"), overwrite: true);
        File.Copy(memoryObject, Path.Combine(output, "crt_memory.witos.obj"), overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(output, "archive-report.json"), JsonSerializer.Serialize(new
        {
            platformObjects = platformManifest, archiveSha256 = Hash(archive), minipalSha256 = Hash(minipalArchive), memorySha256 = Hash(memoryObject), stackSha256 = Hash(stackObject), clockSha256 = Hash(clockObject), clockBindingSha256 = Hash(clockBinding), fatalSha256 = Hash(fatalObject), affinitySha256 = Hash(affinityObject), mathSha256 = Hash(mathObject), logSha256 = Hash(logObject), members, commands, guestManagedRuntime = false,
            localInputs = new[] { "src/Kernel/include/witos/handles.h", "tests/User.X64/runtime_seh.cpp", "tests/User.X64/runtime_failfast.cpp", "src/Kernel/include/witos/fatal_info.h", "src/Runtime.NativeAot/failfast_exception.witos.cpp", "tests/User.X64/runtime_raise.cpp", "src/Kernel/include/witos/exception.h", "src/Kernel.Arch.X64/user_exception.c", "tests/User.X64/runtime_vectored.cpp", "src/Runtime.NativeAot/context_conversion.witos.h", "src/Runtime.NativeAot/native_exception.witos.cpp", "src/Kernel.Arch.X64/native_exception.asm", "src/Kernel/include/witos/exception.h", "src/Kernel.Arch.X64/user_exception.c", "src/Kernel.Arch.X64/entry.asm", "tests/User.X64/runtime_exception.cpp", "src/Kernel.Arch.X64/user_runtime_exception_fixture.asm", "tests/User.X64/runtime_unwind.cpp", "tests/User.X64/runtime_unwind_protected.cpp", "tests/User.X64/runtime_unwind_entry.cpp", "src/Kernel.Arch.X64/user_runtime_unwind_fixture.asm", "src/Kernel/include/witos/unwind_metadata.h", "src/Runtime.NativeAot/unwind_scope.witos.cpp", "src/Runtime.NativeAot/unwind_scope.witos.h", "tests/User.X64/runtime_stack_lease.cpp", "src/Kernel/include/witos/stack_lease.h", "src/Kernel.Arch.X64/user_stack_lease.c", "tests/User.X64/runtime_console.cpp", "src/Runtime.NativeAot/native_console.witos.cpp", "src/Runtime.NativeAot/native_processor.witos.cpp", "src/Kernel.Arch.X64/native_console.asm", "src/Kernel.Arch.X64/native_processor.asm", "src/Kernel.Arch.X64/user_runtime_console_fixture.asm", "src/Kernel/include/witos/console_info.h", "tests/User.X64/runtime_object_wait.cpp", "src/Runtime.NativeAot/native_wait.witos.cpp", "src/Kernel.Arch.X64/native_wait.asm", "src/Kernel/include/witos/wait_objects.h", "tests/User.X64/runtime_thread_references.cpp", "tests/User.X64/runtime_thread_entry.cpp", "src/Runtime.NativeAot/native_thread_handles.witos.cpp", "src/Runtime.NativeAot/native_thread_create.witos.cpp", "src/Kernel.Arch.X64/native_thread_create.asm", "src/Kernel.Arch.X64/native_thread_handles.asm", "src/Kernel/include/witos/thread_reference.h", "src/Kernel.Arch.X64/user_runtime_reference_fixture.asm", "tests/User.X64/runtime_services.cpp", "src/Runtime.NativeAot/native_services.witos.cpp", "src/Kernel.Arch.X64/native_services.asm", "src/Kernel.Arch.X64/user_runtime_services_fixture.asm", "tests/User.X64/runtime_memory.cpp", "src/Runtime.NativeAot/native_memory.witos.cpp", "src/Kernel.Arch.X64/native_memory.asm", "src/Kernel.Arch.X64/user_runtime_memory_fixture.asm", "tests/User.X64/runtime_random.cpp", "src/Runtime.NativeAot/native_random.witos.cpp", "src/Kernel.Arch.X64/native_random.asm", "src/Kernel.Arch.X64/user_runtime_random_fixture.asm", "tests/User.X64/runtime_security.cpp", "src/Kernel.Arch.X64/user_runtime_security_fixture.asm", "src/Runtime.NativeAot/security_cookie.witos.cpp", "src/Runtime.NativeAot/security_handler.witos.cpp", "src/Kernel.Arch.X64/security_cookie.asm", "src/System.Native/native_security.h", "tests/User.X64/runtime_format.cpp", "src/Runtime.NativeAot/native_format.witos.cpp", "src/Runtime.NativeAot/format_fixed.witos.cpp", "src/Runtime.NativeAot/format_fixed.witos.h", "src/Kernel.Arch.X64/native_format.asm", "src/Kernel.Arch.X64/user_runtime_math_fixture.asm", "tests/User.X64/runtime_math.cpp", "tests/User.X64/math_log_vectors.h", "src/Runtime.NativeAot/native_math.witos.cpp", "src/Runtime.NativeAot/math_bits.witos.h", "src/Runtime.NativeAot/math.lock.json", "artifacts/runtime-config/source/log.openlibm.c", "tests/User.X64/runtime_affinity.cpp", "src/Runtime.NativeAot/gc_affinity.witos.cpp", "artifacts/runtime-config/source/gcaffinity.slice.cpp", "tests/User.X64/runtime_fatal.cpp", "src/Runtime.NativeAot/fatal.witos.cpp", "src/System.Native/diagnostics.h", "tests/User.X64/runtime_clock.cpp", "src/Runtime.NativeAot/native_clock.witos.cpp", "src/Kernel.Arch.X64/native_clock.asm", "src/Kernel.Arch.X64/user_runtime_clock_fixture.asm", "tests/User.X64/runtime_cpu.cpp", "src/Kernel.Arch.X64/user_runtime_cpu_fixture.asm", "src/Kernel.Arch.X64/minipal_cpu.witos.cpp", "src/Kernel.Arch.X64/minipal_cpu.witos.h", "tests/User.X64/runtime_stack.cpp", "src/Kernel.Arch.X64/user_runtime_stack_fixture.asm", "src/Kernel.Arch.X64/chkstk.asm", "tests/User.X64/runtime_crt.cpp", "src/Runtime.NativeAot/crt_memory.witos.c", "tests/User.X64/runtime_time.cpp", "src/Runtime.NativeAot/minipal_time.witos.cpp", "src/Runtime.NativeAot/minipal_time.witos.h", "tests/User.X64/runtime_config.cpp", "tests/User.X64/runtime_instance.cpp", "tests/User.X64/runtime_barrier.cpp", "artifacts/runtime-config/source/startup.objects.slice.cpp", "artifacts/runtime-config/source/threadstore.witos.cpp", "artifacts/runtime-config/source/thread.witos.cpp", "artifacts/runtime-config/include/stressLog.h", "tests/User.X64/runtime_allocator.cpp", "tests/User.X64/protocol.h", "artifacts/runtime-config/source/allocheap.witos.cpp", "artifacts/runtime-config/source/dispatch.shared.slice.cpp", "artifacts/runtime-config/source/dispatch.aot.slice.cpp",
                "src/Runtime.NativeAot/runtime-overlay.cmake", "src/Runtime.NativeAot/config-probe/CMakeLists.txt",
                "src/Runtime.NativeAot/crt_config.witos.cpp", "src/Runtime.NativeAot/pal_init.witos.cpp", "src/Runtime.NativeAot/pal_environment.witos.h",
                "src/System.Native/tls.h", "src/Kernel/include/witos/user_abi.h",
                "artifacts/runtime-config/source/rhconfig.witos.cpp", "artifacts/runtime-config/source/gcconfig.slice.cpp",
                "artifacts/runtime-config/source/gcenv.config.slice.cpp" }
                .Select(p => new { path = p, sha256 = Hash(Path.Combine(root, p)) })
        }, Json));
        Console.WriteLine($"[SOURCE-PASS] Configuration probe: {names.Length} exact source objects; collector and thread attachment excluded.");
    }

    public static async Task BuildImageAsync(string root, string output, string msvc)
    {
        // The runtime-config command refreshes source/objects first. Ordinary
        // boot builds never silently use a cached upstream configuration object.
        var archive = Path.Combine(root, "artifacts", "runtime-config", "WitOS.ConfigProbe.lib");
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts", "runtime-config", "archive-report.json")));
        if (Hash(archive) != report.RootElement.GetProperty("archiveSha256").GetString())
            throw new InvalidDataException("Configuration probe archive differs from its verified build.");
        foreach (var input in report.RootElement.GetProperty("localInputs").EnumerateArray())
            if (Hash(Path.Combine(root, input.GetProperty("path").GetString()!)) != input.GetProperty("sha256").GetString())
                throw new InvalidDataException("Configuration source inputs changed after archive verification; rerun runtime-config.");
        var securityObjects = NativePlatformObjects.Read(Path.Combine(root,"artifacts/runtime-config"), report.RootElement.GetProperty("platformObjects"));
        var recordBindings = securityObjects.Record;
        var minipalArchive = Path.Combine(root, "artifacts", "runtime-config", "WitOS.Minipal.lib");
        if (Hash(minipalArchive) != report.RootElement.GetProperty("minipalSha256").GetString())
            throw new InvalidDataException("Minipal archive changed after source verification.");
        var memoryObject = Path.Combine(root, "artifacts", "runtime-config", "crt_memory.witos.obj");
        if (Hash(memoryObject) != report.RootElement.GetProperty("memorySha256").GetString())
            throw new InvalidDataException("Native memory object changed after archive verification.");
        var stackObject = Path.Combine(root, "artifacts", "runtime-config", "chkstk.obj");
        if (Hash(stackObject) != report.RootElement.GetProperty("stackSha256").GetString())
            throw new InvalidDataException("Stack probe object changed after archive verification.");
        var stackTest = Path.Combine(output, "runtime_stack_test.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
            ["/nologo", "/c", "/Fo" + stackTest, Path.Combine(root, "src", "Kernel.Arch.X64", "user_runtime_stack_fixture.asm")], root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var sdkVersion = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Include", sdkVersion);
        var crt = Path.Combine(output, "crt_config.witos.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
            ["/nologo", "/c", "/TP", "/std:c++17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/O1", "/GR-", "/DNDEBUG",
                $"/I{Path.Combine(vc, "include")}", $"/I{Path.Combine(sdk, "ucrt")}", $"/Fo{crt}",
                Path.Combine(root, "src", "Runtime.NativeAot", "crt_config.witos.cpp")], root);
        string[] shared = ["native_start.obj", "environment_pal_environment.witos.obj", "native_new.witos.obj",
            "native_error.obj", "native_environment.obj", "dynamic_image.obj", "dynamic_thread.obj",
            "dynamic_tls.witos.obj", "dynamic_tls_metadata.obj", "gcenv.witos.obj", "pal_pal.witos.obj", "pal_pal_error.witos.obj", "pal_pal_memory.witos.obj", "module_pal_module.witos.obj", "crst.witos.obj", "mutex.witos.obj"];
        var image = Path.Combine(output, "RuntimeConfigFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64", "/fixed:no", "/dynamicbase",
                "/incremental:no", "/Brepro", "/opt:ref", "/include:_tls_used", "/merge:.CRT=.rdata", "/base:0x180000000",
                $"/out:{image}", $"/map:{Path.Combine(output, "RuntimeConfigFixture.map")}", archive, minipalArchive, memoryObject, stackObject, stackTest, crt, .. recordBindings, .. shared.Select(p => Path.Combine(output, p))], root);
        var bytes = await File.ReadAllBytesAsync(image);
        using var reader = new PEReader(new MemoryStream(bytes, writable: false));
        var h = reader.PEHeaders.PEHeader ?? throw new InvalidDataException("Configuration PE missing.");
        if (h.ImportTableDirectory.Size != 0 || h.DelayImportTableDirectory.Size != 0 || h.LoadConfigTableDirectory.Size != 0 ||
            reader.PEHeaders.CorHeader is not null || h.ThreadLocalStorageTableDirectory.Size != 40 || h.BaseRelocationTableDirectory.Size == 0)
            throw new InvalidDataException("Configuration image violates the import-free static TLS profile.");
        var text = new StringBuilder("/* Real upstream native configuration probe; generated. */\nstatic const unsigned char wit_runtime_config_image[] = {\n");
        for (var i = 0; i < bytes.Length; i += 16)
            text.AppendLine("    " + string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        text.AppendLine("};");
        var raw = (byte[])bytes.Clone();
        // Controlled fixture: retain identical code/sections, omit only the
        // PE32+ TLS directory so the kernel supplies no compiler GS page.
        var tlsDirectory = checked(BitConverter.ToInt32(raw, 0x3c) + 24 + 112 + 9 * 8);
        raw.AsSpan(tlsDirectory, 8).Clear();
        using (var plain = new PEReader(new MemoryStream(raw, writable: false)))
            if (plain.PEHeaders.PEHeader!.ThreadLocalStorageTableDirectory.Size != 0 ||
                plain.PEHeaders.PEHeader.ThreadLocalStorageTableDirectory.RelativeVirtualAddress != 0)
                throw new InvalidDataException("Raw configuration fixture retained a compiler TLS directory.");
        await File.WriteAllBytesAsync(Path.Combine(output, "RuntimeConfigRawFixture.pe"), raw);
        text.AppendLine("static const unsigned char wit_runtime_config_raw_image[] = {");
        for (var i = 0; i < raw.Length; i += 16)
            text.AppendLine("    " + string.Join(", ", raw.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        text.AppendLine("};");
        var map = await File.ReadAllTextAsync(Path.Combine(output, "RuntimeConfigFixture.map"));
        foreach (var symbol in new[] { "__chkstk", "wit_chkstk_end" }) {
            var match = System.Text.RegularExpressions.Regex.Match(map, @"\b" + symbol + @"\s+([0-9a-fA-F]{16})\b");
            if (!match.Success) throw new InvalidDataException("Stack probe marker missing from map: " + symbol);
            var rva = Convert.ToUInt64(match.Groups[1].Value, 16) - h.ImageBase;
            text.AppendLine($"#define WIT_STACK_PROBE_{(symbol == "__chkstk" ? "BEGIN" : "END")} 0x{rva:X}U");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "runtime_config_image.h"), text.ToString(), Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "runtime-config", "image-report.json"), JsonSerializer.Serialize(new
        {
            guestManagedRuntime = false, imageBytes = bytes.Length, imageSha256 = Hash(image), unwindEntries = h.ExceptionTableDirectory.Size / 12,
            recordBindings=recordBindings.Select(p=>new{file=p,sha256=Hash(p)}), rawImageSha256 = Hash(Path.Combine(output, "RuntimeConfigRawFixture.pe")), archiveSha256 = Hash(archive), crtSha256 = Hash(crt), sharedObjects = shared.Select(p => new { file = p, sha256 = Hash(Path.Combine(output, p)) })
        }, Json));
        var clockObject = Path.Combine(root, "artifacts", "runtime-config", "native_clock.witos.obj");
        var clockBinding = Path.Combine(root, "artifacts", "runtime-config", "native_clock.obj");
        if (Hash(clockObject) != report.RootElement.GetProperty("clockSha256").GetString() ||
            Hash(clockBinding) != report.RootElement.GetProperty("clockBindingSha256").GetString())
            throw new InvalidDataException("Clock objects changed after archive verification.");
        var fatalObject = Path.Combine(root, "artifacts", "runtime-config", "fatal.witos.obj");
        if (Hash(fatalObject) != report.RootElement.GetProperty("fatalSha256").GetString())
            throw new InvalidDataException("Fatal diagnostics objects differ from the source archive.");
        var affinityObject = Path.Combine(root, "artifacts", "runtime-config", "gc_affinity.witos.obj");
        if (Hash(affinityObject) != report.RootElement.GetProperty("affinitySha256").GetString())
            throw new InvalidDataException("GC affinity adapter differs from the source archive.");
        var mathObject = Path.Combine(root, "artifacts", "runtime-config", "native_math.witos.obj");
        var logObject = Path.Combine(root, "artifacts", "runtime-config", "log.openlibm.obj");
        if (Hash(mathObject) != report.RootElement.GetProperty("mathSha256").GetString() || Hash(logObject) != report.RootElement.GetProperty("logSha256").GetString())
            throw new InvalidDataException("Math objects differ from the source archive.");
        await RuntimeCpuImage.BuildAsync(root, output, msvc, archive, minipalArchive, memoryObject, crt, clockObject, clockBinding, fatalObject, affinityObject, mathObject, logObject, securityObjects);
        Console.WriteLine($"RuntimeConfigFixture: {bytes.Length} bytes, real upstream configuration methods, no OS/CRT imports.");
    }
}
