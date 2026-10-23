# ADR 0030: Two boot paths to one boot contract, and the PocketTerm35 with a Raspberry Pi 5 as phase H's goal

**Status:** Accepted 2026-10-23 by the author's decision.
**Date:** 2026-10-23.
**Scope:** How WitOS boots on boards without a usable UEFI firmware, beginning with the Raspberry Pi 4 and 5, and the
hardware goal of phase H. It amends [ADR 0028](ADR-0028-First-Release-Target.md), whose orientation was a Pi 4 booted
by the `pftf/RPi4` UEFI firmware, with the Pi 5 deferred until its UEFI settled.

## Context

WitOS boots one way: its UEFI loader, linked into the kernel image, reads the memory map, the firmware's tables, an
entropy seed, the framebuffer, the boot package and the root task, exits the boot services and hands the kernel the
boot contract (`witos/boot.h`, version 7). Everything above that contract (the common kernel, the system layer, .NET)
is the same on every board; a board adds its platform in the kernel and its drivers in services.

The author's goal for WitOS is full computers, phones and tablets on current ARM and x86 processors, and embedded
systems with substantial hardware: terminals, televisions, medical equipment, cars. UEFI serves part of that: PCs,
servers, VMware and Arm SystemReady machines. Most embedded ARM boards, the Raspberry Pis and phones boot another way:
their bootloaders load a Linux arm64 kernel image with a device tree and an initial ramdisk, by the protocol Linux
defines (`Documentation/arch/arm64/booting.rst`). Raspberry Pi OS boots so: the Pi's own firmware loads the kernel,
the board's device tree with its overlays and the `initramfs` that `config.txt` names, with neither UEFI nor U-Boot.

What the Pi boards offer today:

- **Pi 4.** `pftf/RPi4` is a maintained EDK II, but in QEMU's `raspi4b` it stops at the RNG200, which QEMU does not
  model (H0.1), so no gate judges a Pi 4 booted through it.
- **Pi 5.** Its UEFI is a community effort whose upstream is archived; the most active fork targets the D0 revision and
  CM5, and the upstream notes that newer EEPROM firmware breaks UEFI's graphics on D0 boards. U-Boot's Pi 5 support is
  partial. No emulator models the Pi 5.

On 2026-10-23 the author set phase H's hardware goal: WitOS on the PocketTerm35 with a Pi 5, with the Pi 4 as the
first stage, since QEMU models it. The author has no board yet and starts in the emulator, then on a bare board.

## Decision

1. **Two producers of one boot contract.** The UEFI loader stays the path of PCs, VMware, servers and Arm SystemReady
   machines. A second entry boots by the Linux arm64 boot protocol and builds the same contract; the kernel never
   learns which path loaded it, and nothing of a firmware's structures reaches the common kernel.
2. **The entry.** An image with the Linux arm64 image header, entered at EL2 or EL1 with the MMU off and the device
   tree's physical address in x0 (x1–x3 zero), in a boot layer of its own beside `src/Boot.Uefi`:
   - it leaves EL2 for EL1, which the kernel requires, and gives the kernel the processor state it gets from UEFI on
     `virt` (an identity map with the MMU on), or the kernel's architecture layer learns to start without it, whichever
     the first step measures to be smaller;
   - it reads the device tree: the memory nodes less `/memreserve/`, `/reserved-memory`, the tree, the image and the
     initial ramdisk; `/chosen`'s ramdisk range and entropy seed;
   - it builds the contract: the memory map, the device tree's address (the field `virt` already uses), the entropy
     seed (from `/chosen`, else the board's generator; no seed, no boot, as on the UEFI path), the framebuffer (a
     `simple-framebuffer` node, or the board's own means, the VideoCore's mailbox on a Pi), and the package and the root
     task from the initial ramdisk, one file the image tool writes;
   - the secondary processors start by the device tree's `enable-method`: PSCI where TF-A runs (the Pi 5's firmware),
     a spin table where the firmware's stub parks them (the Pi 4's default, QEMU's `raspi4b`).
3. **The Pi's card.** A FAT partition with the Pi firmware's files pinned by SHA-256 as firmware is pinned (the Pi 4's
   GPU files and device tree; the Pi 5's device tree, its firmware being in EEPROM), a `config.txt` that names the
   kernel and the ramdisk (`kernel=`, `initramfs … followkernel`, `arm_64bit=1`, the display's overlay), the image and
   the ramdisk; an ext4 partition with the applications layer (`image --rpi4`, `image --rpi5`).
4. **Gates.** QEMU's `virt` boots the image through `-kernel` and `-initrd`, so the entry is judged on the board every
   ARM64 scenario runs on; `raspi4b` boots it through `-kernel`, `-dtb` and `-initrd` without firmware, and judges the
   Pi 4's platform and the devices QEMU models (the SD host, DWC2, the mailbox's framebuffer). The Pi 5 has no emulator:
   its processor, GIC-400 and timer are the Pi 4's path, and the rest is judged in the author's sessions.
5. **The goal.** Phase H ends on the PocketTerm35 with a Pi 5. The Pi 4 is the first stage: in the emulator first, on a
   board once one is bought.
6. **Later, by the same entry.** Phones and tablets, whose bootloaders boot an arm64 image with a device tree and a
   ramdisk; U-Boot's `booti` on embedded boards; riscv64 by its own Linux protocol (a0 the hart, a1 the device tree)
   when a RISC-V board becomes a target. x64 keeps UEFI alone.

## Consequences

- Phase H's steps are renumbered: H0 (measurement: H0.1 done, H0.2 the emulator's measurement of this path, H0.3 the
  author's session on a bought board), H1–H5 the PC and VMware (H1 the platform and ABI-1 1.5, H2 the USB keyboard, H3
  storage, H4 the network, H5 the VMware session), H6 this entry, H7 the Pi 4's platform, H8 its devices and card, H9
  the Pi 5's platform and the RP1, H10 the release on the PocketTerm35 with a Pi 5.
- The Pi 5 asks for what the Pi 4 does not: the PCIe root complex with the MIP as the ARM64 server of
  [ADR 0029](ADR-0029-PC-Interrupts-Power-and-Device-Table.md)'s message interrupts; the RP1, one PCIe function with
  many peripherals, whose children the platform describes from the device tree or a service of its own hands to their
  drivers (H9 decides); its xHCI under H2's driver; its Ethernet; and, most likely, a DPI driver of WitOS's own for the
  PocketTerm35's panel, since Raspberry Pi drives the Pi 5's DPI output only through Linux's KMS. Its real-time clock,
  behind the firmware, gives UTC where the Pi 4 has none.
- Risks the first step measures: whether QEMU and the Pi firmware put an entropy seed in `/chosen`; the processor state
  at entry in QEMU and on the boards; the framebuffer the firmware describes, if any.
- **Rejected:** the community EDK II for the Pi 5. Its upstream is archived, its forks disagree, D0 boards lose UEFI's
  graphics, and no emulator judges it.
- **Rejected:** U-Boot's UEFI as the Pi 5's firmware. Its Pi 5 support is partial, it adds a pinned firmware the
  author's boards do not need, and the device tree entry is wanted anyway for phones and embedded boards.
- **Rejected as the Pi 4's path:** `pftf/RPi4`. It boots the unchanged UEFI image on hardware, but no gate can judge
  it; a session may still compare it by hand.
