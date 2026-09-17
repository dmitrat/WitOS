# M2 — Sparse user memory

Status: introduced in WitOS 0.0.5; updated through 0.0.8 image loading, 2026-09-17. This extends the [isolated native execution slice](M2-Isolated-Execution.md); the guest still does not run .NET.

## Purpose and scope

The pinned NativeAOT GC separates virtual reservation from physical commitment. A component can now reserve 32 GiB even in a 128 MiB VM, commit a few pages at distant offsets, discard them and reuse its address range. This supplies tested kernel primitives for a future runtime adapter.

Each address space has a dynamic arena from `0x0000010000000000` (1 TiB, PML4 slot 2) up to `0x0000011000000000` (exclusive). Fixed code, startup data and stack mappings remain in slot 1. Slot 0 contains shared supervisor mappings. The dynamic-memory calls cannot alter either the fixed image or the kernel.

Prototype limits are eight live reservations and 128 owned physical frames per component. The latter includes its PML4, all private page tables, fixed image/data/stack pages and dynamic backing pages. A normal fixture starts with 13 owned frames including its main-thread TLS page; each additional thread adds five frames for its stack and TLS. These explicit bounds keep syscall work and resource use finite; they are not a suitable final managed-heap quota.

## Memory calls in experimental ABI v7

Transport and preservation rules are unchanged: INT 0x80, RAX call, RCX/RDX/R8 arguments, RAX status and RDX result. Startup and query report version 5. Calls 0–3 remain query/write/exit/close. The memory semantics introduced in v2 are unchanged; calls 9–12 add [thread operations](M2-User-Threads-and-Tls.md).

| Call | RCX | RDX | R8 | Successful result |
| --- | --- | --- | --- | --- |
| 4 Reserve | Byte size | Byte alignment | Unused | Base address |
| 5 Commit | Address | Byte size | Protection | Zero |
| 6 Decommit | Address | Byte size | Unused | Zero |
| 7 Protect | Address | Byte size | Protection | Zero |
| 8 Release | Exact reservation base | Unused | Unused | Zero |

Unused arguments are ignored. All failed returning calls yield a zero result.

Sizes must be nonzero multiples of 4096; addresses must be page-aligned. Reserve alignment must be a power of two from 4096 through 64 GiB. There is no implicit rounding, fixed-address reservation or partial release. Commit/decommit/protect must fit wholly within one live reservation owned by the current component, even when two reservations are adjacent.

Protection is exactly one of: 0 (no access), 1 (read), 3 (read/write). All dynamic pages are NX. Write-only, execute and unknown bits return invalid argument; executable image pages use the separate controlled loader path.

Memory errors:

| Status | Meaning |
| --- | --- |
| 6 Invalid argument | Zero/misaligned size, misaligned address, invalid alignment or protection |
| 4 Bad address | Outside the dynamic arena or overflowing its exclusive end |
| 8 No memory | No suitable virtual gap, reservation slots, owned-frame quota or physical frames |
| 9 Not reserved | Valid arena range not contained in one local reservation; release base not found |
| 10 Not committed | Protect range contains a hole |

Range validity is checked before protection. An oversized, page-aligned reserve reports no memory. Ordinary exhaustion returns a status; ownership corruption remains a kernel invariant failure.

## State and failure semantics

Reserve creates only a metadata record. It creates no page tables or backing frames. Allocation chooses the first aligned gap; release makes that gap reusable. Virtual addresses are not generation-tagged, so a stale pointer can refer to a later reservation at the same address.

Commit allocates and zeroes every newly committed page before exposing it. Already committed pages keep their data and protection, including no-access pages; changing rights requires Protect. Failure rolls back every addition made by that call and reclaims any empty or partially constructed page-table path. Existing pages and reservations are preserved. This applies to both quota and physical exhaustion.

Protect first verifies the complete range is committed, then changes every leaf. Failure leaves the entire range unchanged. No-access pages retain ownership and contents using a software PTE bit while the hardware present bit is clear.

Decommit removes mappings, returns backing frames and prunes empty private page tables. It preserves the reservation, ignores already uncommitted holes and discards contents. Recommit allocates zeroed memory again. Release performs the same discard over the entire reservation and removes its metadata. Exit/fault/budget teardown also frees committed no-access pages and remaining reservations.

Active address-space changes invalidate local translations before freed frames can be reused. Inactive spaces are flushed on CR3 entry. There is no PCID, shared/global user mapping or SMP in this slice. The syscall interrupt gate keeps IF clear throughout the operation; only the selected thread executes and it cannot be preempted during a memory syscall.

Decommit/release visit owned frames rather than iterating all pages in a potentially enormous reservation. Commit/protect bound their page walk by the frame quota. Reserve scans the bounded reservation table. These algorithms must be reconsidered when increasing limits or adding concurrency.

## Validation

Run:

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- test
```

The suite retains 18 VM scenarios. Successful boots require 106 user groups (21 isolation, 11 memory, 10 thread, 14 wait, 16 image, 11 native-bootstrap and twenty-two GC memory/discovery/event/time groups plus a clock-domain group) and 34 contained user faults.

| Added group | Evidence |
| --- | --- |
| MemorySparseAndPrivate | Two spaces reserve 32 GiB each without spending frames; commit first/last pages with only seven additional frames each; release in one cannot affect the other |
| MemoryQuotaRollback | Commit across a 1 GiB page-table boundary fails partway through its quota; original contents/rights and physical free count survive; smaller retry works |
| MemoryReservationErrors | Alignment, zero/overflow lengths, arena/metadata exhaustion, hole reuse, fixed/kernel-region exclusion and adjacent-reservation boundaries |
| MemoryPhysicalOom | A test allocator receives six exclusively borrowed real frames; capacities 1–5 force failure at each table/data allocation step, verify rollback and subsequent recovery |
| MemoryLifecycle | Ring-3 calls exercise recoverable quota failure, commit, no-access/read/write changes, atomic protect failure, decommit/recommit zero-fill, release/reuse, full buffer checking and dynamic cross-page output |
| MemoryReservedFault | Reading a reserved but uncommitted page faults with PF error 4 |
| MemoryDecommittedFault | Reading a previously accessed/decommitted page faults with PF error 4 |
| MemoryReleasedFault | Reading after release faults with PF error 4 |
| MemoryReadOnlyFault | Writing after reducing a previously writable mapping faults with PF error 7 |
| MemoryNoAccessFault | Reading after removing access faults with PF error 4 |
| MemoryNxFault | Executing dynamic data faults with PF error 21 |

Each fault checks selectors, address and expected state, closes handles, frees the component and runs a fresh normal component. Physical free counts detect leaks. The host checks the ordered marker list and fault count before accepting success.

## Remaining runtime work

This is not a NativeAOT port or a general virtual-memory manager. There is no GC/PAL adapter, reset/write-watch implementation, demand paging, swap, shared mapping, NUMA policy, large pages or dynamic executable allocation. Bounded threads, raw TLS, join and events are implemented; compiler/managed TLS, runtime exception delivery and GC rendezvous remain absent.

Next investigate the guest NativeAOT target/bootstrap and runtime coordination. Managed execution requires those mechanisms, an actual upstream runtime build and a supported ABI/backend; the hosted Windows probe remains reference evidence only.
