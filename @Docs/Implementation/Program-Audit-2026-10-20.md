# Program audit, 2026-10-20

**Scope.** Everything built since the [audit of 2026-10-14](Program-Audit-2026-10-14.md): the rest of phase N (N0 and
N4.1–N4.5), phase C (C1.1–C1.4, C2.1–C2.7, C4.1–C4.3), phase V (V0–V3), phase P (P1.0–P1.5, P2), phase W up to HTTP
(W0, W1.1, W1.2a–b, W2.1a–b, W2.2) and the two fixes of the system layer the console found (S8.1, S8.2): 46 entries of
`Plan-Completed.md` in six days, 50 pull requests merged into `main` (#123–#172) and three stacked on it (#173–#175).
The audit asks the three questions of the previous one — does the work still follow
[ADR 0024](ADR-0024-Three-Layers-and-Unix-Form-Runtime.md) and its specifications
([RFC 0011 v3](../RFC-0011-Kernel-Architecture-and-ABI.md),
[RFC 0015 v2](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md), the Implementation Strategy §157), now
with [ADR 0025](ADR-0025-Devices-as-NativeAOT-Services.md), [ADR 0027](ADR-0027-Base-Applications.md) and
[RFC 0020](../WitOS-RFC0020-Terminal-Console-and-PowerShell-Architecture.md) beside them; is each completed step as good
as the plan says; do the documents still describe the tree — and a fourth, which the author's own sessions raised: what
do the gates not measure.

**Method.** The documents were read again against the tree at commit `ea88ba6` (branch `s8.2-readytorun-relocations`:
`main` at #172 with #173–#175 stacked) with `main` merged in, which brought the two pull requests the stack's branches
were based before (#151, the host retry of a NuGet suite; #152, a riscv64 exclusion). The kernel's sources were searched for every word that would mean policy; the
quotas, the patch sets, the exclusion lists, the recorded differences and the sizes of each layer were counted; the
state of CI was taken from the pull requests and from `main`'s own runs. Nothing was booted for this audit; the
evidence of each step is the guest acceptance its document records. Where this audit gives a number beside the one of
2026-10-14, the earlier number is in parentheses.

## Verdict

The program is on its line. The kernel still holds mechanisms only, and a third ISA was added without a change in
`src/Kernel`; ABI-1 grew twice, additively, each time for a consumer with a test; the runtime is unchanged upstream
.NET on three ISAs with a patch set inside the budget; every new piece of policy — the terminal, the screen, the
network stack, the base applications — is a NativeAOT service or an application in a process of its own, reached
through channels. The five goals of ADR 0024: four are proven (1, 2 literally since N4.4 and V3, 3 since phase V, 5 on
two ISAs with riscv64 recorded as the exception), and goal 4 is measured and still inside its budget.

What is not in order is of two kinds. Bookkeeping again: the plan's state section, README and AGENTS.md had fallen
behind the steps (two ISAs where there are three, "no libc sockets yet" after W2.1, a scenario count and a patch count
from earlier weeks), and `main`'s own CI runs are cancelled by each next merge, so no run of `main` has finished since
2026-10-19. And a finding about the gates: the two defects of this period that mattered to a person at the keyboard —
the screen console's lag (C2.4) and the exit that hung on four processors (S8.1) — were found by the author's hands, not
by a gate. The gates run in QEMU's virtual time and judge correctness exactly; they do not measure latency as felt. S8.1
added a rate check to the compatibility suite; the lag has no gate and the author deferred its speed to platforms
nearer real hardware. The plan now records both as an open question with a default: measure before changing.

## 1. Against the goals of ADR 0024

| Goal | Evidence since 2026-10-14 | Gap |
| --- | --- | --- |
| 1. Three layers; the kernel's hardware part may move to firmware | ABI-1 1.3 (C2.1: `DEVICE_MEMORY`'s window) and 1.4 (P1.4: `THREAD_QUERY` version 6), both additive; `Kernel.Arch.RV64` and `Kernel.Platform.QemuVirtRv` added with nothing in `src/Kernel`; the terminal's line discipline, the screen, the TCP/IP stack and the sockets live in services and the libc | libwitos is static and outside ABI-2 (recorded, ADR 0026); one interrupt binding per line keeps a keyboard and a network adapter off one board (open question) |
| 2. Pure .NET components run unchanged, with NuGet dependencies, built on Windows | N4.4 and V3: the suite and the portable application published on Windows by the stock SDK run as those bytes on the three ISAs, each check as recorded; the suite has 100 checks in 12 areas with 3 recorded differences; `Process.Start`, the terminal, time zones, `/tmp` and file modes arrived with N4; PowerShell 7.6.7 and CSharpRepl 0.9.2 run unchanged; Arlecchino.Commander and Terminal.Gui's UICatalog run on the screen; `HttpClient` reaches a server | TLS, ASP.NET Core, IPv6 and local sockets (W3–W4); `FileSystemWatcher` and diagnostics absent and recorded |
| 3. A new platform costs a kernel port and a recompilation | riscv64: 2,600 lines of `Kernel.Arch.RV64` and 1,034 of `Kernel.Platform.QemuVirtRv` against ARM64's 2,519 and 976, plus 282 lines of `ElfToPe` in the tool; `tests/Kernel` and the fixtures ran unchanged; layer 2 recompiled with ISA branches only where the ABI has them (the context, the trap causes, the atomics); the runtime by RID and five patches of the community port | The NuGet libraries' own suites and .NET 8 are not run on riscv64 (recorded); the community JIT's BigInteger bug costs 12 test exclusions |
| 4. A new .NET release costs little | The `release/10.0` set is 40 files (26): the 4 of the `-unix` `System.Diagnostics.Process` (N4.2), the 5 of the riscv64 port and ILC's target (V2.4), the 2 project flavours of HTTP and TLS (W2.2) and `peimagelayout.cpp` (S8.2); CoreCLR 16 against FreeBSD's 31 and Haiku's 18, the native libraries 6 against 62 and 11; `release/8.0` unchanged at 26 | Five of the sixteen CoreCLR files are the RISC-V port's own fixes and belong upstream whatever the target; the pin stays at 10.0.12 until .NET 11's GA (open question) |
| 5. Several .NET versions at once | Unchanged since N3.3: 8.0.31 and 10.0.12 on x64 and ARM64, CI's `side-by-side` job green on #172 | `release/8.0` does not build for riscv64 (recorded in the matrix and AGENTS.md) |

## 2. The kernel against RFC 0011

**Mechanisms only.** A search of `src/Kernel` for the words that would mean policy (ELF, musl, libc, dotnet, POSIX,
CoreCLR, JIT, virtio, PCI class, socket, TCP, framebuffer, ext4, FAT, path, directory, environment, file name) finds
one hit outside comments: `kernel.c` validates the boot contract's framebuffer block (page alignment, a nonzero
geometry, a stride of at least the width, one of the two pixel formats the loader may report, the bytes covering the
rows) before the platform describes the framebuffer as a device of the firmware bus. That is validation of the
handoff, as the memory map's is, and the common kernel reads none of the block afterwards; the display service alone
draws. The common kernel is 10,878 lines with its headers (9,913): the kernel lock and the request network, the
per-processor state, the kernel stack window, affinity and the device window took the 965 lines, and no policy came
back.

**The third ISA.** Phase V added `Kernel.Arch.RV64` and `Kernel.Platform.QemuVirtRv` and changed nothing in
`src/Kernel` or `tests/Kernel`; the fixtures and the root task compiled through their `#else` branches. The one
exception the pinned toolchain forced — `lld-link` links no RISC-V PE/COFF — stayed in the tool (`ElfToPe` converts
the position-independent ELF into the PE32+ EDK II loads) and the loader is unchanged. What the third ISA exposed was
not in the kernel: the toolchain's image format, musl's dynamic linker refusing initial-exec TLS in a library
`dlopen` opens, `-mno-relax` leaving `tail` as a two-instruction sequence through `t1`, the PLIC raising a masked
source on a priority write, and QEMU's PAUSE not yielding under `-icount`. Each is recorded in the V documents with the
measurement that found it.

**ABI-1.** 1.4, with 56 calls (the highest number 94; none reused). The two changes since 1.2 are a third argument of
`DEVICE_MEMORY` (the page of the region an object of at most 64 pages starts at, so that a framebuffer of 1.9 MB is
reached window by window) and a sixth version of `THREAD_QUERY`'s record with the thread's processor, which the libc's
`getcpu` and `sched_getcpu` answer from for the server collector. `THREAD_AFFINITY` existed since K7.1 and gained a
dispatcher that honours it at P1.4; no new call. Each change is additive, each has a layer-2 consumer with a test, as
§5 requires, and `ABI-Reference.md` equals the headers by the host test.

**Several processors (phase P).** Every entry into the kernel's state takes one kernel lock, processors ask each
other through a pending set and one doorbell interrupt, every spin-wait takes an architecture's pause, device lines
reach the boot processor alone, and the gates run every layer-2 scenario on one and two processors and the
applications under load on four. The design is the one RFC 0011 §7.9 sketches, and the quotas grew with it (threads
64, events 128, processes 32, with the kernel stacks taken from the allocator at P1.5, which resolved the larger half
of the design debt the previous audit recorded; the process records stay static, 11.6 MiB at 32 slots). What phase P
did not measure is the cost of the one lock under contention: S8.1 found that three threads spinning in the libc's
lock through `THREAD_YIELD`, with the runtime's suspension kicking them with activations every millisecond, made each
system call of the fourth thread cost 90 ms. The fix was in the libc (a lock held across a service round trip), the
kernel's part — a coarse lock and a round-robin dispatcher that gives a woken thread no priority — is recorded as an
open question with the default of measuring first (section 8).

**Devices.** The framebuffer travels in boot contract v7 and the device table as a device of the firmware bus; the
virtio console, keyboard and network adapter are PCI functions the platform enumerates; the kernel still programs no
device and knows no class. One binding per line is now a real limit: `BootRequest.Network` refuses a keyboard beside
the adapter, since both would bind the line of the entropy source's slot. The plan carries it as a mechanism step
(ABI-1 1.5) for the slice that first needs the network on the screen console.

## 3. The runtime against RFC 0015

**The patch set and the order of preference.** 40 files in `release/10.0` by kind: 6 in `eng` (unchanged), 16 in
`src/coreclr` (the 10 of R8.1; the riscv64 port's five — general-dynamic thread statics with the JIT's inlining of them
off through the runtime's own knob, the PAL exception helper's return address, W^X kept on where upstream turns it
off, the dispatch stub's tail call through `t2`, ILC's target details; and S8.2's branch of `peimagelayout.cpp`), 12
in `src/libraries` (the 6 of R8.1, the 4 of `System.Diagnostics.Process`'s `-unix` flavour with its three WitOS files,
the project flavours of `System.Net.Http` and `System.Net.Security`) and 6 in `src/native` (the 4 of R8.1,
`pal_mount.c` and `pal_process.c`). None changes CoreLib semantics, the GC algorithm, the JIT's code generation or a
host's resolution logic; the riscv64 thread-statics change turns an existing optimization off through the knob the
runtime already has for it. Where WitOS differed from Linux in this period the order of §4 was kept: S8.1 took an
existing knob (`DOTNET_MultiCoreJitNoProfileGather`, in the install's environment) and a libc change over any runtime
change; S8.2 took a `TARGET_WITOS` branch beside Apple's for the one rule WitOS has and Linux has not (writable and
executable at once is never granted); W2.2 took project flavours, which change what is compiled, not what it does.

**What belongs upstream.** Five of the sixteen CoreCLR files are fixes of the community RISC-V port that any musl
system would need; the 12 riscv64 exclusions of `System.Runtime.Numerics.Tests` name one bug of that JIT (dynamic PGO
with on-stack replacement and a `BigInteger` passed in registers); S8.2's branch is the Apple ARM64 rule under
another name and would generalize to "a platform that refuses W|X". None needs the `witos` target to be filed. The
plan's open question on the upstream proposal now distinguishes the bug reports, which can go first, from the target,
which waits for T4.

**Platform identity.** `OSPlatformName` is `WITOS`, the RIDs are `witos-x64`, `witos-arm64` and `witos-riscv64`, no
`IsWitOS()` API exists; the one honest consequence (an application that infers Windows from "not Linux, not macOS") is
still the first recorded difference.

**The feature profile.** ADR 0026 replaced §8's table of phases with the fact, and the fact moved in this period:
processes (N4.2), the console (N4.3) and time zones (N4.1) are reached; networking is partial — sockets, `Dns` and
`HttpClient` over HTTP/1.1, IPv4 alone, no TLS (W2.2); cryptography stays the browser's set until W3.1; diagnostics
stays absent. `expectations.json` records 3 differences for WitOS (`Platform.KnownOperatingSystem`, `FileIO.Watcher`,
`Network.IPv6`) and none for the hosts; `Compatibility-Differences.md` has 24 rows (a row for the startup profiles
joined at S8.1), each with the check that verified it; the matrix has 24 capabilities (15 supported, 5 partial, 2
absent, 2 planned) and is required current by a host test. The exclusions: 25 of the PAL suite (unchanged), 12 of the
CoreCLR tests (11), 13 methods of the library tests (1; the twelve riscv64 ones above, `RotateRight` the last of them
at V2.4's follow-up), 30 of the NuGet suites (34; N4.1 closed four, file modes and `/tmp`, as the previous audit
expected). The tools refuse an exclusion without a reason.

## 4. The system layer

**Size and provenance.** WitOS writes 11,379 lines of the libc's bottom (7,042): `files.c` 2,134, `socket.c` 1,231,
`syscall.c` 1,098, `signal.c` 990, `namespace.c` 832, `memory.c` 830, `process.c` 804, `pipe.c` 656, `thread.c` 516,
`terminal.c` 436, `shared.c` 379, `epoll.c` 344, `futex.c` 298, `system.c` 216 and the start files; libwitos 1,540
(1,411), the root task 155, the sysroot's additions 1,081. The 4,337 new lines are the Linux shape reproduced on
purpose — sockets over the network service's session, `epoll`, termios over the terminal service, pipes between
processes, `SIGCHLD` and `waitid`, the remote files' transfers — and ADR 0024's decision 4 places all of it in layer 2:
no application sees it, and every difference from Linux an application can see has its row. musl's patches are 9 (8;
riscv64's `syscall_arch.h`), compiler-rt's 1 (`clear_cache.c` left out of riscv64's builtins). ABI-2 is 1.1: `libc.so`
exports 1,647 names on x64, 1,644 on ARM64 and 1,647 on riscv64, recorded in `build/abi2.json` and required by every
sysroot build.

**The two fixes.** Both defects of S8 were in this layer and both were found outside the gates. S8.1: the libc held
its one table lock through every namespace round trip, so a process writing a file 4 bytes at a time while three
threads contended for the lock — CoreCLR's startup profile at exit, written only with two or more processors — took
minutes; the fix moved a volume file's transfers outside the lock behind a session "turn", as pipes, the terminal and
sockets already were, and the metadata calls still hold the lock while they wait (recorded). S8.2: an arm64
ReadyToRun image carries base relocations in its code, CoreCLR opens the section with `PAGE_EXECUTE_READWRITE` on
every Unix but Apple's ARM64, and the libc's rule refused it; no test had an executable image with code relocations
until the SDK's. Each got its gate: a rate check in the compatibility suite on four processors, and `libc_hello.c`
flipping a page of a shared memory file between writable and executable on the three ISAs. The differential check of
the sysdeps against Linux stays the open default; neither defect was a difference from Linux's semantics.

## 5. The services and the applications

**ADR 0025.** Seven services now run in processes of their own — the device manager (2,626 lines), four virtio drivers
(block 698, console 658, input 671, net 724), the namespace service (1,729), the terminal service (1,291), the display
service (703) and the network service (1,239) — each started by the process manager with one granted capability,
answering ready with the endpoint it offers, stopped in order before the root task ends. `OutWit.OS` is 17,748 lines
(8,476): `FileSystems` 5,674, `Networking` 4,550, `Display` 2,093, `Hardware` 1,844 (the virtio transport and the
split virtqueue shared by three drivers), `Storage` 1,605, `Terminals` 581; each area is used by a service, the
`Display` area also by the host's judge of the screen, and 4,958 lines of host tests (2,820) cover them, the TCP/IP
stack against a simulated segment that loses and reorders frames. Nothing of it is in the kernel.

**RFC 0020 and ADR 0027.** PowerShell is an ordinary replaceable application from the data volume; the line
discipline is the terminal service's and the libc keeps nothing of it; the screen is the display service drawing a
terminal emulator shared with the host's judge, and a board without a display is unchanged. Base applications are a
layer of the data volume from four pinned publications (`PowerShell`, `Arlecchino.Commander`, `DotNet.Sdk`,
`CSharpRepl`), with launchers from upstream's apphost, no `/bin/sh`, and the installer in the boot package; the .NET
SDK is laid per ISA, since its assemblies carry ReadyToRun code of their RID, and riscv64 has no publication
(recorded). C3, the native module, is the horizon after M8.

**The network (phase W).** Decision 2 of W0, the author's: the stack is managed C# in the NativeAOT service. The
service owns the packet channel and the timers on one thread, each session a thread under one lock; a request never
waits (`EAGAIN`, then a readiness event); the libc serves `AF_INET` over one session per process and reports readiness
to `poll` and `epoll`, which CoreCLR's socket engine needs. One session per process is a serialization point every
socket call of a process crosses; it is right for now and is the thing to measure first when ASP.NET Core (W3.2)
puts a server on it.

## 6. Evidence and CI

**Scenarios.** The `test` suite boots 34 scenarios on x64, 28 on ARM64 and 28 on riscv64 (27 and 21), each layer-2
scenario on one and two processors; `runtime-witos` boots 26 runtime scenarios per ISA (17), from the M3 acceptance
through the services, the terminal, the screen, PowerShell, the file manager, the installer, CSharpRepl, the
compatibility suite, the libraries and the processors; `runtime-side-by-side`, `runtime-published` and
`runtime-nuget-suites` are commands of their own. Every boot runs in QEMU's virtual time with the instruction clock
scaled to the processors (P2) and is judged by its required lines.

**CI.** 21 checks per pull request: `boot`, `boot-arm64`, `linux-selftest`, `itanium-reference`, `linux-host` on three
ISAs, `nuget-suites` on two ISAs and four groups, `side-by-side` on two ISAs, `windows-publish` and
`published-on-windows` on three ISAs. #172 merged on 2026-10-20 with all 21 green; #173, #174 and #175 are stacked on
it and green but for their `linux-host` legs still running at the time of writing.

**`main`'s own runs.** The workflow cancels the run in progress of a ref when a new push arrives, and `main` received
seven merges between 2026-10-20 03:10 and 13:58 UTC: the runs of #166, #167, #168, #170 and #171 were cancelled by the
next merge, the run of #169 failed in `nuget-suites (arm64, test-frameworks)` on the known NUnit async-void flake
(the guest passed 6381 of 6475, the host 6382) and was cancelled before anyone looked, and the last run of `main` that
finished green is #164's (P2, 2026-10-19). The evidence of every step is its pull request's run, which is sound; but
a branch whose own runs never finish cannot tell a flake from a regression that slipped through a stack. Done with
this audit: the workflow cancels a run in progress for every ref but `main`, whose runs now finish one at a time, the
newest waiting; the next merge's run is the first that will be read.

**Flakes, 2026-10-14 to 2026-10-20.** Recorded in the plan with the rule that a recurrence gets a reproduction step:

- *On the runners' hosts.* MemoryPack's test runner dies (2026-10-15 twice, 18, 19 and 20 on #172 and #173): exit 139
  or 134 with no summary, and #173's log shows the host's CoreCLR throwing `AccessViolationException` in
  `VirtualDispatchHelpers.VirtualFunctionPointer` under FluentAssertions' `BeEquivalentTo`, in a different test each
  time; never in the guest, never in the container. The tool runs a host suite once more when a signal killed it
  (#151, 2026-10-18), but no crashing job has contained that code yet: every branch of the stack C2.4–S8.2 is based
  before #151, and a pull request's run merges its head with its base branch, not with `main`. Math.NET's `numacc1`
  accuracy once. Roslyn's
  `CompilerResolverTests` on the runner's dotnet layout (N0.2, excluded with its reason). Fetches of libc-test from
  repo.or.cz and of ncurses from GNU's redirector (fixed with a list of mirrors).
- *In the guest, timing.* NUnit's `DelayedConstraint` family three times on one processor (P1.x branches) and
  `AsyncVoid` twice (#123, `main` after #169); the `serialization-numerics` group near the job's wall limit once
  (#171); the arm64 `runtime-coreclr-tests` run stopped by its wall limit twice (now two hours); the instruction clock
  aggregated over four processors (fixed in P2).
- *In the guest, not understood.* The CoreCLR GC test's SIGSEGV once (2026-10-12); `ConcurrentBag` and
  `ConcurrentQueue` on riscv64 once; `System.Collections.Concurrent.Tests` at the 32-thread edge on riscv64 once (64
  since P2); the suite's `Network.TcpEcho` on ARM64 once (#170).

The one that recurs is MemoryPack on the host, six times. Two things follow. A stacked branch's run proves the branch
with its base, not with `main`: a fix merged into `main` meanwhile (#151 here) reaches the stack's runs only when a pull
request is retargeted or its branch merges `main` — which the audit's own branch now does. And the first crash that
goes through the retry and still fails is a `class:` exclusion with that reason or a step that finds the runner's
defect, not a seventh rerun.

**What the gates do not measure.** Both defects a person felt in this period passed every gate: the screen console
under TCG lags (C2.4, closed by the author's decision to judge speed nearer real hardware), and `exit` on four
processors hung (S8.1, a 90 ms system call under a lock storm, correct and slow). A gate in virtual time with required
lines judges what happened, never how long a person waited; `shell --accel` on WHPX or KVM is the only place latency
is felt, and it is a session, not a gate. S8.1 added the one rate gate the plan has (500 small writes under contention
at 1,000 per second of the guest's clock). The plan records the remaining latency debt — metadata calls under the
libc's table lock, the spin-yield of `__wit_lock`, the woken thread's latency under the round-robin dispatcher, the one
kernel lock — with the default of measuring a live guest through QMP, as S8.1 did, before any change.

## 7. The documents

| Document | Found | Done |
| --- | --- | --- |
| `PLAN.md` | §3 said the libc has no sockets (W2.1 closed on 2026-10-19), counted 21 runtime scenarios (26) and 37 patch files (40), dated the goals' state 2026-10-19, listed the known defects to 2026-10-19 and had no open question for latency or the write-behind | §1 and §3 refreshed, the audit referenced, the defects of 2026-10-20 added, two open questions added with their defaults |
| `README.md` | "Current status" named two ISAs and phases A, K, S, R, D and N; "Scope" called phase C begun and phase W "later", and the root task's triples were two | Rewritten for three ISAs, phases C (C3 apart), V and P complete and W at HTTP; C2.5, S8.1, S8.2 and the network paragraph added |
| `AGENTS.md` | The state sentence of "Project direction" described ABI-1 1.2, two ISAs and R2–N3; the rules below it were kept current step by step | The sentence rewritten; the rules unchanged |
| `Plan-Completed.md` | The reading rule said a step closes on x64 and ARM64 | Three ISAs since V3, with the recorded exceptions |
| RFC 0011 §9.1, §10.2; RFC 0015 §4, §8 | The previous audit's two rows: revised by fact under ADR 0026 at N4.5 (`libwitos` static, the proposal with T4, the profile as a fact) | Resolved |
| `@Docs/WitOS-Console-Logos.md` | Added at C1.4 beside the vision documents without an ADR | A design asset, not a change of a vision document; noted, left as it is |
| `Compatibility-Differences.md`, `Compatibility-Matrix.md`, `ABI-Reference.md` | Equal to the tree by host tests | — |
| Implementation Strategy §157 | The mapping of milestones to phases holds: M4–M6 closed, M7 is phase W | — |

## 8. Debt and follow-ups carried into the plan

- W3.1 next: TLS over a pinned OpenSSL with a root store, `SslStream` and `HttpClient` over HTTPS against the runner's
  test CA; the cryptography flavour of R4.1 leaves with it.
- `main`'s runs finish since this audit (section 6); the first one to finish is to be read, a failure in it
  investigated, not rerun.
- MemoryPack on the runners' hosts: the first crash that goes through the retry of #151 is an exclusion with its reason
  or a step; the crash's signature is recorded in the plan.
- One binding per line: a mechanism step (ABI-1 1.5) when the network meets the screen console or a fifth driver.
- Latency the gates do not measure: measure a live guest first (QMP); a step on the woken thread's latency (RFC 0005)
  if NUnit's `DelayedConstraint` family recurs on four processors or a session hangs again.
- The namespace service's write-behind (a journal transaction per `WRITE`): a storage step by the author's decision;
  the runtime's profile gathering stays off through its knob until then.
- Upstream: the RISC-V port's five fixes and its JIT's BigInteger bug can be reported before T4; the `witos` target
  waits for the own triple (ADR 0026).
- riscv64: the NuGet libraries' own suites and .NET 8 (recorded exceptions), the SDK (no publication).
- Open questions unchanged: hierarchical quotas, the static process records, `libwitos.so.1`, the .NET 11 pin, the
  differential check of the sysdeps, the chip boundary.

## 9. What the plan looks like now

`PLAN.md` is unchanged in form: the goals with their state as of 2026-10-20, the architecture, where the program stands
on one screen with the network as far as HTTP, the decisions, the phase table with C, V and P complete, the open steps
(T4, D7, W3.1, W3.2, W4), the rules and the open questions, now with latency and the write-behind among them. Every
completed step with its evidence is in `Plan-Completed.md`, the two audits are linked from the plan, README and
AGENTS.md describe the tree of S8.2, and the next step is W3.1.
