# M0 — Independent x64 boot

Status: implemented and locally verified on 2026-09-16.

This document records the original M0 slice. The [first M1 slice](M1-Memory-and-Exceptions.md) and [completed M1 kernel core](M1-Kernel-Core.md) describe the later implementation.

## What runs

```text
QEMU q35 / x64 / TCG
    -> EDK II UEFI
    -> EFI/BOOT/BOOTX64.EFI
    -> WitOS UEFI adapter
    -> ExitBootServices
    -> WitBootInfo
    -> common kernel
    -> COM1 output and QEMU test exit
```

The adapter and kernel are separately organized source components linked into one EFI executable for M0. UEFI provides the executable loader. No custom disk bootloader is required.

After successful `ExitBootServices`, execution never returns to firmware and makes no further boot-services calls. Kernel console output uses port I/O directly. This proves execution after firmware handoff; it does not prove a complete kernel, hardware isolation or managed execution.

## Implementation choices

- **C17 and MSVC x64** for the small native seed, using existing development tools.
- **UEFI adapter** as one of the boot paths allowed by the implementation strategy.
- **One internal boot structure** containing version, architecture, flags and normalized memory regions.
- **C# host tools** with no third-party NuGet dependencies.
- **Standard FAT16 boot medium**, written by a small image builder. This is host-side packaging, not a new guest filesystem.
- **Pinned QEMU 11.1.0** with its bundled EDK II firmware.
- **Headless TCG**, without guest networking or host hardware passthrough.
- No project license has been selected by this implementation step.

The Windows host restriction is a tooling limitation of the first slice, not a requirement for the eventual operating system.

## Boot handoff

The adapter validates the UEFI system/boot-services table signatures and minimum table sizes. It reads the memory map into a fixed 128 KiB buffer, checks descriptor size/version and normalizes up to 1024 regions.

Only `EfiConventionalMemory` becomes `WIT_MEMORY_USABLE`. All other UEFI types are conservatively reserved. This preserves the image, current stack, firmware page tables and runtime data.

No firmware allocation occurs between the final memory-map read and `ExitBootServices`. If firmware rejects a stale map key, the adapter obtains a new map and retries, at most three times. Unsupported maps fail explicitly.

On success, the adapter disables maskable interrupts, sets the exited-services flag and enters the common kernel. The common kernel rejects invalid magic, version, size, architecture, flags, counts and malformed region lengths.

The boot adapter is trusted. These validation checks are not a security boundary against malicious firmware or arbitrary pointers.

## Test protocol

| Guest outcome | Serial evidence | QEMU exit |
| --- | --- | --- |
| Successful boot | ExitBootServices, Boot.Contract and Boot.Hello markers in order | 33 |
| Invalid boot contract | ExitBootServices followed by the expected panic; no success markers | 35 |
| Hung guest | Successful boot markers followed by no exit | Host timeout |

The x64 test device receives 0x10 for success or 0x11 for panic. QEMU maps it to `(value << 1) | 1`. The host wrapper normalizes passing scenarios to exit 0.

The deliberate timeout scenario must first reach the kernel; merely timing out during firmware startup is a test failure. Logs from each scenario are saved separately.

## Verified locally

- Host tool builds with zero warnings and zero errors.
- Normal boot at 128 MiB and 512 MiB RAM.
- Additional normal 256 MiB development run.
- Invalid boot version is rejected after firmware exit.
- A deliberately hung kernel is killed and classified as a timeout.
- PE inspection confirms x64 EFI subsystem and absence of native imports.

GitHub Actions runs the same integration suite and uploads the ordinary boot image, debug symbols and logs.

## Deliberate limitations

- The kernel still uses UEFI's stack, address mapping and descriptor-table setup. It cannot reclaim boot-services or loader memory yet.
- No physical allocator, own page tables, IDT, interrupt handling, SMP, scheduler or user mode.
- Arbitrary CPU exceptions are not handled yet. The tested panic is an explicit validation failure.
- COM1 is fixed at 0x3F8 for the QEMU PC target; it is not a generic device-discovery mechanism.
- Test completion uses an x86 QEMU debug-exit port. It is not a production shutdown API.
- Fixed boot buffers limit accepted firmware maps.
- The image is tested only on QEMU. It has not been validated for physical machines or firmware flashing.
- `WitBootInfo` uses 64-bit identity-mapped pointers for M0 and can evolve.
- The guest contains no .NET runtime yet.

## References

- [UEFI specification](https://uefi.org/specifications)
- [EDK II UEFI ABI declarations](https://github.com/tianocore/edk2/blob/master/MdePkg/Include/Uefi/UefiSpec.h)
- [QEMU Windows distribution linked from qemu.org](https://www.qemu.org/download/#windows)
- [Pinned QEMU installer and published checksums](https://qemu.weilnetz.de/w64/)
- [QEMU debug-exit implementation](https://github.com/qemu/qemu/blob/master/hw/misc/debugexit.c)

QEMU and firmware are downloaded tools with their own licenses, retained in the extracted distribution under `.tools/`; their source or binaries are not vendored into WitOS.
