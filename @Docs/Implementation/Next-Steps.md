# Immediate development sequence

The core objective remains a minimal hardware-dependent kernel, a common system layer supporting upstream .NET, and applications/shells above .NET.

## Completed

- M0 independent boot and initial M1 memory/paging/timer/kernel-context mechanisms.
- Seventeen real VM scenarios, now with 21 required M2 groups inside successful boots.
- First M2 slice: a separately built native ring-3 component, private address spaces and handles, query/write/exit/close calls, contained faults, safe return checks, time budgeting and teardown.
- Pinned .NET 10.0.8 source/package evidence and a hosted NativeAOT reference probe.
- RFC 0015 maps the runtime requirements to the remaining substrate work.

See [M2 implementation](M2-Isolated-Execution.md), [NativeAOT evidence](NativeAot-Host-Probe.md) and [RFC 0015](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).

Guest .NET does not run yet.

## Next: runtime-capable memory and execution

1. Add per-address-space virtual reservations, sparse commitment, decommit/recommit with zero-fill, protection and release.
2. Define recoverable allocation failure and ownership rules instead of kernel panics for ordinary resource exhaustion.
3. Extend fixed user activations into dynamic user threads, TLS and explicit exit/join.
4. Add blocking/waking and deadline semantics without lost wakeups.
5. Add scoped communication/capability transfer when required by a tested service boundary.
6. Provide process-local GC rendezvous/context operations and validated runtime fault delivery.
7. Select the actual guest NativeAOT ABI/backend through a porting experiment; record all upstream changes.

The current INT 0x80 ABI is a small experimental boundary. Preserve its tests while deliberately evolving it; do not treat the fixed test-image layout as a final executable format.

## M3 acceptance

Run the real NativeAOT component inside WitOS with allocations/GC, finalization, exceptions and thread activity. A passing hosted Windows probe remains reference evidence only. CoreCLR and unchanged ordinary IL applications belong to the later M6 milestone.

## Current limits

One x64 CPU, QEMU q35, usable physical addresses below 4 GiB, two fixed component slots with one user thread active at a time, PIC/PIT timing and baseline x87/SSE context state. Firmware memory is not automatically reclaimed. No general loader, user TLS, IPC channels or managed runtime exists yet.

Broad hardware, firmware flashing, GPU drivers, a custom filesystem, GUI, stores and distributed orchestration do not block the next slice.
