# ADR 0029: The PC's mechanisms for release 1 — message interrupts, port regions, power and a larger device table

**Status:** Proposed 2026-10-23; awaits the author's decision. Nothing here is implemented until it is accepted.
**Date:** 2026-10-23.
**Scope:** The additions to ABI-1 (version 1.5) and the platform policy that plan steps H1.2 and H2 need, as
[H0](H0-Hardware-Measurement.md) §5 measured and proposed them. H1.1 ([H1.1-PC-Platform.md](H1.1-PC-Platform.md)), the
PC platform without new ABI, is done.

## Context

VMware Workstation, the first target of [ADR 0028](ADR-0028-First-Release-Target.md), boots the unchanged x64 kernel
since H1.1, but its drivers cannot run there yet:

- **No INTx line exists.** Every function's Interrupt Line register reads 0xFF and PIIX4's PIRQ routing registers read
  0x80808080. Every device release 1 drives offers MSI (AHCI) or MSI-X (NVMe, e1000e, vmxnet3, xHCI).
- **The keyboard is on ports.** The i8042 (0x60 and 0x64) is the one release-1 device on port I/O. A port region is
  described in the device table today but never granted.
- **The machine never turns off.** QEMU's exit port does nothing there, so the kernel halts with the machine up.
  VMware's FADT names PM1a_CNT 0x444 and RESET_REG 0xCF9, its DSDT's `_S5` is a static `Package {0, 0}`, and both paths
  worked in H0.
- **The table is too small.** VMware's machine has 46 functions, 33 of them bridges, against 16 descriptors.

On q35 the kernel binds a line once, and four INTx lines serve every virtio function. A keyboard and a network adapter
cannot share a board for that reason (W1.1).

## Decision (proposed)

1. **Message interrupts.** `INTERRUPT_BIND_MESSAGE` (call 86, the device family) takes a request
   `WitMessageBindRequest { Version, Size, Device, Event, Flags }`. The device handle needs `BIND` and the event needs
   `SIGNAL`. The kernel allocates a vector on the boot processor, the processor device lines reach, and writes the
   message's address and data into the request. It returns an interrupt handle with `QUERY`, `DUPLICATE` and `TRANSFER`.
   - **The driver programs its device.** It writes the address and data into its function's MSI capability or an MSI-X
     table entry, through the configuration region and the BAR it already maps. The kernel still programs no device.
   - **Delivery.** Each message sets the event. A message is an edge, so nothing stays masked and `INTERRUPT_ACK` on
     such a binding is accepted and does nothing.
   - **Lifetime and trust.** The binding holds the device and the event, ends with its last handle, and frees the
     vector. With no IOMMU, the device holder remains the trust boundary (K3.2): its device can write any message, as it
     can write any memory.
   - **Where it exists.** Where the board has message interrupts, `QUERY` reports a new feature bit,
     `WIT_ABI_FEATURE_MESSAGE_INTERRUPTS`. On x64 these are the local APIC's, which then stays on with one processor
     too. ARM64's `virt` would take them through the GICv3 ITS in a later step, and riscv64 and the Pi 4 report none.
     Elsewhere the call is `UNSUPPORTED`.
   - **What it unblocks.** VMware's drivers, and q35's virtio functions through MSI-X, so the four-line limit and the
     keyboard and network clash end.
2. **Port regions.** `DEVICE_PORT` (call 87) reads or writes one, two or four bytes at an offset within a port region
   of an acquired device (`BIND`). Its arguments pack the region, the offset, the width and the direction. It is
   validated like every call, costs nothing at a context switch, and is `UNSUPPORTED` where no port region exists
   (ARM64, riscv64). The TSS's I/O bitmap waits for a device whose port traffic a call cannot carry. The i8042 and COM1
   become descriptors of the platform under the firmware bus, from the FADT's `IAPC_BOOT_ARCH` and the DSDT's fixed
   resources read as data, in H2.
3. **Power without AML.**
   - **Soft-off at the end.** The PC platform reads the FADT and the DSDT's `_S5` when it is a static package. When the
     root task ends, `wit_platform_finish` writes QEMU's exit port and then the soft-off, so a machine without the exit
     port turns off.
   - **Reset on request.** A reset is asked for: `SYSTEM_POWER` (call 95) with `OFF` or `RESET`, behind a capability of
     kind `POWER` that the root task alone receives as its fifth initial handle, as `CLOCK_SET`'s clock is its fourth.
     The PC resets through RESET_REG.
   - **Without the means.** A board that lacks the means answers `UNSUPPORTED`, never a pretended success. Anything but
     a static `_S5` gives no soft-off, and the kernel halts with a line that says why.
   - **No ACPICA.** INTx routing would need `_PIC` and `_PRT` evaluated, which message interrupts avoid.
4. **The device table.** The platform describes no PCI bridges, since no driver drives one. `WIT_DEVICE_CAPACITY`
   rises to 64 (four pages), and the table object's size is the header plus `Count` descriptors rounded to pages. A
   reader takes `Count` as before and maps that size instead of one page.
5. **Version.** ABI-1 becomes 1.5: two calls in the device family, one call, one handle kind, one right if `POWER`
   needs its own, and one feature bit. Each is an addition, `ABI-Reference.md` follows the headers, and nothing changes
   meaning.

What H0 §5 also proposed needs no ABI and is recorded by the plan, not here. The PC platform's facts are done in H1.1.
The Pi's GICv2 is judged on `virt,gic-version=2` in H7. Time on the Pi without an RTC is the firmware's time plus SNTP.

## Consequences

- H1.2 implements decisions 1, 3 and 4 with their gates. The q35 virtio drivers move to MSI-X under `runtime-devices`,
  the soft-off is judged on QEMU's `pc` machine and the reset on VMware by hand. H2 implements decision 2 with the PS/2
  keyboard driver.
- The device manager reads a larger table and no bridge records, and its discovery lines change accordingly.
- **Rejected:** ACPICA in a user-space service for release 1. It brings AML evaluation and a large pinned dependency for
  the two values a static package already gives. It stays the path for an ordinary PC beyond VMware (M8–M10), where
  `_PRT` and devices without MSI appear.
- **Rejected:** the TSS's I/O permission bitmap for port access. It is rewritten at every switch to a process with a
  different bitmap, for a keyboard that moves a byte per key.
