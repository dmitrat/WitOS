# ADR 0028: The first release — a console on real hardware, VMware first, a Raspberry Pi 4 handheld as the target

**Status:** Accepted 2026-10-21 by the author's decision.
**Date:** 2026-10-21.
**Scope:** What the first conditional release of WitOS is, which hardware it is judged on and in which order, and what
that asks of the plan (phase H).

## Context

Everything WitOS runs on today is QEMU: the q35 chipset on x64, the `virt` boards on ARM64 and riscv64, virtio devices
for the disk, the console, the keyboard and the network, QEMU's `ramfb` for the screen. Every gate is a QEMU boot in
virtual time. The audit of 2026-10-20 ([Program-Audit-2026-10-20.md](Program-Audit-2026-10-20.md)) found the program on
its line and the console usable by hand, and the plan carried "real hardware" as a horizon: the author's decision of
2026-10-17 named VMware as the waypoint, an ordinary PC without virtio, and left the hardware steps to be detailed when
the track opened.

On 2026-10-21 the author fixed the first conditional release: *working in console mode, fully, on the Waveshare
PocketTerm35* ([waveshare.com/pocketterm35](https://www.waveshare.com/pocketterm35.htm)), as an orientation rather than
a promise to install WitOS on that very device; and added that VMware stays and comes first: the author wants to see
WitOS on VMware before anything else.

What the PocketTerm35 is, as measured from its vendor pages and the community's notes on 2026-10-21 (to be confirmed on
the device, plan step H0):

- A handheld case around a **Raspberry Pi 4B (2 GB)** or **Pi 5 (1 GB)** with a 5,000 mAh battery and a UPS board; no
  processor of its own. The Pi's Ethernet and USB-A ports are reachable through the case; power is USB-C.
- A **3.5-inch 640×480 IPS panel** driven over the Pi's **DPI** interface on the 40-pin header
  (`dtoverlay=waveshare-35dpi-4b` or `-5b` in `config.txt`), with a Goodix GT911 capacitive touch panel over I²C.
- A **67-key QWERTY keyboard** with a D-pad and game buttons, scanned by an **RP2040** running CircuitPython and
  presented to the Pi as a **USB HID keyboard** over the Pi's USB-C OTG port in host mode (`dtoverlay=dwc2,dr_mode=host`),
  that is on the SoC's DWC2 controller, not on the VL805 xHCI behind PCIe that serves the USB-A ports.
- Storage on a microSD card; a speaker and a 3.5 mm jack; the RP2040 also controls brightness and volume.

What the firmware side looks like: the Raspberry Pi 4 has a maintained UEFI firmware (EDK II, `pftf/RPi4`) that boots
`efi/boot/bootaa64.efi` from an MBR FAT partition, defaults to ACPI tables with a Device Tree mode in its menu, includes
TF-A, and whose GOP comes from the VideoCore's mailbox framebuffer, which the GPU firmware scans out on the display
`config.txt` configured. The Pi 5's UEFI is a community effort whose upstream is archived and whose forks disagree on
the current board revision. QEMU's `raspi4b` machine (the pinned QEMU 11.1) emulates the GIC-400, the SD host, the DWC2
USB host, the framebuffer and the PL011, and has no PCIe root, no GENET Ethernet and no xHCI.

VMware Workstation presents an ordinary x86-64 PC with EFI firmware: an Intel chipset of the i440BX line with legacy PCI
configuration, ACPI tables, a PS/2 keyboard, a 16550 serial port, SATA (AHCI) or NVMe storage, e1000e or vmxnet3
network adapters, USB controllers and an SVGA adapter with a GOP mode. Nothing of it is virtio.

## Decision

1. **The first conditional release is a console on real hardware.** Its definition: WitOS boots from the device's own
   storage through UEFI, shows its console on the device's screen, takes the device's keyboard, keeps `/data` on the
   device's storage, reaches the network through the device's wired adapter with TLS, keeps time, and runs PowerShell,
   the base applications and the .NET compatibility suite as they run in QEMU today; shutdown and reboot work. The
   orientation device is the PocketTerm35 on a Raspberry Pi 4B. Wi-Fi, Bluetooth, touch, audio, the battery gauge and
   the Pi 5 are not in the first release (section Consequences names where they go).
2. **VMware comes first.** The hardware track opens with an x86-64 PC as VMware Workstation presents it, judged first
   by QEMU's equivalents under CI and then by the author's hands on VMware; the Raspberry Pi 4 follows on the same
   services and the same contracts. The author wants to see WitOS on VMware before the handheld.
3. **The architecture does not bend for hardware.** A board is a `Kernel.Platform.<board>` (and, for x86-64 PCs, the
   platform's own PCI configuration, HPET and interrupt routing); a device is a driver in a process of its own over
   libwitos and `OutWit.OS`, serving the protocols that exist (`witos/block.h`, `witos/input.h`, `witos/packet.h`,
   `witos/serial.h`), so that the namespace, display, terminal and network services do not change; the kernel learns
   no device class. What the kernel gains is a mechanism with a consumer, additive to ABI-1: message-signalled
   interrupts and a line shared by several bindings (the audit's open question, now a need), and on x86-64 a port
   capability for the drivers that need I/O ports (PS/2, the 16550), each by an ADR when its slice comes.
4. **Measure before the first line (H0), as V0 did.** The facts above are the vendor's and the community's; the plan's
   first hardware step boots the current images on VMware and on a Pi 4 with the UEFI firmware, records what the
   loader and the kernel see and where they stop, settles whether `pftf/RPi4` boots in QEMU's `raspi4b` and whether the
   DPI panel shows the firmware's framebuffer, and writes the decisions with the measurements.
5. **No AML interpreter in the kernel; preferably none at all for the first release.** On the PC the drivers' devices
   interrupt through MSI/MSI-X, which needs no `_PRT`; the fixed ISA lines (the PS/2 keyboard, the serial port) are the
   platform's. If a device of the release turns out to need the ACPI namespace, ACPICA is pinned open code in the system
   layer, as the plan said, never in the kernel.

## Consequences

- The plan gains phase **H** ("Железо: первый релиз") with the slices H0 (measurement), H1–H6 (the PC: platform, ports,
  AHCI and NVMe, USB with HID, e1000e, the VMware session), H7–H9 (the Pi 4: platform, its devices, the SD image and
  the release checklist), and the order H0 → H1–H6 → W3.1 → H7–H9: TLS stays before the handheld, since the release's
  network means PowerShell Gallery, NuGet and the installer's HTTPS sources, and it comes after VMware by the author's
  wish.
- The release is judged twice: every device the plan can emulate is gated in QEMU under CI (AHCI, NVMe, xHCI with a USB
  keyboard, e1000e, SDHCI, DWC2 with a USB keyboard, GICv2), and the hardware itself is judged by the author's sessions
  recorded in the step's document, as C2.4 was. A hardware fact a gate cannot see (the DPI panel, GENET, the VL805
  behind the Pi's PCIe) is recorded with the session that measured it.
- The USB stack is the one new subsystem the release cannot avoid: a USB core (enumeration, hubs, HID report parsing)
  in a service, over two host controller drivers (xHCI for PCs and the Pi's USB-A ports, DWC2 for the Pi's USB-C port,
  where the handheld's keyboard lives). It is also the first driver whose device has children, and the device manager's
  policy grows a bus below a controller.
- After the first release: Wi-Fi (the Pi's CYW43455 over SDIO with its firmware blob, 802.11 and WPA2 in the network
  service) as release 1.1, since a handheld wants it and the first release has the wired port; the Pi 5 when its UEFI
  firmware settles; touch, audio and the battery gauge as applications of the device's I²C and the RP2040; a generic PC
  beyond VMware (the ACPI namespace, an IOMMU) on the way to M8–M10.
- The time axis: the audit of 2026-10-20 estimated the network to TLS at one to two weeks, the PC platform at two to
  three, and the Pi 4 with USB at one to two months, at the pace of the virtual-hardware phases; hardware sessions have
  no QMP, no deterministic clock and no judge in CI, so the estimate is a range, not a date.

## Alternatives considered

- **The Pi 5 first** (the newer board, more memory per price): its UEFI firmware is unmaintained upstream and its
  peripherals sit behind the RP1 southbridge over PCIe, which no emulator has; rejected for the first release, kept as
  a follow-up.
- **A generic x86-64 PC before VMware:** needs USB for the keyboard, the ACPI namespace for interrupt routing and an
  IOMMU before anything shows; VMware gives a PC with PS/2, a serial port and MSI-capable devices, so the console
  appears after far fewer steps. Rejected as the first target, kept as the direction.
- **An x86 handheld** instead of the Pi: none with a keyboard and a screen of the PocketTerm35's class ships with UEFI
  documentation the plan could measure against; the Pi 4's firmware and community make it the measurable choice.
- **Wi-Fi in the first release:** a wireless stack with WPA2 is the size of the TCP/IP stack again; the handheld's
  wired port is reachable, so Wi-Fi follows the release rather than gating it.

## Related

- [ADR 0024](ADR-0024-Three-Layers-and-Unix-Form-Runtime.md) (the layers), [ADR 0025](ADR-0025-Devices-as-NativeAOT-Services.md)
  (devices as services), [ADR 0027](ADR-0027-Base-Applications.md) (the applications of a working console).
- [RFC 0007](../RFC-0007-Universal-Hardware-Interface.md) (the UHI), [RFC 0011 v3](../RFC-0011-Kernel-Architecture-and-ABI.md)
  §7.7 (devices, interrupts and DMA as mechanisms), [RFC 0020](../WitOS-RFC0020-Terminal-Console-and-PowerShell-Architecture.md)
  (the console).
- [Program-Audit-2026-10-20.md](Program-Audit-2026-10-20.md) §8 (one binding per line; what the gates do not measure).
- The plan: `PLAN.md` §3 and §5, phase H.
