# E3 — The USB drive

Plan step E3 (phase E, goal Ц6; the author's decision of 2026-10-28: a USB drive is needed anyway, on the boards too,
and is done now): a USB flash drive plugged into the xHCI controller is served as a disk, its volumes mount below
`/media` as volumes of the catalog with the removable origin (ADR 0032), and a drive taken out leaves the catalog and a
drive put back mounts again. The pieces are the USB mass storage class over the USB core of H2, the xHCI driver as a
disk driver beside its keyboards, the device manager forwarding the drive to the namespace service, and the namespace
service taking drives that come and go after its start.

## 1. Mass storage

`OutWit.OS.Hardware.Usb.UsbMassStorage` (host-tested):

- **Bulk-Only Transport** (USB MSC BOT 1.0, `BulkOnlyTransport`): each SCSI command as a 31-byte command block wrapper
  (`USBC`, a tag of its own, the data length, the direction, the logical unit), its data phase, and a 13-byte status
  wrapper checked by its signature, its tag and its length. A data phase the device stalls is cleared and the status
  read; a stalled status is cleared and read once more; a phase error, a wrapper of another tag or a transfer that fails
  is answered by the reset recovery of BOT 5.3.4 — the Bulk-Only Mass Storage Reset, then both endpoints' halts
  cleared — before the command fails with an `IOException`.
- **The SCSI disk** (`ScsiDisk : IBlockDevice`): INQUIRY (a direct-access device, its vendor, product, revision and
  removable bit), TEST UNIT READY until a unit attention passes (a medium absent fails at once, a unit not ready is asked
  again after 100 ms, at most 20 times), READ CAPACITY (10), and (16) when (10) answers its maximum, MODE SENSE (6)'s
  write protect (a unit that refuses it is taken as writable, as Linux's sd takes it); then READ and WRITE (10), or
  (16) beyond 2^32 blocks or 65535 blocks a command, straight from and to the client's physical segments, and
  SYNCHRONIZE CACHE, whose illegal request (a drive without a cache) passes. A failed command's REQUEST SENSE names it in
  the error: the key, the additional sense code and qualifier, and the key's meaning.
- **The pipes** (`IBulkOnlyPipes`, `UsbBulkPipes` over `IUsbHost`): the command, status and small data blocks through the
  bulk endpoints' own buffers, the data phase through the segments; a halt cleared on the controller's side and then at
  the device by CLEAR_FEATURE(ENDPOINT_HALT); the reset as the interface's class request.

## 2. The USB core and the controller

- `UsbBus` recognises a drive — an interface of class 08 (mass storage), subclass 06 (SCSI's transparent command set),
  protocol 50 (Bulk-Only) with a bulk IN and a bulk OUT endpoint — before HID; it configures both endpoints, sets the
  configuration, asks Get Max LUN (a STALL is one unit, BOT 3.2) and announces the drive (`TakeStorage`: arrived or
  left, with its slot, interface, endpoints and their Device Context Indexes). A device that goes announces its drive's
  departure before its slot is disabled.
- `IUsbHost` gains `ConfigureBulk`, `Bulk` and `BulkBuffer`. `XhciController` configures the pair in one Configure
  Endpoint command (types 6 and 2, an average TRB of 3 KiB, no ESIT payload), and a bulk transfer is one Transfer
  Descriptor of chained Normal TRBs, cut at 64 KiB boundaries (xHCI 1.2 §4.11.7.1), each interrupting on a short
  packet and the last on completion, with TD Size counting the packets left; the transfer is waited for by its own
  events, taken out of the order the core reads (`TakeTransfer`), an event of an earlier descriptor's last TRB after its
  short packet dropped. A STALL resets the endpoint on the controller's side (Reset Endpoint, Set TR Dequeue Pointer)
  and answers `Stalled`; another error or a timeout of 30 s stops and skips the descriptor and answers `Failed`.

## 3. The driver, the device manager and the namespace

- **The xHCI driver** (`src/Services/Drivers/Xhci`) opens a drive the bus announces as a `ScsiDisk` (16 segments of
  64 KiB, 64 KiB a command, so that a descriptor's TRBs fit a ring), serves it with OutWit.OS's `BlockServer` on a block
  channel of its own, and sends the channel's other end to the device manager in a disk message with
  `WIT_NAMESPACE_DISK_REMOVABLE` (and `READ_ONLY` for a write-protected medium), named
  `<controller>/usb-<port>`. A drive that goes is let go and announced with `WIT_NAMESPACE_DISK_GONE` and no handle.
  One drive at a time: the driver's wait takes the device manager's channel, the controller's interrupt, the input
  channel and one block channel, OBJECT_WAIT's four handles; a second drive is logged and left.
- **The device manager** waits beside the root task's channel on up to three xHCI drivers' channels and forwards each
  announcement to the namespace service as it is (`[DEVICES] drive … came: to the namespace service`). A stop request
  now skips an announcement queued before the service's answer (`ServiceStop.Request`).
- **The namespace service** takes a removable disk after its start: the block channel attached through its own window,
  each volume a format it reads mounted below `/media` with `ORIGIN_REMOVABLE` and never at a system's place
  (`VolumeMounter.MountAsync(removable: true)`), its catalog volumes kept for the departure. A drive that goes leaves the
  catalog at once, so that no lookup reaches it, and what held it closes; a file of it still open fails with `EIO`,
  since its disk is gone. At the stop the drives go before the disks' volumes.

## 4. The gate

`runtime-usb-drive` (`RuntimeUsbDrive`, after `runtime-smb` in `runtime-witos`): QEMU's `usb-storage` on port 2 of a
`qemu-xhci` controller in the USB keyboard's slot (`BootRequest.UsbDrive`), over a 64 MiB image with an MBR whose one
partition of type 0x0C holds a FAT32 volume labelled `STICK` with a text, as a block node of its own that outlives the
device (`-blockdev`), so that QMP's `device_del` takes the drive out and `device_add` puts it back. A USB controller is a
keyboard's to the device manager, which then gives a board with a framebuffer its display as the console, so the boot
has a terminal whose transcript holds `/bin/init`'s lines. `tests/Runtime.Witos/Drive`, an ordinary program published
without a RID, waits for `/media/STICK`, checks its mtab line (`STICK /media/STICK vfat rw`), reads the text, reads its
space, writes 200,000 bytes and reads them back, prints `[DRIVE] take the drive out` — the runner's `device_del` — and
waits for the drive to leave the tree and `/etc/mtab`, prints `[DRIVE] put the drive back` — `device_add` — and waits
for it to mount again with the file it wrote: `8 of 8 checks passed`. The scenario requires the driver's lines (the
drive, its disk served, its departure), the namespace's mount and departure lines, the checks and the program's exit,
and after the boot the host reads the written bytes out of the drive's image. `runtime-display` keeps its USB keyboard,
hub and hotplug through the same driver.

The host tests: `UsbMassStorageTests` against a drive the test simulates as QEMU's usb-storage answers (the opening with
a unit attention, segments read and written, READ CAPACITY and READ (16) beyond 2 TiB, write protection, a medium error
by its sense, a missing medium, the recoveries of BOT 6.7, the command wrapper's layout), and `UsbBusTests`' drive
configured and announced when it comes and goes.

## 5. What ran

On 2026-10-28 in the Linux container of `build/linux/Dockerfile`; CI's `linux-host` legs run every scenario again in
their `runtime-witos`:

| ISA | `runtime-services` | `runtime-usb-drive` | `runtime-display` |
| --- | --- | --- | --- |
| x64 | nine boots passed | passed in 32 s, 8 of 8 | passed |
| ARM64 | eight boots passed | passed in 33 s, 8 of 8 | passed |
| riscv64 | nine boots passed | passed in 35 s, 8 of 8 | passed |

QEMU attaches the drive as a SuperSpeed device on the controller's root port 2, the second of its USB 3 ports; the
controller interrupts through an MSI-X message on x64 and its INTx line on the virt boards. The ARM64 boot's lines:

```text
[XHCI] port 2: device 46f4:0001, USB 3.0, SuperSpeed, slot 1, packets of 512 bytes on its control endpoint, configuration 1 with interface 0 class 08/06/50
[XHCI] port 2: a drive, interface 0, bulk endpoints 81 and 02 of 1024 bytes, 1 logical unit
[XHCI] port 2: drive "QEMU QEMU HARDDISK 2.5+" (46f4:0001), 67108864 bytes in blocks of 512, fixed, writable: serving the block protocol
```

The first boot on x64 ran its program on the display's console, which the transcript did not hold, so the runner never
saw the program ask for the drive to be taken out, and the guest's waits of two minutes passed in seconds of the host's
while the guest idled; the scenario has its terminal now, and the program waits half an hour.

## 6. Limits kept explicit

- One drive at a time a controller, and its logical unit 0 alone.
- QEMU's usb-storage alone is the gate; a real drive on VMware is the author's session (E5), where VMware's xHCI
  interrupts still wait to be seen (H5): the driver's interrupt slice of 10 ms polls the event ring all the same.
- No UAS (USB Attached SCSI), no CD-ROM (the plan leaves ISO 9660 out) and no exFAT formatting on the host; exFAT drives
  mount through OutWit.Common.Fat.
- No notification of arrival or departure to programs: `/etc/mtab` is read anew at each open (E2.2's rule).
- A drive taken out while a file of it is open loses what the file had not written; the namespace writes through its
  cache, so a write that completed is on the drive.
