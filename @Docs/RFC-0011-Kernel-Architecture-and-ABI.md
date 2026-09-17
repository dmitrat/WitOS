# RFC 0011 — Kernel Architecture & ABI

Draft v0.8. Scope: boot, M1 foundation, M2 isolation, memory, threads/TLS, events/deadlines and bounded PE image loading.

## Objective

Provide the native mechanisms needed below eventual upstream .NET while separating common contracts from UEFI and x64 implementation details.

The first user calls and process-local handles are experimental. This document does not freeze a general syscall SDK, executable format or .NET PAL; runtime requirements must continue to inform those boundaries.

## Components

| Component | Responsibility |
| --- | --- |
| Boot.Uefi | Translate firmware memory and loaded-image information, exit boot services |
| Kernel | Validate the handoff, track physical pages and process-local handle authority |
| Kernel.Arch.X64 | Paging, traps, PIC/PIT, context transitions and isolated user execution |
| WitOS.Dev | Host compilation, image packaging, QEMU execution and validation |

The native kernel components share one EFI executable. Boot handoff remains normalized and firmware-independent. A separate common-kernel PE parser validates user-image bytes; x64 code maps the resulting plan.

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

Page zero and the guard pages around all twelve kernel-owned stacks are absent. Scratch map/protect/unmap operations invalidate local TLB entries and accept only allocated pages in the defined scratch range. They are single-CPU privileged mechanisms, not user authority.

## Execution

An x64 trampoline enters a 64 KiB kernel stack. A 32 KiB double-fault stack uses IST1. Each of two demonstration workers has its own guarded 64 KiB stack.

The initial PIC/PIT backend delivers IRQ0 at approximately 100 Hz. Interrupt entry saves GPRs, the hardware return frame and x87/SSE state. Dispatch can select another saved context, restored with IRETQ.

The workers demonstrate timer-driven preemption and preserved register state. This is not yet a general scheduler API, user process model, SMP scheduler or AVX-capable context manager.

## Failure behavior

Contract errors and CPU exceptions produce bounded serial diagnostics and a distinct VM failure status. Exception reports include vector, error, RIP, CS, RFLAGS, interrupted RSP, SS and CR2.

Kernel CPU exceptions remain fatal. User faults terminate the current component and return to its supervisor; a subsequent component can run. Timer interrupts can return to the same user context or stop it at the test budget. Managed exception translation/unwinding is still absent.

## First M2 user boundary

A separately linked native fixture runs in ring 3 with its own CR3 and private mappings. Kernel mappings remain supervisor-only. Each component has a private typed handle table and up to four threads with separate guarded user/kernel stacks and raw TLS blocks. Two spaces may coexist; one component runs at a time, with timer/yield dispatch among its threads.

Experimental user ABI v4 uses INT 0x80 for query, checked terminal write, component exit, close, memory and thread operations, typed events and tick-clock deadlines. Call numbers, statuses and startup layout are defined in `user_abi.h` and verified by the independently compiled fixture. Full details and limits are in [M2 implementation](Implementation/M2-Isolated-Execution.md).

Fault/exit/budget paths close handles, restore the kernel CR3 and supervising context, and allow owned pages to be reclaimed. Timer returns preserve condition codes; syscall return flags follow the declared ABI. Invalid return state is rejected before IRETQ.

## Acceptance and next ABI work

The 17-scenario VM suite preserves M1 coverage and requires 72 M2 groups in successful boots: privilege boundaries, user mappings, ABI/handles, faults, safe returns, time budgeting and teardown. See [M1 history](Implementation/M1-Kernel-Core.md) and [M2 evidence](Implementation/M2-Isolated-Execution.md).

The [sparse user-memory contract](Implementation/M2-User-Memory.md) provides private reservations, zero-fill and recoverable all-or-nothing commitment, with explicit prototype quotas. The [thread/TLS decision](Implementation/M2-User-Threads-and-Tls.md) records bounded scheduling, consuming join and raw FS storage. The [event/deadline decision](Implementation/M2-Events-and-Deadlines.md) records signal persistence, timeout/close ordering and kernel idle, including the delivered-tick clock's limits. The [guest PE contract](Implementation/M2-Pe-Image-Loading.md) adds protected sections, zero-fill and bounded DIR64 fixups with rollback. Before M2 stabilizes a user ABI, continue matching the selected upstream runtime's requirements for thread-local storage, threads, waits/wakes, clocks, exceptions and startup. Add mechanisms when a tested vertical slice needs them.
