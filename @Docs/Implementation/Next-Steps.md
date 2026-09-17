# Immediate development sequence

The core objective remains a minimal hardware-dependent kernel, a common system layer supporting upstream .NET, and applications/shells above .NET.

## Completed

- M0 boot and M1 paging/protection/timer/kernel-context foundation.
- Seventeen VM scenarios with 89 required user groups and 33 contained user faults in successful boots.
- M2 ring-3 isolation, sparse memory, bounded threads/raw FS TLS/join, events/deadlines and idle.
- Bounded guest PE loading with section protection, zero-fill, relocations and allocation rollback.
- ABI v5 readonly image description; user-space C startup with checked callbacks, run-once state and reverse cleanup.
- Structural validation/exposure of ordinary x64 function/unwind metadata, without exception dispatch or stack walking.
- .NET 10.0.8 audit of 31 source/license files and two hosted NativeAOT evidence probes.
- Source-port direction selected: Windows x64 code generation/PE plus an explicit WitOS user-space adapter.
- First `GCToOSInterface` memory slice compiled against unchanged pinned headers and executed in ring 3; unsupported GC initialization still fails strict linking.

See [native module/bootstrap handoff](M2-Native-Module-Bootstrap.md), [NativeAOT target evidence](NativeAot-Target-Bootstrap.md), [backend decision and memory adapter](NativeAot-Gc-Memory-Port.md) and [RFC 0015](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).

WitOS 0.0.10 executes the native memory adapter in the guest. It still does not run .NET or its collector there.

## Next: extend the selected source port

1. Establish the pinned full native-runtime source build and explicit source overlay/build selection. The current reproducible recipe compiles only five GC OS methods, not the collector or full runtime.
2. Implement real GC environment initialization, memory/CPU discovery and synchronization; connect runtime thread attachment/TLS to the tested WitOS lifecycle. Keep unimplemented methods unresolved rather than returning synthetic success.
3. Add module boundaries, fault/context delivery, unwinding and process-local GC rendezvous; expand image/stack/commit limits for the actual profile.
4. Keep ReadyToRun/TypeManager/GC-static/frozen-object/eager-constructor initialization inside the real user-space runtime. It already requires real GC support.
5. Replace delivered-PIT-tick timing with a suitable elapsed-time source before claiming full runtime timing.
6. Add IPC/capability transfer when a tested service boundary needs it.

The image descriptor, native initializer helper and memory adapter are separate building blocks. They are not NativeAOT module registration, a CLR initializer or a Windows CRT/TLS implementation.

## M3 acceptance

Run a real NativeAOT component inside WitOS with allocations/GC, finalization, exceptions and thread activity. Hosted probes remain reference evidence only. CoreCLR and unchanged ordinary IL applications belong to the later M6 milestone.

## Current limits

One x64 CPU, QEMU q35, physical usable addresses below 4 GiB, two component slots and one active component. Each supports four threads, four events, eight handles, eight dynamic reservations and 128 owned frames. Dynamic VA spans 64 GiB; PE images use a separate window with a 256 KiB cap.

The PE profile permits up to sixteen sections and 128 plain version-1 unwind records. DLL/import/TLS/handler/chained semantics remain unsupported. Kernel stacks use a fixed guarded pool; x87/SSE context state and the ten-tick activation budget remain. The clock counts delivered IRQ0 only. Firmware memory is not automatically reclaimed.

No managed runtime, compiler/managed TLS, exception unwinder, general Windows loader, multi-object waits or IPC channels exist yet. Broad hardware, firmware flashing, GPU, filesystems, GUI and distributed orchestration do not block the next adapter experiment.
