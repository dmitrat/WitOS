# ADR 0013: Bounded native runtime allocation

**Status:** Implemented in WitOS 0.0.16; ABI remains v9. Revised 2026-10-05 (P6.4.i3b): size-class pages, page runs
and the large heap of the full runtime profile.
**Date:** 2026-09-20.
**Scope:** Nonthrowing C++ allocation for native runtime structures, and the Local and C allocation families over the
same heap. No guest managed runtime, collector, full CRT or managed heap is claimed.

## Interface and implementation

`native_new.witos.cpp` implements the actual C++ `operator new/new[](size_t, const std::nothrow_t&)`, ordinary/sized/nothrow-placement delete forms, and the `std::nothrow` object using the toolchain's unchanged `<new>` declarations. It is compiled both into the import-free native guest fixture and the actual upstream Runtime.WorkstationGC archive via the WitOS CMake overlay. Windows reference inputs remain unchanged. The same heap serves two further families through `native_heap.witos.h`: Local allocations (`LocalAlloc`-style buffers of the diagnostics) and the C allocations of the UCRT subset (`malloc`/`realloc`/`free`, P6.4.h). A block is released only through its own family.

The allocator lazily reserves one private dynamic reservation: its metadata pages, one guard page that is never committed, then the payload arena. The quotas follow the component's profile. A component loaded with the full runtime profile, which `WIT_CALL_MEMORY_QUERY` reports as the runtime reservation capacity, gets a 4 MiB arena and 65,536 live blocks; every other component keeps the 256 KiB arena and 128 live blocks. The owned-memory limit does not choose: a supervisor may lower it after admission, as the GC initialization-failure test of `runtime-boot` does to make the collector's own commit fail, and the runtime's startup must still have its heap then. Alignment is 16 bytes. A zero-size request consumes a distinct minimum-size block; oversized, fragmented, over-capacity or physically unbackable requests return null. No allocator initialization, compiler TLS, kernel events or recursive C++ allocation is needed.

A request of at most 2,048 bytes takes the lowest free block of a page of its size class (21 classes, 16 to 2,048 bytes); blocks of all three families share class pages. A larger request takes the first run of free whole pages. Each payload page has an 84-byte descriptor in the metadata pages: its class or run, live count, lowest-free hint, list links and two bits per block holding the block's family. No metadata lies inside a payload or after one, and the guard page separates the descriptors from the first payload page. A list per class holds the pages with a free block and a bitmap marks the pages in use, so neither allocation nor release searches the live blocks; the previous bootstrap allocator's first-fit registry scanned all of them for every request.

A payload page is committed with its first block and decommitted with its last; the metadata page that describes it is committed with it and stays committed until the reservation is released. Kernel all-or-nothing commitment rolls back its own pages on failure, and the heap decommits the metadata pages it committed for the failed request, so a failed allocation changes no memory accounting. If the first allocation cannot commit, its newly reserved arena is released. Metadata is published only after commitment succeeds.

A class page's live count plays the part of the former per-page reference count: a page is never decommitted while it holds a live block. The final release returns the entire reservation and its remaining backing/page tables, after checking that every page and list is empty. Ordinary allocation data is uninitialized; recommitted pages are zero because of the kernel contract, but reused blocks have no zeroing guarantee.

The runtime archive compiles the heap without optimization (`/Od`, the archived objects' plain-unwind profile), where nothing is inlined and every function with a frame has its own unwind record, and the default-profile probes that link it are just under the 128-record limit (`RuntimeComFixture` 127, `RuntimeThreadFixture` 126, the same as with the bootstrap allocator). The revision therefore keeps the bootstrap allocator's set of functions: the work is in `allocate` and `owned`, which checks an address once for both release and `wit_native_c_size`, and the small repeated expressions (a descriptor's address, a page's use bit, a block's state) are macros; it has 21 records at `/Od`, as before, and 12 at `/O1` instead of 14. A first version with separate helpers had 36, and its stack copy of the memory-query record, a GS buffer, pulled the GS exception handler's object into the probes; the record is now static under the gate.

A process-private gate serializes all metadata changes. Contenders yield so a preempted owner can resume on one CPU. Memory syscalls do not park, and the gate does not consume the limited event pool. Cross-thread deletion is supported after the caller synchronizes object lifetime. Metadata and arena die with their component on abnormal termination.

## Explicit limits and failure behavior

This is still a bounded allocator. The default profile's 256 KiB arena and 128 blocks are deliberate bounds under the 512 KiB component quota, and many guest tests exhaust exactly those 128 blocks to inject allocation failure while other memory stays available. The full profile's 4 MiB arena and 65,536 blocks were sized for the host runtime fixture, where copying the classic locale alone creates every facet with its strings and exceeded the 128 blocks; they are to be raised against the measured needs of the .NET host and CoreCLR. A run can be rejected when free pages are fragmented. Clients must not independently decommit/protect/release the allocator-owned arena through raw memory calls.

Null deletion is harmless. Interior, foreign-family and immediately repeated frees are refused without dereferencing caller-provided metadata: the address must fall in the payload range, its page must be in use, and the descriptor must name a run starting there or a live block of the family at a block boundary. C++ deletes fail fast; the Local and C families report the refusal to their callers: `LocalFree` returns the handle with `ERROR_INVALID_HANDLE`, as on Windows, and the UCRT subset's `free`/`realloc` end the process. A stale pointer after address reuse cannot be distinguished from the new allocation; as with native C++ allocators, invalid object lifetimes remain a caller error. Size arguments on sized delete are not used to discover ownership.

Throwing new (P6.4.g) and the C allocation functions (P6.4.h) are built over this heap by the C++ runtime and the UCRT subset; over-aligned allocation remains unimplemented and is not replaced by a successful stub. No new kernel API or ABI revision is required.

## Guest tests

Six required groups cover the default profile:

- `NativeHeap`: distinct/aligned zero and small allocations, page-spanning/large requests, data preservation, real new-expressions and scalar/array/sized deletes; preferred and relocated images.
- `NativeHeapReuse`: exhaustion of the 128 blocks, repeated hole reuse, shared-page lifetimes, prompt decommit, zero-on-recommit and arena exhaustion.
- `NativeHeapFailure`: exhausted reservation slots, first-allocation rollback under physical pressure, partial-commit failure with live data preserved, and recovery after releasing pressure.
- `NativeHeapThreads`: two guest workers allocate/write/yield/verify/free, then transfer allocations to the parent for deletion after join. Supervisor requires thread creation, switches, join and reap.
- `NativeHeapFailFast`: interior and double free terminate at the expected boundary; a fresh component subsequently succeeds.
- `NativeHeapProtection`: actual NX execution fault and read fault after the last allocation is freed, with checked vector/error/address/selectors and complete teardown. The guest reports the block's address, since the metadata pages now precede the first block, and the fault must be at exactly that page.

`Code.NativeHeapLarge` (P6.4.i3b) covers the full profile in the host runtime fixture: exactly 65,536 live blocks, then a refused allocation that leaves the memory accounting unchanged; a request one byte beyond the arena refused; a run of 4 MiB − 64 KiB beside a C block grown through `realloc`; and 60,000 seeded operations over every size class, runs and all three families, each block filled and checked before release, with wrong-family and interior releases refused. Afterwards the component owns, reserves and commits exactly what it did before.

Normal completion must restore allocator snapshots and leave no dynamic reservation. The kernel supervisor additionally checks complete physical-page recovery on every outcome.

Two exact expectations elsewhere follow the heap's layout. The interface-dispatch probe of `runtime-config` keeps four committed pages, not two: the dispatch heap's page, the native heap's metadata page and two class pages, because its `AllocHeap` object and `BlockListElem` differ in size class, where the first-fit heap packed them into one page. The GC initialization-failure component of `runtime-boot` lowers the budget to 27 pages beyond its admitted image, not 24: before the collector initializes, the runtime's first native blocks fall into four size classes and take four class pages and the metadata page, three pages more than the first-fit heap's packing, and the finalizer thread, which starts first, takes 18. One page then remains, and the collector's first bookkeeping commit of two pages fails exactly as before. A kernel trace of that component's reservations and commits established these numbers; without the change, the failure moved to the finalizer thread's stack, which is not a commit, and the protocol rightly rejected it.

The initial guest runtime-port run, Release build, 35-file source audit, six-group hosted probe and full runtime-source build (including runtime-target) passed. The source-built Windows reference passed four execution groups. The full VM suite passed all 18 scenarios, with all 121 required user groups and 38 contained user faults in successful boots.

## Source-link evidence

The WitOS Workstation archive now contains 70 members. The build verifies that the allocation adapter's compiled object is present byte-for-byte, alongside the existing GC/Crst objects. The minipal archive still contains 11 members.

The strict link resolves both nothrow allocation functions, sized scalar delete, array delete and std::nothrow: 136 unresolved symbols remain (seven GC environment, four deliberately excluded guest transport, 125 other platform/runtime requirements). The strict missing FlushProcessWriteBuffers fixture still fails for exactly that unimplemented requirement. This is not a fully linked or runnable runtime.

The local native fixture is 24,576 bytes, with 64 plain unwind entries and no OS/CRT imports. Compiler-dependent sizes/counts are evidence, not fixed cross-toolchain expectations.

## Next work

See [M3 integration plan](M3-Runtime-Integration-Plan.md). Thread attachment/compiler TLS, remaining PAL/CoreLib/CRT behavior, GC coordination, module registration and exceptions still block guest .NET execution. Raise allocator/image/stack limits against real workload needs during that integration.
