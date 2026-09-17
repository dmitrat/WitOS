# NativeAOT host reference and source audit

Status: implemented and verified locally on 2026-09-16.

This is a Windows-hosted experiment. The separate [M2 implementation](M2-Isolated-Execution.md) now provides native user isolation; this experiment does not provide a guest .NET runtime.

## Reproduce

```powershell
dotnet run --project tools/WitOS.Dev -- runtime-audit
dotnet run --project tools/WitOS.Dev -- runtime-probe
```

The commands use the existing Windows/.NET/Visual Studio host toolchain. The first execution needs GitHub raw-source and NuGet network access. Subsequent runs can use the local caches.

- `.tools/runtime-audit/`: SHA-256-verified source files.
- `.tools/nuget/`: published NativeAOT packages, kept separate from user/global caches.
- `artifacts/runtime-probe/`: build log, probe logs, import reports and source-audit report.
- `artifacts/runtime-probe/publish/NativeAotProbe.exe`: hosted native executable.

The project is under `experiments/` and is intentionally outside the ordinary kernel-tool solution build. Running its managed DLL is not equivalent to publishing the NativeAOT experiment; the native-identity assertion rejects a JIT execution.

## Pins

The source and binary package versions are 10.0.8. Compiler packages are locked by NuGet version/content hash, while 26 selected source files and the package VMR manifest are locked by SHA-256.

Published packages report a dotnet/dotnet VMR commit, not the dotnet/runtime tag SHA. The audit verifies that the VMR's runtime component maps to the reviewed runtime commit. The probe independently validates both the compiler package and the NativeAOT runtime pack's nuspec repository metadata.

Source lock: [upstream.lock.json](../../experiments/NativeAotProbe/upstream.lock.json).
Package lock: [packages.lock.json](../../experiments/NativeAotProbe/packages.lock.json).

No upstream code is vendored into Git. The caches retain the upstream files and their notices locally.

## Checks

Six groups exercise NativeAOT identity, GC roots/finalizers, exception/filter/finally/null/divide behavior, threads/TLS/Monitor/Join/atomics/GC, wait/reset/timeout behavior and Task/cancellation/clock behavior.

The host tool requires the expected runtime version, all six success markers, successful process exit, no failure marker and no timeout. It verifies an x64 native console PE without a CLR header.

The profile explicitly chooses invariant globalization, workstation/non-concurrent GC and the non-Windows-specific ThreadPool implementation option. It does not promise the removal of every Windows service dependency.

## Observed local result

All six groups passed. The reference executable was 1,558,528 bytes with 158 direct import symbols from 11 libraries:

| Library group | Observation |
| --- | --- |
| KERNEL32 | Memory, threads, waits, context/unwind, time, console and other linked services |
| ADVAPI32 | Event reporting and process-token related support |
| bcrypt | BCryptGenRandom |
| ole32 | COM/apartment support in the Windows runtime path |
| CRT API-set libraries | Heap, strings, math, locale, runtime and stdio support |

Exact size and import counts are observations for the local native linker/SDK, not fixed conformance thresholds. CI may use another supported MSVC revision.

Additional local checks confirmed that modifying a cached source byte causes a SHA-256 audit failure, and that the restored source verifies again. Every direct import library/symbol was independently compared against MSVC dumpbin.

## Interpretation limits

The import report covers the PE's direct import table and reports the delay-import directory size. It does not enumerate dynamically resolved calls, forwarded exports, all transitive DLL dependencies or platform-specific instructions. Imports can remain linked without being exercised by the probe.

Therefore 158 is neither the number of mandatory kernel calls nor a complete porting estimate. Source analysis supplies requirements that static imports cannot reveal, including TLS layout and GC suspension protocols.

This build uses published NativeAOT packages. It is not a source build of the entire runtime and does not prove a WitOS backend. Its purpose is a reproducible reference workload and dependency evidence for RFC 0015.

## CI and next step

A separate hosted-runtime workflow runs the source audit and native probe. Kernel VM tests remain separate so a passing Windows probe cannot be mistaken for guest runtime support.

The M2 memory/thread/event slices are now implemented. The [target/bootstrap experiment](NativeAot-Target-Bootstrap.md) extends hosted evidence to native C entry, static ILC artifacts and strict link boundaries. Guest loading, runtime adaptations and fault/GC integration remain, following [RFC 0015](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).
