# WitOS

WitOS is an experimental operating system built around a minimal native kernel and standard upstream .NET. The intended system places portable OS services, applications and shells above the runtime. Hardware-specific mechanisms stay behind explicit interfaces.

The hardware layer may eventually be supplied in firmware. The first implementation uses QEMU and UEFI to test the same separation without custom hardware.

## Current status

**WitOS 0.0.8: the guest loads native PE32+ images with protected sections, zero-fill and relocations.**

The kernel boots independently through UEFI and runs separately built native components in ring 3 with private mappings and handles. Its bounded PE loader parses complete files inside the guest, maps sections and applies relocations. The component writes through a checked syscall and exits; its faults are contained while the kernel runs the next component. Within a component, up to four user threads can run with timer preemption, separate stacks/TLS and blocking join. Manual/auto-reset events, sleep and absolute deadlines work with kernel idle when all threads are blocked. M1 paging, protection, timer and kernel-context checks remain part of every successful boot.

**The guest does not run .NET yet.** The C# code in `tools/` runs on the development computer. NativeAOT system components are a later milestone; standard CoreCLR applications follow after that.

## Quick start

Development host for this first slice:

- Windows x64.
- .NET SDK **10.0.300** (latest patch in that feature band is allowed).
- Visual Studio / Build Tools with **Desktop development with C++**, including the x64 MSVC compiler and MASM.
- Git.
- 7-Zip at its normal installation location, for extracting QEMU.

From the repository root:

```powershell
dotnet run --project tools/WitOS.Dev -- setup
dotnet run --project tools/WitOS.Dev -- doctor
dotnet run --project tools/WitOS.Dev -- run
```

`setup` downloads QEMU **11.1.0**, verifies a pinned SHA-512 digest and extracts it into `.tools/`. It does not run the installer, edit PATH, or install a Windows service. The download is approximately 197 MiB. All subsequent builds and VM tests can run offline.

`run` builds an x64 EFI executable, packages it into a 32 MiB FAT16 disk image and boots a headless QEMU VM with software emulation, one CPU, 256 MiB RAM and no networking. No Hyper-V configuration is required.

Expected guest output includes:

```text
[BOOT] UEFI x64 adapter
[BOOT] ExitBootServices OK
WitOS 0.0.8 (M2 PE image loading)
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
```

The native kernel currently always builds in Debug mode, including when the host tool uses Release.

The integration suite boots seventeen real VM scenarios:

- Normal boot with 128 MiB and 512 MiB RAM, including real-page read/write, reserved-memory, exhaustion, reuse and invalid-map checks.
- Rejection of an invalid boot-contract version and overlapping firmware memory regions.
- Actual breakpoint, divide error, invalid opcode, general protection, page fault and double fault.
- Hardware-enforced refusal of code writes, data execution, lower/upper stack guard access, read-only alias writes and unmapped alias reads.
- Detection and termination of a deliberately hung guest after full initialization.

Normal boots also verify map/protect/unmap behavior, aliasing, TLB invalidation, timer delivery, progress of both preempted contexts and preserved GPR/SSE state. Exception tests validate vector, error code, register frame, fault address and stack selection. The double-fault test deliberately invalidates the main stack and requires diagnostics from the emergency stack.

Successful boots also require 72 M2 groups: ring-3 entry, ABI/handles, private memory, user-fault containment, safe return state, timer budgeting, register/flag preservation, zero-fill and resource teardown, plus sparse reservations, commit/decommit/protect/release, recoverable exhaustion and hardware memory faults, plus user-thread preemption, TLS/register state, join/cycle handling, slot reuse and child-fault cleanup, plus event state/rights, wakeups, close/timeout ordering, signal handoff and kernel idle, plus PE validation/loading, relocation, BSS, allocation rollback and hardware section protection. They run within the same real VM.

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

This still runs on Windows. The guest now has a restricted native PE loading path; the runtime OS backend, DLL/TLS/import semantics and managed execution are not yet ported. See [measured requirements and next work](@Docs/Implementation/NativeAot-Target-Bootstrap.md).

## Layout

```text
src/Boot.Uefi/          Firmware-specific entry and handoff adapter
src/Kernel/             Boot validation, physical pages and process-local handles
src/Kernel.Arch.X64/     Paging, traps, context transitions and user execution
tests/User.X64/         Unprivileged native ABI/isolation fixture
tools/WitOS.Dev/         C# build, VM tests and runtime investigation tools
experiments/NativeAotProbe/ Hosted reference; not guest runtime code
experiments/NativeAotTarget/ Native bootstrap and target artifact evidence
@Docs/                  Architecture drafts and implementation notes
.github/workflows/      Automated native build and VM tests
```

The core kernel does not include UEFI structures. The output is a freestanding PE/COFF EFI image with no Windows or C-runtime imports. MSVC is a host compiler, not a guest dependency.

## Scope and next work

M2 uses two fixed component slots and activates one component at a time. Up to four threads share its private address space and handles; each has separate guarded user/kernel stacks and a raw FS-based TLS block. Shared kernel mappings stay supervisor-only. Native components use experimental ABI v4 for query/write/exit/close, memory, thread, event and clock operations. Earlier one-page fixtures remain alongside the new PE path, whose profile permits up to 16 sections and a 256 KiB mapped image. Each space can reserve within a separate 64 GiB virtual arena without allocating backing RAM; commitment is explicitly limited to 128 owned frames including page tables and fixed mappings. Firmware memory remains reserved, and usable physical addresses remain below 4 GiB.

User faults terminate that component; kernel faults remain fatal diagnostics. General Windows/DLL loading, imports, compiler/managed TLS, unwind integration, mutexes/multi-object waits, IPC channels, SMP and managed execution remain future work. The pinned target/bootstrap experiment now supplies measured object, TLS, unwind and dependency requirements. The bounded guest PE loading contract is implemented; next are module/bootstrap contracts and selection of the source-level runtime backend. The current clock counts delivered PIT ticks (nominal 100 Hz), pauses when IRQ0 is disabled and does not provide calibrated elapsed time.

- [Architecture document index](@Docs/README.md)
- [M0 implementation history](@Docs/Implementation/M0-Boot.md)
- [Initial M1 memory and exception slice](@Docs/Implementation/M1-Memory-and-Exceptions.md)
- [Completed initial M1 kernel core](@Docs/Implementation/M1-Kernel-Core.md)
- [M2 isolated native execution and ABI](@Docs/Implementation/M2-Isolated-Execution.md)
- [M2 sparse user memory and failure semantics](@Docs/Implementation/M2-User-Memory.md)
- [M2 user threads, TLS and join](@Docs/Implementation/M2-User-Threads-and-Tls.md)
- [M2 events, deadlines and kernel idle](@Docs/Implementation/M2-Events-and-Deadlines.md)
- [M2 guest PE image loading](@Docs/Implementation/M2-Pe-Image-Loading.md)
- [RFC 0011: initial kernel boot contract](@Docs/RFC-0011-Kernel-Architecture-and-ABI.md)
- [Immediate development sequence](@Docs/Implementation/Next-Steps.md)
