# Immediate development sequence

The core objective remains a minimal hardware-dependent kernel, a common system layer supporting upstream .NET, and applications/shells above .NET.

## Completed

- M0 independent boot and initial M1 memory/paging/timer/kernel-context mechanisms.
- Seventeen real VM scenarios, now with 56 required M2 groups inside successful boots.
- M2 isolation: separately built native ring-3 components, private address spaces and handles, contained faults, safe returns and teardown.
- M2 memory: sparse reservations, zero-filled commit/recommit, protection, release and atomic rollback on allocation failure.
- M2 threads: four thread slots, timer/yield dispatch, guarded stacks, raw FS TLS, exit and consuming join with cycle rejection.
- M2 waits: manual/auto-reset events, persistent signals, atomic parking, timeout/close results, absolute tick deadlines, sleep and kernel HLT idle.
- Experimental user ABI v4 covers the tested native mechanisms.
- Pinned .NET 10.0.8 source/package evidence and a hosted NativeAOT reference probe.

See [isolation](M2-Isolated-Execution.md), [memory](M2-User-Memory.md), [threads/TLS](M2-User-Threads-and-Tls.md), [events/deadlines](M2-Events-and-Deadlines.md), [NativeAOT evidence](NativeAot-Host-Probe.md) and [RFC 0015](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).

Guest .NET does not run yet.

## Next: actual NativeAOT target and bootstrap evidence

1. Select a concrete guest NativeAOT ABI/backend through a reproducible experiment with the pinned upstream sources/compiler. Inventory generated objects, required native symbols, relocations, module metadata, TLS and unwind behavior.
2. Build the minimal guest loader/bootstrap/runtime adapter demanded by that evidence. Record all upstream changes and keep hosted reference results separate from guest execution.
3. Provide process-local GC rendezvous/context operations and validated runtime fault delivery.
4. Adapt GC/PAL memory and synchronization interfaces, expanding quotas and mechanisms only when the actual runtime profile requires them.
5. Replace the delivered-PIT-tick clock with a suitable elapsed-time source before claiming full runtime monotonic timing. Current absolute-deadline tests establish ordering, not wall-time accuracy.
6. Add IPC/capability transfer only when a tested service boundary needs it.

Preserve executable tests while evolving the INT 0x80 boundary. Raw TLS pages and fixed test-image layouts are not final compiler/executable contracts.

## M3 acceptance

Run a real NativeAOT component inside WitOS with allocations/GC, finalization, exceptions and thread activity. A passing hosted Windows probe remains reference evidence only. CoreCLR and unchanged ordinary IL applications belong to the later M6 milestone.

## Current limits

One x64 CPU, QEMU q35, usable physical addresses below 4 GiB, two component slots and one active component. Each supports four threads, four events, eight shared handles, a 64 GiB dynamic virtual arena, eight reservations and 128 owned frames including page tables, user stacks and TLS. Kernel stacks remain in a fixed guarded pool.

PIC/PIT timing, baseline x87/SSE and the ten-tick test budget remain. The nominal 100 Hz clock advances only on delivered IRQ0 and pauses while that interrupt is disabled. Firmware memory is not automatically reclaimed.

No general loader, compiler/managed TLS, semaphores/mutexes/multi-object waits, IPC channels or managed runtime exists yet. Broad hardware, firmware flashing, GPU, custom filesystems, GUI and distributed orchestration do not block the next slice.
