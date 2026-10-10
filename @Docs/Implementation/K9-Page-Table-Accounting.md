# K9 — Memory accounting by the page tables

Until K9 an address space recorded its pages in four arrays: the frames it owned, with their virtual addresses, and the
alias entries of its object mappings, with their frames. The process page quota sized each array, which was 2048
entries in a system layer process. An alias entry also counted against that quota, although it costs the space no
frame.

R3.1 hit this limit. When musl's dynamic linker loaded CoreCLR's JIT after the runtime, `dlopen` failed with
`ENOMEM`: the code of `libcoreclr.so` and `libclrjit.so`, mapped from the package as aliases, plus their copied data
needed more than 2048 entries. The quota could not grow, since every address space record would have grown with it.

K9 makes the page tables the record of what a space holds, so the quota no longer sizes anything.

## What changed

- **The leaves are the record.** A leaf marked `OWNED` holds a frame of the space, and one marked `ALIAS` holds a page
  of a memory object. These are the software bits each architecture already kept: bits 9 and 10 of an x64 entry, bits
  55 and 56 of an ARM64 descriptor. The space keeps four counts instead of the arrays:
  - `OwnedCount`: the frames the space owns, its page tables included;
  - `TableCount`: those of them that are page tables;
  - `DynamicCount`: those that are leaves in the dynamic arenas;
  - `AliasCount`: the alias entries.

  `allocate` and `free_owned` keep the counts and panic when a count would go below zero.
- **A walk that skips absent tables.** `wit_arch_page_next(space, address, end)` returns the lowest page in
  `[address, end)` whose leaf is not empty, or `end`. Each architecture walks its four levels and skips a whole span
  at an absent or supervisor table, so a sparse range costs its populated tables, not its pages. Three paths use it:
  - decommit;
  - the release of a reservation;
  - the teardown of a space.
- **The commit rollback** keeps one bit for each page of the range that the commit added, in a static bitmap. A
  range has at most a page quota of pages, so the bitmap has 65536 bits. Memory calls run one at a time, with
  interrupts disabled, on the one processor that takes calls. A failed commit unmaps exactly the pages whose bits are
  set and leaves the pages it found as they were.
- **The teardown** unmaps every leaf of the user range `[WIT_USER_BASE, WIT_USER_MEMORY_LIMIT)`:
  - owned frames go back to the allocator;
  - the tables they empty are pruned with them;
  - the root goes last.

  If any count is left nonzero afterwards, the kernel panics.
- **`MEMORY_QUERY`** reports `DynamicCommittedBytes` and `PrivatePageTableBytes` from the counts. It no longer scans
  an array.
- **An alias entry costs no quota.** An object mapping is bounded by the object's size (64 pages) and by the
  reservation table, not by the page quota. A committed range therefore holds at most:
  - the space's own pages (`PageLimit`);
  - plus 64 alias pages for each reservation.

  `MEMORY_PROTECT` and `CODE_PUBLISH` bound their walk by that limit. A longer range has a page that is not committed,
  so the call returns `NOT_COMMITTED`. `MEMORY_RESET` and `MEMORY_COMMIT` act on owned pages alone and keep the page
  quota as their bound.
- **The quota of a system layer process** (`WIT_PROCESS_PAGE_CAPACITY`) goes from 2048 to 65536 pages, which is
  256 MiB. This applies to the root task and to every process `PROCESS_CREATE` makes. In practice the guest's physical
  memory runs out first, and the memory-pressure event reports it. A fixture keeps its 128 pages.
- **The kernel image** loses four arrays of 2048 entries (64 KiB) from each address space record.

ABI-1 stays 1.0. No call, structure, status or right changes, and the quotas are prototype bounds, not ABI promises
(`limits.h`).

## Tests

- **The pressure check of the root task** (`tests/User/root_mechanisms.c`) still commits its headroom down to the low
  mark.
  - At 128 MiB the headroom is now the guest's physical memory. The quota no longer binds.
  - The commit takes page tables from that same headroom: one for every 512 pages, plus at most three above them. The
    check therefore subtracts `headroom / 512 + 3` pages and leaves 8 pages of margin, so the remainder lands at or
    below the low mark.
- **The self-test kernels** still run the existing memory self-tests of `tests/Kernel/user_memory_tests.c` against
  the fixture profile, unchanged, and they all pass:
  - `User.MemoryFixedAndPartial`;
  - `User.MemoryPhysicalOom`;
  - `User.MemorySparseAndPrivate`, which covers private table accounting;
  - `User.MemoryQuotaRollback`, which covers the rollback of a failed commit;
  - `User.MemoryReservationErrors`.

  The memory and object fixtures pass too.
- **Both suites pass on both ISAs:** `test` (27 scenarios on x64) and `test --arch arm64` (21).

## Limits kept explicit

- **The rollback bitmap is static** and serves one memory call at a time. Phase P, which runs threads on several
  processors, must give each processor that takes calls its own bitmap, or serialize memory calls explicitly.
- **A walk of a whole space covers the user range alone.** The shared kernel branch is supervisor-only and is never
  entered.
- **The page quota bounds owned pages, not alias entries.** Aliases are bounded by the object table (64 objects of
  64 pages, kernel-wide), the package mappings of 64 pages and the reservation table.
