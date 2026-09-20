# ADR 0012: Committed memory reset for the GC adapter

**Status:** Implemented in WitOS 0.0.15.
**Date:** 2026-09-20.
**Scope:** Native GCToOSInterface::VirtualReset and user ABI v9. No guest collector or managed runtime execution.

## Contract and decision

The pinned upstream `src/coreclr/gc/env/gcenv.os.h` and `src/coreclr/gc/windows/gcenv.windows.cpp` define reset as discarding uninteresting data without decommitting the range. WitOS implements an eager discard: zero the existing backing pages, retaining commitment, reservation, physical ownership and access protection. This is real memory behavior; it does not pretend to release physical RAM or implement demand paging.

A decommit/recommit sequence is unsuitable: it would expose an intermediate hole, could fail on recommit, and could lose original protection. An unchecked user-space memset would fault on protected pages or partially modify a range before discovering a hole. The kernel can validate all owned mappings and zero their existing physical backing with interrupts disabled on the supported single CPU.

## ABI v9

Call 26, `MemoryReset(base, size, flags=0)`, accepts a nonempty page-aligned range contained in one dynamic reservation. Fixed images/stacks, other components and supervisor mappings are outside this authority. The result register is zero on success and failure.

- Nonzero flags, zero size or misalignment: `INVALID_ARGUMENT`.
- Outside the dynamic arena or address overflow: `BAD_ADDRESS`.
- Range not contained in one live reservation: `NOT_RESERVED`.
- Any uncommitted page, or more pages than the 128-frame ownership quota permits: `NOT_COMMITTED`.

Validate the complete range before changing bytes. Failures leave data and all memory accounting unchanged. Success zeroes only the selected pages, including committed read-only/no-access pages, without changing PTEs, access rights, page-table ownership, reservations or accounting. No new frames are required, including at commitment exhaustion. The operation is serialized with IF clear and bounded by the prototype quota. There is no deferred discard or asynchronous reclamation.

The ABI version becomes 9; startup remains 24 bytes and the memory-information layout remains unchanged.

## Upstream adapter

`VirtualReset` follows the existing adapter's address and size policy: reject null/unaligned addresses, zero length and arithmetic overflow; round nonzero lengths upward to 4 KiB. It calls the generic reset syscall and returns its success status. Upstream declarations and pinned source files remain unchanged.

`unlock=true` returns false **before any mutation**. WitOS has no working-set lock/unlock API; this does not claim that feature is implemented. Callers must own/synchronize discarded data as required by the GC contract. Reset is not a GC rendezvous or synchronization primitive.

## Evidence

Two required guest groups are added:

- `User.GcMemoryReset`: run at preferred and relocated image bases; verify eager discard, adjacent-page preservation, size rounding, invalid options/ranges, an interior uncommitted hole, unchanged allocator snapshots, no-access commitments, exhaustion without new allocation, repeated reset, recommit preservation and release/teardown.
- `User.GcResetProtection`: actual write faults after resetting read-only and no-access pages, with expected vector/error/address/selectors, followed by a successful fresh component.

The host runner requires 115 user groups and 36 contained user faults in successful boots. It validates markers and VM outcome independently. The native fixture has no OS/CRT imports; the local build is 20,992 bytes with 58 ordinary unwind records.

The strict fixture link now deliberately roots the still-unimplemented `GCToOSInterface::FlushProcessWriteBuffers`, and requires exactly that unresolved-symbol failure. Full source-built WitOS archives resolve VirtualReset and retain 141 unresolved symbols: seven GC-environment requirements, four deliberately excluded guest transport symbols and 130 other platform/runtime requirements. The dependency count is diagnostic, not a completion percentage.

Release build, source audit (35 files), hosted runtime probe and full native source build including runtime-target passed locally. The source-built Windows reference passed all four execution groups. The full 18-scenario VM suite and the separate 256 MiB runtime-port guest run passed. Both normal RAM profiles and the timeout scenario required all 115 user groups and 36 contained user faults.

## Next work

Connect real runtime thread attachment/TLS and native allocation to the tested guest lifecycle. Add process-local GC coordination and fault/unwind support, then raise image/stack/commit limits for actual runtime requirements. Large pages, write-watch and working-set locking remain unported. M3 still requires real managed allocation/collection, finalization, exceptions and thread activity inside WitOS.
