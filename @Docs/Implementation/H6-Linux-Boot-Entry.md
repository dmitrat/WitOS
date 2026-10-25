# H6 — The Linux arm64 boot protocol's entry

[ADR 0030](ADR-0030-Boot-Paths-and-the-Pi-5-Target.md) gives WitOS two producers of one boot contract: the UEFI loader
for PCs, VMware and servers, and an entry by the Linux arm64 boot protocol for the boards whose bootloaders load a
Linux image with a device tree and an initial ramdisk, the Raspberry Pis first. H6 is that entry. The kernel never
learns which path loaded it: both build the same `WitBootInfo`.

## 1. The pieces

| Where | What |
| --- | --- |
| `src/Boot.Linux` (the `boot-linux` layer, ARM64's kernel only) | `entry.S`: the protocol's entry (§3). `fdt.c`: the device tree's memory, reservations, initial ramdisk and entropy seed. `main.c`: the boot contract (§4). `boot_linux.h`: the initial ramdisk's format and what the three share. The layer links into the same kernel image as `src/Boot.Uefi`, whose reader of the image's own PE headers (`wit_boot_describe_image`) it calls. |
| WitOS.Dev `LinuxImage` | The kernel's PE image laid out flat, every section at its address, with the protocol's header written over the DOS header (§2). |
| WitOS.Dev `InitialRamdisk` | The root task's flat image and the boot package in one file: a page of header ("WITSRD1", the version, each part's offset and bytes), then each part at a page boundary. |
| `KernelImageBuilder` | For an architecture with `LinuxImage` (ARM64), every kernel build writes `WitOS-<arch>.Image` and `WitOS-<arch>.initrd` beside the boot disk; the boot disk keeps the UEFI loader's `BOOTAA64.EFI`. |
| `BootRequest.LinuxBoot`, `Virtualization` | QEMU's `-kernel` and `-initrd` in place of the firmware and the boot disk; `virtualization=on` gives the board the EL2 QEMU enters at. |
| `release --arch arm64` | Two more boots of the release kernel by the protocol (§5). |

## 2. The image

A bootloader copies the Image file to memory and enters its first byte. The PE image the linker writes keeps its sections
at file offsets of 512 bytes' alignment, and the merged `.bss` takes addresses but no file bytes, so `LinuxImage` lays it
out at its addresses: `SizeOfImage` bytes, 17 MiB for the release kernel (its PE file has 183 KiB), most of them the
`.bss` zeros.

The protocol's 64-byte header goes over the DOS header, as Linux's own images do it:

- word 0 is `ccmp x18, #0, #0xd, pl`, a NOP whose first bytes are "MZ";
- word 1 branches to `wit_linux_entry`, whose address the tool reads from the link map;
- `text_offset` is 0, `image_size` the image's size, and the flags are a little-endian kernel, 4 KiB pages and an image
  placed anywhere;
- the magic "ARM\x64" is at offset 56, and the last word stays the PE header's offset.

The tool also sets the file alignment to the section alignment and each section's file offset to its address, so that
the flat file is still a valid PE image; the host test reads it back with .NET's PE reader.

## 3. The entry

The protocol enters at EL2 or EL1 with the MMU and the data cache off, interrupts masked and the device tree's address
in x0 (H0.2 measured QEMU: EL1 on `virt`, EL2 on `virt,virtualization=on` and `raspi4b`). The entry:

1. **Leaves EL2** for EL1:
   - HCR_EL2 with RW, so that EL1 runs AArch64;
   - the counters open to EL1 (CNTHCTL_EL2), CNTVOFF_EL2 zero;
   - no trap of the floating-point unit, of the debug and monitor registers, or of CP15 (CPTR_EL2, MDCR_EL2, HSTR_EL2);
   - no stage 2 (VTTBR_EL2);
   - the GICv3 CPU interface's registers open to EL1 where the processor has them (ICC_SRE_EL2's SRE and Enable);
   - SCTLR_EL1's RES1 bits with the MMU off;
   - an exception return to EL1h with DAIF masked.
2. **Opens the floating-point unit** (CPACR_EL1), since the kernel's C uses its registers.
3. **Builds the first stage's identity map.** A level-0 table whose first entry is a level-1 table of gigabyte blocks:
   normal memory for the gigabytes of the image's first and last bytes and of the device tree, device memory for the rest
   of 512 GiB. The tables are invalidated from the data cache, written as they were with it off, before the walker reads
   them through it. Then MAIR, TCR (T0SZ 16, write-back walks, TTBR1's walks off), TTBR0 and the MMU and the caches on.
   The geometry is the kernel's, which was measured: `wit_a64_install_tables` writes TCR before TTBR0, and QEMU drops its
   TLB at the TCR write. With a first stage of three levels from level 1, the instruction after the TCR write was fetched
   through this table read as a level-0 one: a level-0 translation fault (ESR 0x86000004) in an endless loop at the
   synchronous vector, whose fetch faulted too.
4. **Applies the image's base relocations** where it was loaded: each `IMAGE_REL_BASED_DIR64` entry of the PE base
   relocation directory gets the difference from the preferred base (0x140000000). This happens with the MMU and the
   caches on, so that no stale line of the image's bytes outlives it. Nothing before it reads an absolute address: every
   reference is `adrp`/`add`.
5. **Calls the C** on a stack of its own in the image, with the tree and the level it entered at.

## 4. The contract

`wit_linux_main`:

- **Reads the device tree** once (`fdt.c`):
  - the root's #address-cells and #size-cells;
  - every memory node's reg;
  - the memory reservation block and the children of /reserved-memory;
  - /chosen's `linux,initrd-start`, `linux,initrd-end` and `rng-seed`.
- **Maps all of the tree's memory as normal memory** (the second stage, in the same geometry). A gigabyte wholly memory
  is one block. A gigabyte with some memory is a table of 2 MiB blocks, normal where they hold memory. Every other gigabyte
  is device memory.
- **Builds the memory map.** Each memory range in whole pages is cut where the reservations lie. The reservations are the
  tree's, the tree itself, the image and the initial ramdisk, each widened to whole pages.
- **Takes the root task and the package** from the initial ramdisk, which must be page-aligned: extents of 1 and 2 MiB
  over its parts, which lie in the map's reserved memory as the kernel requires.
- **Takes 32 bytes of `rng-seed`** as the entropy seed. Without one there is no boot, as on the UEFI path without its RNG
  protocol.
- **Fills the rest of the contract:** no framebuffer and no ACPI, and the tree's address as the UEFI path gives it. The
  flag that no firmware service runs is set.

## 5. Measured

`release --arch arm64` boots the release kernel five times: its three UEFI boots as before, and two by the protocol.

| Boot | QEMU | What ran |
| --- | --- | --- |
| `release-linux-256` | `virt` at EL1, 256 MiB, two processors | `[BOOT] Linux arm64 entry at EL1`; the tree at 0x48200000 (1 MiB, QEMU's padding); the ramdisk at 0x48000000; 7 memory regions, 237 MiB usable; both processors online through PSCI over `hvc`; the root task's mechanisms and its exit with 0 |
| `release-linux-el2-128` | `virt,virtualization=on` at EL2, 128 MiB, one processor | `[BOOT] Linux arm64 entry at EL2`, then EL1 as above; the tree at 0x44200000 and the ramdisk at 0x44000000; 109 MiB usable; the root task's exit with 0 |

The UEFI boots passed again with the layer linked in, and `test --arch arm64` with them (§7).

## 6. What is not yet

- **`raspi4b`.** The kernel's console is the platform's, and the ARM64 kernel's platform is `virt`'s, whose PL011 is at
  0x09000000. QEMU's `raspi4b` also puts no entropy seed in the tree (H0.2). The Pi 4's platform, with its console on the
  Pi's PL011, its GIC-400 and its spin-table secondaries, is H7, and H7's gate boots its kernel through this entry with a
  seed the runner adds to the tree.
- **Several processors entered at EL2.** The EL2 boot runs on one processor. The kernel starts the others through PSCI
  over `hvc`, which QEMU serves at EL1; whether its PSCI answers `hvc` from an EL1 below an EL2 is not measured.
- **A framebuffer.** No `simple-framebuffer` appears on `virt` without its firmware, and the Pi's comes from its
  VideoCore mailbox (H7).
- **The layer-2 and runtime scenarios** still boot through the UEFI path; only the release suite runs both.
- **RISC-V's protocol** (a0 the hart, a1 the tree) waits for a RISC-V board.

## 7. The gate

| Suite (container) | Result |
| --- | --- |
| `release --arch arm64` | passed: `release-128`, `release-512`, `release-display-256` through UEFI; `release-linux-256` and `release-linux-el2-128` by the protocol |
| `test --arch arm64` | passed: all 29 scenarios — the self-test kernel's foundation and users, the layer-2 scenarios on one and two processors, the reset, the rejected contract and map, the faults and the timeout |
