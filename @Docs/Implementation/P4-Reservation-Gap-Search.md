# P4 — the guest stalls in serialization-numerics: the reservation gap search

Plan step P4, opened and closed 2026-10-23–24. The group `serialization-numerics` of the NuGet libraries' suites (N2.2)
went silent in the guest until the job's wall-clock limit, on x64 and ARM64. The plan had called the group "near the
job's wall limit"; it was not. The kernel's search for a free range of a process's address space rescanned the whole
reservation table after every range it met, under the kernel lock with interrupts disabled. A .NET process with
1600 reservations packed low paid some 12 million steps for every `mmap`, and CoreCLR maps a writable view for every
method it compiles. The search now sorts the ranges in use and meets each once (§4).

## 1. The occurrences

| When | Where | What stopped |
| --- | --- | --- |
| 2026-10-20, CI of PR #171 | `nuget-suites (x64, serialization-numerics)` | the guest did not finish after `MemoryPack.Tests`; a rerun passed; recorded as "near the wall limit" |
| 2026-10-23, CI of PR #192 (H1.2b) | the same job on x64 | silent in `MemoryPack.Tests` after a line with a GUID until the 4-hour limit, where the whole job takes about an hour; the monitor's last samples showed all four processors halted in the kernel's idle path |
| 2026-10-23, CI of PR #192 | `nuget-suites (arm64, serialization-numerics)` | silent in `MathNet.Numerics.Tests` after `CubicSplineTest.NaturalSplineGetMinMaxTvalues` until the 4-hour limit, where the job takes about two |
| 2026-10-23, the container, run 4 of the first series (§2) | the group's image on x64, 4 processors | silent in `MemoryPack.Tests` after the GUID line for 15 minutes while the other runs finished the suite in 3–5 minutes; the core of §3 |

PR #193, which carried the same code as #192, passed the group on both ISAs: the stall did not come every time.

## 2. Reproduction

The group's boot was replayed many times at once in the container, outside the tool:

- `runtime-nuget-suites --group serialization-numerics` built the image and the data disk. The boot's QEMU command line
  was read from `/proc/<pid>/cmdline` the moment QEMU started, and that boot was killed before the guest touched its
  data disk.
- Each replay ran the same command with its own serial log, firmware variables and QMP socket, and the data disk under
  `snapshot=on`, so that every replay started from the same disk.
- A replay whose serial log stayed silent for a set time was taken as stalled. Its QMP session recorded `info registers
  -a`, dumped the guest's memory as an ELF core (`dump-guest-memory` without paging) and quit it.

The first series ran 5 replays on x64 and 4 on ARM64 with a silence limit of 15 minutes:

- x64 run 4 stalled in `MemoryPack.Tests`, as on CI.
- Three x64 runs were taken as stalled in `MathNet.Numerics.Tests` after the same CubicSpline line as CI's ARM64 boot.
  They were false alarms: NUnitLite prints only the tests that write output, and after that line thousands of tests run
  silently, most of the suite's 959 s of the guest's time in a passing CI run. Fifteen minutes of the host's time was
  too short with nine guests sharing the container. The later series used 60 minutes.

## 3. The cause

The core of x64 run 4, read with the kernel's map and the layout of its records from its own headers (the offsets from
the pinned clang, as `guest-threads` takes them; `guest-threads` reads ARM64 cores alone, so this one was read by hand):

- **The processors.** Processor 0 was in the kernel with interrupts disabled, at `wit_virtual_gap+0x24c` and, in the
  sample five seconds later, `wit_virtual_gap+0x1bb`, its RAX inside the reservation table of process 9. Processors 1–3
  were in `wit_arch_spin_pause` on threads' kernel stacks with interrupts disabled: waiting for the kernel lock.
- **The process.** Process 9 was `dotnet` running `MemoryPack.Tests`. It had 1600 reservations: about a thousand object
  mappings (325 of 256 KiB and 388 of 64 KiB windows of shared memory chunks, 135 of 60 KiB, 137 of 4 KiB) and the rest
  plain. The arena began with the GC's range of 4.8 GiB, followed by some 1500 small ranges packed in 44 runs with
  holes of a page or so between them.
- **The search.** `reserve_within` asked `wit_virtual_gap` for the lowest free range of the size, from the arena's
  base. `wit_virtual_gap` scanned the ranges in table order until one met the candidate, moved the candidate beyond it,
  and scanned the table again from its first entry. It passed the table's whole capacity, `ReservationLimit`, 8192 for
  a system layer process, empty slots included. With the arena packed from its base, a reservation met about 1500
  ranges in address order, each found by a scan of up to 8192 entries: some 12 million steps, about 0.2 s of the
  guest's time, under the kernel lock with interrupts disabled. Every anonymous `mmap`, every window of a shared memory
  chunk and every view CoreCLR's double mapper takes to write compiled code paid it.
- **Why not every run.** First fit fills a hole that fits, and the search stops at the first. Whether the low holes fit
  the next request depends on the order in which views were mapped and unmapped, which the threads' timing decides.
  When they did not, every request walked the whole packed chain.

## 4. The fix

`wit_virtual_gap` (`src/Kernel/virtual_gap.c`) takes scratch room for the ranges from its caller. It validates every
range as before, copies the ranges in use into the scratch, sorts them by base with a heapsort (in place, bounded, no
recursion), and sweeps them in address order:

- a range that ends at or below the candidate is passed;
- a range that starts at or beyond the candidate's end leaves the candidate free;
- any other range moves the candidate to its own end, aligned.

The candidate only moves beyond a range that overlaps it, so it stays the lowest aligned address that no range met so
far excludes. The answer is the old one, at O(n log n) in the ranges in use. `user_space.c` gives it a static array of
`WIT_PROCESS_RESERVATION_CAPACITY` ranges: memory calls run one at a time under the kernel lock, as the commit's static
bitmap already assumes.

The host test (`VirtualGapTests`, `tests/WitOS.Dev.Tests/Native/VirtualGap.c`) keeps its exhaustive cases (every
occupancy of eight units, every bound, size and alignment, the ranges in reverse order) and adds a table of 8000 ranges
in a permuted order whose only fitting hole lies after the 7000th.

## 5. The check

The second series ran the image with the fixed kernel, 12 replays on x64 and 10 on ARM64, with a silence limit of 60
minutes:

- No replay stalled. Every one ended by itself in 51–71 minutes of the host's time.
- Every suite became faster, by the guest's time of each process (`apps_init`'s report):

| Suite | x64 before | x64 after | ARM64 before | ARM64 after |
| --- | --- | --- | --- | --- |
| MessagePack.Tests | 219–248 s (median 233) | 91–99 s (median 95) | 268–298 s (median 282) | 87–100 s (median 96) |
| MemoryPack.Tests | 167–183 s (median 176) | 76–91 s (median 88) | 193–212 s (median 195) | 81–101 s (median 88) |
| MathNet.Numerics.Tests | 959 s (a passing CI run) | 796–815 s (median 810) | 1055 s (a passing CI run) | 852–868 s (median 860) |

- One x64 replay ended with `/bin/init`'s status 1: `MemoryPack.Tests` died by SIGSEGV after 14 s, a read of address 0
  in user code (`[USER-FAULT] vector=14 error=4 address=0`). That is not a stall. It is the guest's second occurrence of
  a crash of that suite (§6).
- `test` passes on x64, ARM64 and riscv64, and CI runs the group on both ISAs with this PR.

## 6. What remains

- **MemoryPack's SIGSEGV in the guest.** CI's run of `main` on 2026-10-23 saw `MemoryPack.Tests` end by SIGSEGV after
  44 s on ARM64, and the second series here after 14 s on x64. The host runners' .NET had crashed in the same suite
  before (`AccessViolationException` in `VirtualDispatchHelpers.VirtualFunctionPointer`, the plan's known defects), but
  nothing yet ties the guest's crash to that one. The plan gives it a step of its own, P5.
- **The idle processors at CI's 4-hour mark.** The samples of PR #192's x64 boot, taken at the limit, showed every
  processor idle, not a processor in the search with the others spinning. P5's hunt reproduced that picture with the
  search fixed, once in about a hundred x64 replays: the kernel live, every thread of `MemoryPack.Tests` waiting on a
  handle nobody set (`P5-MemoryPack-Crash.md` §5). It is P5's, not this step's.
- **The garbage collector's rate.** The cores of the MathNet false alarms showed 181,000 garbage collections with 67 MB
  of a heap limit of 805 MB committed. That is a high rate for such a heap. This step did not measure why; a
  performance step may.
