# Program audit, 2026-10-14

**Scope.** Everything built since [ADR 0024](ADR-0024-Three-Layers-and-Unix-Form-Runtime.md) (2026-10-06): phases A, K,
T1–T3, S, R, D and N1–N3 of `PLAN.md`, 119 completed steps in eight days. The audit asks three questions: does the
work still follow the decision and its specifications ([RFC 0011 v3](../RFC-0011-Kernel-Architecture-and-ABI.md),
[RFC 0015 v2](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md), the Implementation Strategy §157); is
each completed step as good as the plan says; and do the documents still describe the tree. It ends with what changed
in the plan.

**Method.** The documents were read again against the tree at commit `12585ae` (branch `n3.3-side-by-side`): the
kernel's sources were searched for every word that would mean policy, the quotas and the patch sets were counted, the
exclusion lists and the compatibility differences were compared with the rules that govern them, and the state of CI
was taken from the open pull requests. Nothing was booted for this audit; the evidence of each step is the guest
acceptance its own document records.

## Verdict

The program is on its line. The three layers exist as ADR 0024 draws them, both interfaces are versioned and checked
by tools, the runtime is unchanged upstream .NET in its Unix form with a patch set well inside the budget, and every
difference from Linux an application can see is explicit and recorded. Five goals of ADR 0024: three are proven (1, 4,
5), one is proven in substance but not in its literal wording (2: the application published on Windows and run on
WitOS), one has its kernel-level evidence and waits for phase V (3).

What is not in order is bookkeeping, not architecture: the two most recent pull requests are red in CI for two
environmental reasons, the plan's state section and open questions had fallen behind the steps, and README and
AGENTS.md still described the state of R2–R3.

## 1. Against the goals of ADR 0024

| Goal | Evidence | Gap |
| --- | --- | --- |
| 1. Three layers; the kernel's hardware part may move to firmware | The kernel's policy left at K8.4a–K8.4c; ABI-1 1.0 at K8.4d, 1.1 at R7.2b, 1.2 at D1.2, each additive; ABI-2 1.0 at N3.3, checked by every sysroot build; `Kernel.Arch.*` and `Kernel.Platform.*` hold every instruction and board device | libwitos is static and outside ABI-2 1.0 (recorded, N3.3) |
| 2. Pure .NET components run unchanged, with NuGet dependencies, built in Visual Studio on Windows | R6: an application published by the stock SDK without a RID runs unchanged; N1: 64 checks in eleven areas, 4 recorded differences; N2.1 and N2.2: six libraries, 66,383 of their own tests, 0 failures, 34 exclusions with reasons | The guest runs a copy published on the Linux host; the literal chain Windows publish → WitOS run is not demonstrated (plan step N4.4). `Process.Start`, console input and time zones are absent (N4) |
| 3. A new platform costs a kernel port and a recompilation | ARM64: about 2,300 lines of `Arch` and `Platform` over the common kernel; every layer-2 source is one for both ISAs; the runtime differs by RID alone | The proof is a third ISA (phase V) |
| 4. A new .NET release costs little | The `release/10.0` set is 26 files, 10 of CoreCLR against FreeBSD's 31 and Haiku's 18, 3 native-library files against 62 and 11; R8.1 carried it from 10.0.8 to 10.0.12 with no line written by hand; R8.2 rehearsed .NET 11 RC1 | The pin moves to .NET 11 after its GA; the set is not yet proposed upstream |
| 5. Several .NET versions at once | N3.3: 8.0.31 and 10.0.12 in one install, the host picks by `runtimeconfig` and `rollForward`, 10 of 10 checks in every combination on both ISAs | CI's `side-by-side` job is red (section 6) |

## 2. The kernel against RFC 0011

**Mechanisms only.** A search of `src/Kernel` for the words that would mean policy (ELF, musl, libc, dotnet, POSIX,
path, directory, file name, environment, CoreCLR, JIT, package table) finds them only in comments that explain why a
quota has the value it has, or that say the kernel reads no image, name or path. `witos/package.h` says the libc
validates the package's table; `witos/flat.h` says the flat format is a boot format and not an application format.
The common kernel is 9,913 lines with its headers, against the 9,241 of 2026-10-06 of which about 3,900 were policy:
the policy left and channels, memory objects, devices, interrupts and DMA, processes, UTC and the SMP groundwork took
its place.

**ABI-1.** Version `(major << 16) | minor`, 1.2; the three changes since 1.0 are an operation's new state (`BUDGET`), a
flag (`STACK_ADJACENT`) and a delivery rule (a device handle that is its origin's last reference), each additive, each
needed by a layer-2 consumer with a test, as §5 requires. `ABI-Reference.md` equals the headers by a host test. No call
of the list K8.4a removed came back.

**Quotas.** `limits.h` grew with the runtime: the kernel's object table from 64 to 4096, a system layer process's
handles to 2048, reservations to 8192, pages to 262,144 (1 GiB), threads to 32, events to 64, objects to 1024. Each
value is named with the step and the measurement that set it. This is not drift (the quotas are the kernel's and a
process's profile), but it is a design debt: the per-process arrays are static per registry slot and the registry has
eight slots, so the kernel's static memory grows with every quota, and the next consumer that fills a table costs
another kernel change. The plan carries it as an open question to settle in phase P, where the process record changes
anyway.

**The four defaults of ADR 0024.** The libc (musl), the kernel toolchain (clang and lld) and the signals (minimal
POSIX signals in the libc over fault delivery and activations) are confirmed by the evidence of phases S–N: no
Linux shape leaked above layer 2 (every difference an application sees is in `Compatibility-Differences.md` and each
row names the check that verified it), MSVC builds no part of any kernel image, and CoreCLR's PAL and System.Native
run on the signals the libc gives them, with the PAL suite at 280 of 305 and none of the 25 exclusions about signals
(9 want executable anonymous memory, 5 hold 64 threads, 4 create directories, 3 start processes, 2 write outside their
working directory, 1 uses `chmod`, 1 wants `EFAULT` from a pipe). The chip boundary stays as the default: no hardware
partner.

## 3. The runtime against RFC 0015

**The patch set and the order of preference.** The 26 patches of `release/10.0` by kind: 6 in `eng` (build identity,
the toolchain file, the configure, the measured `tryrun.cmake`), 10 in `src/coreclr` (the build, the DAC export, the
GC's and the PAL's platform branches, NativeAOT's targets, ILC's target name, one test's CMake), 6 in `src/libraries`
(the RID graphs, `OperatingSystem`, `Environment.WitOS.cs`, CoreLib's project, the cryptography project's flavour) and
4 in `src/native` (the static apphost, libunwind's CMake, the libraries' CMake, globalization's). None changes CoreLib
semantics, the GC algorithm, the JIT or a host's resolution logic. Where WitOS differs from Linux the order of §4 was
kept: configure checks first (the 30 `try_run` answers are measured in the guest, never reasoned), runtime knobs
second (`DOTNET_GCTotalPhysicalMemory` from the root task, invariant globalization in a runtimeconfig), `TARGET_WITOS`
branches third (the stack size in `pal.cpp`, the process barrier, the dumps). The 8.0 set is 25 files and a backport,
as §7 allows for a release without the target.

**Platform identity.** `OSPlatformName` is `WITOS`, the RIDs are `witos-x64` and `witos-arm64`, no `IsWitOS()` API
exists, and `IsWindows`, `IsLinux`, `IsMacOS` and `IsFreeBSD` are false; N1 records the one honest consequence (an
application that infers Windows from "not Linux, not macOS").

**The feature profile (§8).** The "later" column is reached for globalization (ICU from source, R4.3), files (the
namespace service, D5) and persistence (D6). It is not reached for processes (`Process.Start` is unsupported), the
console (write-only over the kernel log, no input) and time zones (no tzdata). Those three are what the gate N4 of §9
names, and the plan details them as N4.1–N4.3.

**The compatibility differences.** Every "absent" or "unsupported" cell of §8 fails explicitly and has a row in
`Compatibility-Differences.md` with the check that verified it; `expectations.json` records 4 differences for WitOS
and none for Windows or Linux; each of the 34 NuGet suite exclusions, 25 PAL exclusions, 11 CoreCLR test exclusions
and 1 library test exclusion names its reason, and the tools refuse one without. Four of the NuGet exclusions and two
of the N1 differences are the same two gaps (file modes on a volume, no `/tmp`), which N4.1 closes.

## 4. The system layer

**Size and provenance.** WitOS writes 7,042 lines of the libc's bottom (`src/Substrate/libc`: `files.c` 1,682,
`signal.c` 863, `syscall.c` 758, `memory.c` 758, `namespace.c` 558, `process.c` 459, `thread.c` 456, `shared.c` 361,
`futex.c` 298, `pipe.c` 224, `system.c` 165, the start files), 1,411 of libwitos, 118 of the root task and 565 of the
sysroot's additions; musl, LLVM's runtimes, compiler-rt's builtins, ICU and the kernel headers for the runtimes' build
are pinned and change only through 8 musl patches (two `syscall_arch.h`, `dynlink.c`'s two hooks, the `membarrier.h`
linkage guard, four of musl's own later fixes). This is the one place where Linux's shape is reproduced on purpose
(pipes, `/dev/shm`, `flock`, `poll`, the signal frames), and ADR 0024 decision 4 places it there: private to layer 2.
Its tests are WitOS's own (`libc_hello.c`, `process_init.c`, `devices_init.c`), musl's libc-test, upstream's PAL suite
and the .NET suites above it; no differential run of the sysdeps against Linux exists. The plan records that as an open
question with the default that the present coverage suffices.

**ABI-2.** `libc.so` exports 1,647 names on x64 and 1,644 on ARM64, none `__wit_`-prefixed, recorded in
`build/abi2.json` and required by every sysroot build. RFC 0011 §9.1 lists `libwitos.so`; the tree has `libwitos.a`
and the plan's open question says when that changes. §9.1 also calls the `*-unknown-witos` triple's sysroot the
authoritative header set; the triple is still `*-unknown-linux-musl` (T4 open), and the header set is the sysroot of
R1.2a. Both are facts the RFC's next revision should take.

## 5. The services (ADR 0025)

The device manager, the virtio-blk driver and the namespace service are NativeAOT programs in processes of their own,
start through the process manager with one granted capability, receive their authority over that channel, stop in
order before the root task ends, and reach the kernel only through libwitos and `OutWit.OS`, which grows only by what
a service uses (8,476 lines, tested on the host with 2,820 lines of tests). The block, filesystem and namespace
contracts follow the storage document, FAT comes from `OutWit.Common.Fat` on nuget.org unchanged, and ext4 is judged
by e2fsprogs. Nothing of this is in the kernel, and the kernel's one change for it (a device handle moving with its
origin's last reference) is a mechanism.

## 6. Evidence and CI

**Scenarios.** The `test` suite boots 27 scenarios on x64 and 21 on ARM64 (release and self-test kernels, the six
layer-2 scenarios, the fault and rejection scenarios); `runtime-witos` boots 17 runtime scenarios per ISA; every one
runs in QEMU's virtual time and is judged by its required lines, never by an exit code alone. CI runs the Windows
suites, the Linux parity suites, the runtime on both ISAs, the NuGet suites by ISA and group, and the side-by-side
install by ISA.

**What is red.** PR #120 (N3.1) is merged. PR #121 (N3.2) and PR #122 (N3.3) are not: both fail `side-by-side` on
both ISAs, and #122 also fails `nuget-suites (x64, roslyn)`.

- *`side-by-side`.* The 8.0 build's crosscomponents configure (`build-runtime.sh ... -hostos linux -component
  crosscomponents`) stops at `pal/src/configure.cmake:1279`: "Cannot find liblttng-ust-dev". The WitOS patch of that
  file exempts `CLR_CMAKE_TARGET_WITOS`, and the main configure passes; the crosscomponents configure of the host-side
  DAC does not take the exemption and needs LTTng's header as 10.0's does. The container image and the `linux-host`
  job install `liblttng-ust-dev`; the `side-by-side` job's package list omits it. The guest acceptance passed on both
  ISAs in the container; the fix is the job's package list (plan step N0.1).
- *`nuget-suites (x64, roslyn)`.* The host run of `Microsoft.CodeAnalysis.UnitTests` on GitHub's runner fails one
  test, `CompilerResolverTests.ResolveReturnsNullForNonHostAssembly`, with "Could not load file or assembly
  'System.Runtime, Version=10.0.0.0'", and the tool rightly stops: a test that fails on the host leaves the selection
  with its reason. In the container the suite passes. The difference is the runner's dotnet layout (`DOTNET_ROOT`
  from `setup-dotnet`, several runtimes) against the SDK 10.0.109 the suite's `global.json` takes; the test resolves a
  compiler assembly against the host. Either the test leaves the selection with that reason or the host run is made
  independent of the runner's dotnet (N0.2).

**Known defects without a step.** `LargeObjectAlloc` of the `GC/GC` group faults now and then in
`TableFreeSingleHandleToCache` (R7.2b; `NoGC` always does and is excluded with that reason); one CI run saw a SIGSEGV
in `GetAllocatedBytesForCurrentThread`; `System.Threading.Channels.Tests` once reported "Out of memory" (R7.3, outside
the selection). None is understood. The plan keeps them visible with the rule that a recurrence gets a reproduction
step, not an exclusion.

## 7. The documents

| Document | Found | Done |
| --- | --- | --- |
| `PLAN.md` | The headers of R4, D5 and N2 were `[ ]` while every sub-step was `[x]`; §3 was one paragraph of 60 lines that said "phase D has begun" after D6.3 and listed writable storage as missing; six questions of §7 were resolved by K3.2, K4/K8.4c, K6, S3.1, R3.2b and R3.3 and still open; the K9 note gave the quota as 256 MiB after N2.2 made it 1 GiB | Rewritten: goals with their state, a phase table, the open steps alone (T4, N4.1–N4.5, V, P, W), N0 for the CI fixes, resolved questions in a table with where each was resolved, open ones with defaults. The completed steps moved verbatim to [Plan-Completed.md](Plan-Completed.md) with the three headers corrected |
| `README.md` | "Current status" ended with "CoreCLR and the JIT next" and "Scope and next work" was one paragraph of the whole history | Both rewritten as short lists that point to the plan and the completed-steps document |
| `AGENTS.md` | "Project direction" described the guest as NativeAOT passing M3 (R2); the plan rule did not know the completed-steps document | Both sentences updated |
| RFC 0011 v3 §9.1, §12 | `libwitos.so` and the `*-unknown-witos` sysroot are the design, not yet the tree; the six open questions of §12 are resolved | Left as written: vision documents change by ADR. The plan's §7 records the resolutions and N4.5 revises the RFC |
| RFC 0015 v2 §4 ¶1, §8 | The first paragraph describes the Windows-form overlay of 2026-10-06 as present; the profile table's "first guest profile" column is history for globalization, files and persistence | Left as written for the same reason; N4.5 revises §8 by the facts |
| `Compatibility-Differences.md`, `ABI-Reference.md` | Equal to the tree | — |

## 8. Debt and follow-ups carried into the plan

- N0.1, N0.2: the two CI failures above.
- N4.1: time zones, `/tmp`, file modes on volumes, the process environment's default (today `root_task.c` sets
  `PATH` and `DOTNET_GCTotalPhysicalMemory`: a layer-3 knob set by layer 2, which RFC 0015 §4 allows as preference 2,
  but which belongs in the process manager's description of an application's environment).
- N4.2: `Process.Start` over `posix_spawn`, which needs pipes between processes (today a pipe lives within one
  process).
- N4.3: the terminal of RFC 0020, in two slices (the device and service; the standard streams and `System.Console`).
- N4.4: the literal goal 2 chain, Windows publish → WitOS run, in CI.
- N4.5: the public compatibility matrix (Strategy §145), the revision of RFC 0015 §8 and RFC 0011 §9.1 by the facts,
  the upstream proposal.
- Open questions: hierarchical quotas; the kernel's static per-slot tables; `libwitos.so.1`; the .NET 11 pin; a
  differential check of the sysdeps against Linux; the chip boundary.

## 9. What the plan looks like now

`PLAN.md` holds the goals with their state, the architecture, where the program stands (one screen), the decisions
and the fate of ADR 0024's defaults, the phase table and the open steps, the rules, and the open questions. Every
completed step with its evidence is in `Plan-Completed.md`, in the words the plan used when the step closed; a step
that closes from now on moves there in the same pull request, and the plan stays a list of current work.
