# Immediate development sequence

The core objective remains a minimal hardware-dependent kernel, a common system layer supporting upstream .NET, and applications/shells above .NET.

## Completed

- M0 boot and M1 paging/protection/timer/kernel-context foundation.
- Eighteen VM scenarios with 106 required user groups and 34 contained user faults in successful boots.
- M2 ring-3 isolation, sparse memory, bounded threads/raw FS TLS/join, events/deadlines and idle.
- Bounded guest PE loading with section protection, zero-fill, relocations and allocation rollback.
- ABI v7 retains the readonly image description and adds an atomic allocator snapshot; user-space C startup with checked callbacks, run-once state and reverse cleanup.
- Structural validation/exposure of ordinary x64 function/unwind metadata, without exception dispatch or stack walking.
- .NET 10.0.8 audit of 32 source/license files and two hosted NativeAOT evidence probes.
- Source-port direction selected: Windows x64 code generation/PE plus an explicit WitOS user-space adapter.
- GC memory and discovery methods compile against unchanged pinned headers and execute in ring 3; initialization validates ABI/snapshot data, and the GC clock/sleep hooks use IRQ-independent HPET time.
- GC events support manual/auto signals, poll/finite/infinite waits, bounded slot reuse and close cancellation; native contenders yield instead of preventing their owner from running.
- Full upstream nativeaot component built from the pinned source tree in separate Windows-reference and WitOS-overlay profiles; source-built Windows GC/TLS execution passes and incomplete WitOS dependencies are inventoried.

See [native module/bootstrap handoff](M2-Native-Module-Bootstrap.md), [NativeAOT target evidence](NativeAot-Target-Bootstrap.md), [backend decision and memory adapter](NativeAot-Gc-Memory-Port.md), [full native source build](NativeAot-Source-Build.md), [GC discovery](NativeAot-Gc-Discovery.md), [GC events](NativeAot-Gc-Events.md), [monotonic GC time](NativeAot-Gc-Time.md) and [RFC 0015](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).

WitOS 0.0.13 executes the native memory/discovery/event/time adapters in the guest. It still does not run .NET or its collector there.

## Next: extend the selected source port

1. Use the source-built archive's unresolved-symbol inventory to implement the remaining native locks and memory/reset semantics; connect runtime thread attachment/TLS to the tested WitOS lifecycle. Keep unimplemented methods unresolved rather than returning synthetic success.
2. Add module boundaries, fault/context delivery, unwinding and process-local GC rendezvous; expand image/stack/commit limits for the actual profile.
3. Keep ReadyToRun/TypeManager/GC-static/frozen-object/eager-constructor initialization inside the real user-space runtime. It already requires real GC support.
4. Extend the explicit q35 clock profile to discovered hardware when broadening platform support; keep monotonic counts distinct from UTC and legacy IRQ ticks.
5. Add IPC/capability transfer when a tested service boundary needs it.

The image descriptor, native initializer helper and GC OS adapters are separate building blocks. They are not NativeAOT module registration, a CLR initializer or a Windows CRT/TLS implementation.

## M3 acceptance

Run a real NativeAOT component inside WitOS with allocations/GC, finalization, exceptions and thread activity. Hosted probes remain reference evidence only. CoreCLR and unchanged ordinary IL applications belong to the later M6 milestone.

## Current limits

One x64 CPU, QEMU q35, physical usable addresses below 4 GiB, two component slots and one active component. Each supports four threads, four events, eight handles, eight dynamic reservations and 128 owned frames. Dynamic VA spans 64 GiB; PE images use a separate window with a 256 KiB cap.

The PE profile permits up to sixteen sections and 128 plain version-1 unwind records. DLL/import/TLS/handler/chained semantics remain unsupported. Kernel stacks use a fixed guarded pool; x87/SSE context state and the ten-tick activation budget remain. HPET supplies monotonic time; PIT supplies scheduling and the legacy delivered-tick domain. Firmware memory is not automatically reclaimed.

No managed runtime, compiler/managed TLS, exception unwinder, general Windows loader, multi-object waits or IPC channels exist yet. Broad hardware, firmware flashing, GPU, filesystems, GUI and distributed orchestration do not block the next adapter experiment.
