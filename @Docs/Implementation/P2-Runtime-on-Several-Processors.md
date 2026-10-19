# P2 — the runtime on several processors

Plan step P2 (phase P, `P1.0-SMP-Inventory.md` section 6): upstream .NET in its Unix form on a board of several
processors — `Environment.ProcessorCount` above one, the server collector and the thread pool scaled to the processors,
and the scenarios of N1 and N2 under load on several processors. P1 (P1.1–P1.5) made threads run on every processor;
the runtime needed no patch. Made on 2026-10-19.

## 1. What the runtime asks of the system layer

- **The processor count** comes from `sched_getaffinity`, which CoreCLR's PAL and the GC read for the process and musl's
  `sysconf(_SC_NPROCESSORS_ONLN)` counts. The libc has answered with the thread's mask since P1.4, every online
  processor by default, so `Environment.ProcessorCount` was already the board's count.
- **The current processor**: the GC is built with `HAVE_SCHED_GETCPU` (a configure check), and the server collector picks
  an allocating thread's heap by `sched_getcpu`; CoreCLR's PAL and the managed `Thread.GetCurrentProcessorId` ask the
  same. musl's `sched_getcpu` is `SYS_getcpu` (there is no vDSO), which the libc answered with `ENOSYS`, so the GC got
  `-1` as a processor number. The libc now answers `getcpu` with the processor that runs the caller,
  `THREAD_QUERY`'s `Processor` (version 6, P1.4), and node 0, the one memory node; as on Linux the answer may be stale
  once the call returns. A system call per answer: Linux's x64 vDSO reads it with `RDPID` or `LSL`, and the managed
  side caches it per thread and refreshes it at a measured rate.
- **Threads**: the server collector keeps a collecting thread and a background thread per processor beside the thread
  pool's workers, the finalizer, the tiered compiler's worker, System.Native's signal thread and the timer thread. On
  four processors the server run ended with `Out of memory.` from a thread the runtime could not create: the process
  had reached its 32 threads. `WIT_PROCESS_THREAD_CAPACITY` is 64 now, the libc's futex slots 64 (a slot a thread, K5.3)
  and `WIT_PROCESS_EVENT_CAPACITY` 128 (the slots' events and the exit word's, with room for the rest). A thread's
  kernel stack has come from the allocator since P1.5, so the image grows by the thread records alone (some 300 bytes
  a thread of each slot).

## 2. The x64 trampoline page

With 512 MiB on two processors the x64 kernel panicked: `No usable page below 1 MiB for the start of secondary
processors`. The allocator hands out the lowest free page first, and the page tables the boot makes before it starts
the processors — the identity map of a larger memory and, since P1.5, the stack window's tables — took every usable
page below 1 MiB that the firmware left. The x64 paging now claims the trampoline page, the lowest free usable one at or
above 64 KiB, before its first table (`claim_trampoline_page` in `paging.c`, read by `wit_x64_trampoline_page`); a board
with one processor never uses it, and only a board with more refuses its absence.

## 3. The dispatcher's walks

With 64 threads a process, `process`'s twenty sleeping children failed on ARM64 and riscv64 on one processor: four of
them had slept their three seconds and exited before the last one started. The dispatcher walked every thread a
process may run at every pick (`pick`, `wit_user_wait_expire`, `wit_user_wait_timed`, the idle path's check, and on
several processors `thread_ready_for` at every exit from the kernel), so doubling the capacity doubled the work of every
dispatch with twenty-two processes alive, and the memory-pressure update walked every event slot of every process. A
process now keeps `ThreadTop`, one past its highest thread that is not empty (`wit_user_thread_top_update` at a
thread's creation and reap, zero at a component's start and teardown), where those walks end, and counts its
memory-pressure events (`MemoryPressureCount`), so a process of a few threads and no pressure event costs a few steps
whatever its capacities. `process_init.c` now prints the counts when the crowd's check fails.

## 4. `THREAD_YIELD`'s value

The server collector's run on four processors ended on ARM64 and riscv64 with a fault report of vector `~0`: the libc
had rejected a delivery it took for a fault of no handler, while the kernel's record was an activation. A probe of the
libc's rejection sites showed the callback's vector register zero with its token and address intact. `THREAD_YIELD`
wrote its value — whether another thread was picked — into the caller's frame after its dispatch, and a dispatch that
picks the caller again has already delivered a pending activation into that frame; on ARM64 and riscv64 the value
register (x1, a1) is the callback's vector register, on x64 (RDX) its address register, which the libc reads only to
recognize sigreturn's marker, so x64 passed. The libc yields while sigreturn's marker waits for room in a full
activation queue, which the collector's suspensions of every thread fill. The value now goes into the frame before
the dispatch, and the dispatch that resumes the yielding thread itself sets it to zero before any delivery
(`wit_user_yield`, `Dispatcher.Yielding`). The defect is as old as `THREAD_YIELD`'s value; one processor reached it too,
whenever the caller was the only ready thread.

## 5. The tick budget

CI's runs of the libraries' own suites (N2.2), which P2 boots on four processors, ended the Roslyn and Math.NET suites
part way, after 2666 to 11035 seconds, each process ended without a fault report. A system layer process's tick budget
is an hour of processor time (R7.2b), and every processor's tick charges the component it runs (P1.3b).

Under QEMU's instruction clock the four processors run in turn on one host thread, sharing one instruction count. So
four busy processors do in a virtual second the work one does, while charging four ticks for it, and a suite busy on all
of them spent its hour in 45 minutes.

The budget is now an hour for each online processor (`WIT_PROCESS_TICK_BUDGET * wit_processors_online()`), the machine's
hour, which bounds a deadlock or a runaway process as it did on one processor. `apps_init.c` now names the signal a
command ended by (`SIGXCPU` for a budget, `SIGSEGV` for a fault) instead of printing `-1`.

## 6. The proof

- `tests/Runtime.Witos/Processors` is an ordinary console project, published by the stock SDK without a RID once as it
  is and once with `ServerGarbageCollection=true`, as an application selects the server collector (its runtimeconfig
  then sets `System.GC.Server`). Its argument is the number of processors to expect; its eleven checks are the
  processor count, the collector its configuration asked for, the heaps (`GC.GetConfigurationVariables()`'s
  `HeapCount`: one, or a processor's each under the server collector), the processor ids (as many threads as
  processors spinning until together they saw every processor's `Thread.GetCurrentProcessorId`), interlocked
  increments and a contended `lock` on every processor with exact totals, `Parallel.For` and PLINQ sums, the thread
  pool (its minimum equals the processor count, and as many work items as processors that wait for each other all
  run), allocation on every processor while forced compacting collections run, with every kept array checked
  afterwards, producers and consumers through a bounded `BlockingCollection`, and two thousand tasks. On the Windows
  host it passes on 32 processors under both collectors, and it fails when told to expect another count.
- The `runtime-processors` scenario (`RuntimeProcessors`, after `runtime-libraries` in `runtime-witos`) boots both
  publishes through `dotnet` beside the device services (`RuntimeApps`) on four processors and requires
  `[P2] 4 processors, workstation GC: 11 of 11 checks passed`, the same of the server collector and each run's exit
  with zero.
- The scenarios of applications under load boot on four processors (`RuntimeProcessors.PROCESSORS`): N1's suite
  (`runtime-compatibility`, and `runtime-published` with a publish of another host), N2.1's applications
  (`runtime-libraries`) and N2.2's suites (`runtime-nuget-suites`, a group a CI job).
- `process_init.c` checks `sched_getcpu`: a processor of the mask, and on two processors the processor a thread pinned
  to each in turn runs on.

## 7. What ran

In the Linux host's image, with the final code:

- `test --arch x64`: PASS, all 34 scenarios; `test --arch arm64` and `test --arch riscv64`: PASS, all 28 each —
  `process` and `process-smp2` with the twenty children and `sched_getcpu`, the libc's sixty-fifth thread refused.
- `release` on the three ISAs.
- `runtime-processors`: `[P2] 4 processors, workstation GC: 11 of 11 checks passed` and the same of the server
  collector on x64 (75 s), ARM64 (68 s) and riscv64 (111 s).
- On four processors: `runtime-compatibility` with no unexpected result on x64 (131 s), ARM64 (134 s) and riscv64
  (187 s); `runtime-libraries` with both runners' summaries clean on x64 (218 s), ARM64 (279 s) and riscv64 (341 s).
  On two processors they had taken 134 s and 191 s on x64, and `runtime-portable-app` passed 12 of 12.
- N2.2's suites (`runtime-nuget-suites`) and `runtime-published` on four processors are CI's: the `nuget-suites` job, a
  group a job on x64 and ARM64, and `published-on-windows` on the three ISAs.
- `WitOS.Dev.Tests`: every host test passed; `format` is clean.

## 8. Left for later

- `Process.ProcessorAffinity` still throws `PlatformNotSupportedException` (WitOS's `Process.WitOS.cs`), although the
  libc now reads and sets a thread's mask; a change of the runtime's patch set with its own check.
- `getcpu` is a system call; a per-processor word user mode reads (x64's `RDPID` with `IA32_TSC_AUX`, as Linux sets it)
  would spare it, once a measurement shows the cost.
- QEMU's single-threaded TCG runs the processors in turn under `-icount`, so the guest shows correctness on several
  processors, not speed; a speedup is measured on hardware virtualization (C2.7) or real hardware.
