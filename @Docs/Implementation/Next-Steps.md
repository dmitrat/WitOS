# Immediate development sequence

The core objective remains a minimal hardware-dependent kernel, a common system layer supporting upstream .NET, and applications/shells above .NET.

## Completed

- M0 independent boot and initial M1 memory/paging/timer/kernel-context mechanisms.
- Seventeen real VM scenarios, with 56 required M2 groups inside successful boots.
- M2 isolated ring-3 components, sparse memory with atomic allocation failure, four bounded threads, raw FS TLS and consuming join.
- M2 manual/auto-reset events, absolute tick deadlines, timeout/close completion and kernel idle.
- Experimental user ABI v4 covers the tested native mechanisms.
- Pinned .NET 10.0.8 source/package audit and hosted NativeAOT reference probe.
- NativeAOT target/bootstrap experiment: exact static ILC archive, PE/TLS/unwind inspection, real native C-host entry/GC/TLS checks and strict-link dependency boundaries.

See [isolation](M2-Isolated-Execution.md), [memory](M2-User-Memory.md), [threads/TLS](M2-User-Threads-and-Tls.md), [events/deadlines](M2-Events-and-Deadlines.md), [target/bootstrap evidence](NativeAot-Target-Bootstrap.md) and [RFC 0015](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).

Guest .NET does not run yet. The guest remains WitOS 0.0.7; the new target experiment runs on Windows.

## Next: guest image/metadata loading

1. Implement a bounded guest PE32+ loading contract with controlled native images: separate RX/RO/RW sections, zero-fill, relocations, entry validation and explicit rejection of unsupported imports/TLS.
2. Use the measured NativeAOT module requirements to define runtime module registration, bootstrap order, TLS and unwind boundaries. PE/COFF and Microsoft x64 are the initial candidate; the source-level runtime OS backend remains undecided.
3. Port the selected runtime/PAL/CoreLib platform paths with a recorded upstream patch set. Provide process-local GC rendezvous and validated runtime fault/context delivery.
4. Adapt memory/thread/wait APIs, expand commitment and stack limits, and add mechanisms when required by the real runtime profile.
5. Replace the delivered-PIT-tick clock with a suitable elapsed-time source before claiming full runtime monotonic timing.
6. Add IPC/capability transfer when a tested service boundary needs it.

The target experiment found Windows TLS callbacks/indexing, thousands of unwind entries and a mapped image already larger than the current frame quota. An absent CLR header or a successful hosted native call does not make that DLL a guest-ready image.

## M3 acceptance

Run a real NativeAOT component inside WitOS with allocations/GC, finalization, exceptions and thread activity. Hosted reference/native-bootstrap probes remain evidence only. CoreCLR and unchanged ordinary IL applications belong to the later M6 milestone.

## Current limits

One x64 CPU, QEMU q35, usable physical addresses below 4 GiB, two component slots and one active component. Each supports four threads, four events, eight shared handles, a 64 GiB dynamic virtual arena, eight reservations and 128 owned frames including page tables, user stacks and TLS. Kernel stacks remain in a fixed guarded pool.

PIC/PIT timing, baseline x87/SSE and the ten-tick test budget remain. The nominal 100 Hz clock advances only on delivered IRQ0 and pauses while that interrupt is disabled. Firmware memory is not automatically reclaimed.

No general loader, compiler/managed TLS, semaphores/mutexes/multi-object waits, IPC channels or managed runtime exists yet. Broad hardware, firmware flashing, GPU, custom filesystems, GUI and distributed orchestration do not block the next slice.
