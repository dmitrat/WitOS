# ADR 0013: Bounded native runtime allocation

**Status:** Implemented in WitOS 0.0.16; ABI remains v9.
**Date:** 2026-09-20.
**Scope:** Nonthrowing C++ allocation for native runtime structures. No guest managed runtime, collector, full CRT or managed heap is claimed.

## Interface and implementation

`native_new.witos.cpp` implements the actual C++ `operator new/new[](size_t, const std::nothrow_t&)`, ordinary/sized/nothrow-placement delete forms, and the `std::nothrow` object using the toolchain's unchanged `<new>` declarations. It is compiled both into the import-free native guest fixture and the actual upstream Runtime.WorkstationGC archive via the WitOS CMake overlay. Windows reference inputs remain unchanged.

The allocator lazily reserves one 256 KiB private dynamic arena, with at most 128 simultaneous allocations and 16-byte alignment. A zero-size request consumes a distinct minimum-size allocation; oversized, fragmented, metadata-exhausted or physically unbackable requests return null. No allocator initialization, compiler TLS, kernel events or recursive C++ allocation is needed.

A bounded first-fit registry stores offsets/sizes separately from user payloads. Only pages touched by a successful allocation are committed. Kernel all-or-nothing commitment preserves pages shared with existing allocations and rolls back partial new mappings on failure. If the first allocation cannot commit, its newly reserved arena is released. Metadata is published only after commitment succeeds.

Per-page reference counts prevent freeing a page that still contains another live allocation. Delete decommits pages whose count becomes zero. The final delete releases the entire reservation and its remaining backing/page tables. Ordinary allocation data is uninitialized; recommitted pages are zero because of the kernel contract, but reused sub-page bytes have no zeroing guarantee.

A process-private gate serializes registry/refcount changes. Contenders yield so a preempted owner can resume on one CPU. Memory syscalls do not park, and the gate does not consume the limited event pool. Cross-thread deletion is supported after the caller synchronizes object lifetime. Metadata and arena die with their component on abnormal termination.

## Explicit limits and failure behavior

This is a bootstrap allocator, not the final full-runtime capacity. The 256 KiB arena and 128 descriptors are deliberate bounds under the existing component quota. The single arena can reject a large request when free space is fragmented. Clients must not independently decommit/protect/release the allocator-owned arena through raw memory calls.

Null deletion is harmless. Interior, foreign and immediately repeated frees fail fast without dereferencing caller-provided metadata. A stale pointer after address reuse cannot be distinguished from the new allocation; as with native C++ allocators, invalid object lifetimes remain a caller error. Size arguments on sized delete are not used to discover ownership.

Throwing new, over-aligned allocation, malloc/calloc/realloc and C++ exception-runtime support remain unimplemented. They are not replaced by successful stubs. No new kernel API or ABI revision is required.

## Guest tests

Six required groups cover:

- `NativeHeap`: distinct/aligned zero and small allocations, page-spanning/large requests, data preservation, real new-expressions and scalar/array/sized deletes; preferred and relocated images.
- `NativeHeapReuse`: descriptor exhaustion, repeated hole reuse, shared-page lifetimes, prompt decommit, zero-on-recommit and arena exhaustion.
- `NativeHeapFailure`: exhausted reservation slots, first-allocation rollback under physical pressure, partial-commit failure with live data preserved, and recovery after releasing pressure.
- `NativeHeapThreads`: two guest workers allocate/write/yield/verify/free, then transfer allocations to the parent for deletion after join. Supervisor requires thread creation, switches, join and reap.
- `NativeHeapFailFast`: interior and double free terminate at the expected boundary; a fresh component subsequently succeeds.
- `NativeHeapProtection`: actual NX execution fault and read fault after the last allocation is freed, with checked vector/error/address/selectors and complete teardown.

Normal completion must restore allocator snapshots and leave no dynamic reservation. The kernel supervisor additionally checks complete physical-page recovery on every outcome. The runner now requires 121 user groups and 38 contained faults in successful boots.

The initial guest runtime-port run, Release build, 35-file source audit, six-group hosted probe and full runtime-source build (including runtime-target) passed. The source-built Windows reference passed four execution groups. The full VM suite passed all 18 scenarios, with all 121 required user groups and 38 contained user faults in successful boots.

## Source-link evidence

The WitOS Workstation archive now contains 70 members. The build verifies that the allocation adapter's compiled object is present byte-for-byte, alongside the existing GC/Crst objects. The minipal archive still contains 11 members.

The strict link resolves both nothrow allocation functions, sized scalar delete, array delete and std::nothrow: 136 unresolved symbols remain (seven GC environment, four deliberately excluded guest transport, 125 other platform/runtime requirements). The strict missing FlushProcessWriteBuffers fixture still fails for exactly that unimplemented requirement. This is not a fully linked or runnable runtime.

The local native fixture is 24,576 bytes, with 64 plain unwind entries and no OS/CRT imports. Compiler-dependent sizes/counts are evidence, not fixed cross-toolchain expectations.

## Next work

See [M3 integration plan](M3-Runtime-Integration-Plan.md). Thread attachment/compiler TLS, remaining PAL/CoreLib/CRT behavior, GC coordination, module registration and exceptions still block guest .NET execution. Raise allocator/image/stack limits against real workload needs during that integration.
