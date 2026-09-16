# WitOS

WitOS is an experimental operating system built around a minimal native kernel and standard upstream .NET. The intended system places portable OS services, applications and shells above the runtime. Hardware-specific mechanisms stay behind explicit interfaces.

The hardware layer may eventually be supplied in firmware. The first implementation uses QEMU and UEFI to test the same separation without custom hardware.

## Current status

**WitOS 0.0.2: independent boot plus the first M1 memory/exception slice.**

The UEFI adapter obtains the memory map, calls `ExitBootServices`, and transfers control through `WitBootInfo` onto a kernel-owned stack. The kernel installs its own exception tables, allocates and verifies physical pages, prints `Hello from WitOS.`, and exits QEMU with a test result. Fatal CPU exceptions produce register diagnostics; double faults use a separate emergency stack.

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
WitOS 0.0.2 (M1 memory foundation)
Build: <git-revision> | x64 | Debug
[TEST-BEGIN] Boot.Contract
[TEST-PASS] Boot.Contract
[TEST-PASS] Cpu.KernelStack
Kernel stack: ...
[TEST-PASS] Cpu.ExceptionTables
CPU: x86_64
Usable memory: ...
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

The integration suite boots eleven real VM scenarios:

- Normal boot with 128 MiB and 512 MiB RAM, including real-page read/write, reserved-memory, exhaustion, reuse and invalid-map checks.
- Rejection of an invalid boot-contract version and overlapping firmware memory regions.
- Actual breakpoint, divide error, invalid opcode, general protection, page fault and double fault.
- Detection and termination of a deliberately hung guest after full initialization.

Exception tests validate vector, error code, register frame and stack selection. The double-fault test deliberately invalidates the main stack and requires diagnostics from the emergency stack.

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

## Layout

```text
src/Boot.Uefi/          Firmware-specific entry and handoff adapter
src/Kernel/             Common boot validation and physical-page allocator
src/Kernel.Arch.X64/     Stacks, descriptor tables, exceptions, serial and VM exit
tools/WitOS.Dev/         C# build, disk-image and VM-test tool
@Docs/                  Architecture drafts and implementation notes
.github/workflows/      Automated native build and VM tests
```

The core kernel does not include UEFI structures. The output is a freestanding PE/COFF EFI image with no Windows or C-runtime imports. MSVC is a host compiler, not a guest dependency.

## Scope and next work

M1 is not complete. The kernel owns its stacks and exception tables but still retains firmware-provided page tables. Maskable interrupts stay disabled and only one CPU runs. The physical-page allocator accepts usable RAM below 4 GiB and rejects higher usable addresses explicitly. Image and firmware memory remain reserved.

Exceptions are fatal diagnostics; no resumable exception handling, scheduler, user mode or managed runtime exists yet. The next steps are kernel-owned mappings/protection, a timer and execution-context switching. A concrete NativeAOT dependency inventory should inform the later user/kernel ABI.

- [Architecture document index](@Docs/README.md)
- [M0 implementation history](@Docs/Implementation/M0-Boot.md)
- [Current M1 memory and exception foundation](@Docs/Implementation/M1-Memory-and-Exceptions.md)
- [RFC 0011: initial kernel boot contract](@Docs/RFC-0011-Kernel-Architecture-and-ABI.md)
- [Immediate development sequence](@Docs/Implementation/Next-Steps.md)
