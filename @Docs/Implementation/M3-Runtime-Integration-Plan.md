# M3: Remaining runtime integration work

**Assessment date:** 2026-09-20, based on the 0.0.15 source-link inventory and pinned .NET 10.0.8 runtime. Updated through 0.0.21: native allocation, compiler TLS, kernel thread discovery, PAL memory/waits and detached native workers.

## Estimate and meaning of running .NET

Working estimate at 0.0.15: about eight major work packages, or 12-20 bounded implementation/validation slices of the recent size, to complete M3. This is an engineering planning range, not a schedule or a measured completion percentage. Integration findings may add work, particularly around TLS, stack walking and exception delivery. The native allocation slice below completes part of package 1, not the entire package.

A first real managed entry with normal runtime initialization is expected after roughly 5-7 packages, with prerequisite portions of the others brought forward as needed. This is not permission to bypass GC/bootstrap or replace runtime helpers with successful stubs. Required exception and stack-walk infrastructure cannot simply be postponed if startup or collection needs it.

M3 acceptance remains a real NativeAOT component running inside WitOS with allocation/collection, finalization, exceptions and thread activity. Ordinary unchanged IL programs using CoreCLR remain M6, beyond this estimate.

## Work packages and observable completion

| Package | Work | Evidence required |
| --- | --- | --- |
| 1. Native allocation and CRT substrate | Nothrow runtime new/delete, remaining actually referenced memory/string/math/compiler helpers and well-defined failure behavior | Guest tests, no Windows/CRT imports, expected symbols resolve in the real source archive. 0.0.16 implements only the bounded new/delete part. |
| 2. Compiler/runtime TLS and thread lifecycle | Per-thread compiler TLS layout/template, runtime attachment, callbacks and deterministic shutdown/reuse | Separately compiled TLS accesses from multiple real guest threads; no stale attachment after exit or slot reuse; proper runtime thread registration/shutdown |
| 3. Required PAL and CoreLib platform behavior | Replace required Windows PAL paths and direct CoreLib platform calls for the selected static, single-CPU, workstation/non-concurrent profile | Complete strict link of the selected workload without OS imports or synthetic success; unsupported optional features explicitly excluded/rejected |
| 4. Executable profile and resource budgets | Real image extent, TLS/module metadata, unwind metadata validation, runtime stacks/heap/event/handle limits | Load the actual adapted image with protection and failure rollback; quotas derived from measured requirements |
| 5. GC coordination and stack visibility | Thread rendezvous, transitions, contexts and stack walking needed by the collector; process-local ordering | Real collection preserves live roots across native/managed transitions and multiple threads; no deadlock or lost roots |
| 6. Runtime and module bootstrap | Actual RhInitialize/module registration/TypeManager/GC-static/frozen-object/eager-constructor sequence | Enter managed code through real runtime startup in the guest; repeated entry and allocation/collection work |
| 7. Exception and unwind integration | Native context/fault delivery, runtime exception handlers, managed throw/catch/finally and safe failure boundaries | Real managed exceptions and supported hardware-fault translation; validated unwind/context restoration |
| 8. Finalization and integration acceptance | Finalizer thread, waits, shutdown/failure cleanup and end-to-end regression workload | One guest workload demonstrates all M3 acceptance features; hosted references remain separate |

These packages overlap and are not eight strictly sequential commits. For example, module metadata, stack walking and minimal exception setup may be prerequisites for the first successful runtime initialization.

## Current evidence and blockers

- Kernel mechanisms already work: ring 3 isolation, sparse memory, bounded threads/raw FS TLS, join/events/deadlines, restricted PE loading and native bootstrap.
- Upstream GC memory/discovery/event/time adapters, minipal/Crst locks and VirtualReset execute in the guest.
- 0.0.16 adds bounded, thread-safe C++ nothrow allocation and deletion; it does not implement managed allocation, a complete CRT or the full runtime allocator profile.
- The hosted target reference measured a 950,272-byte mapped image and 2,599 unwind entries, versus a 256 KiB guest image cap and 128 plain unwind records. Its image alone consumes 232 pages, already exceeding the current 128-frame total component quota. See [target evidence](NativeAot-Target-Bootstrap.md); these reference figures are not the final adapted-image requirements.
- A raw FS page does not provide MSVC/ILC compiler TLS or runtime-managed thread attachment. Version 0.0.17 adds a separate validated static MSVC TLS template and GS module-vector page; 0.0.18 adds dynamic C++ TLS initialization/destruction and user-space thread wrappers; actual runtime attachment remains pending. The upstream Windows PAL uses FLS callbacks to call RuntimeThreadShutdown; WitOS needs its own real lifecycle equivalent.
- The source link exposed 141 unresolved symbols at 0.0.15; 0.0.16 resolves five C++ allocation symbols, leaving 136. Four are already-implemented guest transport functions deliberately excluded from the broad diagnostic link. The remainder is neither a complete syscall list nor a direct task count.

## Next implementation boundary

PAL memory, events and non-alertable single-event waits are implemented in 0.0.20. Prioritize the remaining startup/thread/handle/last-error and GC coordination services for real collector initialization. ThreadStore detach calls GC FixAllocContext, so its full acceptance depends on actual GC startup; an isolated success stub is not valid evidence. Version 0.0.19 implements kernel-backed PAL thread/stack discovery; static/dynamic compiler TLS is already implemented. Inspect the actual generated TLS access and pinned startup/thread-store code before selecting its guest layout. Keep architecture-specific segment access in Kernel.Arch.X64 and runtime attachment in user space. Preserve the clean upstream tree and separate source-build profiles.

Continue using strict source-link inventories to track dependencies, but require guest execution evidence for each implemented service. Revisit limits and the estimate when the first complete adapted workload links.

The 0.0.19 source overlay removes Windows PalCommon/PalMinWin and exposes 33 missing PAL methods explicitly. Its 125 unresolved symbols are a reclassified dependency boundary, not nine completed services compared with the prior count of 134. See [the PAL decision](NativeAot-Pal-Thread-Discovery.md).

Version 0.0.20 implements ten more PAL methods and leaves 24 explicit PAL requirements (116 unresolved symbols overall). See [the exact memory/wait contract](NativeAot-Pal-Memory-and-Waits.md).

Version 0.0.21 adds native detached workers for PAL GC/finalizer/helper startup and automatic resource reclamation; 22 PAL methods remain unresolved (114 symbols overall). Actual collector/finalizer execution remains pending. See [the worker contract](NativeAot-Pal-Background-Threads.md).
