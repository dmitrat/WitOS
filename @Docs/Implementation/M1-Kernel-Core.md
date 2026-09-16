# M1 — Kernel core

Status: implemented and locally verified on 2026-09-16.
Version: WitOS 0.0.3. This records the M1 milestone; [WitOS 0.0.4](M2-Isolated-Execution.md) adds the first user isolation boundary.

This completes the initial M1 Definition of Done: physical allocation, virtual mappings and protection, exception diagnostics, timer interrupts, at least two scheduled contexts, and automated QEMU checks. It is a small kernel demonstration, not a production scheduler or a managed OS.

## Boot and ownership

The UEFI adapter now supplies WitBootInfo v2 with the loaded image extent and normalized section permissions. PE parsing stays in Boot.Uefi. The common boot contract contains no PE or firmware structures.

The kernel owns its stacks, GDT/TSS/IDT, physical allocator and page tables. It switches CR3 to a freshly allocated root and stops using firmware mappings. Firmware/loader memory remains reserved conservatively; it is not reclaimed in this milestone.

## Address space

Four-level paging uses 4 KiB pages and a single kernel address space:

- Usable physical RAM is identity-mapped read/write and non-executable.
- Image headers and gaps are read-only and non-executable.
- Image sections receive their normalized read/write/execute permissions.
- Writable executable image sections and overlapping sections are rejected.
- No user-accessible mappings are installed.
- Physical page zero and all stack guard pages remain unmapped.
- Firmware and device regions are not broadly identity-mapped.

CR0.WP enforces read-only pages even in ring 0. IA32_EFER.NXE enables execute-disable checks. Inherited global TLB entries are flushed before activating the new root. PCID and five-level paging are outside the current backend.

The image must be page-aligned, fit below 4 GiB and occupy reserved memory. The physical allocator retains the explicit below-4-GiB usable-address limit.

## Mapping operations

The internal bootstrap API can map, protect and unmap NX scratch pages in a reserved 16 MiB virtual range. It accepts only allocated physical pages, rejects duplicate mappings and invalid addresses, and invalidates affected TLB translations after changes.

It does not expose a frozen syscall API. Callers serialize these bootstrap operations; current setup uses them with interrupts disabled. Callers must remove all aliases before freeing a page. Read-only protection applies to a mapping; a separate writable alias is still writable. Executable scratch mappings are intentionally unavailable.

Page-table pages remain allocated for the kernel's lifetime. Intermediate table allocation exhaustion is fatal in this bootstrap implementation. There is no SMP synchronization, TLB shootdown, page-table reclamation, demand paging, swapping or user address-space management.

## Stack guards

The kernel, double-fault handler and two worker contexts have page-aligned stack regions with an unmapped 4 KiB page at each end: eight guard pages in total.

The ordinary and worker stacks have 64 KiB usable space. The double-fault stack has 32 KiB usable space and remains attached to IST1. Tests fault on both ends of the kernel stack and continue testing actual double-fault delivery on the emergency stack.

## Timer

The controlled one-CPU QEMU PC backend uses the legacy PIT channel 0 and 8259 PIC:

- Local APIC delivery is explicitly disabled; x2APIC mode is rejected.
- The PIC is remapped to vectors 0x20 and 0x28.
- Only IRQ0 is unmasked.
- PIT mode 2 runs at approximately 100 Hz.
- IRQ0 is acknowledged before selecting another context.

This is a bootstrap timer for the pinned PC VM, not the final APIC/HPET timer architecture. It does not establish real-time guarantees or a calibrated public clock API.

## Preemptive execution

The timer interrupt saves all general-purpose registers, the hardware interrupt frame and the baseline x87/SSE state through FXSAVE64. Dispatch returns a saved context; assembly restores it and uses IRETQ.

Two fixed kernel workers share one address space and have separate stacks. They do not voluntarily yield. Each spins until it has received at least three scheduled slices, then marks itself complete. The scheduler returns to the saved bootstrap context when both finish.

Example output:

```text
A: 1
B: 1
A: 2
B: 2
A: 3
B: 3
Timer ticks: 7
Context switches: 7
```

Additional slices are allowed under delayed timer delivery. The tests require ordered per-worker counters, progress by both workers and consistent switch accounting.

Each worker carries distinct GPR, XMM0, XMM6 and MXCSR sentinels. Corruption fails the guest check. Every incoming interrupt context must be aligned and lie in its owning stack.

The baseline QEMU CPU and generated code use x87/SSE state only. AVX/AVX-512/XSAVE state, per-thread FS/GS bases, dynamic thread creation, blocking/waking, priority policy, SMP and user-mode scheduling are not implemented.

Interrupts are enabled only for this controlled demonstration. On completion the bootstrap code disables interrupts and masks the PIC before reporting success.

## Validation

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- test
```

All 17 VM scenarios passed locally:

- Two normal RAM configurations: 128 MiB and 512 MiB.
- Invalid boot version and overlapping memory-map rejection.
- Six CPU exceptions from the previous milestone.
- Write to kernel code: page fault, error 3.
- Execute data: page fault, error 17.
- Lower and upper stack guard accesses: page fault, error 2.
- Write through a newly read-only alias: page fault, error 3.
- Read a removed alias: page fault, error 0.
- Deliberate hang after successful initialization and scheduling: host timeout.

Normal runs also check physical-page behavior, actual mapping reads/writes, aliases, permission changes, unmapping, remapping to a different physical page and TLB invalidation.

The host validates exception vectors, error codes, CR2 addresses and stacks. Successful boots require paging, allocation, mapping, timer and context-state checks before accepting the final success marker. CI runs the same suite.

## Next boundary

M2 introduces separate address spaces and unprivileged execution. Before stabilizing its user/kernel ABI, inventory a pinned upstream NativeAOT runtime's memory, threading, wait, time and exception requirements. Guest .NET is still absent.

## References

- [Intel system programming manuals](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html)
- [PE image format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)
- [QEMU 8259 implementation](https://github.com/qemu/qemu/blob/master/hw/intc/i8259.c)
- [QEMU 8254 implementation](https://github.com/qemu/qemu/blob/master/hw/timer/i8254.c)
