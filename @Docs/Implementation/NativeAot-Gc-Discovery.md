# ADR 0008: Allocator snapshots and GC environment discovery

**Status:** Implemented in WitOS 0.0.11; verified locally on 2026-09-17.
**Scope:** Experimental user ABI v6 introduced allocator snapshots and native GC discovery; [ABI v7](NativeAot-Gc-Time.md) retains that layout and adds monotonic deadlines. The collector itself still does not execute in the guest.

## Context and decision

The source-built runtime exposed missing initialization and memory-discovery methods. GC needs the component's actual limit and current consumption; reporting all guest RAM as the component's usable heap would contradict its 128-owned-frame quota.

Add a bounded, versioned snapshot syscall instead of a collection of individually sampled counters or a continuously updated shared page. The interrupt gate serializes snapshot creation, destination validation and copying with IF clear. No allocation, mapping or lifetime change is needed. The native adapter consumes this snapshot through the existing language-neutral ABI.

The startup prefix stays 24 bytes; its ABI version becomes 6. This is an evolving internal contract, not a public resource SDK.

## Memory query contract

Call 20: `MemoryQuery(buffer, size, version)`. The requested structure version is 1 and the exact size is 96 bytes. Success returns status 0 and result 96. Unsupported version returns `UNSUPPORTED`; any other size returns `INVALID_ARGUMENT`; an unwritable, unmapped or out-of-range destination returns `BAD_ADDRESS`. Failures return result zero and leave the entire destination unchanged. Version and size checks precede pointer validation.

The complete writable destination range is checked before copying through supervisor physical aliases. Cross-page buffers are supported. Readonly headers/startup/code, committed no-access pages, kernel addresses, gaps and overflowing ranges are rejected. The copy contract currently depends on one CPU and serialized mapping changes.

Authoritative structure: `src/Kernel/include/witos/memory_info.h`.

| Fields | Meaning |
| --- | --- |
| Version, Size | Snapshot layout, separate from the overall user ABI version |
| PageSize, ProcessorCount | 4 KiB pages; one online bootstrap processor usable by this backend |
| PhysicalTotalBytes, PhysicalAvailableBytes | Eligible allocator RAM and currently free frames; reserved firmware memory is excluded |
| OwnedLimitBytes, OwnedBytes | Component quota and all owned frames, including fixed mappings, stacks/TLS, image and private page tables |
| VirtualBase, VirtualBytes | Dynamic allocation arena base and capacity, separate from the fixed image/stack area |
| ReservedBytes, ReservationCount, ReservationCapacity | Address-space reservation accounting; reservations consume no backing frames |
| DynamicCommittedBytes | Dynamic backing frames, including committed no-access pages |
| PrivatePageTableBytes | Owned root and private paging structures; shared kernel mappings are excluded |

The snapshot is consistent at the call boundary. Free bytes are not a promise that a later commit succeeds: other allocations and additional page-table overhead may consume capacity. No physical addresses are exposed.

## Upstream GC mapping

All methods use the unchanged .NET 10.0.8 interface. The source audit now pins 32 files, adding `gcenv.windows.inl`: its real inline `GetPageSize` returns 4 KiB. Initialization checks that the kernel agrees; no competing out-of-line implementation is introduced.

- `Initialize` checks ABI v6 and snapshot version/shape, then initializes the upstream-shaped page/processor/allocation-granularity state. Initialization and shutdown are serialized by runtime startup/teardown, before/after worker-thread activity. There is no dynamic C++ initialization or allocator dependency.
- `Shutdown` clears that private environment state and owns no resources to free. `GetTotalProcessorCount` returns the initialized online count. NUMA-aware GC and Windows CPU groups are unavailable in the current kernel.
- `GetVirtualMemoryLimit` returns dynamic arena capacity (64 GiB); `GetVirtualMemoryMaxAddress` returns its exclusive upper address. These are intentionally different for an arena starting at 1 TiB.
- `GetPhysicalMemoryLimit` returns the smaller of allocator RAM and the owned-frame quota. The restriction flag indicates when the quota is the smaller bound.
- `GetMemoryStatus(0, ...)` reports global allocator pressure and free RAM. With a nonzero restriction, the effective limit is the minimum of that argument, allocator RAM and component quota; load uses all component-owned frames and is clamped to 100%. Available physical bytes are bounded by both remaining ownership capacity and actual free RAM.
- The page-file estimate is zero, following the upstream restricted-memory convention; WitOS has no paging store. Null output pointers are supported. Failed snapshot retrieval returns conservative zero availability/100% load, or zero for the limit getters.

The 512 KiB quota includes non-heap costs. It remains a prototype limit, not a claim that the real collector can initialize a useful heap within it. The currently single-CPU, 4 KiB, below-4-GiB allocator contract constrains this adapter.

The later [GC event extension](NativeAot-Gc-Events.md) implements polling/infinite waits and a private yielding gate. The [time extension](NativeAot-Gc-Time.md) now implements monotonic time and finite waits. Remaining native locks, thread attachment and fault/unwind delivery are incomplete. In particular, delivered PIT ticks are not supplied as a GC performance clock. The initial negative link rooted QueryPerformanceFrequency; it now roots VirtualReset after the time implementation.

## Validation and evidence

Local Release build, `runtime-port`, all 17 VM scenarios, `runtime-audit`, `runtime-probe` and `runtime-source` passed; the latter also refreshes `runtime-target`. Successful boots require 93 user groups and 33 contained user faults. Four new groups cover:

1. Environment initialization, repeat initialization, shutdown/reinitialization, processor/page/topology queries and both relocated image bases.
2. Kernel-verified baseline counters and changes after reserve, commit, protection, decommit and release, including private tables and no-access commitment.
3. Cross-page success, invalid version/size/address, and refusal of a buffer whose writable prefix is followed by an absent, readonly or no-access page. The prefix must remain unchanged on every rejection.
4. Execution using a separate allocator backed by 48 real pages borrowed exclusively from the main allocator. Physical memory is smaller than the ordinary ownership quota; both GC limits/pressure and complete teardown are checked against kernel accounting.

The local native fixture is 8,192 bytes with 16 ordinary unwind records and no OS/CRT imports. The full source-built archive still has 67 members; strict linking now reports 155 unresolved symbols, including 20 GC environment requirements (previously 162/27). Counts are local diagnostics, not a compatibility percentage. Implemented discovery methods must resolve; missing performance-clock and event methods must remain visible. The source-built Windows reference still passes its four GC/exception/TLS groups.

## Consequences and next work

GC discovery now observes the same ownership model that enforces memory operations. New page-table costs cannot disappear from pressure accounting, and no-access pages cannot masquerade as free memory. A future SMP backend must replace the current serialization contract before allowing concurrent mapping changes during copy-out.

GC event polling/infinite waits are now implemented. Monotonic time and finite waits are now implemented. Next implement remaining native locks, followed by runtime TLS/attachment and GC rendezvous. Keep quota/image/stack expansion tied to measured runtime requirements. Successful environment initialization is not NativeAOT `RhInitialize`, managed-module initialization or execution of the collector.
