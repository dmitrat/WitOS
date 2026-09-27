# M3: Remaining runtime integration work

**Current reassessment:** 2026-09-27, WitOS 0.0.37, pinned .NET 10.0.8. Historical per-version notes are retained below.

## Estimate and meaning of running .NET

**The earlier 12-20-slice estimate is superseded.** Versions 0.0.16-0.0.36 completed many necessary native services but did not produce a link-complete guest runtime. Counting these small slices understated integration work. There is no defensible remaining percentage or calendar delivery date yet.

The new minimal standard-CoreLib executable allocates objects/arrays, preserves static and local roots through GC.Collect, and passes on Windows. Rooting its real source-built wmain startup with actual WitOS syscall transport and TLS metadata still leaves 83 unresolved symbols. These are exactly the earlier broad inventory minus five already implemented transport symbols and _tls_index; reducing the application to a small Main did not remove the remaining platform requirements. They are neither 83 independent tasks nor a reliable percentage.

Its Windows reference occupies 954,368 mapped bytes (233 pages) and 2,593 unwind entries. Guest limits remain 262,144 image bytes, 128 total owned pages and 128 plain unwind entries. These measurements demonstrate a resource/profile gap, not the final guest image size. Raising quotas alone would not implement handler unwind metadata or runtime stack walking.

Until a full guest-shaped workload links, plan by observable gates:

1. **Complete native link and startup driver:** finish the actually referenced PAL/CRT/platform paths, explicit optional-feature decisions, native process/TLS initialization and publication, using the ordinary CoreLib and genuine upstream bootstrap. The current diagnostic wmain root is not a kernel handoff entrypoint.
2. **Loadable runtime image and measured budgets:** image layout, unwind policy, stacks/TLS and heap/handle/event/page budgets derived from the adapted image and collector initialization requirements.
3. **Runtime threads and collector coordination:** true ThreadStore attachment/shutdown, allocation-context cleanup, root visibility, contexts, suspension and stack walking. Current native records and exit notification are prerequisites, not completion.
4. **First guest managed entry:** normal RhInitialize/module registration/TypeManager/GC-static/frozen-object initialization reaches Main, allocations and a real collection preserve roots. This is the first minimal guest .NET milestone.
5. **Complete M3 behavior:** managed throw/catch/finally, finalization and thread activity pass one end-to-end workload. Required exception/context infrastructure must be brought forward into gate 3 when needed by startup or GC.

Several substantial integration blocks remain; this is not one or two small commits. Re-estimate calendar effort after gate 1, when the final platform boundary and executable profile are concrete. CoreCLR/JIT and unchanged portable IL deployment remain M6, beyond the first NativeAOT guest milestone.

The historical package grouping below remains useful for tracking work, but its old slice-count forecast is no longer a current estimate. This is not permission to bypass GC/bootstrap or replace runtime helpers with successful stubs. Required exception and stack-walk infrastructure cannot simply be postponed if startup or collection needs it.

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

PAL memory, events and non-alertable single-event waits are implemented in 0.0.20. Prioritize the remaining startup/thread/handle and GC coordination services for real collector initialization. ThreadStore detach calls GC FixAllocContext, so its full acceptance depends on actual GC startup; an isolated success stub is not valid evidence. Version 0.0.19 implements kernel-backed PAL thread/stack discovery; static/dynamic compiler TLS is already implemented. Inspect the actual generated TLS access and pinned startup/thread-store code before selecting its guest layout. Keep architecture-specific segment access in Kernel.Arch.X64 and runtime attachment in user space. Preserve the clean upstream tree and separate source-build profiles.

Continue using strict source-link inventories to track dependencies, but require guest execution evidence for each implemented service. Revisit limits and the estimate when the first complete adapted workload links.

The 0.0.19 source overlay removes Windows PalCommon/PalMinWin and exposes 33 missing PAL methods explicitly. Its 125 unresolved symbols are a reclassified dependency boundary, not nine completed services compared with the prior count of 134. See [the PAL decision](NativeAot-Pal-Thread-Discovery.md).

Version 0.0.20 implements ten more PAL methods and leaves 24 explicit PAL requirements (116 unresolved symbols overall). See [the exact memory/wait contract](NativeAot-Pal-Memory-and-Waits.md).

Version 0.0.21 adds native detached workers for PAL GC/finalizer/helper startup and automatic resource reclamation; 22 PAL methods remain unresolved (114 symbols overall). Actual collector/finalizer execution remains pending. See [the worker contract](NativeAot-Pal-Background-Threads.md).

Version 0.0.22 adds real per-thread native last-error and PAL failure diagnostics; 110 unresolved symbols remain, including 22 PAL methods. PalInit is still explicitly unimplemented. See [native last-error](NativeAot-Pal-Last-Error.md).

Version 0.0.23 supplies real single-image PAL identity/bounds, including TLS constructors and worker reuse; 108 unresolved symbols remain, including 20 PAL methods. Tracing PalInit identifies real GCConfig/RhConfig and configuration/environment dependencies as the next startup boundary. This does not complete runtime/managed module registration. See [native module discovery](NativeAot-Pal-Module-Discovery.md).

Version 0.0.24 implements immutable native environment and PAL string conversion, with 105 remaining symbols (18 PAL). The next acceptance boundary is executable real RhConfig/GCConfig, including embedded settings/knobs, numeric/string dependencies and allocation failure semantics; PalInit remains unresolved. See [configuration transport](NativeAot-Pal-Environment.md).

Version 0.0.25 executes real RhConfig/GCConfig methods in a dedicated guest probe, with OOM corrections shared by Workstation and real C string/integer support. Its 100 unresolved symbols still include PalInit and PalAttachThread. Next implement PalInit with explicit startup/profile rules, then actual collector/runtime initialization. See [configuration execution](NativeAot-Runtime-Configuration.md).

Version 0.0.26 implements PalInit over real configuration and GC OS initialization, with explicit readiness/CPU/lifecycle checks. The full link retains 99 unresolved symbols, including PalAttachThread and PalInitComAndFlsSlot. Next follow RhInitialize/InitDLL through native exit callbacks, platform/diagnostic policy, RuntimeInstance and collector startup. See [PAL initialization](NativeAot-Pal-Initialization.md).

Version 0.0.27 implements bounded native exit callbacks and checks their registration in the complete upstream startup source. This removes one strict-link dependency (98 remain); actual RhInitialize/InitDLL execution still requires runtime/GC integration. See [process exit](NativeAot-Process-Exit.md).

Version 0.0.28 executes actual interface-dispatch initialization and allocation over the WitOS PAL, with explicit upstream AllocHeap lock cleanup. This advances the first InitDLL subsystem; the full link remains at 98 unresolved symbols. See [interface-dispatch startup](NativeAot-Interface-Dispatch-Startup.md).

Version 0.0.29 creates the actual upstream RuntimeInstance and empty ThreadStore in the guest, replacing the startup TEB assumption with kernel-confirmed TLS metadata. Both allocation failures roll back; attachment and real GC remain pending. See [RuntimeInstance startup](NativeAot-Runtime-Instance.md).

Version 0.0.30 supplies real GC/PAL process memory barriers under the single-online-CPU contract (ABI v14). Full-link unresolved symbols drop to 96; root enumeration, managed suspension and actual GC startup remain separate work. See [process barriers](NativeAot-Process-Barrier.md).

Version 0.0.31 provides actual CPUID cache-size discovery to the GC through ABI v15, with Intel/AMD validation and no invented fallback. The strict link has 95 unresolved symbols. See [CPU cache discovery](NativeAot-Cpu-Cache.md).

Version 0.0.32 adds real event-only PalCompatibleWaitAny through ABI v16, with up to four validated handles and atomic deadline/close completion. The full link has 94 unresolved symbols. See [WaitAny](NativeAot-Wait-Any.md).

Version 0.0.33 supplies real low-memory notification events to NativeAOT through ABI v17, driven by kernel physical/quota accounting. The full link has 93 unresolved symbols; actual finalizer/GC lifecycle remains pending. See [memory pressure](NativeAot-Memory-Pressure.md).

Version 0.0.35 adds native thread exit notification; 0.0.36 executes actual GC-special Thread record construction and selects an explicit disabled-StressLog profile. Actual ThreadStore attachment and collector startup remain pending.

Version 0.0.37 adds a minimal executable startup diagnostic rooted in the real wmain/normal CoreLib path. See [startup readiness](NativeAot-Startup-Readiness.md). Use that workload to drive integration and retain the broader M3 target as regression coverage.
