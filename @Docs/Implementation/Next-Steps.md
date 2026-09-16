# Immediate development sequence

The core objective remains a minimal hardware-dependent kernel, a common system layer supporting upstream .NET, and applications/shells built above .NET.

## Completed

- M0: independent x64 boot and automated VM execution.
- Initial M1: physical pages, protected kernel mappings, guarded stacks, CPU diagnostics, timer interrupts and two preempted kernel contexts.
- M1 regression suite: seventeen real VM scenarios.
- Runtime evidence gate: pinned .NET 10.0.8 source/package provenance, SHA-256 audit of 22 selected files, a real hosted NativeAOT binary and six semantic probe groups.
- RFC 0015 maps the observed runtime requirements to concrete kernel gaps.

See [M1](M1-Kernel-Core.md), [NativeAOT host evidence](NativeAot-Host-Probe.md) and [RFC 0015](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).

Guest .NET support and M2 isolation do not exist yet.

## Next: M2 isolated native execution

Follow [the M2 implementation slice](M2-Isolated-Execution-Plan.md):

1. Separate user address space and guarded user/kernel stacks.
2. Controlled native image running in ring 3.
3. Minimal versioned query/output/exit call boundary and explicit console authority.
4. Contained user faults, checked user buffers and handle validation.
5. Resource teardown, zero-fill on reuse and timer control of uncooperative execution.

Commit syscall details with executable tests rather than freezing an anticipated full OS API.

## Before M3

Implement the runtime substrate identified in RFC 0015: reserve/commit/decommit/release, dynamic threads and TLS, waits/deadlines, GC rendezvous and managed fault delivery. Select the actual guest runtime ABI/backend through a porting experiment and track all upstream changes.

M3 must run a real NativeAOT component in WitOS, exercising GC, exceptions and threading. CoreCLR and unchanged ordinary IL applications remain the later M6 milestone.

## Current limits

One x64 CPU; QEMU q35; usable physical addresses below 4 GiB; one kernel address space; PIC/PIT timer; baseline x87/SSE state; no automatic firmware-memory reclamation. Bootstrap mapping/allocation APIs are serialized internal mechanisms, not user capabilities.

Broad hardware, firmware flashing, GPU drivers, a custom filesystem, GUI, stores and distribution do not block the next slice.
