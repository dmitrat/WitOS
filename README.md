# WitOS

WitOS is an experimental operating system built around a minimal native kernel and standard upstream .NET. The intended system places portable OS services, applications and shells above the runtime. Hardware-specific mechanisms stay behind explicit interfaces.

The hardware layer may eventually be supplied in firmware. The first implementation uses QEMU and UEFI to test the same separation without custom hardware.

## Current status

**WitOS 0.0.3: the initial M1 kernel-core milestone is implemented.**

The kernel boots independently through UEFI, owns its stacks and page tables, protects code/data, and preempts two kernel contexts using timer interrupts. It validates memory and context state before printing `Hello from WitOS.` and reporting a VM test result. CPU faults produce register diagnostics; double faults use a separate emergency stack.

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
WitOS 0.0.3 (M1 kernel core)
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

Every test creates fresh firmware variable storage. A timeout, unexpected exit, panic or missing success marker fails an ordinary boot test.

Outputs:

```text
artifacts/x64/boot/BOOTX64.EFI       Native UEFI adapter + kernel
artifacts/x64/boot/WitOS-x64.img    Bootable FAT16 disk image
artifacts/x64/boot/WitOS.pdb        Native symbols
artifacts/x64/boot/WitOS.map        Native link map
artifacts/x64/boot/build.txt        Source revision and toolchain
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

## Layout

```text
src/Boot.Uefi/          Firmware-specific entry and handoff adapter
src/Kernel/             Common boot validation and physical-page allocator
src/Kernel.Arch.X64/     Paging, guarded stacks, exceptions, timer and context switches
tools/WitOS.Dev/         C# build, VM tests and runtime investigation tools
experiments/NativeAotProbe/ Hosted reference; not guest runtime code
@Docs/                  Architecture drafts and implementation notes
.github/workflows/      Automated native build and VM tests
```

The core kernel does not include UEFI structures. The output is a freestanding PE/COFF EFI image with no Windows or C-runtime imports. MSVC is a host compiler, not a guest dependency.

## Scope and next work

M1 meets the initial kernel-core criteria on the pinned one-CPU QEMU target. It uses one kernel address space, 4 KiB pages and usable physical addresses below 4 GiB. Firmware memory remains reserved. The legacy PIC/PIT timer and fixed two-worker dispatcher establish the mechanism; dynamic threads, SMP, AVX context state and a general scheduling API remain future work.

CPU exceptions are fatal diagnostics. User-mode isolation and managed execution are not implemented. The NativeAOT inventory and hosted reference are now available. Next is the [first M2 isolated execution slice](@Docs/Implementation/M2-Isolated-Execution-Plan.md), with runtime requirements informing the user/kernel boundary.

- [Architecture document index](@Docs/README.md)
- [M0 implementation history](@Docs/Implementation/M0-Boot.md)
- [Initial M1 memory and exception slice](@Docs/Implementation/M1-Memory-and-Exceptions.md)
- [Completed initial M1 kernel core](@Docs/Implementation/M1-Kernel-Core.md)
- [RFC 0011: initial kernel boot contract](@Docs/RFC-0011-Kernel-Architecture-and-ABI.md)
- [Immediate development sequence](@Docs/Implementation/Next-Steps.md)
