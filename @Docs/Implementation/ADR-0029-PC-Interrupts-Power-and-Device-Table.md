# ADR 0029: Message interrupts, power at the root task's end and a larger device table

**Status:** Accepted 2026-10-23 by the author's decision. It was proposed the same day with a port call and a power
call; the author chose a USB keyboard on every target, which took the port call out of release 1, and power through
the root task's end, which needs no call.
**Date:** 2026-10-23.
**Scope:** The additions to ABI-1 (version 1.5) and the platform policy that plan step H1.2 implements, as
[H0](H0-Hardware-Measurement.md) §5 measured and proposed them. H1.1 ([H1.1-PC-Platform.md](H1.1-PC-Platform.md)), the
PC platform without new ABI, is done. The boot paths of the Raspberry Pi boards are [ADR 0030](ADR-0030-Boot-Paths-and-the-Pi-5-Target.md).

## Context

VMware Workstation, the first target of [ADR 0028](ADR-0028-First-Release-Target.md), boots the unchanged x64 kernel
since H1.1, but its drivers cannot run there yet:

- **No INTx line exists.** Every function's Interrupt Line register reads 0xFF and PIIX4's PIRQ routing registers read
  0x80808080. Every device release 1 drives offers MSI (AHCI) or MSI-X (NVMe, e1000e, vmxnet3, xHCI).
- **The machine never turns off.** QEMU's exit port does nothing there, so the kernel halts with the machine up, and
  nothing resets it. VMware's FADT names PM1a_CNT 0x444 and RESET_REG 0xCF9, its DSDT's `_S5` is a static
  `Package {0, 0}`, and both paths worked in H0.
- **The table is too small.** VMware's machine has 46 functions, 33 of them bridges, against 16 descriptors. Without
  the bridges 13 remain; with the screen that is 14 of 16, and an ordinary PC adds USB controllers, audio and SMBus.

On q35 the kernel binds a line once and four INTx lines serve every virtio function, so a keyboard and a network
adapter cannot share a board (W1.1). The Raspberry Pi 5, the final board of phase H ([ADR 0030](ADR-0030-Boot-Paths-and-the-Pi-5-Target.md)),
has its peripherals in the RP1 chip behind PCIe, whose interrupts reach the GIC as MSI-X writes to the BCM2712's MIP
block: the ARM64 side needs message interrupts too.

The keyboard is USB on every target (the author's decision of 2026-10-23): VMware presents a USB keyboard on its xHCI
(H0's machine had one), the PocketTerm35's RP2040 is a USB HID device, and a Pi's other ports are USB. The i8042 on
ports 0x60 and 0x64, the one release-1 device a port call would have served, is not needed.

## Decision

1. **Message interrupts.** `INTERRUPT_BIND_MESSAGE` (call 86, the device family) takes a request
   `WitMessageBindRequest { Version, Size, Device, Event, Flags, Address, Data }` and returns an interrupt handle with
   `QUERY`, `DUPLICATE` and `TRANSFER`.
   - **Authority.** The device handle needs `BIND`, the event `SIGNAL`, as `INTERRUPT_BIND` asks; the request is checked
     whole before anything is allocated.
   - **What the kernel does.** The platform allocates a message target and the kernel writes its address and data into
     the request's `Address` and `Data`. The driver writes them into its function's MSI capability or an MSI-X table
     entry, through the configuration region and the BAR it already maps: the kernel still programs no device.
   - **Delivery.** Each message sets the event. A message is an edge, so nothing stays masked, and `INTERRUPT_ACK` on
     such a binding is accepted and does nothing; a driver reads its device's state after each wait, as it does now.
   - **Lifetime and trust.** The binding holds the device and the event, ends with its last handle and frees its
     target. With no IOMMU the device holder stays the trust boundary (K3.2): its device can write any message, as it
     can write any memory.
   - **One ABI, served where the board can.** `QUERY` reports `WIT_ABI_FEATURE_MESSAGE_INTERRUPTS` on a board that
     serves the call, and the call is `UNSUPPORTED` elsewhere. On x64 (H1.2) the targets are vectors of the boot
     processor's local APIC, which stays on with one processor too, and the end of an interrupt goes to the APIC; the
     Pi 5's platform serves the call through the MIP (H9). `virt`'s GICv3 ITS and riscv64's IMSIC wait for a step that
     needs them, and the Pi 4's devices keep their SPIs.
   - **What it unblocks.** VMware's drivers, and q35's virtio functions through MSI-X, which ends the four-line limit
     and the keyboard and network clash on the PC boards.
2. **Power at the root task's end.** No call is added: the root task already stops the services and ends, and the
   kernel already acts on its exit status (RFC 0011 §5: a call enters only when no composition provides the mechanism).
   - **Status 0.** The board's test exit (QEMU's exit port, semihosting, the test finisher), then the board's power
     off: ACPI's soft-off on the PC (SLP_TYP of `_S5` with SLP_EN to PM1a_CNT and PM1b_CNT, after `ACPI_ENABLE` to
     SMI_CMD when the firmware left SCI_EN clear), PSCI's `SYSTEM_OFF` on ARM64, SBI's system reset extension on riscv64.
   - **`WIT_ROOT_EXIT_RESET`,** a constant of `witos/root.h` outside every status a program's exit yields: the board's
     reset, through the FADT's RESET_REG on the PC, PSCI's `SYSTEM_RESET`, SBI's reset.
   - **Any other status, and a panic.** The machine halts with its line on the console and on the screen, so that a
     person at the hardware reads what happened.
   - **Without the means.** A PC whose `_S5` is not a static package, or whose FADT has no RESET_REG, halts with a line
     that says so; no AML is interpreted and the kernel never pretends success.
   - **Who asks.** A process that asks the system to stop or restart reaches the root task through the system layer
     (a request of the process manager's protocol, a `shutdown` program for PowerShell's `Stop-Computer` and
     `Restart-Computer`); that is the system layer's step, outside this ADR.
3. **No port access in release 1.** The i8042 and the port-region capability H0 proposed wait for a PC beyond VMware,
   whose laptops keep internal keyboards on the i8042; the call and the TSS's I/O permission bitmap are weighed then.
4. **The device table.** The platform describes no PCI bridges, since no driver drives one and every function keeps its
   bus address; `WIT_DEVICE_CAPACITY` rises to 64 (four pages), and the table object's size is the header plus `Count`
   descriptors rounded to pages. A reader takes `Count` as before and maps that size instead of one page.
5. **Version.** Each slice of H1.2 that lands raises ABI-1's minor: 1.5 with H1.2a (the table's capacity and the root
   task's exit constant), 1.6 with H1.2b (the call in the device family and its feature bit). Each is an addition,
   `ABI-Reference.md` follows the headers, and nothing changes meaning.

The other decisions of H0 §5 need no ABI: the PC platform's facts are done in H1.1, the GICv2 is judged on
`virt,gic-version=2` and `raspi4b` (H7), and time on a board without a real-time clock is the plan's (H7, H9).

## Consequences

- H1.2 implements decisions 1, 2 and 4: the q35 virtio drivers move to MSI-X under `runtime-devices`; the power off and
  the reset are judged on QEMU's `pc`, `virt` and riscv64 boards (the runner learns an outcome for a machine that turned
  itself off) and by hand on VMware; the device manager reads the larger table without bridge records.
- The keyboard of every target is USB (H2: the USB core over xHCI, before storage and the network), and phase H's
  steps are renumbered by [ADR 0030](ADR-0030-Boot-Paths-and-the-Pi-5-Target.md).
- **Rejected:** ACPICA in a user-space service for release 1. It brings AML evaluation and a large pinned dependency for
  the two values a static package already gives; it stays the path for an ordinary PC beyond VMware (M8–M10), where
  `_PRT` and devices without MSI appear.
- **Rejected:** a power call behind a capability of the root task's. It would add a call, a handle kind and a right for
  what the root task's end already carries, and a machine is only turned off once everything has stopped.
- **Rejected for release 1:** a PS/2 keyboard driver. Every target needs the USB stack anyway, and a second keyboard
  path would delay nothing but add a mechanism.
