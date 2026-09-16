# RFC 0011 — Kernel Architecture & ABI

Draft v0.2. Scope: the boot boundary and initial M1 memory/exception foundation.

## Objective

Establish a native boot foundation beneath the eventual standard .NET runtime. Keep the common kernel independent of firmware structures and x64 I/O details.

This RFC does not define future syscalls, process isolation, scheduling, capability tables or the .NET PAL. Those contracts require separate implementation evidence.

## Components

| Component | Responsibility |
| --- | --- |
| Boot.Uefi | Read and translate firmware information, exit firmware boot services |
| Kernel | Validate the handoff/map, manage physical pages and report fatal failures |
| Kernel.Arch.X64 | Kernel stacks, GDT/TSS/IDT, exception entry, serial I/O and VM completion |
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

The x64 trampoline switches to an image-owned 64 KiB stack before entering C. Initialization installs image-owned GDT/IDT/TSS structures and a 32 KiB IST1 emergency stack for double faults.

Page tables remain firmware-provided. Boot-services/loader memory must not become free merely because ExitBootServices succeeded or the active stack changed.

The CPU enters the kernel in x64 long mode with maskable interrupts disabled. The single-CPU page allocator rejects overlapping maps, protects reserved regions and currently supports usable physical addresses below 4 GiB. There is no production virtual-address layout or concurrent allocation contract yet.

## Failure behavior

An invalid handoff produces a bounded serial panic and a distinct VM exit status. Missing serial hardware cannot cause an infinite UART polling loop. A missing QEMU exit device causes the CPU to halt.

CPU exception entry now records vector, error, RIP, CS, RFLAGS, interrupted RSP, SS and CR2 before a fatal panic. Vector 8 uses a separate emergency stack. These handlers do not resume execution or implement language-level exception unwinding.

## Runtime work before stabilizing the user ABI

The kernel ABI must be informed by the selected upstream NativeAOT/CoreCLR requirements: memory reservation/commit/protection, thread-local storage, threads, waits/wakes, monotonic time, exception handling and runtime startup.

The native kernel must not grow public APIs merely to anticipate every RFC. Add a mechanism when the next tested vertical slice requires it.

## Acceptance

The host integration tests must demonstrate normal boot at multiple memory sizes, rejection of a bad contract and detection of a guest that fails to finish.

See [M0 history](Implementation/M0-Boot.md) and the [M1 foundation](Implementation/M1-Memory-and-Exceptions.md) for commands, evidence and current limitations.
