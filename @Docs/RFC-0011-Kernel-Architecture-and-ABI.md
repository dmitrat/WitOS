# RFC 0011 — Kernel Architecture & ABI

Draft v0.1. Scope: the implemented M0 boot boundary only.

## Objective

Establish a native boot foundation beneath the eventual standard .NET runtime. Keep the common kernel independent of firmware structures and x64 I/O details.

This RFC does not define future syscalls, process isolation, scheduling, capability tables or the .NET PAL. Those contracts require separate implementation evidence.

## Components

| Component | Responsibility |
| --- | --- |
| Boot.Uefi | Read and translate firmware information, exit firmware boot services |
| Kernel | Validate WitBootInfo, enter the common kernel path, report fatal contract errors |
| Kernel.Arch.X64 | Direct serial I/O, interrupt control, halt and VM test completion |
| WitOS.Dev | Host compilation, boot-image packaging, QEMU execution and result validation |

At M0 the native components are linked together into one EFI image. Separate source boundaries allow replacing the boot adapter later without teaching the common kernel UEFI types.

## WitBootInfo v1

The authoritative layout is in `src/Kernel/include/witos/boot.h`. Compile-time assertions enforce its current 64-bit layout.

| Field | Meaning |
| --- | --- |
| Magic | Identifies the WitOS boot contract |
| Version | Contract version; currently 1 |
| Size | Complete structure size; currently 40 bytes |
| Architecture | x64 for the first implementation |
| MemoryRegionCount | Number of normalized descriptors, 1 through 1024 |
| MemoryRegions | Identity-mapped pointer to adapter-owned descriptors |
| Flags | Includes successful exit from firmware boot services |

Each memory descriptor contains physical base, byte length, normalized kind and a reserved zero field. Regions are page-aligned. The core validates lengths against overflow.

## Ownership and machine state

The adapter's static buffers and the executable remain resident. Their memory is reserved in the translated map. Only conventional RAM is initially eligible for future allocation.

The current stack and page tables remain firmware-provided. Boot-services/loader memory must not become free merely because ExitBootServices succeeded. M1 must establish ownership before reclaiming it.

The CPU enters the kernel in x64 long mode with maskable interrupts disabled. There is no promise about a production virtual-address layout yet.

## Failure behavior

An invalid handoff produces a bounded serial panic and a distinct VM exit status. Missing serial hardware cannot cause an infinite UART polling loop. A missing QEMU exit device causes the CPU to halt.

This is diagnostic behavior, not recovery from arbitrary CPU faults.

## Runtime work before stabilizing the user ABI

The kernel ABI must be informed by the selected upstream NativeAOT/CoreCLR requirements: memory reservation/commit/protection, thread-local storage, threads, waits/wakes, monotonic time, exception handling and runtime startup.

The native kernel must not grow public APIs merely to anticipate every RFC. Add a mechanism when the next tested vertical slice requires it.

## Acceptance

The host integration tests must demonstrate normal boot at multiple memory sizes, rejection of a bad contract and detection of a guest that fails to finish.

See [M0 implementation notes](Implementation/M0-Boot.md) for commands, evidence and current limitations.
