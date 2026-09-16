# Immediate development sequence

The core objective remains a minimal hardware-dependent kernel, a common system layer supporting upstream .NET, and applications/shells built above .NET.

## Completed: M0 and the first M1 slice

- M0: independent x64 boot, WitBootInfo, serial diagnostics, disk image, C# tooling and automated VM execution.
- M1 foundation: a kernel-owned stack, GDT/TSS/IDT, fatal CPU exception diagnostics, a double-fault emergency stack and a physical-page allocator.
- Eleven real VM scenarios verify normal boot, memory behavior, malformed handoffs, six CPU exceptions and timeout handling.

See [M0 history](M0-Boot.md) and [current M1 foundation](M1-Memory-and-Exceptions.md). Guest .NET support does not exist yet, and M1 is not complete.

## Next: finish M1

1. Define and establish kernel-owned page tables while preserving the image, stacks, boot data and required physical mappings.
2. Add mapping/protection operations and stack guard pages.
3. Validate controlled page faults under the new mappings.
4. Add a timer interrupt, then enable interrupts under explicit control.
5. Introduce two execution contexts and test switching.
6. Reclaim firmware memory only after the active mappings and metadata no longer depend on it.

The current physical allocator is single-CPU and bounded to usable physical addresses below 4 GiB. Evolve these restrictions when the next tested slice requires it.

## Before committing to a broad ABI: NativeAOT investigation

- Select and pin one upstream .NET revision.
- Inventory the actual native runtime/PAL and BCL dependencies for a minimal system component.
- Separate required runtime mechanisms from optional library features.
- Map memory, GC, threads, thread-local storage, waits, exceptions and time to WitOS.
- Define a bootstrap path without dependency cycles through managed services.
- Keep an explicit runtime patch inventory.

Deliver this as RFC 0015 plus executable experiments. It should inform M1/M2 decisions and does not need to wait for storage or graphics.

## M2 and M3 acceptance direction

M2: isolated execution, a small user ABI, controlled communication and rejection of unauthorized access.

M3: a genuine NativeAOT system component in an isolated context, with allocations/GC, exceptions and threading tested in addition to console output.

CoreCLR and unchanged ordinary IL applications remain a separate later compatibility milestone.

## Deferred

Broad hardware, firmware flashing, GPU drivers, a custom filesystem, a graphical shell, stores and distributed orchestration do not block these steps.
