# Immediate development sequence

The core objective remains a minimal hardware-dependent kernel, a common system layer supporting upstream .NET, and applications/shells built above .NET.

## Completed: M0 and initial M1

- Independent x64 boot, WitBootInfo, serial diagnostics, disk images and C# host tools.
- Kernel-owned stacks, GDT/TSS/IDT, fatal CPU diagnostics and emergency double-fault handling.
- Physical pages, kernel-owned page tables, image protection, stack guards and scratch mapping operations.
- Timer interrupts and preemptive switching between two kernel contexts.
- Seventeen real VM scenarios.

See [M0 history](M0-Boot.md), the [first M1 slice](M1-Memory-and-Exceptions.md) and the [completed initial M1](M1-Kernel-Core.md).

Guest .NET support does not exist yet.

## Next: runtime requirements and M2 design

1. Pin one upstream .NET revision and inventory NativeAOT bootstrap dependencies.
2. Map required memory, GC, thread-local storage, threads, waits, time and exception mechanisms onto the kernel.
3. Define the smallest user ABI and loader contract needed for isolated execution and the eventual runtime.
4. Build a separate user address space with a protected kernel boundary.
5. Run an unprivileged native component and validate syscall entry/return.
6. Prove that invalid user accesses terminate that component without corrupting the kernel.
7. Add explicit capability handles and minimal communication as required by the next vertical slice.

The existing scheduler is a fixed two-worker kernel demonstration. General thread lifecycle, blocking/waking and user contexts need explicit contracts and tests.

## NativeAOT bootstrap

Deliver the dependency inventory as RFC 0015 plus executable experiments. Separate required runtime mechanisms from optional BCL features, avoid cycles through managed services and maintain a patch inventory.

M3 must run a real NativeAOT system component in an isolated context, testing GC, exceptions and threading in addition to console output. CoreCLR and unchanged ordinary IL applications remain a separate later milestone.

## Current limits to preserve or deliberately revise

- One x64 CPU, QEMU q35, UEFI boot and Windows-hosted tooling.
- Usable physical addresses below 4 GiB.
- One kernel address space; no user-mode isolation yet.
- Legacy PIC/PIT timer; no real-time guarantees.
- Baseline x87/SSE context state, no AVX/XSAVE or per-thread FS/GS switching.
- Reserved firmware memory is not reclaimed automatically.
- Mapping and physical-page APIs are internal bootstrap mechanisms, not security capabilities.

## Deferred

Broad hardware, firmware flashing, GPU drivers, a custom filesystem, GUI, stores and distributed orchestration do not block these steps.
