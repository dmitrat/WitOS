# H0 — Measurement before the first line of the hardware platforms

Plan step H0 (phase H, [ADR 0028](ADR-0028-First-Release-Target.md)), measured on 2026-10-21 and 2026-10-23 as V0
measured RISC-V: what the unchanged images meet on the release's targets, where they stop, and the decisions that
follow, before any line of `Kernel.Platform.Pc` or `Kernel.Platform.Bcm2711`. The step has three parts:

- **H0.1** (this document's measurements): VMware Workstation, the first target, measured whole; the Raspberry Pi 4B
  through QEMU's `raspi4b` and through `virt` with a GICv2.
- **H0.2** (pending): the emulator's measurement of the Linux arm64 boot path that
  [ADR 0030](ADR-0030-Boot-Paths-and-the-Pi-5-Target.md) chose for the Pi boards.
- **H0.3** (§7, pending): the author's session on a bought board, which nothing emulated can replace.

The author decided on §5's proposals on 2026-10-23: [ADR 0029](ADR-0029-PC-Interrupts-Power-and-Device-Table.md)
accepts the message interrupts, the power without AML and the larger device table, with two changes (the keyboard is USB
on every target, so the port call leaves release 1; power goes through the root task's exit status instead of a call),
and [ADR 0030](ADR-0030-Boot-Paths-and-the-Pi-5-Target.md) boots the Pi boards by the Linux arm64 boot protocol instead of
`pftf/RPi4` and makes the PocketTerm35 with a Pi 5 phase H's goal. The plan's steps were renumbered then: the PC's are
H1–H5, the Pi's H6–H10; this document's references below use the new numbers.

Nothing of the measurement is committed code: the q35 platform was instrumented in a scratch tree and the tree
reverted; the facts are the logs'.

## 1. Method

| Target | How |
| --- | --- |
| VMware Workstation 25.0.1 (build 25219725) on the Windows host | A virtual machine written by hand (`.vmx`): EFI firmware with `efi.serialconsole.enabled`, so that the loader's `[BOOT]` lines reach the first serial port, which writes to a file; 1024 MiB and four processors; the devices a new Workstation machine has (four PCIe root-port bridges of eight functions each, a SATA controller) and those release 1 needs (an NVMe controller, an e1000e and a vmxnet3 adapter, xHCI with a USB keyboard, the SVGA adapter, the HPET); the tool's x64 `libc` image converted by `qemu-img` to a VMDK on the SATA controller. Started and stopped with `vmrun` without a window; no VMware Tools. |
| The q35 platform, instrumented | Printed the ACPI tables, the FADT's power, reset and boot-architecture fields, the whole DSDT in hexadecimal (disassembled on the Linux host by `iasl` 2023 from `acpica-tools`), the MADT's I/O APIC and override entries, every PCI function's raw interrupt register and capability list, PIIX4's PIRQ routing registers and the i8042's status port; the HPET's requirement that its period divide 10^15 relaxed; after the QEMU exit port, a write of ACPI's soft-off, or in a second build of the reset register. |
| QEMU 11.1 (the pin) | `raspi4b` with `pftf/RPi4` v1.53 (`RPi4_UEFI_Firmware_v1.53.zip`, SHA-256 `ca9973e2a7a546b3df871cfb7382e656829114b6dfa424f40dc67cc90a217d88`): `RPI_EFI.fd` placed at address 0 by the generic loader with the first processor's program counter there, as the GPU's `armstub=` places it, and the board's DTB at 0x3E0000 as `config.txt` names it; `virt,gic-version=2` with the pin's EDK II, an entropy device and the existing ARM64 self-test image. |

## 2. VMware: the loader and the kernel

| What | Measured |
| --- | --- |
| Loader | ACPI published, no device tree, `EFI_RNG_PROTOCOL` gave the seed, GOP 1024×768 BGRX at 0xF0000000 (mode 3 of 29, the SVGA adapter), the root task read, `ExitBootServices` returned. 969 MiB of 1024 usable in 29 regions (461 of 512 in the first machine). |
| The unchanged kernel | Stops at `[PANIC] Unsupported q35 HPET`. VMware's HPET (capabilities 0x8086AF01: vendor 0x8086, 16 timers, legacy routing) counts with a period of 69,841,279 fs, 14,318,179 Hz, which does not divide 10^15 as `hpet.c` requires. |
| With that check relaxed | The clock's tests pass; UTC from the CMOS RTC; `Processors present/online: 4/4` through the INIT and start-up interrupts; the PIT's tick; PCI through ports 0xCF8/0xCFC; the root task runs `musl 1.2.5 on x86_64: 177 checks passed, 0 failed`. A boot from the firmware to the end of the libc program takes about four seconds. |
| Exit | QEMU's `isa-debug-exit` (port 0xF4) does nothing; the machine stays up. |

ACPI, read whole:

| Table | Facts |
| --- | --- |
| RSDP and XSDT | revision 2; SRAT, FACP, APIC, MCFG, HPET, WAET and WSMT; no SSDT, no DMAR (no IOMMU), no BGRT |
| FADT (revision 4) | PM1a_EVT 0x440, PM1a_CNT 0x444 (two bytes), PM_TMR 0x448, SCI 9, SMI_CMD 0xB2 with ACPI_ENABLE 0 (the firmware hands over in ACPI mode), IAPC_BOOT_ARCH 0x0003 (legacy devices, an 8042), flags 0x4421 (WBINVD, SLP_BUTTON, RESET_REG_SUP, PCI_EXP_WAK); RESET_REG system I/O 0xCF9, value 6 |
| MADT | local APIC 0xFEE00000, PCAT_COMPAT; I/O APIC 4 at 0xFEC00000, GSI base 0; IRQ 0 overridden to GSI 2 (active high, edge); LINT1 the NMI of each processor |
| MCFG | ECAM at 0xE0000000, segment 0, buses 0–127 |
| HPET | block 0xFED00000 |
| DSDT (42,215 bytes, `PTLTD`) | `_S5` is a static `Package {0, 0}`; `_PIC` stores its argument in `GPIC`; `PCI0._PRT` returns `PPRT ()`, which in APIC mode (`GPIC == 1`) is a static package of 128 entries `{slot | 0xFFFF, pin, 0, GSI}` with GSIs from 17, and in PIC mode names the link devices `LNKA`… whose `_SRS` program PIIX4; the keyboard controller `PNP0303` at ports 0x60 and 0x64 with IRQ 1, the RTC `PNP0B00`, the HPET `PNP0103`, 34 COM ports `PNP0501` |

Power, tried from the kernel's last instruction:

- **Soft-off.** SLP_EN with `_S5`'s SLP_TYP 0 (0x2000) written to PM1a_CNT 0x444: VMware's log reads `VMX: Issuing
  power-off request` the same instant, and the machine is off.
- **Reset.** 6 written to 0xCF9: the processors reset (`CPU reset: soft`) and the machine boots again, 26 times in 100
  seconds, each boot to the end of the libc program.

## 3. VMware: the PCI functions

Every function with an interrupt pin has 0xFF in its Interrupt Line register, and PIIX4's PIRQ routing registers
(00:07.0, 0x60–0x63) read 0x80808080: the firmware routes no INTx at all.

| Function | Device | Pin | MSI | MSI-X |
| --- | --- | --- | --- | --- |
| 00:00.0 | 8086:7190 host bridge (440BX) | — | — | — |
| 00:01.0 | 8086:7191 PCI bridge | — | — | — |
| 00:07.0 | 8086:7110 PIIX4 ISA bridge | — | — | — |
| 00:07.1 | 8086:7111 IDE | — | — | — |
| 00:07.3 | 8086:7113 PIIX4 power management | — | — | — |
| 00:07.7 | 15AD:0740 VMCI | A | 1 vector | 3 |
| 00:0F.0 | 15AD:0405 SVGA II | A | — | — |
| 00:11.0 | 15AD:0790 PCI bridge | — | — | — |
| 02:00.0 | 15AD:0774 UHCI | A | — | — |
| 02:01.0 | 15AD:07E0 AHCI | A | 1 vector, 64-bit | — |
| 00:15.0–00:18.7 | 15AD:07A0 PCIe root port, 32 functions | — | 1 vector, maskable | — |
| 03:00.0 | 8086:10D3 e1000e (82574L) | A | 1 vector | 5 |
| 0B:00.0 | 15AD:07B0 vmxnet3 | A | 1 vector | 25 |
| 13:00.0 | 15AD:077A xHCI | A | 1 vector, maskable | 31 |
| 1B:00.0 | 15AD:07F0 NVMe | A | 1 vector | 16 |

49 functions, 33 of them bridges. The kernel's device table holds 16 descriptors (`WIT_DEVICE_CAPACITY`, one page): it
fills before the NVMe controller, the xHCI controller and the framebuffer (`descriptors described 16 of capacity 16`).
The table's header already carries `Count` and `DescriptorSize`.

The ECAM window is MCFG's: the 440BX has no PCIEXBAR, so the q35 platform's reading of it gives none, and the
platform's PIRQ E–H routing finds no ICH9 at 00:1F.0 and keeps the firmware's lines, which are none.

## 4. The Pi 4 in QEMU and the GICv2

- **`pftf/RPi4` in `raspi4b`.** TF-A's BL31 v2.9 runs at EL3 and returns to the UEFI at EL2 (PC 0x20000); the UEFI
  prints its banner on the mini UART (`UEFI firmware (version UEFI Firmware v1.53 …)`) and then takes a data abort at
  0xFE104010: the RNG200, which QEMU does not emulate (it warns that it disabled the DTB's `brcm,bcm2711-rng200`, as it
  disabled PCIe, GENET and the thermal sensor). Loaded as `-kernel` (at 0x80000) nothing runs. The firmware's path
  ends there.
- **`raspi4b`'s devices.** It takes `-device usb-kbd` on the DWC2's bus; it has no PCI bus (`sdhci-pci` is refused), so
  its SD controller is the board's own.
- **`virt,gic-version=2`.** The pin's EDK II runs and starts the loader (device tree published, entropy from the
  virtio device); the ARM64 self-test kernel passes every test up to the scheduler's preemption test, which arms the
  interrupt controller, and stops at a data abort at 0x080A0014: `virt`'s GICv3 redistributor frame, absent under a
  GICv2. Nothing before interrupts assumes a GICv3.

## 5. Decisions proposed

Each is a proposal with its measurement, as written before the author's decision of 2026-10-23 (ADR 0029, ADR 0030;
the introduction says what changed).

1. **Interrupts of the PC: MSI first.** VMware routes no INTx, and every device release 1 drives offers MSI (AHCI) or
   MSI-X (NVMe, e1000e, vmxnet3, xHCI). Proposed for ABI-1 1.5: a binding of a device's MSI or MSI-X vector to an
   event, for which the kernel allocates a vector of the boot processor and returns the message's address and data;
   the driver writes them into its function's capability through its configuration region, so the kernel still
   programs no device. The same binding serves virtio's MSI-X on q35, which ends the four-line limit and the clash of
   the keyboard and the network adapter on one board (W1.1); `virt`'s GICv3 ITS gives it ARM64; the Pi's devices keep
   their SPIs; riscv64 keeps the PLIC's lines until an AIA board. The I/O APIC is not needed for release 1, the PIC
   keeps the ISA lines (the keyboard's IRQ 1), and a line with several bindings stays unneeded.
2. **Port I/O for layer 2: a call on a port-region capability.** The only release-1 device on ports is the i8042
   (0x60 and 0x64, a byte per key event; COM1 and the RTC are the kernel's); everything else is memory-mapped. A call
   that reads or writes one, two or four bytes within the descriptor's port region is validated like every call and
   costs nothing at a switch, where the TSS's I/O bitmap would be rewritten per process; the bitmap waits for a device
   whose port traffic a call cannot carry.
3. **ACPI without AML.** The PC platform reads the FADT and the DSDT's `_S5` when it is a static package (anything else:
   no soft-off, a halt with a line that says so) and resets through RESET_REG; ABI-1 1.5 gains a power call behind a
   capability the root task alone receives, as `CLOCK_SET`'s, which the root task uses when it ends instead of halting
   the machine. No ACPICA for release 1: INTx routing would need `_PIC` and `_PRT` evaluated, which MSI first avoids.
4. **The device table.** The platform describes no bridges (33 of VMware's 49 functions; no driver drives one), and the
   table grows beyond one page (64 descriptors proposed), which readers of `Count` take unchanged; a reader that maps
   one page needs the table's size.
5. **The PC platform's facts for H1.** ECAM from MCFG; the HPET of any period, converted without the divisor; PIIX4
   in place of ICH9 (no PIRQ E–H to route); the PIT's tick and the local APIC timers as on q35; the CMOS RTC; COM1 and
   the i8042 present (FADT, DSDT).
6. **Judging the Pi in CI.** The firmware does not run in `raspi4b` past the RNG200, so the GICv2 is judged on
   `virt,gic-version=2`, where only the GICv3 assumption stops the kernel today (H7: the GIC driver chosen by the board's
   description); the BCM2711's own devices (PL011 at the Pi's base, EMMC2, DWC2, GENET, the PCIe root complex) are
   judged in hardware sessions, and `raspi4b` with `usb-kbd` serves DWC2's driver once a path boots there. A model of the
   RNG200 in the Linux host's QEMU build, or a firmware built without it from a pinned EDK II, is the fallback if the
   sessions prove too slow.
7. **Time without an RTC and Wi-Fi.** The Pi has no RTC: the default stays the firmware's time at boot in the boot
   contract plus SNTP in the network service, and H0.2 measures what `GetTime` returns there. Wi-Fi stays release 1.1.
   (Decided otherwise by ADR 0030: the Pi boots without UEFI, so there is no `GetTime`; the Pi 4 takes UTC from SNTP and
   the Pi 5 from its real-time clock behind the firmware.)

## 6. What H1 inherits

- Boots on VMware already reach the root task with one tolerance; the services' image needs the device table (§5.4)
  and a storage driver (H3) before `/data` exists, and the GOP framebuffer before the display service shows anything.
- A boot to the end of a program takes seconds, so VMware sessions are cheap; the serial port to a file is the log.
- The gates stay QEMU's: `-machine pc` without virtio judges H1, `qemu-xhci` with `usb-kbd` H2, `ich9-ahci` and `nvme`
  judge H3, `e1000e` H4.

## 7. H0.3: the author's session on a bought board (pending)

Written for `pftf/RPi4` before ADR 0030; the session now boots H6's image through the Pi firmware's own path
(`config.txt` naming the kernel and the ramdisk), records the device tree the firmware passes, its framebuffer, its
entropy seed and the processor state at entry, and may compare `pftf/RPi4` by hand as below. A Pi 4B comes first, a Pi 5
later.

A Pi 4B (2 GB is enough) with an HDMI monitor, a USB keyboard and a UART adapter on GPIO 14 and 15 at 115,200 baud;
an SD card with a FAT partition holding `pftf/RPi4` v1.53 as released (`RPI_EFI.fd`, the GPU's files, `config.txt`) and
the unchanged ARM64 image (`efi/boot/bootaa64.efi`, the package and the root task). To record:

- the firmware's mode (ACPI by default, Device Tree in its menu) and what the loader prints in each: ACPI tables or the
  device tree, `EFI_RNG_PROTOCOL` (the RNG200 is there), the GOP's mode on HDMI and, once there, on the DPI panel with
  `dtoverlay=waveshare-35dpi-4b`, the memory map;
- where the kernel stops: expected at the GIC (GIC-400, a GICv2), as on `virt,gic-version=2`;
- what the firmware's `GetTime` returns without an RTC, and whether PSCI's `CPU_ON` starts the other cores.
