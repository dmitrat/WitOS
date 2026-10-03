# WitOS

WitOS is an experimental operating system built around a minimal native kernel and standard upstream .NET. The intended system places portable OS services, applications and shells above the runtime. Hardware-specific mechanisms stay behind explicit interfaces.

The hardware layer may eventually be supplied in firmware. The first implementation uses QEMU and UEFI to test the same separation without custom hardware.

**Roadmap:** [План запуска .NET / PLAN.md](PLAN.md) — текущий этап, оставшиеся работы и критерии готовности.

## Current status

**Current local implementation: upstream .NET 10.0.8 NativeAOT and standard CoreLib execute inside WitOS. P5/M3 is complete in the tested x64/UP profile: GC, managed exceptions, finalization, standard Thread/Monitor/TLS, failure recovery and repeated combined acceptance pass together. P6.1–P6.3 are complete and P6.4 has reached static DLL TLS. Current interfaces: user ABI v48 / boot ABI v4; the kernel banner prints both from the headers. Work now follows the [Q2 consolidation and ARM64 plan](@Docs/Implementation/Q2-Consolidation-and-Arm64-Plan.md) before P6 continues.**

The kernel boots independently through UEFI and runs separately built native components in ring 3 with private mappings and handles. Its bounded PE loader parses complete files inside the guest, maps sections and applies relocations. A freestanding C startup layer receives image metadata, runs native initializers and enters the program in user space. The component writes through a checked syscall and exits; its faults are contained while the kernel runs the next component. Within a component, up to four user threads can run with timer preemption, separate stacks/TLS and blocking join. Manual/auto-reset events, sleep and absolute deadlines work with kernel idle when all threads are blocked. M1 paging, protection, timer and kernel-context checks remain part of every successful boot.

The C# code in `tools/` runs on the development computer; the separate NativeAotBoot executable runs in the guest through the actual runtime and collector. Standard CoreCLR/JIT applications remain P6. See the [M3 profile and reproduction commands](@Docs/Implementation/M3-NativeAOT-Profile.md) and [P5 audit](@Docs/Implementation/P5-Completion-Audit.md).

The [Q1 quality follow-up](@Docs/Implementation/Q1-Quality-Hardening.md) is complete: strict evidence/runner checks, both GC hijack paths, named native object manifests and measured parser coverage with sanitizer/fuzz lanes. The next implementation stage is P6.1.

## Quick start

Development host for this first slice:

- Windows x64.
- .NET SDK **10.0.300** (latest patch in that feature band is allowed).
- Visual Studio / Build Tools with **Desktop development with C++**, including the x64 MSVC compiler, MASM and Windows SDK headers.
- Git.
- 7-Zip at its normal installation location, for extracting QEMU.

On some Windows hosts, process creation stalls system-wide for up to about 20 seconds while the host process tests create and kill job trees: an unrelated process start waits while the CPU stays idle. The timing-bounded host process tests (`ProcessesTests`) then fail with cleanup or deadline errors; rerun them alone before suspecting the tool. Excluding the repository from Microsoft Defender real-time scanning still helps build speed, because Defender inspects every new binary under `artifacts/` and `.tools/`, but it did not remove these stalls.

From the repository root:

```powershell
dotnet run --project tools/WitOS.Dev -- setup
dotnet run --project tools/WitOS.Dev -- doctor
dotnet run --project tools/WitOS.Dev -- run
```

`setup` downloads QEMU **11.1.0**, verifies a pinned SHA-512 digest and extracts it into `.tools/`. It does not run the installer, edit PATH, or install a Windows service. The download is approximately 197 MiB. The first image build also downloads pinned .NET GC/PAL interface headers and their license. After these files are cached, builds and VM tests can run offline.

`run` builds an x64 EFI executable, packages it into a 32 MiB FAT16 disk image and boots a headless QEMU VM with software emulation, one CPU, 256 MiB RAM and no networking. No Hyper-V configuration is required.

Expected guest output includes:

```text
[BOOT] UEFI x64 adapter
[BOOT] ExitBootServices OK
WitOS user ABI v48, boot ABI v4
Build: <git-revision> | x64 | Debug
[TEST-BEGIN] Boot.Contract
[TEST-PASS] Boot.Contract
[TEST-PASS] Cpu.KernelStack
Kernel stack: ...
[TEST-PASS] Cpu.ExceptionTables
CPU: x86_64
Usable memory: ...
[TEST-PASS] Memory.KernelPaging
[TEST-PASS] Memory.StackGuards
...
A: 1
B: 1
A: 2
B: 2
A: 3
B: 3
[TEST-PASS] Cpu.Timer
[TEST-PASS] Scheduler.Preemption
[TEST-PASS] Scheduler.RegisterState
[USER] Hello from ring 3.
...
[TEST-PASS] User.Isolation
Kernel initialized.
Hello from WitOS.
[TEST-PASS] Boot.Hello
```

The host tool returns exit code 0 only after checking both guest markers and the QEMU exit status.

## Build and test

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- build
dotnet run --project tools/WitOS.Dev --configuration Release -- test
dotnet run --project tools/WitOS.Dev --configuration Release -- release
dotnet run --project tools/WitOS.Dev --configuration Release -- build --arch arm64
dotnet run --project tools/WitOS.Dev --configuration Release -- test --arch arm64
```

The native kernel currently always builds in Debug mode, including when the host tool uses Release.

`--arch arm64` builds `BOOTAA64.EFI` with the MSVC ARM64 cross tools (Visual Studio component `Microsoft.VisualStudio.Component.VC.Tools.ARM64`) and boots it on the QEMU `virt` board with GICv3. That kernel runs the kernel foundation and EL0 components through the common user-mode policy: it checks the boot contract, installs its EL1 exception vectors, initializes the physical page allocator, installs its own translation tables with guarded stacks, opens the boot package, seeds ChaCha20, checks the generic counter, runs the foundation self-tests, preempts two kernel workers with the GICv3 virtual timer while checking their general, NEON and FPCR state, runs the shared user isolation tests with the ARM64 port of the user fixture (private address spaces, system calls and handles, 18 contained EL0 faults, sparse user memory, the timer budget and preserved state across preemption), reports `Hello` and exits through Arm semihosting. Its suite requires success at 128 and 512 MiB, rejection of an invalid boot contract and of an overlapping memory map, a reported breakpoint, undefined instruction and data abort, the six memory permission faults of the x64 suite, and a timeout after a successful boot.

Every test scenario builds a self-test kernel: the sources in `tests/Kernel.X64` and the white-box checks guarded by `WITOS_SELFTEST` run during boot before `Hello`. `release` builds the kernel without them, rejects a link map that names self-test code, and boots it with 128 MiB and 512 MiB of RAM; that kernel initializes, reports `Hello` and exits without running user components.

The integration suite boots nineteen real VM scenarios:

- Normal boot with 128 MiB and 512 MiB RAM, including real-page read/write, reserved-memory, exhaustion, reuse and invalid-map checks.
- An additional Intel Nehalem CPU-model boot and bounded Intel/AMD cache-discovery checks.
- Rejection of an invalid boot-contract version, overlapping firmware memory regions and a missing required HPET clock.
- Actual breakpoint, divide error, invalid opcode, general protection, page fault and double fault.
- Hardware-enforced refusal of code writes, data execution, lower/upper stack guard access, read-only alias writes and unmapped alias reads.
- Detection and termination of a deliberately hung guest after full initialization.

Normal boots also verify map/protect/unmap behavior, aliasing, TLB invalidation, timer delivery, progress of both preempted contexts and preserved GPR/SSE state. Exception tests validate vector, error code, register frame, fault address and stack selection. The double-fault test deliberately invalidates the main stack and requires diagnostics from the emergency stack.

Successful boots also require 178 user groups: ring-3 entry, ABI/handles, private memory, user-fault containment, safe return state, timer budgeting, register/flag preservation, zero-fill and resource teardown, plus sparse reservations, commit/decommit/protect/release, recoverable exhaustion and hardware memory faults, plus user-thread preemption, TLS/register state, join/cycle handling, slot reuse and child-fault cleanup, plus event state/rights, wakeups, close/timeout ordering, signal handoff and kernel idle, plus PE validation/loading, relocation, BSS, allocation rollback and hardware section protection, plus native C startup, image descriptors, initializer rollback/run-once behavior and structural unwind validation, plus the upstream GC memory/discovery adapter, hardware protection, atomic snapshot copies, physical pressure, GC events, monotonic deadlines, recursive mutexes, blocking handoff, Crst lifecycle checks, atomic committed-memory reset, native C++ allocation/reclamation, static compiler TLS, dynamic C++ TLS lifecycle, kernel-backed PAL thread discovery, PAL memory/event/wait services, detached PAL worker lifecycle, per-thread native error diagnostics, module lookup/bounds, immutable configuration values, UTF conversion, native process cleanup and CPU cache discovery. They run within the same real VM.

Every test creates fresh firmware variable storage. A timeout, unexpected exit, panic or missing success marker fails an ordinary boot test.

Outputs:

```text
artifacts/x64/boot/BOOTX64.EFI       Native UEFI adapter + kernel
artifacts/x64/boot/WitOS-x64.img    Bootable FAT16 disk image
artifacts/x64/boot/WitOS.pdb        Native symbols
artifacts/x64/boot/WitOS.map        Native link map
artifacts/x64/boot/build.txt        Source revision and toolchain
artifacts/x64/boot/UserFixture.pe  Separately linked isolation/memory fixture
artifacts/x64/boot/ThreadFixture.pe Separately linked thread/TLS fixture
artifacts/x64/boot/WaitFixture.pe   Separately linked event/deadline fixture
artifacts/x64/boot/PeFixture.pe     Complete relocatable guest PE image
artifacts/x64/boot/PeFixedFixture.pe Complete image for preferred-base loading
artifacts/x64/boot/BootstrapFixture.pe Freestanding C startup and unwind fixture
artifacts/x64/boot/GcMemoryFixture.pe Upstream GC memory interface adapter fixture
artifacts/x64/boot/gc-memory-build.json Source hashes, build identity and link evidence
artifacts/logs/                   Serial, stderr and outcome logs
```

Failure-injection images have separate output directories and do not replace the normal boot image.

## NativeAOT reference experiment (hosted)

The runtime investigation pins .NET 10.0.8, verifies selected upstream source hashes and package provenance, and runs a real NativeAOT reference workload **on Windows**:

```powershell
dotnet run --project tools/WitOS.Dev -- runtime-audit
dotnet run --project tools/WitOS.Dev -- runtime-probe
```

This is separate from the guest VM. It checks GC/finalization, exceptions, threads/TLS, waits, Tasks and clocks, then reports native OS imports. Sources and NuGet packages are cached under `.tools/`; logs and reports are under `artifacts/runtime-probe/`. A separate CI workflow publishes the hosted reference artifacts.

See [RFC 0015](@Docs/RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md) and [experiment notes](@Docs/Implementation/NativeAot-Host-Probe.md).

## NativeAOT target/bootstrap experiment (hosted)

```powershell
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-target
```

This builds both a static ILC archive and a shared NativeAOT module from ordinary upstream CoreLib. A separate C executable, importing only Kernel32, loads the module and checks initialization, allocations/GC, exceptions and TLS across native threads. It does not start CoreCLR.

The command also inspects COFF relocations and PE TLS/unwind data, verifies archive contents and performs strict links without runtime or OS/CRT libraries. Missing-symbol failures are recorded as dependency evidence; no dummy implementations are provided. Reports and hashes are in `artifacts/runtime-target/`.

This command remains Windows-hosted evidence. Actual guest execution is checked separately by `runtime-boot`; general Windows/DLL loading is outside the selected static-image profile. The [target/bootstrap notes](@Docs/Implementation/NativeAot-Target-Bootstrap.md) retain the original dependency measurements.

## NativeAOT source port (first guest slice)

```powershell
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-port
```

This builds a native C++ implementation of `GCToOSInterface` memory, discovery, event and time methods plus minipal/Crst mutexes against the unchanged .NET 10.0.8 headers, then boots QEMU and checks actual WitOS memory operations. It has no Windows/CRT imports. The compiler/format direction is Windows x64 code generation with a WitOS source adapter; the full native source-build recipe is available below. This adapter-only command is distinct from the full managed `runtime-boot` acceptance. See [the decision, exact contract and evidence](@Docs/Implementation/NativeAot-Gc-Memory-Port.md).

## NativeAOT native libraries from source

```powershell
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-source
```

This fetches the pinned upstream native tree and builds separate Windows-reference and WitOS-overlay libraries with the upstream CMake recipe. It runs the source-built Windows runtime through the native host, then verifies the strict WitOS link inventory and builds the complete guest driver. The broad inventory intentionally omits six transport/TLS symbols; the executable resolves them and has no OS imports. Python 3 and CMake/Ninja are required in addition to the normal tools. Reports and input hashes are written to `artifacts/runtime-source/`; the command does not start a managed runtime in the guest. See [the source-build contract and results](@Docs/Implementation/NativeAot-Source-Build.md).

## Upstream configuration in the guest

    dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-config

This refreshes the native source build and executes actual configuration methods in QEMU at 128 and 512 MiB RAM, plus 256 MiB Intel Nehalem and AVX-capable max profiles. Each boot checks 285 native user groups and 66 expected contained faults. The ordinary `test` command runs twenty kernel integration scenarios without requiring a full runtime source build.

The native fatal-diagnostic cases also verify bounded output, revoked capabilities and raw termination without cleanup; see [the exact contract](@Docs/Implementation/NativeAot-Fatal-Diagnostics.md).

The real GC affinity parser runs with WitOS flat-index syntax; this does not apply CPU affinity or enable SMP. See [parsing semantics and guest evidence](@Docs/Implementation/NativeAot-Gc-Affinity-Parsing.md).

This tests PalInit and configuration/GC OS initialization, not a running collector or managed code. The explicit RhConfig OOM overlay and source provenance are described in [ADR 0022](@Docs/Implementation/NativeAot-Runtime-Configuration.md).

## Full managed guest acceptance

```powershell
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-boot
```

This builds the pinned runtime and exact standard-CoreLib managed object used by the Windows reference, then runs the complete guest matrix. Each profile executes base A/B/A/B, with four combined GC/EH/finalization/Thread cycles per positive component and actual ThreadStore audits between cycles. Startup, OOM, raw/fault worker and managed stack-overflow cases are checked independently. Current-attempt status, immutable images/logs and hashes are under `artifacts/x64/runtime-boot/`; interrupted/failed attempts do not retain a current success claim.

## Layout

```text
src/Boot.Uefi/               Firmware-specific entry and handoff adapter
src/Kernel/                  Architecture-independent kernel: memory, handles, processes, threads, loader
src/Kernel.Arch.X64/         x64 traps, frames, contexts, page tables and user transitions
src/Kernel.Platform.Q35/     q35 board devices: COM1, PIC/PIT, HPET and test exit
src/Kernel.Arch.A64/         ARM64 vectors, frames, EL0 entry, kernel and user page tables
src/Kernel.Platform.QemuVirt/ QEMU virt board: PL011, generic counter, GICv3 timer and semihosting exit
src/Runtime.Native/          User-space native base: startup, syscalls, threads, TLS, images, files
src/Runtime.Pal.Win32/       Win32 API names for the upstream runtimes
src/Runtime.NativeAot/       NativeAOT platform adapters and source overlay
src/Runtime.CoreClr/         CoreCLR host and runtime adapters
build/                       Kernel target, layer and format manifests
tests/Kernel/                Kernel and user-isolation self-tests shared by both architectures (WITOS_SELFTEST only)
tests/Kernel.X64/            x64 kernel self-tests, linked only into WITOS_SELFTEST kernels
tests/Kernel.A64/            ARM64 kernel self-tests, fault scenarios and user fault expectations
tests/User/                  Architecture-independent fixture protocol
tests/User.X64/              Unprivileged native ABI, isolation and runtime fixtures
tests/User.A64/              ARM64 user fixtures, preprocessed with the ABI constants
tests/WitOS.Dev.Tests/       Host tests (NUnit)
tools/WitOS.Dev/             C# build, VM tests and runtime investigation tools
experiments/NativeAotBoot/   Combined guest and Windows-reference acceptance
experiments/NativeAotProbe/  Hosted reference; not guest runtime code
experiments/NativeAotTarget/ Native bootstrap and target artifact evidence
@Docs/                       Architecture drafts and implementation notes
.github/workflows/           Automated native build and VM tests
```

The core kernel does not include UEFI structures. The output is a freestanding PE/COFF EFI image with no Windows or C-runtime imports. MSVC is a host compiler, not a guest dependency.

## Scope and next work

The selected x64/UP system profile uses a static image and experimental user ABI v48. Ordinary native image limits remain separate from the measured full-runtime profile: 1088 KiB image, 4096 unwind entries, 8 MiB owned backing, 32 reservations, 16 events and 32 handles. Four thread slots include Main and the actual finalizer. User stacks are fixed at 64 KiB; stack overflow terminates the component. Kernel-owned references, identities, contexts and immutable image metadata underpin actual managed thread lifecycle and GC root walking.

P6 is the next architecture milestone: upstream CoreCLR/JIT, executable-memory/code-registration support and unchanged portable assemblies with ordinary SDK/TFM/NuGet workflows. General dynamic module loading, ThreadPool/Task/async, filesystem/network/UTC services, SMP and broad API compatibility are not established by this NativeAOT acceptance. Firmware memory remains reserved; the q35 HPET clock is monotonic, not UTC. See the [supported M3 profile](@Docs/Implementation/M3-NativeAOT-Profile.md) for precise limits and [PLAN.md](PLAN.md) for current progress.

- [Architecture document index](@Docs/README.md)
- [M0 implementation history](@Docs/Implementation/M0-Boot.md)
- [Initial M1 memory and exception slice](@Docs/Implementation/M1-Memory-and-Exceptions.md)
- [Completed initial M1 kernel core](@Docs/Implementation/M1-Kernel-Core.md)
- [M2 isolated native execution and ABI](@Docs/Implementation/M2-Isolated-Execution.md)
- [M2 sparse user memory and failure semantics](@Docs/Implementation/M2-User-Memory.md)
- [M2 user threads, TLS and join](@Docs/Implementation/M2-User-Threads-and-Tls.md)
- [M2 events, deadlines and kernel idle](@Docs/Implementation/M2-Events-and-Deadlines.md)
- [M2 guest PE image loading](@Docs/Implementation/M2-Pe-Image-Loading.md)
- [M2 native image handoff and C bootstrap](@Docs/Implementation/M2-Native-Module-Bootstrap.md)
- [NativeAOT backend decision and GC memory adapter](@Docs/Implementation/NativeAot-Gc-Memory-Port.md)
- [NativeAOT native source build and remaining dependencies](@Docs/Implementation/NativeAot-Source-Build.md)
- [GC discovery and atomic allocator snapshots](@Docs/Implementation/NativeAot-Gc-Discovery.md)
- [GC events, lifecycle and yielding](@Docs/Implementation/NativeAot-Gc-Events.md)
- [HPET time and finite GC deadlines](@Docs/Implementation/NativeAot-Gc-Time.md)
- [Recursive native mutexes, Crst and thread identity](@Docs/Implementation/NativeAot-Mutexes.md)
- [RFC 0011: initial kernel boot contract](@Docs/RFC-0011-Kernel-Architecture-and-ABI.md)
- [Committed memory reset and GC adapter](@Docs/Implementation/NativeAot-Gc-Reset.md)
- [Native runtime allocation](@Docs/Implementation/NativeAot-Native-Heap.md)
- [Static compiler TLS](@Docs/Implementation/NativeAot-Compiler-Tls.md)
- [Dynamic C++ TLS lifecycle](@Docs/Implementation/NativeAot-Dynamic-Tls.md)
- [WitOS PAL and current-thread discovery](@Docs/Implementation/NativeAot-Pal-Thread-Discovery.md)
- [PAL memory, events and waits](@Docs/Implementation/NativeAot-Pal-Memory-and-Waits.md)
- [Detached PAL workers](@Docs/Implementation/NativeAot-Pal-Background-Threads.md)
- [Per-thread native last-error](@Docs/Implementation/NativeAot-Pal-Last-Error.md)
- [Native PAL module discovery](@Docs/Implementation/NativeAot-Pal-Module-Discovery.md)
- [M3 integration plan and estimate](@Docs/Implementation/M3-Runtime-Integration-Plan.md)
- [Immutable native environment and PAL strings](@Docs/Implementation/NativeAot-Pal-Environment.md)
- [Upstream runtime configuration and native C support](@Docs/Implementation/NativeAot-Runtime-Configuration.md)
- [Native PAL initialization and startup policy](@Docs/Implementation/NativeAot-Pal-Initialization.md)
- [Immediate development sequence](@Docs/Implementation/Next-Steps.md)

The minimal executable startup assessment runs as part of runtime-source and runtime-config, or through:

    dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-readiness

It checks an ordinary standard-CoreLib Main on Windows, then performs a strict native-entry link with the WitOS source libraries and real syscall/TLS objects. It reports dependencies and measured Windows-reference image costs in artifacts/runtime-readiness. It neither boots a managed guest nor supplies missing runtime stubs. See [the updated distance to M3](@Docs/Implementation/M3-Runtime-Integration-Plan.md).
