using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Pe;

namespace WitOS.Dev.NativeAot;

// Evidence inventory, not a substitute for guest implementation or dynamic reachability.
internal static class RuntimePlatformBoundary
{
    #region Fields

    private static readonly RuntimePlatformBoundaryGroup[] GROUPS = [
        new("attachment", "P1.8/P3", "Required: real runtime attach/detach and private exit notification; no fake COM/FLS initialization.", "PalInitComAndFlsSlot PalAttachThread"),
        new("contexts", "P1.8/P3", "Required: kernel-authorized contexts, suspension, hijack and restore. CET may be disabled only with a verified hardware/context profile.", "PalGetCompleteThreadContext PalSetThreadContext PalAllocateCompleteOSContext PalRestoreContext PalHijack PalGetHijackTarget PalAreShadowStacksEnabled GetSSP SetSSP PopulateControlSegmentRegisters"),
        new("names", "P1.6", "Required: real single-image identity and native thread names, with explicit lifetime and allocation contracts.", "PalSetCurrentThreadName PalSetCurrentThreadNameW PalGetModuleFileName"),
        new("gc-os", "P1.7", "Required boundary: keep genuine GC paths. Unsupported large pages/write-watch require explicit failure contracts and verified callers; debug break must trap.", "GetWriteWatch ResetWriteWatch VirtualReserveAndCommitLargePages DebugBreak"),
        new("services", "P1.5/P3", "Required: implement direct/import bindings over real capabilities; pseudo handles cannot stand in for context/ownership capabilities.", "CloseHandle __imp_CloseHandle CreateEventExW SetEvent Sleep WaitForMultipleObjectsEx VirtualAlloc VirtualFree DuplicateHandle GetCurrentProcess GetCurrentThread __imp_GetCurrentThreadId GetCurrentProcessorNumberEx GetThreadPriority"),
        new("com", "P1.6/P1.8", "Required apartment lifecycle for reached CoreLib thread paths: real per-thread initialization/refcount/query semantics. General COM activation is outside bring-up; E_NOTIMPL is not a successful MTA fallback.", "CoGetApartmentType CoInitializeEx CoUninitialize"),
        new("console-module", "P1.5/P1.6", "Required console/image operations. General DLL loading remains unsupported; remove Windows startup discovery only through an explicit source adaptation.", "WriteFile GetStdHandle GetConsoleOutputCP GetModuleFileNameW __imp_GetModuleHandleW __imp_GetProcAddress"),
        new("crt", "P1.3/P1.6", "Required: exact compiler marker, numeric/formatting/UTF conversion contracts and matching ownership for allocated buffers.", "MultiByteToWideChar WideCharToMultiByte LocalFree __stdio_common_vsnprintf_s _fltused log"),
        new("diagnostics", "P1.6", "Preserve meaningful failure diagnostics. Windows Event Log is optional only with honest unavailable/failure paths verified in CoreLib; native debugger discovery must be truthful.", "FormatMessageW RegisterEventSourceW DeregisterEventSource ReportEventW __imp_IsDebuggerPresent"),
        new("exceptions", "P1.8/P3", "Required real exception/context/unwind behavior; do not disable managed exceptions or return synthetic unwind success.", "RaiseFailFastException __imp_RaiseFailFastException __imp_RaiseException __imp_AddVectoredExceptionHandler __imp_RtlVirtualUnwind __C_specific_handler"),
        new("compiler-security", "P1.3/P1.4", "Implement real GS cookie/check before protected execution. CFG must be a consistent explicit compiler profile, never a dummy dispatch function.", "__GSHandlerCheck __guard_dispatch_icall_fptr __security_check_cookie __security_cookie"),
        new("entropy", "P1.4", "Required entropy-backed generation. No timestamps, thread IDs or noncryptographic PRNG as substitutes for BCryptGenRandom.", "BCryptGenRandom")
    ];

    #endregion

    #region Functions

    public static async Task WriteAsync(string root, string managedObject, CoffObjectInfo coff,
        string linkLog, string[] unresolved, string[] compilerArguments)
    {
        var lookup = new Dictionary<string, RuntimePlatformBoundaryGroup>(StringComparer.Ordinal);
        foreach (var group in GROUPS)
            foreach (var symbol in group.Symbols.Split(' '))
                if (!lookup.TryAdd(symbol, group))
                    throw new InvalidDataException("Duplicate platform policy: " + symbol);
        var unknown = unresolved.Select(Name).Where(n => !lookup.ContainsKey(n)).ToArray();
        if (unknown.Length != 0)
            throw new InvalidDataException("Unclassified runtime platform dependencies: " + string.Join(", ", unknown));
        var output = Path.Combine(root, "artifacts", "runtime-readiness");
        var entries = unresolved.Select(display =>
        {
            var name = Name(display);
            var group = lookup[name];
            var decorated = Regex.Match(display, @"\((\?[^\s]+)\)$");
            var target = decorated.Success ? decorated.Groups[1].Value : display;
            var native = linkLog.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("unresolved external symbol " + display, StringComparison.Ordinal)).ToArray();
            if (native.Length == 0)
                throw new InvalidDataException("Dependency has no linker evidence: " + display);
            var managed = (coff.ExternalReferences ?? throw new InvalidDataException("Missing managed COFF references."))
                .Where(r => r.Target == target).ToArray();
            return new { name, symbol = target, group.Id, group.Plan, group.Decision, linkerReferences = native, managedReferences = managed };
        }).ToArray();
        var summary = new StringBuilder("# P1 platform dependency evidence\n\n");
        summary.AppendLine("These are link/object references, not proof that every site executes in the minimal workload. Names of containing symbols come from COFF offsets; data references are not represented as calls. Guest runtime execution remains pending.\n");
        foreach (var entry in entries)
        {
            summary.AppendLine($"## {entry.name} ({entry.Plan})\n\n{entry.Decision}\n");
            foreach (var site in entry.managedReferences)
                summary.AppendLine($"- ILC: `{site.ContainingSymbol ?? "<no external owner>"}` in `{site.Section}` +0x{site.Offset:X}, relocation {site.Kind}.");
            foreach (var site in entry.linkerReferences)
                summary.AppendLine("- Link: `" + site + "`");
            summary.AppendLine();
        }
        await File.WriteAllTextAsync(Path.Combine(output, "platform-boundary.md"), summary.ToString());
        await File.WriteAllTextAsync(Path.Combine(output, "platform-boundary.json"), JsonSerializer.Serialize(new
        {
            guestManagedExecution = false,
            managedObjectSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(managedObject))).ToLowerInvariant(),
            compilerKnobs = compilerArguments.Where(a => a.StartsWith("--runtimeknob:", StringComparison.Ordinal)),
            generatorSources = new[] { "tools/WitOS.Dev/NativeAot/RuntimePlatformBoundary.cs", "tools/WitOS.Dev/NativeAot/RuntimeReadiness.cs", "tools/WitOS.Dev/Pe/NativeObject.cs" }
                .Select(p => new { file = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p)))).ToLowerInvariant() }),
            entries,
            scope = "Every current unresolved symbol classified; COFF relocation owners and actual linker reference evidence. No runtime service is implemented by this report."
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"[READINESS-PASS] P1 platform policy covers {entries.Length} unresolved symbols with link evidence and ILC relocation owners.");
    }

    #endregion

    #region Tools

    private static string Name(string display)
    {
        var cpp = Regex.Match(display, @"__cdecl (?:GCToOSInterface::)?(\w+)\(");
        return cpp.Success ? cpp.Groups[1].Value : display;
    }

    #endregion
}
