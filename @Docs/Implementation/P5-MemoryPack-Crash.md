# P5 — MemoryPack.Tests crashes or stops in the guest

Plan step P5, opened 2026-10-24 under the plan's rule that a failure seen twice becomes a step. `MemoryPack.Tests`, in
the group `serialization-numerics` of the NuGet libraries' suites (N2.2), now and then dies by SIGSEGV in the guest, and
now and then stops with all its threads waiting. P4 (`P4-Reservation-Gap-Search.md`) removed the kernel's quadratic
reservation search, which had made the group stall for hours. What this document describes remains after that fix.
The step's first slice makes a crash name its module; the cause is open.

## 1. The occurrences

| When | Where | What happened |
| --- | --- | --- |
| 2026-10-23, CI's run of `main` | `nuget-suites (arm64, serialization-numerics)` | `MemoryPack.Tests` ended by signal 11 after 44 s |
| 2026-10-24, the container, P4's second series (fixed kernel) | x64 replay 6 of 12 | ended by signal 11 after 14 s, a read of address 0 (`[USER-FAULT] vector=14 error=4 address=0`) |
| 2026-10-24, the container, this step's hunt (§3) | x64 replay 24 | ended by signal 11; the libc's report names the place (§4) |
| 2026-10-24, the container, this step's hunt | x64 replay 32 | stopped after `Starting: MemoryPack.Tests` with every thread waiting (§5) |

All four came in the first seconds of the suite, while xunit runs its test collections in parallel. Before P4's fix
the same suite also stalled in the kernel's reservation search. That was a different mechanism: one processor
searching under the kernel lock while the others spun for it.

## 2. A fault names its module

Before this step a process that died by a fault left only the kernel's line: an address, but not the image that
holds it. Linux's report of a fault names the image (`segfault at … in libfoo.so[…]`). In WitOS the kernel knows no
image, so the libc reports it (`signal.c`, `report_fatal`).

Before the libc rejects a synchronous fault that ends the process (the default action, an ignored or a blocked
synchronous signal, a fault it cannot classify), it writes:

- **The first line, before reading anything from the process's memory:** the signal, pc, the address concerned and sp.
- **The place:** pc's module and offset. The modules come from musl's list for debuggers (`_dl_debug_addr`), and each
  module's own program headers are read where its ELF header lies at its base. Nothing is locked and nothing is
  allocated, since the process is ending.
- **The callers:** the return addresses of the frame-pointer chain (x29 on ARM64, s0 on riscv64, rbp on x64), each
  named the same way. The chain is read only within the thread's stack bounds that the interrupted context carries.
  It stops at an address below 64 KiB, where no code lies, which is what a register that held no frame gives in code
  built without frame pointers.

A static program has no module list and gets the first line alone. The spawn scenario checks the report on the three
ISAs:

- the dynamic program reads address 0 in its library on purpose (`dynamic_main fault`, `dynamic_read`);
- `spawn_main` requires the process to end faulted;
- the scenario requires the report to name `libdynamic.so`.

The release judge used to forbid every kernel fault line. `BootRequest.UserFaults` now lets a scenario declare the
faults it provokes, and the judge requires exactly that many (`BootValidationTests`).

## 3. The hunt

The method is P4's (§2 there): the group's image is replayed many times at once with the data disk under
`snapshot=on`, a dump on silence, and the libc's report in the image. Each replay stops once MathNet starts, which is
after MemoryPack ended one way or another, so a replay takes minutes instead of an hour.

| Series | x64 | ARM64 |
| --- | --- | --- |
| first (40 + 30) | 40 passed MemoryPack | 30 passed |
| second (80 + 60, counts at the time of writing) | 67 reached MathNet's start: 1 crash (replay 24), 1 stop (replay 32) | 53 reached it, no failure |

The rate is about one crash and one stop in a hundred x64 replays, with 14 guests sharing the container's processors.

## 4. The crash: the garbage collector meets a zeroed object

Replay 24's report, named with the runtime build's `libcoreclr.so.dbg`:

```
[LIBC] SIGSEGV ends the process: pc 0x100007e91cb, address 0x0
[LIBC]   at libcoreclr.so+0x5721cb  WKS::gc_heap::find_first_object
[LIBC]   called from +0x577cfd      WKS::gc_heap::mark_through_cards_for_segments
                     +0x568ae2      WKS::gc_heap::mark_phase
                     +0x564fbf      WKS::gc_heap::gc1
                     +0x57198b      WKS::gc_heap::garbage_collect
                     +0x56034b      WKS::GCHeap::GarbageCollectGeneration
                     +0x56173a      WKS::gc_heap::trigger_gc_for_alloc
                     +0x56243b      WKS::gc_heap::try_allocate_more_space
                     +0x58f250      WKS::GCHeap::Alloc
                     +0x438603      Alloc
                     +0x436535      AllocateObject
                     +0x435d50      RhpGcAlloc
                     +0x598dde      RhpNewObject
```

An allocation started a collection. While marking through the card table, the collector walked objects of an older
generation and read an object whose method table pointer was zero. Memory the collector believes holds objects read
as zeros. That points at memory the collector committed and wrote, then found replaced. The candidates the step
looked at so far:

- **The kernel's commit.** Ruled out. `MEMORY_COMMIT` of a range keeps the pages it finds and maps only the absent
  ones (`memory_commit`).
- **Stale translations on another processor.** Not found. A shootdown goes to every processor whose dispatcher has
  the space, idle ones included. x64 runs without PCID, so loading another root flushes the user translations, and
  every unmap and every switch runs under the kernel lock.
- **The libc's mapping calls the collector makes** (`mprotect` to commit, `mmap(MAP_FIXED, PROT_NONE)` to decommit,
  `madvise` to reset). Open.

## 5. The stop: every managed thread waits

Replay 32's core, read with the kernel's map and the layout of its records from its own headers (x64, by hand):

- **The kernel was live.** At the dump's instant, processor 0 spun for the kernel lock on the stack of the process's
  signal thread, which polls a pipe every 10 ms through `CLOCK_READ`. Processor 1 held the lock and read the HPET.
  Processors 2 and 3 were idle. The timed waits' deadlines lay 10 ms and 3.4 s ahead of the clock, so timers expired
  as they should.
- **The process had stopped.** Its stacks were read by scanning each thread's stack for addresses inside the modules
  of musl's list:
  - the main thread and six managed threads waited in `WaitHandle_WaitOneCore` or
    `WaitHandle_WaitMultipleIgnoringSyncContext`, through `Thread::DoAppropriateWait`, the PAL's
    `WaitForMultipleObjectsEx`, `pthread_cond_timedwait` and the libc's `__wit_futex`;
  - the finalizer waited for its event;
  - no thread ran.
- **What that means.** Nothing woke the handle the threads waited for. Either a wake was lost below (the PAL's
  condition variables over the libc's futex slots, `futex.c`), or the state the waiters depend on was damaged, as in
  §4. The libc's futex slots were read for a race between a wake and a deadline. A wake marks the slot under the
  slots' lock before it sets the event, and a waiter that timed out reads the mark under the same lock, so that race
  loses nothing. No defect was found there.

## 6. Next

- Heap verification in the guest at each collection (CoreCLR's `DOTNET_HeapVerify`, where the build serves it) on the
  replays, so that the corruption is caught at the collection after it happens, not at the next card walk.
- A dump at the fault, not after it. The report gives the place; the heap's state needs the process stopped there.
- The libc's `mmap`, `mprotect` and `madvise` on the collector's ranges, under several threads.
