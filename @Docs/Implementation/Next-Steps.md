# Immediate development sequence

The core objective remains a minimal hardware-dependent kernel, a common system layer supporting upstream .NET, and applications/shells above .NET.

## Completed

- M0 boot and M1 paging/protection/timer/kernel-context foundation.
- Eighteen VM scenarios with 162 required user groups and 51 contained user faults in successful boots.
- M2 ring-3 isolation, sparse memory, bounded threads/raw FS TLS/join, events/deadlines and idle.
- Bounded guest PE loading with section protection, zero-fill, relocations and allocation rollback.
- ABI v14 adds a process data-memory barrier for the single online CPU, retains readonly image descriptions, allocator snapshots, monotonic deadlines and kernel-owned thread identity, and provides committed-memory reset, atomic current-thread discovery, a truthful yield result, detached thread creation and per-thread native error storage; user-space C startup with checked callbacks, run-once state and reverse cleanup.
- Structural validation/exposure of ordinary x64 function/unwind metadata, without exception dispatch or stack walking.
- .NET 10.0.8 audit of 58 source/license files and two hosted NativeAOT evidence probes.
- Source-port direction selected: Windows x64 code generation/PE plus an explicit WitOS user-space adapter.
- GC memory and discovery methods compile against unchanged pinned headers and execute in ring 3; initialization validates ABI/snapshot data, and the GC clock/sleep hooks use IRQ-independent HPET time.
- GC events support manual/auto signals, poll/finite/infinite waits, bounded slot reuse and close cancellation; native contenders yield instead of preventing their owner from running.
- Recursive minipal mutexes and checked Release Crst execute in the guest, with real blocking, ownership checks, bounded reuse and fail-fast cleanup.
- GC VirtualReset validates the entire range before eagerly discarding data, retaining backing/commitment/protection even under quota pressure; see [the exact contract](NativeAot-Gc-Reset.md).
- Bounded native C++ nothrow new/delete executes in the guest and resolves in the source-built runtime archive; see [native allocation](NativeAot-Native-Heap.md).
- Static single-module compiler TLS executes in ring 3 with validated PE templates, separate GS pages, preemption/idle restoration and thread rollback/reuse; see [compiler TLS](NativeAot-Compiler-Tls.md).
- Actual C++ thread_local constructors/destructors run through a bounded user-space lifecycle, including eager/lazy entry, normal/explicit thread exit and failure containment; see [dynamic TLS](NativeAot-Dynamic-Tls.md).
- Four actual NativeAOT PAL thread-discovery functions execute against the kernel-owned thread snapshot; the WitOS source profile now replaces the Windows PAL with an explicitly incomplete adapter. See [PAL boundary and GC dependency](NativeAot-Pal-Thread-Discovery.md).
- PAL memory allocation/protection/free, unnamed events, non-alertable single-event waits, close, sleep and truthful yield execute in the guest; see [memory and waits](NativeAot-Pal-Memory-and-Waits.md).
- Detached native callbacks start through all three PAL background/finalizer/helper entrypoints, run C++ TLS cleanup and automatically reclaim their stack/TLS/private identity; see [PAL workers](NativeAot-Pal-Background-Threads.md).
- Native last-error is thread-local before compiler TLS, binds the real upstream direct/import symbols and reports failures across the implemented PAL methods; see [the error contract](NativeAot-Pal-Last-Error.md).
- PAL module lookup and inclusive bounds use the shared checked image handoff before native TLS constructors and across worker reuse; see [module discovery](NativeAot-Pal-Module-Discovery.md).
- Immutable native environment tables and PAL UTF-16/UTF-8 conversion execute before/during TLS and worker lifecycles; see [configuration transport](NativeAot-Pal-Environment.md).
- The dedicated runtime-config probe executes real RhConfig/GCConfig methods, precedence/default/refresh/enumeration semantics, checked string OOM paths and a C-locale CRT slice with thread-local errno; see [configuration execution](NativeAot-Runtime-Configuration.md).
- PalInit now executes real GCConfig/GC OS initialization, requires published environment and compiler TLS, enforces the single-CPU policy and caches readiness without resetting refreshed configuration; see [PAL initialization](NativeAot-Pal-Initialization.md).
- Native process callbacks execute after TLS cleanup, with LIFO, bounded re-registration and checked upstream atexit failure; see [process exit](NativeAot-Process-Exit.md).
- Actual InterfaceDispatch_Initialize and AllocHeap execute with OOM/retry, native worker allocations and checked lock cleanup; see [interface-dispatch startup](NativeAot-Interface-Dispatch-Startup.md).
- Actual RuntimeInstance and empty ThreadStore creation execute with both allocation-failure paths, adapted kernel-confirmed TLS metadata and native worker visibility; see [runtime instance startup](NativeAot-Runtime-Instance.md).
- GCToOSInterface and PAL process write-buffer flushes execute through the UP kernel fence; argument/error/TLS/thread behavior is tested and SMP remains unsupported. See [process memory barriers](NativeAot-Process-Barrier.md).
- Full upstream nativeaot component built from the pinned source tree in separate Windows-reference and WitOS-overlay profiles; source-built Windows GC/TLS execution passes and incomplete WitOS dependencies are inventoried.

See [native module/bootstrap handoff](M2-Native-Module-Bootstrap.md), [NativeAOT target evidence](NativeAot-Target-Bootstrap.md), [backend decision and memory adapter](NativeAot-Gc-Memory-Port.md), [full native source build](NativeAot-Source-Build.md), [GC discovery](NativeAot-Gc-Discovery.md), [GC events](NativeAot-Gc-Events.md), [monotonic GC time](NativeAot-Gc-Time.md), [native mutexes](NativeAot-Mutexes.md) and [RFC 0015](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).

WitOS 0.0.30 executes the native memory/reset/discovery/event/time, minipal/Crst and nothrow allocation adapters, plus native PAL environment/string services and bounded native process cleanup, in the guest. The separate runtime-config command adds fifteen groups to each of two guest boots, exercising upstream configuration, native PAL initialization, interface-dispatch allocation and RuntimeInstance/empty ThreadStore creation and GC/PAL data-memory barriers. It still does not run managed .NET or its collector there.

The [BootTo.NET source review](BootToNET-Review.md) keeps the upstream runtime direction and existing WitOS runner. Its immediate result is expanded hosted root/unwind coverage; it recommends an explicit minimal bring-up profile for the next full startup workload.

## Next: extend the selected source port

See [the M3 work-package estimate](M3-Runtime-Integration-Plan.md): roughly eight major packages / 12-20 bounded slices estimated at 0.0.15; native allocation now completes part of the first package.

1. Continue through actual RhInitialize/InitDLL after the implemented PalInit: use the implemented native exit registry and define the explicit WitOS handling of Windows startup/diagnostic paths, then advance hardware exception integration and real GC initialization. Interface-dispatch, selected GC-event/restricted-callout initialization and RuntimeInstance/empty ThreadStore creation now execute; the Windows TEB dependency in startup TLS metadata is adapted. Actual ThreadStore attachment and detach remain pending. Module lookup already supplies image identity. Use the explicit PAL inventory to complete startup/thread/handle and GC coordination services for real runtime and GC initialization, then connect actual ThreadStore attachment/shutdown to the tested TLS lifecycle. Full detach already calls GC FixAllocContext, so do not substitute a fake collector to claim lifecycle completion. Complete remaining CRT/PAL behavior and extend memory semantics as required by the runtime profile. Committed reset is implemented; working-set unlock and large pages remain unsupported. Keep unimplemented methods unresolved rather than returning synthetic success.
2. Connect the implemented native image bounds to actual runtime module registration; add fault/context delivery, unwinding and process-local GC rendezvous. Expand image/stack/commit limits for the actual profile.
3. Keep ReadyToRun/TypeManager/GC-static/frozen-object/eager-constructor initialization inside the real user-space runtime. It already requires real GC support.
4. Extend the explicit q35 clock profile to discovered hardware when broadening platform support; keep monotonic counts distinct from UTC and legacy IRQ ticks.
5. Add IPC/capability transfer when a tested service boundary needs it.

The image descriptor, native initializer helper and GC OS adapters are separate building blocks. They are not NativeAOT module registration, a CLR initializer or a Windows CRT/TLS implementation.

## M3 acceptance

Run a real NativeAOT component inside WitOS with allocations/GC, finalization, exceptions and thread activity. Hosted probes remain reference evidence only. CoreCLR and unchanged ordinary IL applications belong to the later M6 milestone.

## Current limits

One x64 CPU, QEMU q35, physical usable addresses below 4 GiB, two component slots and one active component. Each supports four threads, four events, eight handles, eight dynamic reservations and 128 owned frames. The native heap uses one lazy 256 KiB arena, 128 allocation descriptors and 16-byte alignment; its limits still need expansion for full runtime integration. The native mutex registry supports sixteen locks; contended locks retain one shared-quota event until destruction. Dynamic VA spans 64 GiB; PE images use a separate window with a 256 KiB cap. The immutable native environment supports sixteen entries with ASCII names up to 63 units and UTF-16 values up to 1,023 units; the separate UTF-8 copy helper accepts up to 32,767 UTF-16 units.

The PE profile permits up to sixteen sections and 128 plain version-1 unwind records. One static TLS module with at most 3,840 bytes per thread is supported; TLS callbacks, DLL/import/handler/chained semantics remain unsupported. Kernel stacks use a fixed guarded pool; x87/SSE context state and the ten-tick activation budget remain. HPET supplies monotonic time; PIT supplies scheduling and the legacy delivered-tick domain. Firmware memory is not automatically reclaimed.

No managed runtime, managed TLS/ThreadStore attachment, exception unwinder, general Windows loader, multi-object waits or IPC channels exist yet. Broad hardware, firmware flashing, GPU, filesystems, GUI and distributed orchestration do not block the next adapter experiment.
