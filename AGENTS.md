# Working on WitOS

## Project direction

The core objective is standard upstream .NET above a minimal native kernel and a hardware-independent system layer. Shells and applications eventually run on that .NET platform. Current guest code includes the M1 foundation, M2 ring-3 isolation, sparse user memory, bounded user threads/TLS and events/deadlines; host-side C# tooling must not be described as guest .NET support.

Original vision documents live in `@Docs/`. Concrete implementation status and deliberate limitations live in `@Docs/Implementation/`. Preserve original drafts unless the task calls for revising them.

## Boundaries

- Keep UEFI details in `src/Boot.Uefi/`.
- Keep x64 instructions, descriptor tables and QEMU-specific test mechanisms in `src/Kernel.Arch.X64/`.
- Keep the common kernel independent of firmware structure definitions.
- Use ordinary .NET for development tools.
- Do not add speculative public resource APIs, distributed services or GUI work to a boot milestone.
- The kernel now owns its page tables, but firmware memory remains reserved until reclamation is explicitly designed and tested.
- Treat mapping/physical-page operations as serialized bootstrap APIs. Current allocation and mapping setup runs with interrupts disabled.
- Context switching currently preserves baseline x87/SSE state; do not introduce AVX/XSAVE assumptions without extending state management and tests.
- Keep shared kernel mappings supervisor-only. Never reuse the kernel scratch branch as a user page-table branch.
- Validate user handles and complete buffer ranges before copying/output. Close handles and free only component-owned pages on teardown.
- Keep dynamic reservations separate from fixed image/stack mappings. Reservation must not consume backing RAM; failed commit must roll back additions; decommit retains the reservation and recommit zeroes pages.
- Track committed no-access pages as owned. Invalidate active translations before reuse, reclaim empty private page tables, and keep memory-call work bounded by prototype quotas.
- Validate user return addresses/selectors/flags and the selected thread's owning stacks before IRETQ. Timer returns must preserve condition codes; syscall flags follow the explicit ABI.
- Switch TSS.RSP0 and raw FS-based TLS with the selected user thread. Keep FSGSBASE disabled and restore zero FS base on kernel supervision; the current kernel has no segment-based TLS.
- Publish join wait edges and wakeup results with interrupts disabled. Reject cycles before parking, preserve thread resources until exit/join/close, and roll back all partial thread creation.
- Serialize event state checks, parking and completion publication with interrupts disabled. Expire parked deadlines before later signal/close calls; completed waits must not follow a reused event handle.
- Distinguish join edges from event/sleep waits. Idle IRQs must resume their CPL0 frame without replacing saved user contexts; use the adjacent STI/HLT/CLI path with zero kernel FS base.
- The current clock counts delivered PIT ticks only. Do not describe its nominal frequency as calibrated elapsed time or wall-clock support.
- Keep NativeAOT Static/Shared intermediate and output paths separate; their same-named libraries are different artifacts. Preserve strict missing-symbol link checks without dummy runtime/OS implementations or forced linking.
- Treat the boot handoff and experimental user ABI as evolving contracts, not a frozen public SDK.

## Validation

For native, boot, image or runner changes, run:

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- test
```

Use `setup` once for the pinned local QEMU package. The test tool must distinguish successful boot, panic, unexpected exit and timeout. Never infer a passing boot from an exit code or log line alone.

For runtime experiment or source-pin changes, run `runtime-audit`, `runtime-probe` and `runtime-target` through the same tool. Their results are hosted Windows evidence, including native C-host bootstrap, never proof that .NET runs in the guest. Preserve the source/package pins and explicit profile limitations unless the task deliberately updates them.

Keep generated images, binaries, firmware, downloads, logs and credentials out of Git. They belong in ignored `artifacts/` or `.tools/`. Record actual results and limitations in documentation; do not mark a future milestone complete based on M0 boot.
