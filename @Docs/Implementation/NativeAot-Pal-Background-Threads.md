# ADR 0018: Detached PAL worker threads

**Status:** Implemented in WitOS 0.0.21; user ABI v12.
**Date:** 2026-09-20.
**Scope:** Actual native callbacks launched through the upstream PAL background/finalizer/helper signatures, with C++ TLS cleanup and automatic resource reclamation. No collector, managed finalizer, ThreadStore attachment or guest .NET execution is claimed.

## ABI v12: Detached creation

ThreadCreate, call 9, accepts flags zero for the existing joinable behavior or WIT_THREAD_DETACHED (bit 0). Other bits, including high 64-bit bits, are invalid. Detached success returns zero rather than an external join handle; failures also leave the result zero.

The kernel still allocates a generation-bearing identity while the thread is live. That handle has no JOIN right. ThreadCurrent/ThreadQuery may reveal the borrowed identity, but joining it is denied and closing it while live is busy. When the thread exits, the kernel automatically closes its identity and frees its user stack, raw FS TLS and compiler GS TLS. Its slot becomes reusable with a fresh generation. Startup and existing snapshot layouts are unchanged.

Automatic reaping occurs after the user exit syscall is entered, on the thread's fixed kernel stack. Only component-owned user pages are freed; kernel stacks remain in their fixed pool. The dispatcher selects another saved context or finishes the component if no runnable/parked threads remain. It never returns to the freed user stack/TLS.

Unknown flags, invalid code entry, thread/handle capacity and allocation failure are checked before publication. Partial creation rolls back pages and its private handle. The original joinable path retains its previous ownership and consuming-join behavior.

## User-space and PAL lifecycle

The native thread helper adds a detached variant while retaining the existing joinable API. Both use the same bounded start-record publication/copy-out gate, compiler TLS entry and TLS-aware exit. A detached callback therefore runs constructors first and destructors before the kernel sees its exit.

`pal_threads.witos.cpp` implements the unchanged pinned Pal.h declarations:

- PalStartBackgroundGCThread
- PalStartFinalizerThread
- PalStartEventPipeHelperThread

Each validates the callback's executable image range, publishes its pointer/context under a yielding gate and invokes the detached native helper. A small adapter converts the upstream uint32 callback result to the native exit value. Startup/copy-out and rollback are serialized; records are released before invoking arbitrary callbacks. Invalid callbacks or unavailable resources return false. A true return means creation succeeded, not that the callback has already completed. The caller keeps the context alive for its use by the worker.

This one-CPU prototype has one scheduling class and a fixed 16 KiB user stack per thread. Finalizer startup does not claim priority promotion or support the Windows default-stack-size configuration. Actual runtime stack/quota requirements still need integration work. There is no cancellation, detached join or cross-process thread control.

Orderly callback return and explicit native thread exit run TLS cleanup. A constructor/callback/destructor failure still aborts the component; the kernel then reclaims the whole address space and handles. It does not run further user destructors during abrupt termination.

## Guest evidence

The fixture defines a real C++ thread_local object that allocates in its constructor and frees in its destructor. A handshake keeps cleanup active until the parent observes the private handle as busy; only after cleanup completes may it become stale. All three PAL entrypoints are exercised.

Five required groups cover:

- PalBackgroundLifecycle: twelve serial workers using all three entrypoints, normal callback return, generation changes, no join right, live close rejection, TLS cleanup and unchanged allocator snapshots. Also runs at a relocated image base.
- PalBackgroundCapacity: three live workers fill the remaining thread slots; repeated failed creation does not leak start records; release/reaping restores capacity. One callback uses explicit TLS-aware thread exit.
- PalBackgroundRollback: fail each of six child stack/FS/GS allocations under actual frame pressure, using every PAL entrypoint. No callback runs on failure; snapshots recover and a subsequent worker succeeds.
- DetachedLastExit: the main thread exits orderly first, then the final detached worker runs/cleans up and completes the component with its own exit code. Both main and worker TLS cleanup are verified.
- PalBackgroundIsolation: real callback and destructor hardware faults, plus constructor fail-fast, occur in a live detached thread. Component teardown recovers every frame and a fresh component succeeds.

The supervisor checks detached create/reap counts, zero joins, handles/events, reservation and owned-frame recovery, fault identity and selectors. Successful boots require 147 user groups and 49 contained user faults.

The local fixture is 10,752 bytes with 34 plain unwind records and no Windows/CRT imports. The build reuses verified objects produced earlier in the same image-build pipeline and records their hashes alongside local inputs in pal-background-build.json. Kernel CI uploads that report and PalBackgroundFixture.pe.

Release build, runtime-port, the 41-file source audit and hosted reference probe passed. Full runtime-source (including runtime-target) passed all four Windows reference groups. The full VM suite passed all 18 scenarios, with 147 required user groups and 49 contained user faults in successful boots, including both RAM profiles and the expected timeout.

## Source integration and next boundary

Runtime.WorkstationGC now includes pal_threads.witos.cpp and the native thread wrapper, with C17 explicitly selected for the C source in the target directory. Both objects are verified byte-for-byte in the archive. Upstream sources remain unchanged and the Windows reference retains its own implementation.

The Workstation archive has 74 members. The strict link leaves 114 unresolved symbols: seven GC environment, 22 PAL, five deliberately excluded guest transport/startup helpers and 80 other platform/runtime requirements. Two formerly unresolved entrypoints resolve; the EventPipe helper is implemented/tested as well but was not an unresolved root of the selected diagnostic workload.

Next complete PAL initialization/last-error/handle semantics, module registration and GC coordination needed for the real runtime. Full ThreadStore teardown still calls real GC allocation-context cleanup. These native worker APIs provide the execution substrate, not evidence that managed GC/finalization already runs in WitOS.

Version 0.0.22 adds [creator-local error reporting and worker last-error isolation](NativeAot-Pal-Last-Error.md), including real C++ TLS constructors/destructors.
