# RFC 0011 — Kernel Architecture & ABI

Draft v0.3. Scope: the implemented boot and M1 kernel foundation.

## Objective

Provide the native mechanisms needed below eventual upstream .NET while separating common contracts from UEFI and x64 implementation details.

This document does not freeze future syscalls, capability handles, user isolation or the .NET PAL. Runtime requirements must inform those boundaries.

## Components

| Component | Responsibility |
| --- | --- |
| Boot.Uefi | Translate firmware memory and loaded-image information, exit boot services |
| Kernel | Validate the handoff, track physical pages, run the integrated kernel checks |
| Kernel.Arch.X64 | Stacks, GDT/TSS/IDT, paging, exceptions, PIC/PIT and context switching |
| WitOS.Dev | Host compilation, image packaging, QEMU execution and validation |

The native components currently share one EFI executable. The common kernel receives normalized information and does not parse firmware or PE structures.

## WitBootInfo v2

The authoritative layout is in `src/Kernel/include/witos/boot.h`. Version 2 is 72 bytes on the current 64-bit ABI. It replaces v1 internally; old versions are rejected.

| Field | Meaning |
| --- | --- |
| Magic, Version, Size | Contract identification and validation |
| Architecture | x64 for this implementation |
| MemoryRegionCount, MemoryRegions | 1–1024 normalized physical-memory regions |
| Flags | Successful exit from boot services |
| ImageBase, ImageSize | Page-aligned resident native-image extent |
| ImageSectionCount, ImageSections | 1–16 normalized section mappings |
| Reserved | Must be zero |

Each memory region contains base, byte length, usable/reserved kind and a zero reserved field. Each image section contains base, byte length, read/write/execute flags and a zero reserved field.

Pointers initially use UEFI's identity mapping. The resident image and boot metadata retain identity mappings after switching to the kernel's own root.

## Ownership

All non-conventional firmware memory remains reserved. Image, boot metadata, descriptor tables and static stacks reside in reserved image memory. A physical page is not available merely because the firmware exited.

The kernel allocates and retains its page-table pages. It does not reclaim old firmware allocations in M1. Physical-page bookkeeping currently accepts usable addresses below 4 GiB only.

## Mapping and protection

The kernel installs four-level, 4 KiB mappings for usable RAM and its image. Code sections are read/execute; mutable data is non-executable. Writable executable image sections are invalid. CR0.WP and EFER.NXE enforce these rules in ring 0.

Page zero and the guard pages around all four stacks are absent. Scratch map/protect/unmap operations invalidate local TLB entries and accept only allocated pages in the defined scratch range. They are single-CPU privileged mechanisms, not user authority.

## Execution

An x64 trampoline enters a 64 KiB kernel stack. A 32 KiB double-fault stack uses IST1. Each of two demonstration workers has its own guarded 64 KiB stack.

The initial PIC/PIT backend delivers IRQ0 at approximately 100 Hz. Interrupt entry saves GPRs, the hardware return frame and x87/SSE state. Dispatch can select another saved context, restored with IRETQ.

The workers demonstrate timer-driven preemption and preserved register state. This is not yet a general scheduler API, user process model, SMP scheduler or AVX-capable context manager.

## Failure behavior

Contract errors and CPU exceptions produce bounded serial diagnostics and a distinct VM failure status. Exception reports include vector, error, RIP, CS, RFLAGS, interrupted RSP, SS and CR2.

CPU exceptions are currently fatal. Timer interrupts return to an execution context. No language-level exception unwinding or recoverable user fault handling exists yet.

## Acceptance and next ABI work

The 17-scenario suite covers multiple RAM sizes, invalid handoffs, CPU faults, protection faults, mappings, preemption and timeouts. See [M1 implementation](Implementation/M1-Kernel-Core.md).

Before M2 stabilizes a user ABI, determine the selected upstream runtime's requirements for reserve/commit/protect, thread-local storage, threads, waits/wakes, clocks, exceptions and startup. Add mechanisms when a tested vertical slice needs them.
