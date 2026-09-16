# WitOS

WitOS is an experimental operating system built around a minimal native kernel and standard upstream .NET. The intended system places portable OS services, applications and shells above the runtime. Hardware-specific mechanisms stay behind explicit interfaces.

The hardware layer may eventually be supplied in firmware. The first implementation uses QEMU and UEFI to test the same separation without custom hardware.

## Current status

**M0: independent x64 boot works locally in QEMU.**

The UEFI adapter obtains the memory map, calls `ExitBootServices`, and transfers control to a native kernel entry point through `WitBootInfo`. The kernel validates the contract, reports usable RAM, prints `Hello from WitOS.`, and exits QEMU with a test result.

**The guest does not run .NET yet.** The C# code in `tools/` runs on the development computer. NativeAOT system components are a later milestone; standard CoreCLR applications follow after that.

## Quick start

Development host for this first slice:

- Windows x64.
- .NET SDK **10.0.300** (latest patch in that feature band is allowed).
- Visual Studio / Build Tools with **Desktop development with C++**, including the x64 MSVC compiler.
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
WitOS 0.0.1 (M0)
Build: <git-revision> | x64 | Debug
[TEST-BEGIN] Boot.Contract
[TEST-PASS] Boot.Contract
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

The native M0 kernel currently always builds in Debug mode, including when the host tool uses Release.

The integration suite boots real VMs and checks:

1. Normal boot with 128 MiB RAM.
2. Normal boot with 512 MiB RAM.
3. Rejection of a deliberately invalid boot-contract version.
4. Detection and termination of a deliberately hung guest after it reached the kernel.

Every test creates fresh firmware variable storage. A timeout, unexpected exit, panic or missing success marker fails an ordinary boot test.

Outputs:

```text
artifacts/m0/boot/BOOTX64.EFI       Native UEFI adapter + kernel
artifacts/m0/boot/WitOS-x64.img    Bootable FAT16 disk image
artifacts/m0/boot/WitOS.pdb        Native symbols
artifacts/m0/boot/WitOS.map        Native link map
artifacts/m0/boot/build.txt        Source revision and toolchain
artifacts/logs/                   Serial, stderr and outcome logs
```

Failure-injection images have separate output directories and do not replace the normal boot image.

## Layout

```text
src/Boot.Uefi/          Firmware-specific entry and handoff adapter
src/Kernel/             Common native kernel entry and boot-contract validation
src/Kernel.Arch.X64/     x64 serial I/O, interrupt disable and QEMU test exit
tools/WitOS.Dev/         C# build, disk-image and VM-test tool
@Docs/                  Architecture drafts and implementation notes
.github/workflows/      Automated M0 build and VM tests
```

The core kernel does not include UEFI structures. The output is a freestanding PE/COFF EFI image with no Windows or C-runtime imports. MSVC is a host compiler, not a guest dependency.

## Scope and next work

M0 deliberately retains the firmware-provided stack, page tables and CPU setup. Interrupts are disabled after leaving UEFI. Only UEFI conventional memory is reported as usable; image, stack and firmware memory remain reserved. There is no allocator, scheduler, user mode, exception handler, filesystem service, network stack or managed runtime.

The next work is M1 memory/exception foundations plus a concrete NativeAOT dependency inventory. Runtime requirements should inform the later kernel ABI before it is treated as stable.

- [Architecture document index](@Docs/README.md)
- [M0 implementation and limitations](@Docs/Implementation/M0-Boot.md)
- [RFC 0011: initial kernel boot contract](@Docs/RFC-0011-Kernel-Architecture-and-ABI.md)
- [Immediate development sequence](@Docs/Implementation/Next-Steps.md)
