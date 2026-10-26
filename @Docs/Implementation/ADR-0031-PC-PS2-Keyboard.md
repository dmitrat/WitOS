# ADR 0031: The PC's PS/2 keyboard, and port I/O on a device's port region

**Status:** Accepted 2026-10-26 by the author's decision.
**Date:** 2026-10-26.
**Scope:** The PC's i8042 keyboard controller beside the USB keyboard, the one ABI-1 call it needs (version 1.7) and the
platform's description of the controller. It amends [ADR 0029](ADR-0029-PC-Interrupts-Power-and-Device-Table.md),
decision 3 ("no port access in release 1") and its rejected PS/2 driver. Plan step H5.2 implements it.

## Context

ADR 0029 made the keyboard of every target USB: VMware presents a USB keyboard on its xHCI, the PocketTerm35's RP2040
is a USB HID device, and a Pi's other ports are USB. Port access left release 1 with the i8042, the one release-1 device
a port call would serve.

H5 measured where VMware's keys go ([H5-VMware-Session.md](H5-VMware-Session.md) §1.4):

- Keys sent through VMware's VNC server reach its PS/2 keyboard, whatever the guest does with either keyboard.
- On 2026-10-25 the author typed on the Workstation console, the VM grabbed. A scratch kernel read every key from the
  8042 as a scan code of set 1. The xHCI driver had configured VMware's USB keyboard (0e0f:0006) and polled its
  interrupt endpoint in the same boot, and logged none.

So with `keyboard.vusb.enable = "TRUE"` VMware still gives the guest its keys through the 8042, and WitOS cannot be used
on VMware without it. The author decided on 2026-10-26 to bring WitOS on VMware to a state the author runs it in,
through a PS/2 keyboard by this ADR. Laptops keep their built-in keyboards on the i8042 too, as ADR 0029 noted.

## Decision

1. **A PS/2 keyboard on the PC, beside the USB one.** The USB keyboard stays the keyboard of every target. On a PC whose
   firmware names an i8042, a driver service serves its keyboard on the input protocol (`witos/input.h`), as the xHCI
   and virtio input drivers do. The display service takes up to two keyboards' channels and merges their keys. A PC with
   a PS/2 keyboard, a USB keyboard or both types into the same console.

2. **The platform describes the controller.** The PC platform appends a descriptor of the new bus
   `WIT_DEVICE_BUS_LEGACY`: a device of the board's legacy resources that its firmware tables name.
   - **When.** The FADT's `IAPC_BOOT_ARCH` names an 8042, or the FADT predates that field (revision 1 or 2, QEMU's `pc`).
   - **What.** `Identity[0]` = `WIT_LEGACY_DEVICE_I8042`; region 0 is port 0x60 (data), region 1 port 0x64 (status and
     command), each of one byte; line 0 is the keyboard's IRQ 1 and line 1 the auxiliary device's IRQ 12, both of the
     new kind `WIT_DEVICE_LINE_EDGE`.
   - The platform reads nothing of the controller. Whether it answers is the driver's to find (a status of 0xFF is no
     controller).

3. **Port I/O: one call on a held device's port region.** `DEVICE_PORT` (call 87, the device family) takes a request
   `WitDevicePortRequest { Version, Size, Region, Operation, Width, Offset, Value }` and returns the byte, word or double
   word read, or zero for a write.
   - **Authority.** The device handle needs `BIND`, held by the caller as `DEVICE_MEMORY`'s is.
   - **Validation.** The whole request is checked before the port is touched: the version and size, a region of the
     descriptor whose flags are `WIT_DEVICE_REGION_PORT`, an operation of `READ` or `WRITE`, a width of 1, 2 or 4 aligned
     in its offset, the access within the region, a written value that fits the width, and zero reserved words.
   - **Where.** On x64 alone; ARM64 and riscv64 have no port space, and the call is `UNSUPPORTED` there. `QUERY` reports
     `WIT_ABI_FEATURE_DEVICE_PORTS` where the architecture serves it.
   - **Why a call, not the TSS's I/O permission bitmap.** A keyboard moves a byte or two an interrupt, so a call costs
     nothing that matters. The bitmap would be rewritten at every switch to the driver's process, for every processor's
     task state. It waits for a device whose port traffic a call cannot carry, as H0 proposed.

4. **Version.** ABI-1 1.7: a call, a bus, a device kind, a line kind and a feature bit, each an addition.
   `ABI-Reference.md` follows the headers.

## Consequences

- H5.2 implements the call (kernel and libwitos), the platform's descriptor, the driver
  (`OutWit.OS.Hardware.I8042`, `/system/Drivers/I8042`: the controller left as the firmware set it, translation to scan
  code set 1 kept, the keyboard's interrupt enabled, set 1's codes translated to Linux's key codes), and the display
  service's second keyboard.
- The driver is judged in QEMU, whose q35 and `pc` machines carry an i8042 with a PS/2 keyboard, and by hand on VMware.
- The mouse of IRQ 12 stays undriven: the console has no pointer.
- **Rejected:** VMware settings in place of a driver. The settings tried (`keyboard.vusb.enable`,
  `keyboard.allowBothIRQs`) did not move the keys, and each further trial needs a person at the console. A driver also
  serves laptops.
- **Rejected:** a kernel driver for the 8042. The kernel programs no device, and the device's bytes are the driver's.
