using System.Security.Cryptography;
using System.Text.Json;

namespace WitOS.Dev.NativeAot;

internal static class RuntimeStartupSources
{
    #region Functions

    public static async Task PrepareAsync(string root, UpstreamSourceLock pin)
    {
        var output = Path.Combine(root, "artifacts", "runtime-config", "source");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        async Task<string> Read(string name)
        {
            var source = pin.Sources.Single(s => s.Path == name);
            var path = await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools", "runtime-audit"),
                "runtime", pin.RuntimeCommit, name, source.Sha256);
            return (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n");
        }
        static string Cut(string source, string first, string next)
        {
            var start = source.IndexOf(first, StringComparison.Ordinal);
            var end = source.IndexOf(next, start < 0 ? 0 : start, StringComparison.Ordinal);
            if (start < 0 || end <= start || source.IndexOf(first, start + first.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Pinned runtime startup slice changed: " + first);
            return source[start..end];
        }
        static string Replace(string source, string before, string after)
        {
            var first = source.IndexOf(before, StringComparison.Ordinal);
            if (first < 0 || source.IndexOf(before, first + before.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Pinned runtime startup correction changed: " + before.Split('\n')[0]);
            return source.Replace(before, after, StringComparison.Ordinal);
        }
        const string prefix = "src/coreclr/nativeaot/Runtime/";
        string[] names = ["RuntimeInstance.cpp", "RuntimeInstance.h", "threadstore.cpp", "threadstore.h", "threadstore.inl",
            "thread.h", "thread.cpp", "thread.inl", "inc/stressLog.h", "DebugHeader.cpp", "RestrictedCallouts.cpp", "RestrictedCallouts.h", "clrgc.disabled.cpp"];
        var ri = await Read(prefix + names[0]);
        var ts = await Read(prefix + "threadstore.cpp");
        const string oldBody = """
    p_tls_index = &_tls_index;

    uint8_t * pTls = *(uint8_t **)(PalNtCurrentTeb() + OFFSETOF__TEB__ThreadLocalStoragePointer);

    uint8_t * pOurTls = *(uint8_t **)(pTls + (_tls_index * sizeof(void*)));

    SECTIONREL__tls_CurrentThread = (uint32_t)((uint8_t *)&tls_CurrentThread - pOurTls);
""";
        const string newBody = """
    static_assert(sizeof(tls_CurrentThread) <= 4096 - WIT_COMPILER_TLS_DATA_OFFSET);
    WitUserThreadInfo info;
    WitU64 copied = 0;
    if (wit_native_call(WIT_CALL_THREAD_QUERY, (uintptr_t)&info, sizeof(info), WIT_THREAD_INFO_VERSION, &copied) != WIT_STATUS_OK ||
        copied != sizeof(info) || info.Version != WIT_THREAD_INFO_VERSION || info.Size != sizeof(info) ||
        !info.CompilerTls || (info.CompilerTls & 4095) || info.CompilerTls > UINTPTR_MAX - 4096 || _tls_index != 0)
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    const uintptr_t first = info.CompilerTls + WIT_COMPILER_TLS_DATA_OFFSET;
    const uintptr_t address = (uintptr_t)&tls_CurrentThread;
    if (address < first || address - first > 4096 - WIT_COMPILER_TLS_DATA_OFFSET - sizeof(tls_CurrentThread))
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    p_tls_index = &_tls_index;
    SECTIONREL__tls_CurrentThread = (uint32_t)(address - first);
""";
        ts = Replace(ts, oldBody, newBody);
        ts = Replace(ts, "#include \"common.h\"", "#include \"common.h\"\nextern \"C\" {\n#include \"bootstrap.h\"\n}");
        await File.WriteAllTextAsync(Path.Combine(output, "threadstore.witos.cpp"), ts);
        var thread = await Read(prefix + "thread.cpp");
        const string duplicateHandle = """
    HANDLE curProcessPseudo = GetCurrentProcess();
    HANDLE curThreadPseudo  = GetCurrentThread();

    // This can fail!  Users of m_hOSThread must be able to handle INVALID_HANDLE_VALUE!!
    DuplicateHandle(curProcessPseudo, curThreadPseudo, curProcessPseudo, &m_hOSThread,
                       0,      // ignored
                       FALSE,  // inherit
                       DUPLICATE_SAME_ACCESS);
""";
        // Preserve the real upstream DuplicateHandle call. WitOS now supplies
        // generation-bearing context capabilities and atomic output-on-success.
        // Assert the pinned body still matches; its INVALID_HANDLE_VALUE fallback
        // remains untouched when duplication fails.
        thread = Replace(thread, duplicateHandle, duplicateHandle);
        thread = Replace(thread, "#include \"common.h\"", "#include \"common.h\"\nextern \"C\" {\n#include \"bootstrap.h\"\n}");
        thread = Replace(thread, "    if (!PalGetMaximumStackBounds(&m_pStackLow, &m_pStackHigh))\n        RhFailFast();",
            "    if (!PalGetMaximumStackBounds(&m_pStackLow, &m_pStackHigh))\n        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);");
        thread = Replace(thread, "#include \"common.h\"", "#include \"common.h\"\n#include \"unwind_scope.witos.h\"");
        foreach (var signature in new[]{
            "void Thread::HijackReturnAddress(PAL_LIMITED_CONTEXT* pSuspendCtx, HijackFunc* pfnHijackFunction)",
            "void Thread::HijackReturnAddress(NATIVE_CONTEXT* pSuspendCtx, HijackFunc* pfnHijackFunction)"})
            thread = Replace(thread, signature + "\n{", signature + "\n{\n" +
                "    // Retain original stack locations through the entire return-address write.\n" +
                "    WitNativeUnwindScope walk(this == ThreadStore::RawGetCurrentThread() ? WIT_THREAD_REFERENCE_CURRENT : (WitU64)m_hOSThread);\n" +
                "    if (walk.Status() != WIT_STATUS_OK) return;\n");
        const string gcRoots = """
void Thread::GcScanRoots(ScanFunc * pfnEnumCallback, ScanContext * pvCallbackData)
{
    this->CrossThreadUnhijack();

#ifdef HOST_WASM
    GcScanWasmShadowStack(pfnEnumCallback, pvCallbackData);
#else
    StackFrameIterator frameIterator(this, GetTransitionFrame());
    GcScanRootsWorker(pfnEnumCallback, pvCallbackData, frameIterator);
#endif
}
""";
        const string scopedGcRoots = """
void Thread::GcScanRoots(ScanFunc * pfnEnumCallback, ScanContext * pvCallbackData)
{
    // The runtime rendezvous stabilizes managed state; the kernel suspension and
    // lease additionally keep the actual foreign stack alive through every use
    // of original saved-register/root locations, including CrossThreadUnhijack.
    const bool foreign = this != ThreadStore::RawGetCurrentThread();
    if (foreign && (m_hOSThread == INVALID_HANDLE_VALUE || SuspendThread(m_hOSThread) == (DWORD)-1))
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    WitNativeUnwindScope walk(foreign ? (WitU64)m_hOSThread : WIT_THREAD_REFERENCE_CURRENT);
    if (walk.Status() != WIT_STATUS_OK)
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);

    this->CrossThreadUnhijack();

#ifdef HOST_WASM
    GcScanWasmShadowStack(pfnEnumCallback, pvCallbackData);
#else
    StackFrameIterator frameIterator(this, GetTransitionFrame());
    GcScanRootsWorker(pfnEnumCallback, pvCallbackData, frameIterator);
#endif

    // Release original-stack authority before allowing the target to run again.
    if (walk.Close() != WIT_STATUS_OK)
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    if (foreign && ResumeThread(m_hOSThread) == (DWORD)-1)
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
""";
        thread = Replace(thread, gcRoots, scopedGcRoots);
        await File.WriteAllTextAsync(Path.Combine(output, "thread.witos.cpp"), thread);
        var stressHeader = await Read(prefix + "inc/stressLog.h");
        stressHeader = Replace(stressHeader, "#ifndef __GCENV_BASE_INCLUDED__\n#if !defined(STRESS_LOG) || defined(DACCESS_COMPILE)",
            "#if !defined(__GCENV_BASE_INCLUDED__) || !defined(STRESS_LOG)\n#if !defined(STRESS_LOG) || defined(DACCESS_COMPILE)");
        stressHeader = Replace(stressHeader, "#define STRESS_LOG_VA(msg)", "#define STRESS_LOG_VA(dprintfLevel, msg)");
        var includeOutput = Path.Combine(root, "artifacts", "runtime-config", "include");
        Directory.CreateDirectory(includeOutput);
        await File.WriteAllTextAsync(Path.Combine(includeOutput, "stressLog.h"), stressHeader);
        var debugHeader = await Read(prefix + "DebugHeader.cpp");
        var stressTypes = Cut(debugHeader, "    MAKE_SIZE_ENTRY(StressLog);", "    MAKE_SIZE_ENTRY(RuntimeInstance);");
        debugHeader = Replace(debugHeader, stressTypes, "#ifdef STRESS_LOG\n" + stressTypes + "#endif\n\n");
        const string stressGlobal = "    void *g_stressLog = &StressLog::theLog;\n    MAKE_GLOBAL_ENTRY(g_stressLog);";
        debugHeader = Replace(debugHeader, stressGlobal, "#ifdef STRESS_LOG\n" + stressGlobal + "\n#endif");
        await File.WriteAllTextAsync(Path.Combine(output, "debugheader.witos.cpp"), debugHeader);
        var includes = ts[..ts.IndexOf("EXTERN_C volatile uint32_t RhpTrapThreads;", StringComparison.Ordinal)];
        var slice = includes +
            Cut(ri, "ThreadStore *   RuntimeInstance::GetThreadStore()", "FCIMPL1(uint8_t *, RhGetCrashInfoBuffer") +
            Cut(ri, "GPTR_IMPL_INIT(RuntimeInstance, g_pTheRuntimeInstance, NULL);", "RuntimeInstance::OsModuleList*") +
            Cut(ri, "RuntimeInstance::RuntimeInstance()", "void RuntimeInstance::EnableConservativeStackReporting()") +
            Cut(ri, "bool RuntimeInstance::Initialize(HANDLE hPalInstance)", "bool RuntimeInstance::ShouldHijackLoopForGcStress") +
            Cut(ts, "ThreadStore::ThreadStore()", "void ThreadStore::AttachCurrentThread(bool fAcquireThreadStoreLock)") +
            Cut(ts, "volatile uint32_t * p_tls_index;", "#else // DACCESS_COMPILE");
        slice += Cut(thread, "ee_alloc_context::PerThreadRandom::PerThreadRandom()", "PInvokeTransitionFrame* Thread::GetTransitionFrame()") +
            Cut(thread, "void Thread::Construct()", "uint64_t Thread::s_DeadThreadsNonAllocBytes") +
            Cut(thread, "void Thread::SetState(ThreadStateFlags flags)", "void Thread::ClearState(ThreadStateFlags flags)");
        var callouts = await Read(prefix + "RestrictedCallouts.cpp");
        slice += "\n#include \"RestrictedCallouts.h\"\n" +
            Cut(callouts, "CrstStatic RestrictedCallouts::s_sLock;", "bool RestrictedCallouts::RegisterGcCallout(");
        var gc = await Read(prefix + "clrgc.disabled.cpp");
        slice += Cut(gc, "void InitializeGCEventLock()", "HRESULT InitializeDefaultGC();");
        await File.WriteAllTextAsync(Path.Combine(output, "startup.objects.slice.cpp"), slice);
        await File.WriteAllTextAsync(Path.Combine(output, "startup-provenance.json"), JsonSerializer.Serialize(new
        {
            pin.RuntimeCommit,
            scope = "Unchanged RuntimeInstance/ThreadStore creation methods, RestrictedCallouts initialization and upstream disabled-standalone-GC event-lock method. Windows TEB access in DAC TLS metadata is replaced with kernel-confirmed compiler TLS bounds; Thread::Construct retains the genuine upstream DuplicateHandle operation over real WitOS context capabilities, including unchanged INVALID_HANDLE_VALUE on failure. Both return-address hijack entrypoints retain a native walk scope through all original-stack pointer uses, without enclosing the preceding Redirect path. Full GcScanRoots retains the original upstream enumeration body inside a kernel stack lease; foreign targets acquire a counted suspension after the runtime rendezvous, and the lease is released before the paired resume. Failure terminates the component instead of scanning an unprotected foreign stack. Stack-discovery failure terminates the native component. WitOS uses upstream NO_STRESS_LOG consistently for the archive and probe; DebugHeader stress types/global are guarded by the actual feature macro; upstream disabled logging macros are exposed under GCENV and the VA signature matches enabled logging. The unchanged PerThreadRandom constructor/TLS definition uses real source-built minipal time and xoshiro. Actual SetGCSpecial/Construct/state/logging methods execute without attachment or collector substitutes.",
            inputs = pin.Sources.Where(s => names.Any(n => s.Path == prefix + n) || s.Path is "src/native/minipal/time.h" or "src/native/minipal/xoshiro128pp.h" or "src/native/minipal/xoshiro128pp.c"),
            stressHeader = new { file = "../include/stressLog.h", sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(includeOutput, "stressLog.h")))).ToLowerInvariant() },
            generated = new[] { "threadstore.witos.cpp", "thread.witos.cpp", "debugheader.witos.cpp", "startup.objects.slice.cpp" }.Select(name => new
            {
                file = name,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, name)))).ToLowerInvariant()
            })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    #endregion
}
