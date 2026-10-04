using System.Security.Cryptography;
using System.Text.Json;

namespace WitOS.Dev.NativeAot;

/// <summary>
/// Fetches the pinned runtime startup sources and applies the recorded WitOS corrections.
/// </summary>
internal static class RuntimeStartupSources
{
    #region Functions

    /// <summary>
    /// Writes the startup source slices with the recorded corrections applied.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="pin">Pinned upstream sources.</param>
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
        async Task<string> Patch(string name, string target)
        {
            var text = UpstreamPatches.Apply(root, "runtime", name, Path.GetFileName(target), await Read(name));
            await File.WriteAllTextAsync(target, text);
            return text;
        }
        const string prefix = "src/coreclr/nativeaot/Runtime/";
        string[] names = ["RuntimeInstance.cpp", "RuntimeInstance.h", "threadstore.cpp", "threadstore.h", "threadstore.inl",
            "thread.h", "thread.cpp", "thread.inl", "inc/stressLog.h", "DebugHeader.cpp", "RestrictedCallouts.cpp", "RestrictedCallouts.h", "clrgc.disabled.cpp"];
        var ri = await Read(prefix + names[0]);
        var ts = await Patch(prefix + "threadstore.cpp", Path.Combine(output, "threadstore.witos.cpp"));
        var thread = await Patch(prefix + "thread.cpp", Path.Combine(output, "thread.witos.cpp"));
        var includeOutput = Path.Combine(root, "artifacts", "runtime-config", "include");
        Directory.CreateDirectory(includeOutput);
        await Patch(prefix + "inc/stressLog.h", Path.Combine(includeOutput, "stressLog.h"));
        await Patch(prefix + "DebugHeader.cpp", Path.Combine(output, "debugheader.witos.cpp"));
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
            patches = new[] { "threadstore.witos.cpp", "thread.witos.cpp", "stressLog.h", "debugheader.witos.cpp" }
                .Select(name => UpstreamPatches.Describe(root, "runtime", name)),
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
