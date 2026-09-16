# Immediate development sequence

The user's core objective is a minimal hardware-dependent kernel, a common system layer that supports upstream .NET, and applications/shells built above .NET. The current boot prototype is the first foundation for that objective.

## Completed: M0

A real x64 UEFI boot path, WitBootInfo, serial diagnostics, a FAT16 boot image, a C# development tool and automated QEMU scenarios.

See [M0](M0-Boot.md). This does not establish guest .NET support.

## Next: M1 memory and exception foundations

1. Establish a kernel-owned stack and document all image/boot reservations.
2. Install an x64 exception table with useful fatal-fault diagnostics before enabling interrupts.
3. Implement a physical-page allocator using only authorized usable regions.
4. Verify allocation, exhaustion and preservation of reserved ranges in QEMU.
5. Introduce kernel-owned mappings and controlled page faults.
6. Add a timer and two simple execution contexts, then test switching.

Do not reclaim firmware or loader memory until the active stack, mappings and boot metadata no longer depend on it.

## Before committing to a broad ABI: NativeAOT investigation

- Select and pin one upstream .NET revision.
- Inventory the actual native runtime/PAL and BCL dependencies for a minimal system component.
- Separate required runtime mechanisms from optional library features.
- Document how memory, GC, threads, thread-local storage, waits, exceptions and time map to WitOS.
- Define a bootstrap path that does not depend on services whose own runtime has not started.
- Keep an explicit patch inventory and avoid claims that a runtime port is merely a linker change.

Deliver this as RFC 0015 plus executable experiments. It should inform M1/M2 decisions; it need not wait for storage or graphics.

## M2 and M3 acceptance direction

M2: isolated execution, a deliberately small user ABI, controlled communication and rejection of unauthorized access.

M3: a genuine NativeAOT system component in an isolated execution context, with allocations/GC, exceptions and threading tested in addition to console output.

CoreCLR and unchanged ordinary IL applications are a separate later compatibility milestone.

## Deferred

Broad hardware, firmware flashing, GPU drivers, a custom filesystem, a graphical shell, application stores and distributed orchestration do not block these steps.

Future hosted experiments may help validate contracts, but the current task sequence preserves the native kernel/runtime objective.
