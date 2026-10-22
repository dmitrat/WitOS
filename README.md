# WitOS

WitOS is an experimental operating system built around a minimal native kernel and standard upstream .NET. The intended system places portable OS services, applications and shells above the runtime. Hardware-specific mechanisms stay behind explicit interfaces.

The hardware layer may eventually be supplied in firmware. The first implementation uses QEMU and UEFI to test the same separation without custom hardware.

**Roadmap:** [План WitOS / PLAN.md](PLAN.md) — цели, фазы A–P и критерии готовности. **Architecture decision (2026-10-06):** [ADR 0024](@Docs/Implementation/ADR-0024-Three-Layers-and-Unix-Form-Runtime.md) — a nano-kernel of mechanisms, a user-space system layer and unchanged upstream .NET in its Unix form (`TargetOS=witos`); the Windows-form host line of P6.4 was frozen, its plan archived, and its code removed at plan step K8.2.

## Current status

**Current local implementation.** The kernel boots through UEFI on x64 (QEMU q35), ARM64 (QEMU `virt`) and riscv64 (QEMU
`virt` over OpenSBI), is compiled by the pinned clang on the three, and keeps the mechanisms of ABI-1 ([RFC 0011 v3](@Docs/RFC-0011-Kernel-Architecture-and-ABI.md)):
processes with private address spaces, the one thread form, memory objects, channels that carry capabilities, events and
the one wait with absolute deadlines, faults and activations delivered to user space, device descriptors with interrupt
bindings and DMA pins, UTC and threads on every processor of the board. ABI-1 is 1.4 and boot ABI v7, printed by the kernel
banner from the headers. Above it runs the system layer (phase S) on the three ISAs: the pinned musl over ABI-1 with threads,
signals and its dynamic linker, LLVM's C++ runtime, a process manager with `posix_spawn`, and ABI-2 1.1 recorded in
`build/abi2.json`. The root task starts WitOS's services, NativeAOT programs in processes of their own ([ADR 0025](@Docs/Implementation/ADR-0025-Devices-as-NativeAOT-Services.md)):
the device manager, a virtio-blk driver per disk, the namespace service with ext4 and FAT volumes written through
the ext4 journal, which e2fsprogs judges, the terminal service with Linux's line discipline over a virtio console, the
display service drawing that terminal on the firmware's framebuffer with a virtio keyboard, and the network service with
a managed TCP/IP stack over virtio-net. Upstream .NET runs in its Unix form (`TargetOS=witos`, [RFC 0015 v2](@Docs/RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md)):
.NET 10.0.12 and 8.0.31 side by side under `/dotnet` with ICU built from source; an ordinary application published by the
stock SDK without a RID runs unchanged as `dotnet App.dll`, uses the volumes through System.IO, and the .NET
compatibility suite and the own test suites of NUnit, xUnit, MessagePack, MemoryPack, Math.NET and Roslyn pass in the
guest as on Linux, every difference recorded in [Compatibility-Differences.md](@Docs/Implementation/Compatibility-Differences.md).
The Windows-form runtime that first passed M3 behind a Win32 facade was frozen by [ADR 0024](@Docs/Implementation/ADR-0024-Three-Layers-and-Unix-Form-Runtime.md)
and removed at plan step K8.2; the kernel policy it needed left the kernel at K8.4a. [PLAN.md](PLAN.md) tracks the
work: phases A, K, T1–T3, S, R, D, N, C (C3 apart), V and P are complete, M4–M6 with them, WitOS claims Gate B, "WitOS runs
.NET" (ADR 0026), and phase W, the network, has reached ASP.NET Core (W3.2) over the TLS and HTTPS of W3.1; N4, the
application environment, brought time
zones from pinned tzdata, a `/tmp` in the namespace service's memory and file modes and times on the volumes (N4.1),
`Process.Start` over the process manager with pipes between processes (N4.2) and the terminal of RFC 0020 over a
virtio console, with Linux's line discipline in a service and `Console.ReadLine`, `ReadKey` and `CancelKeyPress`
working as on Linux (N4.3), and goal 2 taken literally: the suite and a portable application published on Windows by the
stock SDK run on WitOS as exactly those bytes, each check ending as for a Linux publish (N4.4), and the compatibility
matrix that states what the claim means (N4.5); the completed steps
with their evidence are in
[Plan-Completed.md](@Docs/Implementation/Plan-Completed.md), and the audits of 2026-10-14 and 2026-10-20 in
[Program-Audit-2026-10-14.md](@Docs/Implementation/Program-Audit-2026-10-14.md) and
[Program-Audit-2026-10-20.md](@Docs/Implementation/Program-Audit-2026-10-20.md).

**WitOS runs .NET** (Gate B of the Strategy, §61, claimed at plan step N4.5): what the claim means is the
[compatibility matrix](@Docs/Implementation/Compatibility-Matrix.md), rendered from the compatibility suite and kept
current by a host test, and its limits are the [recorded differences](@Docs/Implementation/Compatibility-Differences.md);
the decisions behind it are in [ADR 0026](@Docs/Implementation/ADR-0026-M6-Summary.md).

A self-test kernel also runs the kernel's white-box checks and the user-mode fixture of each mechanism before `Hello`.
The root task, one freestanding C program built by the pinned clang and lld for `x86_64-unknown-linux-musl`,
`aarch64-unknown-linux-musl` and `riscv64-unknown-linux-musl` (plan steps T1 and V2.1), checks its startup capabilities and the mechanisms layer 2 has no other
consumer of yet: waits on several objects, a suspended thread's context, an activation of a thread that has not run,
memory pressure and the reset of committed pages (K8.2).

The C# code in `tools/` runs on the development computer: it builds the kernel, the fixtures, the system layer, the
runtime and the boot images, boots them in QEMU and judges each outcome from the serial log and the exit status.

## Quick start

Development host for this first slice:

- Windows x64.
- .NET SDK **10.0.300** (latest patch in that feature band is allowed).
- Visual Studio / Build Tools with **Desktop development with C++** (the x64 MSVC compiler and Windows SDK headers) for the host tests that compile native harnesses; no kernel image needs it since plan step K8.4a.
- Git.
- 7-Zip at its normal installation location, for extracting QEMU.

A Linux x64 host (plan step T2.1a) builds and boots everything with parity since T2.2: CI runs the host tests, the release kernels and both suites of `test` there, and it is the host of the .NET runtime's build (`runtime-witos`). `build/linux/Dockerfile` describes it (the .NET SDK on Ubuntu 24.04 and what building QEMU needs); `setup` there extracts the pinned LLVM Linux archive and builds QEMU from its pinned source release. See `@Docs/Implementation/T2.1a-Linux-Host.md`.

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
WitOS user ABI 1.4, boot ABI v7
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

`--arch arm64` builds `BOOTAA64.EFI` with the pinned clang for `aarch64-unknown-windows` and lld-link and boots it on the QEMU `virt` board with GICv3. That kernel runs the kernel foundation and EL0 components through the common user-mode policy: it checks the boot contract, installs its EL1 exception vectors, initializes the physical page allocator, installs its own translation tables with guarded stacks, opens the boot package, seeds ChaCha20, checks the generic counter, runs the foundation self-tests, preempts two kernel workers with the GICv3 virtual timer while checking their general, NEON and FPCR state, runs the shared user isolation, thread, wait and image tests with ARM64 ports of their fixtures (private address spaces, system calls and handles, sparse user memory, preempted threads with TPIDRRO_EL0 TLS and FPCR state, joins, events, deadlines and the idle wait) and loads relocatable and fixed ARM64 PE images, 27 contained EL0 faults in all, reports `Hello` and exits through Arm semihosting. Its suite requires success at 128 and 512 MiB, rejection of an invalid boot contract and of an overlapping memory map, a reported breakpoint, undefined instruction and data abort, the six memory permission faults of the x64 suite, and a timeout after a successful boot.

A test scenario builds a self-test kernel, except the layer-2 scenarios, which boot the release kernel: the sources in `tests/Kernel*` and the white-box checks guarded by `WITOS_SELFTEST` run during boot before `Hello`. `release` builds the kernel without them, rejects a link map that names self-test code, and boots it with 128 MiB and 512 MiB of RAM; that kernel initializes, reports `Hello` and starts the root task, which must exit with zero.

The x64 suite boots 27 VM scenarios and the ARM64 suite 21:

- Normal boot with 128 MiB and 512 MiB RAM and with two processors, and on x64 with an Intel Nehalem CPU model; at 128 MiB the root task's mechanism checks must report their line.
- The layer-2 scenarios on the release kernel: `libc`, `libc-test`, `cxx`, `spawn`, `process` and `sysroot`.
- Rejection of an invalid boot contract and of overlapping firmware memory regions; on x64 also of a missing entropy source and a missing HPET.
- The CPU exceptions of each ISA and the six hardware-enforced memory permission faults: code writes, data execution, lower/upper stack guard access, read-only alias writes and unmapped alias reads.
- Detection and termination of a deliberately hung guest after full initialization.

Normal boots also verify map/protect/unmap behavior, aliasing, TLB invalidation, timer delivery, progress of both preempted contexts and preserved GPR/SSE state. Exception tests validate vector, error code, register frame, fault address and stack selection. The double-fault test deliberately invalidates the main stack and requires diagnostics from the emergency stack.

Successful x64 boots also require 80 user groups in order (`tests/Expectations/x64-users.json`): ring-3 entry and isolation, user memory and its faults, threads, events and waits, faults and activations, channels, memory objects, devices, interrupts and DMA, the one thread form, processes, processors, the root task and the kernel policy that leaves at K8.4 (PE loading, native C startup, compiler TLS), plus CPU cache discovery. ARM64 boots require their own suite the same way.

Every test creates fresh firmware variable storage. A timeout, unexpected exit, panic or missing success marker fails an ordinary boot test.

Outputs:

```text
artifacts/x64/boot/BOOTX64.EFI       Native UEFI adapter + kernel
artifacts/x64/boot/WitOS-x64.img    Bootable FAT16 disk image
artifacts/x64/boot/WitOS.pdb        Native symbols
artifacts/x64/boot/WitOS.map        Native link map
artifacts/x64/boot/build.txt        Source revision and toolchain
artifacts/x64/boot/UserFixture.flat The isolation and memory fixture as a flat image (one per mechanism)
artifacts/x64/boot/RootFixture.elf  The root task, before its conversion to the flat image
artifacts/logs/                   Serial, stderr and outcome logs
```

Failure-injection images have separate output directories and do not replace the normal boot image.

## .NET in its Unix form

```bash
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-witos
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-witos --arch arm64
```

On a Linux x64 host (the image of `build/linux/Dockerfile`), `runtime-witos` applies WitOS's patch set (`patches/runtime/release-10.0`, pinned with its branch and commit in `build/runtime/release-10.0.lock.json`; each release branch has its own, N3.1) to the pinned dotnet/runtime and builds it with upstream's `build.sh -os witos`: CoreLib, NativeAOT's native part against the system layer's sysroot, ILC with the target WitOS, the shared framework and the packs the SDK takes. It measures the native configure's `try_run` answers in the guest, publishes the M3 acceptance (`tests/Runtime.Witos/Acceptance`) with `dotnet publish -r witos-<arch>` and runs it in QEMU, where it must report `[M3] NativeAOT on <isa>: 32 runs passed, 0 failed`. See [R2.2](@Docs/Implementation/R2.2-M3-Acceptance.md) and [R2.3b](@Docs/Implementation/R2.3b-SDK-Targets.md).

```bash
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-rebase --tag v10.0.12
```

`runtime-rebase` carries the patch set to another tag of dotnet/runtime. A file upstream did not change keeps its patch, a changed one is merged three ways, and a conflict is left for a person; the patches and a report of the set's size go to `artifacts/runtime-rebase/<tag>`. With `--apply`, a carry without conflicts to a tag of the pinned branch replaces its patch set and moves the pin, which `runtime-witos` then proves on both ISAs. See [R8.1](@Docs/Implementation/R8.1-Servicing-Upgrade.md).

```bash
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-services --arch arm64
```

`runtime-witos` ends by publishing WitOS's NativeAOT services, the device manager first, and booting their scenario, `runtime-devices`. `runtime-services` repeats that step alone with the packages an earlier `runtime-witos` built. See [D1.1](@Docs/Implementation/D1.1-Device-Manager.md).

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
src/OutWit.OS/               The managed capability API of WitOS's own services, over libwitos (D1.1)
src/Services/                WitOS's NativeAOT services: the device manager and the drivers (D1)
src/Sysroot/                 What layer 2 adds to the ABI-1 headers, and the sysroot's CMake platform modules
build/                       Kernel target, layer and format manifests; the Linux host image; the runtime pin and targets
patches/                     WitOS's changes to pinned upstream files (musl, dotnet/runtime)
tests/Kernel/                Kernel and user self-tests shared by both architectures (WITOS_SELFTEST only)
tests/Kernel.X64/            x64 kernel self-tests, linked only into WITOS_SELFTEST kernels
tests/Kernel.A64/            ARM64 kernel self-tests, fault scenarios and user fault expectations
tests/User/                  The root task, the layer-2 test programs and the fixture protocol
tests/User/Fixtures/         The mechanism fixtures in C, one source for both ISAs (K8.3)
tests/Runtime.Witos/         The witos runtime's platform check and its M3 acceptance
tests/Expectations/          The boot markers each suite requires
tests/WitOS.Dev.Tests/       Host tests (NUnit)
tests/OutWit.OS.Tests/       Host tests of the services' managed code (NUnit)
tools/WitOS.Dev/             C# build, VM test, system layer and runtime tools
@Docs/                       Architecture drafts and implementation notes
.github/workflows/           Automated build and VM tests
```

The core kernel does not include UEFI structures. The output is a freestanding PE/COFF EFI image with no Windows or C-runtime imports. The kernel is compiled by the pinned clang and linked by its lld-link on both ISAs (plan step T3); the fixtures are clang's too (K8.3), and no kernel image needs MSVC since K8.4a. The PE loader, files, libraries and process state that RFC 0011 §8 moves to the system layer left `src/Kernel` at plan step K8.4a.

## Scope and next work

Next work is tracked in [PLAN.md](PLAN.md) after [ADR 0024](@Docs/Implementation/ADR-0024-Three-Layers-and-Unix-Form-Runtime.md);
the completed steps with their evidence are in [Plan-Completed.md](@Docs/Implementation/Plan-Completed.md).

- **Kernel (phase K, complete).** ABI-1 1.0 at K8.4d, 1.1 at R7.2b, 1.2 at D1.2, 1.3 at C2.1, 1.4 at P1.4; the page tables record a space's pages
  (K9); the kernel knows no image format, file, path or environment.
- **Toolchain (T1–T3, complete; T4 open).** One pinned clang and lld build the kernel, the system layer and the
  tests on Windows and Linux hosts with parity (T2.2); the own `*-unknown-witos` triple waits for the upstream proposal.
- **System layer (phase S, complete).** musl over ABI-1, threads, minimal signals, LLVM's C++ runtime, `ld.so`, the
  process manager; musl's libc-test, its math suite included, runs a process per test on both ISAs.
- **Runtime (phase R, complete).** NativeAOT and CoreCLR for `witos`, upstream's hosts, an application without a RID
  (R6), upstream's PAL suite, slices of its runtime and library tests (R7), the patch set carried to a servicing release
  with no line written by hand and rehearsed on .NET 11 RC1 (R8).
- **Devices and storage (phase D, complete: M4 and M5).** The device manager and drivers as NativeAOT services, the
  block layer and protocol, FAT through `OutWit.Common.Fat` and a managed ext4 provider with a jbd2 journal judged by
  e2fsprogs, the namespace service behind the libc's POSIX calls and System.IO, programs started from a volume, ordered
  shutdown, power cuts recovered through the journal, a failing disk reported at every layer.
- **Standard .NET (phase N: N1–N3 complete, N4 begun).** The compatibility suite on Windows, Linux and WitOS with its
  differences recorded (N1), six NuGet libraries' representative tests and their own suites (N2), .NET 8 and 10 side by
  side with ABI-2 frozen at 1.0 (N3). N4 brings the application environment: time zones, `/tmp`, file modes and times
  and the process environment's `/etc/environment` (N4.1, done), `Process.Start` with the standard streams as pipes
  between processes, `SIGCHLD`, `waitid` and `kill` of a child (N4.2, done), the terminal of RFC 0020 (N4.3, done)
  and the literal proof of goal 2, published on Windows and run on WitOS (N4.4, done); N4.5 closed M6 with the
  compatibility matrix and ADR 0026, and phase N's first pass is complete.
- **Console and shell (phase C, complete but C3).** Upstream PowerShell 7.6.7, pinned and unchanged, runs on WitOS from the data
  volume as `dotnet pwsh.dll`: the version table, pipelines, files on volumes and an interactive session under PSReadLine
  on the terminal, judged by the `runtime-powershell` scenario on both ISAs (C1.1–C1.3, RFC 0020). What it needed from
  the platform: the mount points for `DriveInfo` (`/etc/mtab` from the namespace service's list), a writable home on the
  data volume, terminfo and `TERM` in the install, and the runner's terminal emulator answering the cursor position
  report; the tool's `shell` command opens that session on the host's console with WitOS's console mark (C1.4). The
  display's devices are there (C2.1): the firmware's framebuffer (ramfb, 800 by 600 on both boards) travels in the boot
  contract v7 and the device table as a device of the firmware bus, which the device manager draws a test pattern into
  through `DEVICE_MEMORY`'s windows, judged in QEMU's screendump, and a virtio keyboard's driver reports keys. The screen
  is a console (C2.2): the display service draws the terminal with a pinned bitmap font (Spleen 8 by 16, 100 by 37 cells)
  and takes the keyboard's keys, the terminal service mirrors its one terminal on the display and the serial console, and
  the host judges the screen by rendering the frame it expects from the mirror's transcript with the same code. PowerShell
  runs on that screen (C2.3): the `runtime-display` scenario types C1's session on the keyboard through QMP and judges the
  screendump at the last prompt pixel for pixel on both ISAs, the mirror answers no terminal query while the display is
  the main console, and the renderer stores only the pixels that changed, so a scroll costs the text that moved. C2 is
  complete. `shell --window` opens that screen in QEMU's window with the window's keys, the console mirroring it, and
  `shell --prepare` builds the session on the Linux host for a host with a screen, which boots it with `--image` (C2.4,
  closed by the author's session); `shell --accel` runs a session of the host's ISA on WHPX or KVM instead of TCG, where a
  key echoes at once (C2.7), and that first boot on a real processor fixed the kernel's refusal of AMD's MXCSR mask. The screen's emulator knows what a full-screen console application needs (C2.6):
  SGR's colours reduced to a 16-colour palette, bold, underline and inverse, the alternate screen, the cursor's visibility,
  the swallowed APC and DCS strings, the device attributes and size answers, wide characters and the modified function
  keys, so PowerShell is drawn in colour. Base applications are a layer of the data volume (C4.1, ADR 0027):
  `/data/apps/<application>/<version>/` from the pinned upstream publications of `build/apps`, launchers written from
  upstream's witos apphost in `/data/apps/bin` on `PATH`, `SHELL` set to pwsh's, a new version installed beside the old
  and a rollback through the `apps` command. The first file manager is one of them (C4.2): Arlecchino.Commander, built
  from its pinned commit by the stock SDK as a portable application and started by `/data/apps/bin/arlc` on the screen,
  where the `runtime-commander` scenario lists `/data`, views a file, copies it and quits through the keyboard on every
  ISA. The installer `witos-apps` (C4.3), WitOS's first console tool on Arlecchino and part of the system, installs and
  updates base applications from a folder feed on any volume, verified by SHA-256, beside the versions installed, and
  switches back to any of them; the `runtime-installer` scenario updates the file manager from a feed on `/fat` and
  rolls back on the screen. C# runs in the console too (C2.5): CSharpRepl, unchanged, over the .NET SDK laid as a base
  application per ISA (x64 and ARM64; riscv64 has no SDK publication), which `dotnet` finds through `/data/global.json`.
  Two defects the console found by hand are fixed in the system layer: a volume file's transfers no longer hold the
  libc's table lock (S8.1, the exit hang on four processors) and ReadyToRun code with base relocations loads without a
  writable and executable window (S8.2). C3, the native WitOS module and its providers, is the horizon after M8.
- **Third ISA (phase V, done).** RISC-V on QEMU's virt board: the measurement V0 found the pinned LLVM links no RISC-V
  PE/COFF, so the loader and the kernel are one position-independent ELF the tool converts to PE32+ with base
  relocations. V1.1 boots that image under EDK II over OpenSBI: the unchanged loader publishes the device tree, reads the
  boot package and leaves the firmware, and the common kernel compiled for riscv64 without a change. V1.2 is the kernel
  itself: Sv48 translation tables, the trap vector with full frames, the time counter and the supervisor timer through
  `sstc`, the PLIC's sources as device lines, the harts and the timebase from the device tree, the goldfish RTC, and the
  kernel foundation's self-tests passing on the third ISA (`test --arch riscv64`, `release --arch riscv64`, CI's `boot`
  and `linux-selftest`). V1.3 is user mode on the third ISA: `ecall`, U-mode frames and contexts, the processes' Sv48
  tables, the ECAM bridge with the PLIC's interrupt map, and the shared user-mode suites, the mechanism fixtures and the
  root task passing unchanged (`test --arch riscv64` against the users expectation). V1.4 starts the other harts
  through SBI HSM and carries the kernel's fence and invalidation requests in the supervisor software interrupt through
  SBI, so `boot-smp2` passes on the third board too and no stub is left: V1, the RISC-V kernel, is complete. V2 brings
  layer 2: musl builds for `riscv64-unknown-linux-musl` over the same dispatch and the first libc program passes its
  165 checks on the third ISA at its first boot (V2.1, the `libc` scenario of `test --arch riscv64`), and LLVM's
  libunwind, libc++abi and libc++ build for the triple unchanged and run S4's exception scenarios with the Itanium
  trace of a native Linux build (V2.2, the `cxx` scenario); libwitos, started programs static and dynamic, `libc.so`
  as the dynamic linker, libc-test's 388 runs and the sysroot with ABI-2 1.1 follow (V2.3), so layer 2 is whole on
  the three ISAs and `test --arch riscv64` runs every scenario the others run. V2.4 brings upstream .NET 10.0.12 in its
  Unix form: `runtime-witos --arch riscv64` passes the same 24 boots as the other ISAs — CoreCLR's and NativeAOT's M3
  acceptance, the PAL suite, upstream's test slices, the NativeAOT device services, the terminal, PowerShell on the
  screen, the compatibility suite and the NuGet libraries — with five patches of the community RISC-V port and one change
  of the board's PLIC handling, and CI runs it as a `linux-host` leg. V3 closes the phase: the compatibility suite and
  the portable application published on Windows by the stock SDK run unchanged on riscv64 with every check as recorded,
  CI runs them there as on the other ISAs, the matrix names the RISC-V board Primary, and from V3 a step closes only when
  it passes on the three ISAs (the NuGet libraries' own suites and .NET 8 beside .NET 10 stay on x64 and ARM64).
- **Several processors (phase P, complete).** P1 runs threads on every processor of the three boards: a kernel lock at
  every entry, requests between processors, a tick and an entry per processor, affinity honoured (ABI-1 1.4) and
  kernel stacks from the allocator in a window with guard pages, so the registry holds 32 processes. P2 runs upstream
  .NET on several processors without a patch: the libc's `getcpu` names the processor that runs a thread, a process
  holds 64 threads, an ordinary application passes its checks on four processors under the workstation and the server
  collector, and the compatibility suite and the NuGet libraries run on four processors.
- **Network (phase W, begun).** The link (W1.1): a virtio-net driver over QEMU's user networking on both hosts. The
  network service `/system/Network` (W1.2) with a managed TCP/IP stack in C#, tested on the host against a simulated
  segment: ARP, IPv4, ICMP, UDP, DHCP from slirp, loopback and TCP (RFC 9293, RFC 6298, NewReno, window scaling). The
  libc's `AF_INET` sockets and `epoll` over one session per process (W2.1), `System.Net.Sockets`, `Dns` and
  `HttpClient` over HTTP/1.1 in the compatibility suite's Network area (W2.2), on the three ISAs. TLS (W3.1): OpenSSL 3.5
  built by the system layer from its pinned release for each ISA, the cryptography and TLS of the Unix form as on Linux,
  Mozilla's root certificates, `SslStream` and HTTPS to the suite's server, to the runner's with a certificate authority
  of each boot trusted through the root store, and to a public site. ASP.NET Core (W3.2): the shared framework built
  IL alone from its pinned source, one for the three ISAs, and an ordinary Kestrel application published without a RID
  that answers the runner through the port forward and itself over HTTPS. IPv6, local sockets and interfaces (W4)
  follow, then the remaining milestones.

The acceptances establish the network as far as ASP.NET Core over HTTPS, and the processors run in turn under QEMU's
instruction clock, so they show correctness, not speed. Firmware memory remains reserved.

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
