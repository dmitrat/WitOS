# P6 — CoreCLR's test 494226 out of its budget, and the GC's cache size

Plan step P6, opened and closed 2026-10-24 under the plan's rule that a failure seen twice becomes a step. Upstream's
CoreCLR test `GC/Regressions/v2.0-rtm/494226` sometimes ended by signal 24 (`SIGXCPU`, the process's tick budget) in
the `runtime-coreclr-tests` scenario on x64.

The test was not deadlocked. It was crawling: near the heap's hard limit, its collector ran thousands of full compacting
collections, and each one gave the loop a few kilobytes. Such a creep is upstream's GC behaviour, and a Linux run
shows it too. The step found something else on the way: a gap in the port. The GC found no processor cache on WitOS, so
a workstation GC's least generation 0 budget fell to 160 KiB.

The step does two things:

- **It closes the gap.** WitOS takes the cache size heuristic that upstream gives a Linux whose sysfs names no cache.
- **It leaves the test out on every ISA.** The creep remains possible with any budget, and upstream leaves the test out
  on arm32, where it meets the same out-of-memory path.

## 1. The occurrences

| When | Where | What happened |
| --- | --- | --- |
| 2026-10-23 | CI, PR #185, `linux-host (x64)` | `494226` ended by signal 24; the rerun passed |
| 2026-10-24 | CI, PR #199, `linux-host (x64)`, both attempts | `494226` ended by signal 24 |

Over the x64 legs of CI since 2026-10-12, that is three of about 178. On 2026-10-13 the test ended once by SIGABRT, but
that was a different matter: the N2.2 experiment without the GC's memory knob (`N2.2-Library-Suites.md`).

The test was already left out on riscv64. V2.4 saw it end there with an `OutOfMemoryException` from the console's first
allocation after the `catch`, and left the reason as an open question.

## 2. What the test does

The test allocates up to 20 million arrays of 10 bytes into a `List<byte[]>` inside a `try` block, pinning every 1024th.
It writes "Test passed" after catching the `OutOfMemoryException` that ends the loop. It passes if the GC's card table
assert is not hit, and a release runtime compiles that assert out.

Each process of the scenario gets `DOTNET_GCTotalPhysicalMemory` from the root task (N2.2). That is the smaller of a
process's page quota and the memory free when the system started: 407.7 MiB in the 512 MiB guest. The GC takes it as a
container's limit and holds the heap to 75% of it, a hard limit of 305.8 MiB. The loop therefore always ends at that
limit, at about 6 million arrays.

Upstream runs the test on machines where the loop completes. Upstream's `issues.targets` leaves it out on arm32
("needs triage"), where it meets the out-of-memory path as it does here.

## 3. The measurement in the guest

The boots were replayed in the container as in P4 (`P4-Reservation-Gap-Search.md` §2), with a diagnostic variant that
was never committed:

- The kernel printed a line the first time an idle tick was charged to a process. That happens only when no thread is
  ready, no wait has a deadline and no line is bound. After the line it charged the process no more, so that a
  deadlocked process would have stayed as it was for a dump.
- The package held the one test, repeated, with environment variables before it if asked.

The scenario's own full run took the test 13 s of the host's time. A boot that starts with the test met the stall
almost every time: 20 replays at once finished none of their first runs in 10 minutes, and four at once passed two runs
and stalled in five. No replay printed the deadlock line, so the process was running.

A dump of a stalled replay (x64) showed the following:

- **The main thread was in the collector.** Its frame-pointer chain ran from `Main` through `AllocateSzArray`,
  `GCHeap::Alloc`, `allocate_soh`, `garbage_collect`, `gc1` and `mark_phase` to `drain_mark_queue`.
- **Every collection was a full blocking compacting one.** The collector's `settings` said generation 2, reason
  `oos_soh` (no room on the small object heap). The collection count rose from 113 at 1.3 minutes to 136 thirty
  seconds later.
- **The heap stood at its limit.** The GC had 320,598,016 bytes committed against a hard limit of 320,606,208, with
  239 MB allocated on the small object heap. That is where a Linux run throws.
- **Progress was tiny.** Across 19 collections the small object heap grew by 50,880 bytes, 2,678 a collection: some
  67 arrays per full compacting collection.
- **Neither the kernel nor the address space was short.** The kernel had 85 MiB of 410 MiB free, and the GC's region
  range was 1.5 GiB, five times the limit.

The collector's generation 0 showed the gap. Its desired allocation was 164,016 bytes, and `llc_size`, the cache size
the GC found, was 262,144, the floor. In `get_gen0_min_size` a workstation GC takes four fifths of the cache, at least
256 KiB, and then five eighths of that under a hard limit: 160 KiB.

## 4. Why the GC found no cache

`GetLogicalProcessorCacheSizeFromOS` in `gc/unix/gcenv.unix.cpp` tries three sources:

1. **`sysconf`'s cache names.** These are glibc's alone. musl defines none, so this source is compiled out.
2. **sysfs** (`/sys/devices/system/cpu/cpu0/cache`). This path is compiled only for `TARGET_LINUX`, and WitOS has no
   `/sys` either.
3. **A heuristic by the number of processors:** 4 MiB for up to four, 8 MiB up to 16, 16 MiB up to 64, 32 MiB beyond.
   It is upstream's own answer for a Linux whose sysfs names no cache; the comment there names musl and ARM64. But it is
   compiled only for `TARGET_LINUX`, and WitOS builds as `TARGET_WITOS`.

So every source gave zero. Nothing else in the runtime reads the cache size.

## 5. The creep on Linux

The test's body ran as a console program, unchanged except that it reports its time and its collections. It ran under
Microsoft's .NET 10.0.12 on the container's Linux, on one processor (`taskset`, `DOTNET_PROCESSOR_COUNT=1`), with the
guest's `DOTNET_GCTotalPhysicalMemory`. An `LD_PRELOAD` shim refused the GC's reads of `/proc/self/statm`, `mountinfo`
and `cgroup`, as on WitOS: without them the GC knows no memory a process uses, and both its memory load and its
available memory read zero.

Each configuration ran 32 times:

| Linux configuration | Gen 2 collections a run | Runs that crept |
| --- | --- | --- |
| the GC's own cache size, from the host's sysfs | 8–12 | 0 |
| `DOTNET_GCgen0size=0x28000` (160 KiB, the guest's budget) | 6–17 | 0 |
| `DOTNET_GCgen0size=0x200000` (2 MiB, the heuristic's budget for one processor) | 8–110 | 3, of 88–110 collections and 20–24 s |
| 2 MiB with `DOTNET_gcConcurrent=0` | 9 | 0 |

Earlier runs had ruled out time: a monotonic clock running 15 or 50 times fast, as a guest sees time under QEMU's
instruction clock, changed nothing.

So the creep is the upstream GC's. It appears at the hard limit with background collection on, it depends on the
generation 0 budget, and it depends on chance. A native full collection of this heap takes about 0.1 s. Under TCG it
takes about a second of the host's time, so a creep of hundreds or thousands of collections outlives an hour of the
guest's processor time.

## 6. The guest's variants

| Guest variant (the test repeated, in a boot that starts with it) | Runs of the test that passed |
| --- | --- |
| x64, as in the scenario | 0; none of 6 replays finished a run |
| x64, `DOTNET_GCgen0size=0x300000` | 60 of 60 |
| x64, `DOTNET_GCgen0size=0x200000` | 80 of 80 |
| x64, `DOTNET_gcConcurrent=0` | 60 of 60 |
| x64, `libcoreclr.so` with this step's patch | 80 of 80 |
| riscv64, `libcoreclr.so` with this step's patch, the exclusion lifted | 0; all 6 replays crept |

The x64 scenario's full run passed with the patch, all 51 runs. On riscv64 the patch gave the GC a cache of 4 MiB, yet
the replays crept: 616 collections, 547 of them full compacting, at about 1 KB a collection, with the heap at its limit.

## 7. The changes

- **The gap.** `patches/runtime/release-10.0/src.coreclr.gc.unix.gcenv.unix.cpp.patch` gains a second hunk: the
  heuristic's condition names `TARGET_WITOS` beside `TARGET_LINUX`. On ARM64 the function applies the heuristic again
  whenever the cache found is not of level 3, and that branch now gives WitOS the same answer. Every .NET process on
  WitOS gets the generation 0 that such a Linux gives it: at least 2 MiB on up to four processors instead of 160 KiB,
  so fewer collections. This is RFC 0015 §4's third choice, a `TARGET_WITOS` branch beside another platform's. The
  first two do not serve: there is no configure check, since the cache's names are glibc's, and no runtime knob of the
  right kind, since `DOTNET_GCgen0size` is a fixed size every application would have to carry.
- **The test.** `494226` leaves `build/runtime/coreclr-tests.json` on every ISA with the reason above. The entry for
  riscv64 alone is replaced, and V2.4's open question is answered.

## 8. What remains

- **.NET 8.** The `release-8.0` set has the same gap and stays unchanged: its support ends on 10 November 2026, and no
  .NET 8 step follows (the author's decision of 2026-10-23).
- **The real cache sizes.** The kernel knows them: `PROCESSOR_QUERY` reports each online processor's caches. But the
  runtime reaches the system only through the libc. musl's `sysconf` has no names for caches, WitOS adds none to the
  libc's namespace, and `/sys` is not emulated. Reporting them would take a WitOS API in the runtime, that is libwitos
  outside the system layer (ADR 0026).
- **Why the guest creeps more often than Linux.** At 160 KiB the guest crept almost always, Linux never. This is not
  known. A candidate is the order in which one processor runs the background GC's thread and the allocating thread:
  WitOS runs its threads round robin in 10 ms ticks and does not preempt on a wakeup.
