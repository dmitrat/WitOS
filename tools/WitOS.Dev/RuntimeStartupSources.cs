using System.Security.Cryptography;
using System.Text.Json;

namespace WitOS.Dev;

internal static class RuntimeStartupSources
{
    public static async Task PrepareAsync(string root, RuntimeExperiment.SourceLock pin)
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
                throw new InvalidDataException("Pinned ThreadStore TLS correction changed.");
            return source.Replace(before, after, StringComparison.Ordinal);
        }
        const string prefix = "src/coreclr/nativeaot/Runtime/";
        string[] names = ["RuntimeInstance.cpp", "RuntimeInstance.h", "threadstore.cpp", "threadstore.h", "threadstore.inl",
            "thread.h", "thread.cpp", "RestrictedCallouts.cpp", "RestrictedCallouts.h", "clrgc.disabled.cpp"];
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
        var includes = ts[..ts.IndexOf("EXTERN_C volatile uint32_t RhpTrapThreads;", StringComparison.Ordinal)];
        var slice = includes +
            Cut(ri, "ThreadStore *   RuntimeInstance::GetThreadStore()", "FCIMPL1(uint8_t *, RhGetCrashInfoBuffer") +
            Cut(ri, "GPTR_IMPL_INIT(RuntimeInstance, g_pTheRuntimeInstance, NULL);", "RuntimeInstance::OsModuleList*") +
            Cut(ri, "RuntimeInstance::RuntimeInstance()", "void RuntimeInstance::EnableConservativeStackReporting()") +
            Cut(ri, "bool RuntimeInstance::Initialize(HANDLE hPalInstance)", "bool RuntimeInstance::ShouldHijackLoopForGcStress") +
            Cut(ts, "ThreadStore::ThreadStore()", "void ThreadStore::AttachCurrentThread(bool fAcquireThreadStoreLock)") +
            Cut(ts, "volatile uint32_t * p_tls_index;", "#else // DACCESS_COMPILE");
        slice += Cut(await Read(prefix + "thread.cpp"), "bool Thread::IsInitialized()", "void Thread::SetGCSpecial()");
        var callouts = await Read(prefix + "RestrictedCallouts.cpp");
        slice += "\n#include \"RestrictedCallouts.h\"\n" +
            Cut(callouts, "CrstStatic RestrictedCallouts::s_sLock;", "bool RestrictedCallouts::RegisterGcCallout(");
        var gc = await Read(prefix + "clrgc.disabled.cpp");
        slice += Cut(gc, "void InitializeGCEventLock()", "HRESULT InitializeDefaultGC();");
        await File.WriteAllTextAsync(Path.Combine(output, "startup.objects.slice.cpp"), slice);
        await File.WriteAllTextAsync(Path.Combine(output, "startup-provenance.json"), JsonSerializer.Serialize(new
        {
            pin.RuntimeCommit,
            scope = "Unchanged RuntimeInstance/ThreadStore creation methods, RestrictedCallouts initialization and upstream disabled-standalone-GC event-lock method. Only Windows TEB access in DAC TLS metadata is replaced with kernel-confirmed compiler TLS bounds. No attachment, collector, dispatch or exception substitutes.",
            inputs = pin.Sources.Where(s => names.Any(n => s.Path == prefix + n)),
            generated = new[] { "threadstore.witos.cpp", "startup.objects.slice.cpp" }.Select(name => new
            {
                file = name, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, name)))).ToLowerInvariant()
            })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
}
