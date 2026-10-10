# WitOS

WitOS is an experimental operating system built around a minimal native kernel and standard upstream .NET. The intended system places portable OS services, applications and shells above the runtime. Hardware-specific mechanisms stay behind explicit interfaces.

The hardware layer may eventually be supplied in firmware. The first implementation uses QEMU and UEFI to test the same separation without custom hardware.

**Roadmap:** [План WitOS / PLAN.md](PLAN.md) — цели, фазы A–P и критерии готовности. **Architecture decision (2026-10-06):** [ADR 0024](@Docs/Implementation/ADR-0024-Three-Layers-and-Unix-Form-Runtime.md) — a nano-kernel of mechanisms, a user-space system layer and unchanged upstream .NET in its Unix form (`TargetOS=witos`); the Windows-form host line of P6.4 was frozen, its plan archived, and its code removed at plan step K8.2.

## Current status

**Current local implementation.** The kernel boots through UEFI on x64 (QEMU q35) and ARM64 (QEMU `virt`), is compiled
by the pinned clang on both, and keeps the mechanisms of ABI-1 ([RFC 0011 v3](@Docs/RFC-0011-Kernel-Architecture-and-ABI.md)): processes with private
address spaces, the one thread form, memory objects, channels that carry capabilities, events and the one wait with
absolute deadlines, faults and activations delivered to user space, device descriptors with interrupt bindings and DMA
pins, UTC and the started secondary processors. User ABI v69 and boot ABI v6 are printed by the kernel banner from the
headers. Above it the system layer (phase S) runs on both ISAs: the pinned musl over ABI-1 with threads, signals and its
dynamic linker, LLVM's C++ runtime, a process manager with `posix_spawn`, and musl's libc-test, its math suite included,
with every test run as a process of its own. Upstream .NET 10 runs in its Unix form (`TargetOS=witos`, phase R): NativeAOT
programs built by the SDK with `dotnet publish -r witos-x64` or `-r witos-arm64` pass the M3 acceptance in the guest on
both ISAs, with the GC, managed exceptions, finalization, threads, the thread pool, waits and the console
([R2.2](@Docs/Implementation/R2.2-M3-Acceptance.md), [R2.3b](@Docs/Implementation/R2.3b-SDK-Targets.md)). The
Windows-form runtime that first passed M3 behind a Win32 facade was frozen by [ADR 0024](@Docs/Implementation/ADR-0024-Three-Layers-and-Unix-Form-Runtime.md) and removed at plan step
K8.2; the kernel policy it needed (the PE loader, the DLL lifecycle, files, the environment) leaves the kernel at K8.4.
The work follows [PLAN.md](PLAN.md): phase K finishes the nano-kernel, T the toolchain and hosts, R the runtime
([RFC 0015 v2](@Docs/RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md)) with CoreCLR and the JIT next; devices and storage (M4, M5) come before standard .NET (M6).

A self-test kernel also runs the kernel's white-box checks and the user-mode fixture of each mechanism before `Hello`.
The root task, one freestanding C program built by the pinned clang and lld for `x86_64-unknown-linux-musl` and
`aarch64-unknown-linux-musl` (plan step T1), checks its startup capabilities and the mechanisms layer 2 has no other
consumer of yet: waits on several objects, a suspended thread's context, an activation of a thread that has not run,
memory pressure and the reset of committed pages (K8.2).

The C# code in `tools/` runs on the development computer: it builds the kernel, the fixtures, the system layer, the
runtime and the boot images, boots them in QEMU and judges each outcome from the serial log and the exit status.

## Quick start

Development host for this first slice:

- Windows x64.
- .NET SDK **10.0.300** (latest patch in that feature band is allowed).
- Visual Studio / Build Tools with **Desktop development with C++**, including the x64 MSVC compiler, MASM and Windows SDK headers, for the user-mode fixtures of the self-test kernels (until plan steps K8.3 and K8.4).
- Git.
- 7-Zip at its normal installation location, for extracting QEMU.

A Linux x64 host (plan step T2.1a) builds and boots everything but the self-test fixtures, which need MSVC: the release kernels of both ISAs and the layer-2 scenarios on them (T2.1b), and it is the host of the .NET runtime's build (`runtime-witos`). `build/linux/Dockerfile` describes it (the .NET SDK on Ubuntu 24.04 and what building QEMU needs); `setup` there extracts the pinned LLVM Linux archive and builds QEMU from its pinned source release. See `@Docs/Implementation/T2.1a-Linux-Host.md`.

On some Windows hosts, process creation stalls system-wide for up to about 20 seconds while the host process tests create and kill job trees: an unrelated process start waits while the CPU stays idle. The timing-bounded host process tests (`ProcessesTests`) then fail with cleanup or deadline errors; rerun them alone before suspecting the tool. Excluding the repository from Microsoft Defender real-time scanning still helps build speed, because Defender inspects every new binary under `artifacts/` and `.tools/`, but it did not remove these stalls.

From the repository root:

```powershell
dotnet run --project tools/WitOS.Dev -- setup
dotnet run --project tools/WitOS.Dev -- doctor
dotnet run --project tools/WitOS.Dev -- run
```

`setup` downloads QEMU **11.1.0**, verifies a pinned SHA-512 digest and extracts it into `.tools/`. It does not run the installer, edit PATH, or install a Windows service. The download is approximately 197 MiB. `setup` also extracts the pinned clang and lld and the musl and libc-test sources the system layer is built from. After these files are cached, builds and VM tests can run offline.

`run` builds an x64 EFI executable, packages it into a 32 MiB FAT16 disk image and boots a headless QEMU VM with software emulation, one CPU, 256 MiB RAM and no networking. No Hyper-V configuration is required.

Expected guest output includes:

```text
[BOOT] UEFI x64 adapter
[BOOT] ExitBootServices OK
WitOS user ABI v69, boot ABI v6
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

`--arch arm64` builds `BOOTAA64.EFI` with the pinned clang for `aarch64-unknown-windows` and lld-link (its EL0 fixtures still need the MSVC ARM64 cross tools, Visual Studio component `Microsoft.VisualStudio.Component.VC.Tools.ARM64`, until K8.3) and boots it on the QEMU `virt` board with GICv3. That kernel runs the kernel foundation and EL0 components through the common user-mode policy: it checks the boot contract, installs its EL1 exception vectors, initializes the physical page allocator, installs its own translation tables with guarded stacks, opens the boot package, seeds ChaCha20, checks the generic counter, runs the foundation self-tests, preempts two kernel workers with the GICv3 virtual timer while checking their general, NEON and FPCR state, runs the shared user isolation, thread, wait and image tests with ARM64 ports of their fixtures (private address spaces, system calls and handles, sparse user memory, preempted threads with TPIDRRO_EL0 TLS and FPCR state, joins, events, deadlines and the idle wait) and loads relocatable and fixed ARM64 PE images, 27 contained EL0 faults in all, reports `Hello` and exits through Arm semihosting. Its suite requires success at 128 and 512 MiB, rejection of an invalid boot contract and of an overlapping memory map, a reported breakpoint, undefined instruction and data abort, the six memory permission faults of the x64 suite, and a timeout after a successful boot.

A test scenario builds a self-test kernel, except the layer-2 scenarios, which boot the release kernel: the sources in `tests/Kernel*` and the white-box checks guarded by `WITOS_SELFTEST` run during boot before `Hello`. `release` builds the kernel without them, rejects a link map that names self-test code, and boots it with 128 MiB and 512 MiB of RAM; that kernel initializes, reports `Hello` and starts the root task, which must exit with zero.

The x64 suite boots 27 VM scenarios and the ARM64 suite 21:

- Normal boot with 128 MiB and 512 MiB RAM and with two processors, and on x64 with an Intel Nehalem CPU model; at 128 MiB the root task's mechanism checks must report their line.
- The layer-2 scenarios on the release kernel: `libc`, `libc-test`, `cxx`, `spawn`, `process` and `sysroot`.
- Rejection of an invalid boot contract and of overlapping firmware memory regions; on x64 also of a missing entropy source and a missing HPET.
- The CPU exceptions of each ISA and the six hardware-enforced memory permission faults: code writes, data execution, lower/upper stack guard access, read-only alias writes and unmapped alias reads.
- Detection and termination of a deliberately hung guest after full initialization.

Normal boots also verify map/protect/unmap behavior, aliasing, TLB invalidation, timer delivery, progress of both preempted contexts and preserved GPR/SSE state. Exception tests validate vector, error code, register frame, fault address and stack selection. The double-fault test deliberately invalidates the main stack and requires diagnostics from the emergency stack.

Successful x64 boots also require 112 user groups in order (`tests/Expectations/x64-users.json`): ring-3 entry and isolation, user memory and its faults, threads, events and waits, faults and activations, channels, memory objects, devices, interrupts and DMA, the one thread form, processes, processors, the root task and the kernel policy that leaves at K8.4 (PE loading, native C startup, compiler TLS), plus CPU cache discovery. ARM64 boots require their own suite the same way.

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
artifacts/x64/boot/RootFixture.elf  The root task, before its conversion to the flat image
artifacts/logs/                   Serial, stderr and outcome logs
```

Failure-injection images have separate output directories and do not replace the normal boot image.

## .NET in its Unix form

```bash
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-witos
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-witos --arch arm64
```

On a Linux x64 host (the image of `build/linux/Dockerfile`), `runtime-witos` applies WitOS's patch set (`patches/runtime`, pinned with its commit in `build/runtime/runtime.lock.json`) to the pinned dotnet/runtime and builds it with upstream's `build.sh -os witos`: CoreLib, NativeAOT's native part against the system layer's sysroot, ILC with the target WitOS, the shared framework and the packs the SDK takes. It measures the native configure's `try_run` answers in the guest, publishes the M3 acceptance (`tests/Runtime.Witos/Acceptance`) with `dotnet publish -r witos-<arch>` and runs it in QEMU, where it must report `[M3] NativeAOT on <isa>: 32 runs passed, 0 failed`. See [R2.2](@Docs/Implementation/R2.2-M3-Acceptance.md) and [R2.3b](@Docs/Implementation/R2.3b-SDK-Targets.md).

## Layout

```text
src/Boot.Uefi/               Firmware-specific entry and handoff adapter
src/Kernel/                  Architecture-independent kernel: memory, handles, processes, threads, channels, devices
src/Kernel.Arch.X64/         x64 traps, frames, contexts, page tables and user transitions
src/Kernel.Platform.Q35/     q35 board devices: COM1, PIC/PIT, HPET, RTC and test exit
src/Kernel.Arch.A64/         ARM64 vectors, frames, EL0 entry, kernel and user page tables
src/Kernel.Platform.QemuVirt/ QEMU virt board: PL011, generic counter, GICv3, PL031 and semihosting exit
src/Substrate/               The system layer: WitOS's part of the libc, libwitos and the pins of its upstream sources
src/RootTask/                The system layer's root task and process manager
src/Sysroot/                 What layer 2 adds to the ABI-1 headers, and the sysroot's CMake platform modules
src/Runtime.Native/          User side of the kernel policy that leaves at plan step K8.4 (PE startup, files, libraries)
build/                       Kernel target, layer and format manifests; the Linux host image; the runtime pin and targets
patches/                     WitOS's changes to pinned upstream files (musl, dotnet/runtime)
tests/Kernel/                Kernel and user self-tests shared by both architectures (WITOS_SELFTEST only)
tests/Kernel.X64/            x64 kernel self-tests, linked only into WITOS_SELFTEST kernels
tests/Kernel.A64/            ARM64 kernel self-tests, fault scenarios and user fault expectations
tests/User/                  The root task, the layer-2 test programs and the fixture protocol
tests/User/Fixtures/         The mechanism fixtures in C, one source for both ISAs (K8.3)
tests/User.X64/              x64 PE fixtures of the kernel policy, which MSVC builds until K8.4
tests/User.A64/              The ARM64 PE fixture of the kernel policy, preprocessed with the ABI constants
tests/Runtime.Witos/         The witos runtime's platform check and its M3 acceptance
tests/Expectations/          The boot markers each suite requires
tests/WitOS.Dev.Tests/       Host tests (NUnit)
tools/WitOS.Dev/             C# build, VM test, system layer and runtime tools
@Docs/                       Architecture drafts and implementation notes
.github/workflows/           Automated build and VM tests
```

The core kernel does not include UEFI structures. The output is a freestanding PE/COFF EFI image with no Windows or C-runtime imports. The kernel is compiled by the pinned clang and linked by its lld-link on both ISAs (plan step T3); the self-test fixtures still use MSVC, a host compiler and never a guest dependency, until plan steps K8.3 and K8.4. The PE loader, file, library and process-state code of `src/Kernel` and the code of `src/Runtime.Native` are the kernel policy RFC 0011 §8 moves to the system layer; they leave at plan step K8.4.

## Scope and next work

Next work is tracked in [PLAN.md](PLAN.md) after [ADR 0024](@Docs/Implementation/ADR-0024-Three-Layers-and-Unix-Form-Runtime.md): K8 finishes the nano-kernel (the mechanism fixtures without MSVC and PE, then the kernel policy leaves and ABI-1 1.0 is declared), T2.2 brings the Linux host to parity, and phase R continues with CoreCLR and the JIT (R3) towards unchanged portable assemblies with ordinary SDK/TFM/NuGet workflows ([RFC 0015 v2](@Docs/RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md)); devices and storage (M4, M5) come before standard .NET (M6), as the [Implementation Strategy §157](@Docs/WitOS%20—%20Implementation%20Strategy%20%26%20Milestone%20Plan.md) records. The NativeAOT acceptance does not establish dynamic loading of managed code, filesystem or network services, threads on several processors or broad API compatibility. Firmware memory remains reserved.

- [Architecture document index](@Docs/README.md)
- [ADR 0024: three layers and the Unix-form runtime](@Docs/Implementation/ADR-0024-Three-Layers-and-Unix-Form-Runtime.md)
- [RFC 0011 v3: kernel architecture, ABI-1 and ABI-2](@Docs/RFC-0011-Kernel-Architecture-and-ABI.md)
- [RFC 0015 v2: the .NET runtime port and its compatibility contract](@Docs/RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md)
- [ABI-1 reference](@Docs/Implementation/ABI-Reference.md)
- [The first libc program over ABI-1](@Docs/Implementation/S1.1-Musl-Over-ABI-1.md)
- [The process manager](@Docs/Implementation/S6.1-Process-Manager.md)
- [The M3 acceptance of the Unix form](@Docs/Implementation/R2.2-M3-Acceptance.md)
- [Programs built by the SDK's targets](@Docs/Implementation/R2.3b-SDK-Targets.md)
- [The removal of the Windows-form line](@Docs/Implementation/K8.2-Frozen-Line-Removal.md)
- [M0 implementation history](@Docs/Implementation/M0-Boot.md)
- [Initial M1 memory and exception slice](@Docs/Implementation/M1-Memory-and-Exceptions.md)
- [Completed initial M1 kernel core](@Docs/Implementation/M1-Kernel-Core.md)
- [M2 isolated native execution and ABI](@Docs/Implementation/M2-Isolated-Execution.md)
- [M2 sparse user memory and failure semantics](@Docs/Implementation/M2-User-Memory.md)
- [M2 user threads, TLS and join](@Docs/Implementation/M2-User-Threads-and-Tls.md)
- [M2 events, deadlines and kernel idle](@Docs/Implementation/M2-Events-and-Deadlines.md)
- [M2 guest PE image loading](@Docs/Implementation/M2-Pe-Image-Loading.md)
- [M2 native image handoff and C bootstrap](@Docs/Implementation/M2-Native-Module-Bootstrap.md)
- [The Windows-form line's plan (P6.4), removed at K8.2](@Docs/Implementation/P6.4-Plan.md)

## License

WitOS is licensed under the [Apache License, Version 2.0](LICENSE); see [NOTICE](NOTICE). Pinned upstream sources that the
build downloads keep their own licenses, recorded beside their pins.
