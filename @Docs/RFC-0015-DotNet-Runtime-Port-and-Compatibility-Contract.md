# RFC 0015 — .NET Runtime Port & Compatibility Contract

Draft v0.10. Status: source inventory, hosted evidence probes, bounded M2 mechanisms, guest native C bootstrap, GC environment adapters and recursive minipal/Crst mutexes implemented; guest managed runtime port not implemented.

## 1. Evidence baseline

This RFC targets upstream .NET **10.0.8**:

- runtime repository: `dotnet/runtime`
- tag: `v10.0.8`
- commit: `b82454cad0aaaae3db2cf18fbf2cccc36e201ccc`
- published compiler/runtime package version: `10.0.8`
- package repository: `dotnet/dotnet`
- package VMR commit: `94ea82652cdd4e0f8046b5bd5becbd11461482ca`
- host SDK: `10.0.300`, from the repository's global.json

The VMR source manifest maps its runtime component to the pinned runtime commit. The checked-in [source lock](../experiments/NativeAotProbe/upstream.lock.json) records SHA-256 hashes for 32 selected source/license files and the VMR manifest. The [NuGet lock](../experiments/NativeAotProbe/packages.lock.json) pins the compiler packages and their content hashes.

These are selected integration sources, not a complete dependency closure. This is a reference version for investigation, not a claim that it is the newest release.

## 2. What the runnable experiment establishes

`runtime-probe` publishes a real Windows x64 NativeAOT executable, executes it directly and checks:

- NativeAOT identity and runtime version;
- live object graphs across forced GC, a large object and finalization;
- exceptions, filters, finally, null-reference and divide-by-zero behavior;
- two threads, thread-static isolation, Monitor, Join, atomics and GC during work;
- signal/reset/timeout behavior;
- Tasks, cancellation and a monotonic clock.

PE inspection rejects a managed CLR-header executable and reports direct native imports. A local reference build had 158 direct imports in 11 libraries, including memory, threads, waits, unwind/context APIs, CRT and entropy functions.

This is a **hosted Windows** reference. Its Windows executable is not a WitOS boot component, and its import count is not a required WitOS syscall count. Linked imports, dynamically resolved calls, transitive DLL dependencies and executed calls are different sets.

The probe uses invariant globalization, workstation/non-concurrent GC and the portable .NET ThreadPool selection. These are an explicit experiment profile, not a restriction on eventual ordinary .NET applications.

The [target/bootstrap experiment](Implementation/NativeAot-Target-Bootstrap.md) additionally publishes a static ILC object archive and shared module, then enters the real runtime from a separate C executable with no CoreCLR. GC, exceptions and TLS on native threads pass on Windows. Strict links expose unresolved dependencies without the native runtime and without OS/CRT libraries. PE inspection records TLS callbacks, relocations, unwind entries and image-size requirements. These results remain hosted evidence.

## 3. Integration is broader than Pal.h

The port touches several distinct surfaces:

| Surface | Examples in the pinned source | Placement in WitOS |
| --- | --- | --- |
| Bootstrap and modules | RhInitialize, RhRegisterOSModule, InitializeModules | User-space runtime bootstrap and loader |
| NativeAOT PAL | PalVirtualAlloc, waits, thread attach, stack bounds, hardware faults | Native runtime adapter |
| GC OS interface | Reserve/commit/decommit/release, clocks, CPU count, barriers | Native runtime adapter over kernel mechanisms |
| CoreLib platform paths | Thread creation, platform waits and interop | Selected CoreLib/platform adaptations |
| minipal and native support | Clocks, entropy, architecture features | Native support/provider code |
| BCL interop | Console, filesystem, networking, crypto/globalization shims | Services and adapters, introduced by profile |
| Build/toolchain | RID, target OS, libraries, compiler ABI, TLS, unwind data | Reproducible WitOS target build |

Implementing the declarations in Pal.h alone is not evidence of a complete port.

## 4. Source-backed requirements and M1 gaps

| Requirement | Source evidence | At the M1 audit | Required progression |
| --- | --- | --- | --- |
| Runtime initialization | [Bootstrap/main.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Bootstrap/main.cpp), [startup.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/startup.cpp) | Native kernel entry only | C/C++ runtime setup, image/module metadata and PAL must exist before managed Main |
| Virtual reserve/commit/decommit/release | [gcenv.os.h](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/gc/env/gcenv.os.h) | Physical pages and scratch mappings | Per-address-space reservations, sparse commitment, zero-filled pages, recoverable OOM and clear ownership |
| TLS and thread attachment | [threadstore.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/threadstore.cpp), [Pal.h](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/Pal.h) | No per-thread FS/GS or loader TLS | Defined ABI-specific TLS initialization and switching, attach/detach callbacks |
| Dynamic threads and finalization | [PalMinWin.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/windows/PalMinWin.cpp), [Thread.NativeAot.Unix.cs](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/System.Private.CoreLib/src/System/Threading/Thread.NativeAot.Unix.cs) | Two fixed kernel workers | User stacks, create/start/exit/join, finalizer thread, cancellation-safe lifetime |
| Waits and wakeups | [PalUnix.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/unix/PalUnix.cpp) | No blocking/wakeup subsystem | Atomic state-check and park, persistent event state, wakeup, timeout and teardown rules |
| GC thread suspension | [ThreadStore::SuspendAllThreads](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/threadstore.cpp#L238), [thread.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/thread.cpp) | Preemption preserves CPU state only | Runtime-coordinated rendezvous/context access and supported hijack/safe-point behavior |
| Hardware exception translation | [EHHelpers.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/EHHelpers.cpp), [HardwareExceptions.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/unix/HardwareExceptions.cpp) | Fatal kernel diagnostics | Faulting user component isolated first; later validated runtime fault delivery/context restoration |
| Time | [minipal/time.c](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/native/minipal/time.c) | Approximately 100 Hz interrupt counter | Monotonic time plus frequency/resolution, sleep/deadlines, separate wall clock |
| Entropy and environment | [minipal/random.c](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/native/minipal/random.c) | No provider | Honest scoped providers where selected features require them; no fake secure entropy |
| Native support and BCL | BuildIntegration, platform Thread/CoreLib and direct import report | No runtime shims or BCL providers | Explicit supported profile, dependency inventory and additional adapters |

## 5. Memory semantics

NativeAOT's GC has a separate GCToOSInterface. A combined allocate-and-map function is insufficient.

A reservation must retain virtual identity without immediately consuming backing RAM. Commit must establish zero-initialized private pages. Decommit must remove access and discard content while retaining the reservation. Recommit must not expose stale data. Release invalidates the reservation and its mappings.

Operations need aligned ranges, overflow checks, explicit errors, defined partial-failure behavior and address-space ownership. Out-of-memory in an application must not ordinarily panic the whole kernel.

WitOS 0.0.5 implements these basic reservation/commit/decommit/release semantics through experimental user ABI v2. [Implementation and tests](Implementation/M2-User-Memory.md) cover sparse RAM use, zero-fill, protection, ownership and atomic rollback. Eight reservations and 128 total owned frames per component are deliberate test limits, not sufficient evidence for a production GC heap. The later GC memory/discovery adapter exercises this mechanism; reset semantics, complete allocation policy and managed runtime integration remain unimplemented.

Write-watch is optional: the pinned Unix GC implementation reports it unsupported. It must not be invented as an early mandatory kernel service.

## 6. Threading and GC

WitOS 0.0.6 adds bounded native user threads, timer/yield dispatch, raw FS-based TLS and consuming join with cycle rejection. [Implementation and tests](Implementation/M2-User-Threads-and-Tls.md) establish the native mechanism only: compiler TLS, runtime attach/detach, finalization and GC suspension still need actual upstream integration.

Disabling concurrent/server GC for the experiment does not eliminate thread requirements or stop-the-world coordination. The runtime still uses finalization and ordinary managed threads.

ThreadStore coordinates suspension using transition-frame state and process-wide write-buffer synchronization. The pinned native runtime enables FEATURE_HIJACK on non-WASM targets. Scheduling another thread is not equivalent to reaching a GC-safe suspension point.

Kernel mechanisms must let a runtime coordinate threads within its own protection domain. The kernel should not interpret managed heaps, stack maps or GC generations. Thread/context authority must not accidentally grant access to unrelated applications.

Do not stub suspension/barriers as unconditional success or substitute a never-collecting heap and call it standard NativeAOT support.

WitOS 0.0.7 adds manual/auto-reset events, atomic parking, timeout/close completion, absolute tick deadlines and kernel idle. [Implementation and tests](Implementation/M2-Events-and-Deadlines.md) establish native wait semantics. Its nominal 100 Hz clock advances only on delivered PIT interrupts and pauses while IRQ0 is disabled; it is not yet a complete runtime elapsed-time provider. Mutexes, semaphores, multi-object waits and alertable cancellation remain separate adapter requirements.

## 7. Exceptions, TLS and ABI

The Windows runtime path depends on TEB/TLS conventions and Windows context/unwind APIs. The Unix path depends on compiler TLS, pthread-like services, signals/context translation and unwind support. Neither existing backend is a generic bare-metal adapter.

The null area is platform-specific in the pinned PAL: 64 KiB on Windows, 4 KiB on Unix. M2 user mappings should reserve a conservative low null area; keeping only the kernel's page zero unmapped is not a complete runtime contract.

For M2's first native component, an unhandled user fault can terminate that component. M3 additionally needs the runtime's managed exception paths, including selected hardware-fault translation and safe context restoration. Those operations must validate privilege level, flags, stack and target addresses before resuming.

The loader must establish the selected native ABI, TLS, relocations, zeroed data, module boundaries and unwind metadata. PE/COFF and Microsoft x64 are the measured candidate; WitOS 0.0.8 implements a [restricted native executable loading path](Implementation/M2-Pe-Image-Loading.md) with section protection, zero-fill and internal DIR64 fixups. It rejects DLL/import/TLS/unwind semantics until their runtime contracts are implemented. WitOS 0.0.10 selects Windows x64 code generation with an explicit WitOS source adapter in [ADR 0006](Implementation/NativeAot-Gc-Memory-Port.md). This is not a Windows compatibility personality; the [full native source-build recipe](Implementation/NativeAot-Source-Build.md) is established; remaining guest OS behavior is still pending.

CPU instruction support and saved state must agree. M1 preserves x87/SSE only; any emitted or runtime-selected AVX/extended state requires the corresponding kernel support or an explicitly compatible target profile.

WitOS 0.0.9 supplies [readonly native image information and a user-space C startup helper](Implementation/M2-Native-Module-Bootstrap.md), plus structural validation of plain x64 unwind records. This is not managed-module initialization or an unwinder. The newly pinned StartupCodeHelpers/TypeManager sources make the boundary explicit: real GC and runtime initialization precede GC statics, frozen segments and eager constructors; those operations stay in the runtime.

## 8. Initial M3 profile

Proposed initial system-component profile:

- one controlled x64 virtual target;
- actual upstream NativeAOT compiler and GC, with a recorded platform patch set;
- static application deployment and known dependencies;
- invariant globalization and workstation/non-concurrent GC initially;
- managed allocations, finalization, exceptions, threads/TLS, waits and monotonic time;
- a narrow terminal path through granted authority;
- no dependency cycle through filesystem/network/managed services during bootstrap.

System.IO, sockets, cryptography, globalization, dynamic native loading and diagnostics are supported only when their adapters exist. Unsupported APIs must fail explicitly; successful stubs do not count as compatibility.

NativeAOT has its own feature limitations, including dynamic assembly loading/code generation. Ordinary IL/CoreCLR compatibility remains the later M6 contract.

## 9. M2 ABI implications

Keep mechanisms small and language-neutral. The needed families are:

1. User execution lifetime and component termination.
2. Address-space reservation/commit/protection/release.
3. Thread creation, TLS/context state and termination.
4. Waitable state, wakeups and deadlines.
5. Process-local runtime rendezvous and memory-order guarantees.
6. Fault delivery and carefully checked context return.
7. Explicit handles for terminal/communication authority.

These are semantic requirements, not frozen syscall numbers. Detailed status codes, structures and transitions should be committed with the corresponding native tests.

The immediate M2 slice only needs separate user mappings, a versioned call boundary, granted console output, orderly exit and contained user faults. Broader runtime facilities follow in tested slices before M3.

## 10. Port gates

- **Evidence gate, implemented:** pinned sources/package provenance, actual hosted NativeAOT binary, semantic smoke tests and direct-import report.
- **Target/bootstrap evidence gate, implemented on Windows:** pinned static ILC archive, native C-host entry with real GC/TLS, COFF/PE metadata and strict-link failure inventory. A restricted native PE loader is now implemented; actual runtime bootstrap remains pending.
- **Initial M2 isolation gate, implemented for the controlled fixture:** native unprivileged component, checked pointers/handles, private mappings and contained faults. See [M2 limits and evidence](Implementation/M2-Isolated-Execution.md); general DLL/import/TLS/unwind support and actual runtime services are still pending.
- **First source-adapter gate, implemented:** native GC memory methods compile against pinned, unchanged upstream headers and execute through WitOS syscalls in QEMU; the later [discovery extension](Implementation/NativeAot-Gc-Discovery.md) implements environment initialization and live quota/physical accounting. The [time extension](Implementation/NativeAot-Gc-Time.md) implements GC performance clocks and sleep; the remaining VirtualReset boundary is rejected at link time. This does not execute the collector.
- **Native source-build gate, implemented:** full upstream nativeaot libraries, a tested source-built Windows reference, and a source-built workstation archive with the WitOS memory adapter. Strict linking records incomplete GC/OS requirements; guest execution remains pending.
- **GC event/time adapter, implemented for the controlled profile:** [native GCEvent](Implementation/NativeAot-Gc-Events.md) uses kernel events for poll/finite/infinite waits, safe lifecycle handling and yielding. HPET supplies GC monotonic time.
- **Native mutex adapter, implemented for the controlled profile:** [recursive minipal and checked Release Crst](Implementation/NativeAot-Mutexes.md) use kernel-owned thread identity and event-backed blocking; source-built archives verify replacement of the Windows critical-section implementation.
- **Runtime substrate gate, pending:** memory lifecycle, TLS, blocking/waking and runtime-coordinated suspension have executable tests.
- **M3 gate, pending:** the real NativeAOT component passes the relevant probe cases inside WitOS, including GC and thread activity. Report disabled features and all upstream changes.
- **Maintenance gate, pending:** rebuild/retest against a subsequent upstream revision and measure the adaptation effort.

## 11. Reproduction

```powershell
dotnet run --project tools/WitOS.Dev -- runtime-audit
dotnet run --project tools/WitOS.Dev -- runtime-probe
dotnet run --project tools/WitOS.Dev -- runtime-target
```

The first command verifies cached/downloaded source bytes and the VMR mapping. The second uses locked published packages, checks their repository metadata, builds the native executable and writes reports into `artifacts/runtime-probe/`.

The third command verifies target artifacts, executes a native bootstrap host and records strict-link boundaries under `artifacts/runtime-target/`.

See [host experiment notes](Implementation/NativeAot-Host-Probe.md) and [target/bootstrap evidence](Implementation/NativeAot-Target-Bootstrap.md). Those hosted experiments use unchanged upstream packages. The separate `runtime-port` command builds and executes the WitOS GC memory source overlay against unchanged upstream headers; it exercises native GC environment and minipal/Crst adapters in the guest. `runtime-source` now builds the full native libraries in separate Windows-reference and incomplete WitOS-overlay profiles; no managed guest runtime is claimed.
