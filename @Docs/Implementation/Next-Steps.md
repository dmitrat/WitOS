# Immediate development sequence

The core objective remains a minimal hardware-dependent kernel, a common system layer supporting upstream .NET, and applications/shells above .NET.

## Completed

- M0 independent boot and initial M1 memory/paging/timer/kernel-context mechanisms.
- Seventeen real VM scenarios, now with 42 required M2 groups inside successful boots.
- M2 isolation: separately built native ring-3 components, private address spaces and handles, contained faults, safe return checks, time budgeting and teardown.
- M2 memory: sparse reservations, commit/decommit/recommit with zero-fill, protection, release and recoverable allocation failure with full rollback.
- M2 threads: up to four threads per component, timer/yield dispatch, separate guarded stacks, raw FS-based TLS, thread exit, consuming join, cycle rejection and resource reaping.
- Experimental user ABI v3 covers query/write/exit/close, memory and thread operations.
- Pinned .NET 10.0.8 source/package evidence and a hosted NativeAOT reference probe.

See [M2 isolation](M2-Isolated-Execution.md), [memory](M2-User-Memory.md), [thread/TLS decision and tests](M2-User-Threads-and-Tls.md), [NativeAOT evidence](NativeAot-Host-Probe.md) and [RFC 0015](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).

Guest .NET does not run yet.

## Next: waits, deadlines and runtime execution

1. Add general wait/wake objects with persistent signal state and atomic check-and-park; define timeout and close semantics without lost wakeups.
2. Add a monotonic clock/deadline contract and an idle path when no user thread is ready.
3. Add scoped communication/capability transfer when required by a tested service boundary.
4. Provide process-local GC rendezvous/context operations and validated runtime fault delivery.
5. Select the actual guest NativeAOT ABI/backend through a porting experiment; implement module/compiler TLS and record all upstream changes.
6. Adapt the GC/PAL memory interface to the tested primitives, expand commitment limits and implement additional semantics required by that profile.

Preserve executable tests while evolving the experimental INT 0x80 boundary. The raw TLS page and fixed test-image layout are not final compiler/executable contracts.

## M3 acceptance

Run the real NativeAOT component inside WitOS with allocations/GC, finalization, exceptions and thread activity. A passing hosted Windows probe remains reference evidence only. CoreCLR and unchanged ordinary IL applications belong to the later M6 milestone.

## Current limits

One x64 CPU, QEMU q35, usable physical addresses below 4 GiB, two component slots and one active component. Each supports four thread slots, a 64 GiB dynamic virtual arena, eight reservations and a 128-frame ownership quota including page tables, user stacks and TLS. Kernel stacks remain in a fixed guarded pool. PIC/PIT timing, baseline x87/SSE state and a ten-tick test budget remain in use.

Firmware memory is not automatically reclaimed. No general loader, compiler/managed TLS, general wait objects, IPC channels or managed runtime exists yet.

Broad hardware, firmware flashing, GPU drivers, a custom filesystem, GUI, stores and distributed orchestration do not block the next slice.
