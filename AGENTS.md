# Working on WitOS

## Project direction

The core objective is standard upstream .NET above a minimal native kernel and a hardware-independent system layer. Shells and applications eventually run on that .NET platform. Current guest code is only the M0 native boot foundation; host-side C# tooling must not be described as guest .NET support.

Original vision documents live in `@Docs/`. Concrete implementation status and deliberate limitations live in `@Docs/Implementation/`. Preserve original drafts unless the task calls for revising them.

## Boundaries

- Keep UEFI details in `src/Boot.Uefi/`.
- Keep x64 instructions and QEMU-specific test mechanisms in `src/Kernel.Arch.X64/`.
- Keep the common kernel independent of firmware structure definitions.
- Use ordinary .NET for development tools.
- Do not add speculative public resource APIs, distributed services or GUI work to a boot milestone.
- Any temporary dependency on firmware state must be documented before memory can be reclaimed.
- Treat the boot handoff as an internal evolving contract, not a frozen external ABI.

## Validation

For native, boot, image or runner changes, run:

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- test
```

Use `setup` once for the pinned local QEMU package. The test tool must distinguish successful boot, panic, unexpected exit and timeout. Never infer a passing boot from an exit code or log line alone.

Keep generated images, binaries, firmware, downloads, logs and credentials out of Git. They belong in ignored `artifacts/` or `.tools/`. Record actual results and limitations in documentation; do not mark a future milestone complete based on M0 boot.
