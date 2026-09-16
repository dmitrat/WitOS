# Working on WitOS

## Project direction

The core objective is standard upstream .NET above a minimal native kernel and a hardware-independent system layer. Shells and applications eventually run on that .NET platform. Current guest code is the native M1 kernel foundation with paging, timer interrupts and two preempted kernel contexts; host-side C# tooling must not be described as guest .NET support.

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
- Treat the boot handoff as an internal evolving contract, not a frozen external ABI.

## Validation

For native, boot, image or runner changes, run:

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- test
```

Use `setup` once for the pinned local QEMU package. The test tool must distinguish successful boot, panic, unexpected exit and timeout. Never infer a passing boot from an exit code or log line alone.

For runtime experiment or source-pin changes, run `runtime-audit` and `runtime-probe` through the same tool. Their results are hosted Windows evidence, never proof that .NET runs in the guest. Preserve the source/package pins and explicit profile limitations unless the task deliberately updates them.

Keep generated images, binaries, firmware, downloads, logs and credentials out of Git. They belong in ignored `artifacts/` or `.tools/`. Record actual results and limitations in documentation; do not mark a future milestone complete based on M0 boot.
