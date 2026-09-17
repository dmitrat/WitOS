# Immediate development sequence

The core objective remains a minimal hardware-dependent kernel, a common system layer supporting upstream .NET, and applications/shells above .NET.

## Completed

- M0 independent boot and initial M1 memory/paging/timer/kernel-context mechanisms.
- Seventeen VM scenarios, with 72 required M2 groups inside successful boots.
- M2 isolated ring-3 components, sparse memory, bounded threads/raw FS TLS/join and events/deadlines/idle.
- M2 guest PE32+ loading: protected sections, zero-filled data, bounded DIR64 fixups, entry validation and rollback of unpublished components.
- Experimental user ABI v4 remains unchanged by the internal image-creation API.
- Pinned .NET 10.0.8 source/package audit, hosted semantic probe and native C-host bootstrap/target experiment.

See [isolation](M2-Isolated-Execution.md), [memory](M2-User-Memory.md), [threads/TLS](M2-User-Threads-and-Tls.md), [events](M2-Events-and-Deadlines.md), [guest PE loading](M2-Pe-Image-Loading.md), [NativeAOT target evidence](NativeAot-Target-Bootstrap.md) and [RFC 0015](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).

Guest WitOS is now 0.0.8. It executes native PE images, but .NET does not run inside it yet.

## Next: module bootstrap and runtime backend

1. Select the source-level NativeAOT runtime/PAL/CoreLib backend and record the required patch set. Microsoft x64 and PE/COFF are the measured initial format/call candidate, not a Windows compatibility promise.
2. Define module registration and bootstrap order using the measured NativeAOT sections, code boundaries and class-library callbacks.
3. Add compiler/runtime TLS and unwind metadata/fault handling only with explicit initialization and lifetime contracts. Current PE loading rejects those directories.
4. Provide process-local GC rendezvous/context operations; adapt memory/thread/wait behavior and expand image/stack/commit limits for the actual runtime profile.
5. Replace delivered-PIT-tick timing with a suitable elapsed-time source before claiming full runtime monotonic timing.
6. Add IPC/capability transfer when a tested service boundary needs it.

The hosted module's Windows imports, TLS callbacks/indexing, unwind entries and image size remain real blockers. The new loader establishes section/relocation/protection mechanics, not those missing runtime semantics.

## M3 acceptance

Run a real NativeAOT component inside WitOS with allocations/GC, finalization, exceptions and thread activity. Hosted probes remain reference evidence only. CoreCLR and unchanged ordinary IL applications belong to the later M6 milestone.

## Current limits

One x64 CPU, QEMU q35, usable physical addresses below 4 GiB, two component slots and one active component. Each supports four threads, four events, eight handles, eight dynamic reservations and 128 owned frames. Dynamic VA spans 64 GiB; native PE images have a separate window, a 256 KiB mapped-size cap and a restricted directory/relocation profile. Kernel stacks remain in a fixed guarded pool.

PIC/PIT timing, baseline x87/SSE and the ten-tick test budget remain. The clock advances only on delivered IRQ0. Firmware memory is not automatically reclaimed.

No general Windows/DLL loader, compiler/managed TLS, semaphores/mutexes/multi-object waits, IPC channels or managed runtime exists yet. Broad hardware, firmware flashing, GPU, custom filesystems, GUI and distributed orchestration do not block the next slice.
