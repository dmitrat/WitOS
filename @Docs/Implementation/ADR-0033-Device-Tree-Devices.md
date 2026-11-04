# ADR 0033: Devices of the device tree

**Status:** Proposed 2026-11-04, for the author's decision.
**Date:** 2026-11-04.
**Scope:** How the devices a board's device tree names, rather than a PCI bus, reach the device table and the device
manager. It extends [ADR 0029](ADR-0029-PC-Interrupts-Power-and-Device-Table.md)'s device table. Plan step H8, the
Raspberry Pi 4's devices in the services, needs it first, and H9, the Pi 5, after it.

## Context

The device table is the platform's enumeration, published by the kernel unchanged (RFC 0011 §7.7). The kernel never reads
a descriptor's meaning. The device manager maps a descriptor to a driver, and the kernel mints the capabilities a driver
holds: memory windows, interrupt bindings, DMA pins.

Today the table holds three kinds of device:
- PCI functions behind the PC's ECAM window or the tree's generic ECAM bridge;
- the firmware's framebuffer (C2.1);
- the PC's i8042 (ADR 0031).

On QEMU's virt that covers everything, since its virtio devices are PCI functions. A Raspberry Pi's devices are nodes of
its device tree instead.

The Pi 4's firmware tree (`bcm2711-rpi-4-b.dtb`, pinned since H7.2) holds 27 enabled nodes with a `reg` and a
`compatible`. Among them are the devices of H8:

| Device | Node | Compatible | In QEMU's `raspi4b` |
| --- | --- | --- | --- |
| SD card controller | `/emmc2bus/mmc@7e340000` | `brcm,bcm2711-emmc2` | yes: a `generic-sdhci` (SD 3.0) that `-drive if=sd` attaches the card to |
| USB OTG (the USB-C port, PocketTerm35's keyboard) | `/soc/usb@7e980000` | `brcm,bcm2708-usb` | yes: `dwc2-usb`; the node is `disabled` until the firmware's `dtoverlay=dwc2` |
| Ethernet | `/scb/ethernet@7d580000` | `brcm,bcm2711-genet-v5` | no |
| PCIe root complex (VL805 xHCI behind it) | `/scb/pcie@7d500000` | `brcm,bcm2711-pcie` | no |

The kernel itself drives some of the 27 nodes and must keep them: the GIC, the console's UART, the watchdog its power
uses (H7.3), the mailbox its framebuffer request uses (H7.3), the local interrupt controller and the timer. The rest
belong to user space.

A driver needs more than regions and lines. The SD controller needs its base clock, and `bus-width` and
`non-removable` say how the card is wired. DWC2 needs its `dr_mode`. GENET needs `phy-mode` and its MDIO child. A
node's `dma-ranges` say how its bus sees memory, and on the Pi 4 the low 1 GiB the SD controller reaches by DMA is not
at the processor's addresses. These are properties of the tree, and their meaning is a driver's, not the kernel's.

## Decision (proposed)

1. **A bus of the tree.** The ARM platform appends a descriptor of the new bus `WIT_DEVICE_BUS_TREE` for each device node:
   - **Which nodes.** A node whose `status` is `okay` (or absent), that has `reg` and `compatible`, and whose parents up
     to the root are buses, that is nodes with `ranges` (`simple-bus`, `/soc`, `/scb`, `/emmc2bus`).
   - **Which nodes not.** The nodes the kernel drives itself, and the generic ECAM bridge, whose functions the PCI bus
     describes already.
   - **Regions.** Each `reg` entry translated through the parents' `ranges` to the processor's address, as the tree's
     reader translates them today.
   - **Lines.** Each `interrupts` entry of a GIC parent: an SPI's INTID, level or edge by its flags (`WIT_DEVICE_LINE_EDGE`,
     ABI-1 1.7).
   - **The table holds 64 descriptors.** The Pi 4's tree needs 27 less the kernel's.
   - The platform reads no compatible string beyond deciding which nodes are the kernel's own, which it found by
     compatible already (H7.1–H7.3).
2. **The tree beside the table.** The device table object carries the device tree blob after its descriptors, read-only,
   under a new header version: `TreeOffset`, `TreeBytes`.
   - A tree descriptor's `Identity[0]` is its node's offset in the blob's structure block.
   - The device manager reads the node's `compatible` there to choose a driver. The driver reads the node's other
     properties (`bus-width`, `dr_mode`, `dma-ranges`, clocks) from the same pages. `OutWit.OS` gains a managed reader
     of the format.
   - The kernel keeps parsing only what it parses today: memory, its own devices, `reg`, `ranges` and `interrupts`.
   - This is additive: ABI-1 1.8, a feature bit for tree devices, and the table's header version 2, version 1 still
     served.
3. **Disabled nodes stay disabled.** A board's firmware enables optional devices: the Pi's `config.txt` overlays turn
   on DWC2 with `dr_mode = "host"` (`dtoverlay=dwc2,dr_mode=host`), as PocketTerm35's keyboard needs.
   - The SD card's image carries that `config.txt` (H8, `image --rpi4`).
   - For `raspi4b`, the runner writes the overlay's two properties into the tree it boots, as it writes the entropy
     seed (H7.2).
4. **What the device manager gets.** It matches a tree device by its first compatible string it knows: `brcm,bcm2711-emmc2`
   and SDHCI's generic strings to an SDHCI driver over `witos/block.h`, `brcm,bcm2708-usb` and `snps,dwc2` to a DWC2
   driver over H2's USB core, `brcm,bcm2711-genet-v5` to a GENET driver over `witos/packet.h`.
   - Clocks the firmware owns, such as the SD controller's base clock, are not the driver's to set. The driver reads
     what the controller reports (SDHCI's capabilities register) or what the node names (`clock-frequency`).
   - A firmware mailbox service for user space comes only when a device needs one that neither gives.

## Rejected

- **A hash of the compatible string in `Identity`, no tree in user space.** The device manager could choose a driver,
  but every driver needs other properties of its node: `bus-width`, `dr_mode`, `dma-ranges`. They would come through
  more descriptor fields, one per need, or not at all.
- **The tree alone, and `DEVICE_ACQUIRE` by node offset.** The table would stay PCI's, and the kernel would parse a
  node's regions and lines again at every acquisition: a second enumeration path, and the descriptor would no longer
  be the one record of a device.
- **A kernel table of known compatible strings.** The kernel would learn device classes, against RFC 0011 §7.7 and the
  kernel rules.

## Consequences

- H8 becomes a sequence of slices, each judged on `raspi4b` where QEMU emulates the device:
  - H8.1: tree devices, ABI-1 1.8 and the managed reader;
  - H8.2: the SDHCI driver and the SD card as a disk of the namespace service;
  - H8.3: DWC2 with H2's USB core and `usb-kbd`;
  - H8.4: `image --rpi4`.
  - GENET and the VL805 behind the Broadcom root complex are judged on the board alone.
- The Pi 5 (H9) reaches RP1's devices through PCIe. RP1's own children are nodes of the firmware's tree below the PCIe
  node, and this bus describes them once the root complex is up.
- The device manager's table of drivers gains compatible strings beside PCI identities.
