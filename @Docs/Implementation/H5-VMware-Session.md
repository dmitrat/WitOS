# H5 — WitOS on VMware

H5 runs the PowerShell session on VMware Workstation: the PC's one disk (H3.3) on VMware's SATA or NVMe controller, its
e1000e (H4), the console on the firmware's framebuffer and a USB keyboard on its xHCI controller. The author's session
by hand closes the step. This document records the measured facts. Part 1 was measured without a window before that
session, on the development host's VMware Workstation 25.0.1; part 2 is for the session itself.

## 1. Before the session: the headless half (2026-10-25)

### 1.1 The disk and the machine

The disk is `shell --pc --prepare` for x64 at H4's commit. It is 1.74 GB: the 1.5 GiB data disk of a session with the
layer of base applications, then the 128 MiB EFI system partition. The VMDK descriptor names the raw image as its one
flat extent, and VMware attached it as written, with no conversion.

The machine is written by hand, as H0's were:

- EFI firmware with `efi.serialconsole.enabled`; the first serial port goes to a file, which is the kernel's log.
- 1024 MiB and two processors.
- The disk as `sata0:0` or as `nvme0:0`.
- `ethernet0.virtualDev = "e1000e"` on VMware's NAT.
- `usb_xhci.present` with `keyboard.vusb.enable = "TRUE"`.
- Four PCIe root ports; VMware's xHCI registers only behind one (H0).

`vmrun -T ws start … nogui` starts it and `vmrun stop … hard` ends it.

### 1.2 What ran

| | SATA (`sata0:0`) | NVMe (`nvme0:0`) |
| --- | --- | --- |
| Firmware | booted the disk; the loader's `[BOOT]` lines | the same |
| Disk controller | 15ad:07e0 at 02:01.0: AHCI 1.3, 30 ports (implemented 3fffffff), 32 command slots, 64-bit addresses, MSI. Port 0: "VMware Virtual SATA Hard Drive", 1744830464 bytes in blocks of 512, no write cache, link 6 Gb/s | 15ad:07f0 at 13:00.0: NVMe 1.3, "VMware Virtual NVMe Disk", 64 namespaces, queues of up to 256 entries, no volatile write cache, MSI-X entry 0. Namespace 1 takes transfers of up to 1 MiB (MDTS) |
| Partition table and volumes | four partitions; `/boot` from "EFI System", read-only; `/data` the 1.6 GB ext4 volume, `/fat` | the same, after hard stops of the SATA runs: the ext4 probe replayed what they left |
| Network | e1000e 8086:10d3 at 03:00.0 on MSI, address 00:0c:29:ba:a1:54, link up at 1000 Mb/s full duplex. DHCP leased 192.168.119.130/24 from VMware's NAT (router and DNS 192.168.119.2, 1800 s). The DNS probe's answer for `example.com` came through the NAT from the Internet (104.20.23.154) | the same adapter on MSI; DHCP leased 192.168.119.131 |
| USB | xHCI 15ad:077a: 1.20, 127 slots, 8 ports, MSI-X. Port 5 the absolute mouse 0e0f:0003, left; port 6 the keyboard 0e0f:0006, report protocol; port 7 a full-speed hub 0e0f:0002 of 7 ports; port 8 a high-speed hub 0e0f:0002 of 7 ports with one Transaction Translator | — |
| Display | the GOP framebuffer 1024 by 768 BGRX at 0xF0000000: the display service draws 128 by 48 cells; the terminal takes that size | — |
| Session | `[APPS-INIT] in /data`, the banner and `PS /data>` on the screen: a frame 56 s after the machine started showed `[APPS-INIT]` alone, one at 99 s the prompt | the device manager ready; the session not watched |

Other facts of the board: HPET 14,318,180 Hz (period 69,841,279 fs, H1.1), two processors online, 852 MiB usable of
1024 MiB, 43 PCI functions of which 9 described without bridges, ECAM at 0xE0000000, no ICH9 LPC bridge (the firmware's
interrupt lines kept, every function on MSI or MSI-X).

The scenario's probes that need QEMU's user networking fail on VMware, as they must: the ARP request for slirp's gateway
10.0.2.2 and the TCP exchange with the runner's echo server at 10.0.2.100.

The high-speed hub with its Transaction Translator is the first such hub the USB core met outside its host tests (H2.2,
§6).

### 1.3 The screen without a window

VMware's own VNC server shows the screen with no window and no VMware Tools:

- `RemoteDisplay.vnc.enabled = "TRUE"`, `RemoteDisplay.vnc.port` and `RemoteDisplay.vnc.ip = "127.0.0.1"` (otherwise it
  listens on every interface, without a password).
- A plain RFB 3.8 client with security type None takes a frame in raw encoding. The banner and the prompt above are
  such frames.

### 1.4 The console's keys: PS/2 too

Keys sent without a person reach VMware's PS/2 keyboard, not its USB one. A scratch kernel measured where they go; its
change was never committed. The boot processor's tick read the 8042 and logged every byte. A scratch xHCI driver sent
the keyboard more requests.

- **VNC.** RFB `KeyEvent`s through VMware's VNC server reach the firmware: its Boot Manager opens. Under WitOS they
  arrive as scan codes of set 1 at the 8042 (0x1E and 0x9E for a press and a release of A), from the firmware's last
  seconds through the session. In the same boots the USB keyboard was configured with SET_IDLE and its interrupt
  endpoint polled, and the xHCI driver logged no key.
- **No guest action moved them to the USB keyboard.** The guest tried in turn:
  - the keyboard's LEDs set off through SET_REPORT, as Linux's keyboard handler and Windows do when a keyboard starts;
  - the PS/2 keyboard's Disable Scanning (0xF5);
  - the 8042's command byte without its keyboard interrupt;
  - a keyboard reset (0xFF, answered 0xFA and 0xAA) left with scanning disabled;
  - the 8042's keyboard port disabled (0xAD).

  After 0xF5 or 0xAD, VMware drops the keys, and none reaches either keyboard. `keyboard.allowBothIRQs = "FALSE"` in the
  `.vmx`, which a forum thread names beside `keyboard.vusb.enable`, changed nothing.
- **The console's own path is unmeasured.** Input injected into the Workstation window never grabbed the VM: keys sent
  through UI Automation, and a click and keys through `mouse_event` and `keybd_event`. The status bar went on asking to
  click inside. A modal "Removable Devices" hint, which each start opens, keeps the window disabled until it is
  dismissed.
- VMware's USB analyzer (`usb.analyzer.enable`) records its virtual hubs' traffic, not the virtual keyboard's.

The likely reading: VMware's VNC server feeds scan codes to its PS/2 device, while the console's input goes through
VMware's HID layer, which `keyboard.vusb.enable` sends to the USB keyboard. Users of Linux guests report that the
setting moves their keys to the USB keyboard.

The session by hand decided it on 2026-10-25. The author booted the same scratch kernel in VMware Workstation and typed
on its console, the VM grabbed by a click. The 8042 delivered every key as a scan code of set 1: Shift, Ctrl, letters,
digits, and the Ctrl and Alt of VMware's release key (0x1D, 0x38, then 0xB8, 0x9D). The xHCI driver had configured the
USB keyboard 0e0f:0006 and polled its interrupt endpoint in the same boot (`a keyboard of the report protocol on
interface 0 … reporting keys`), and logged no key. A boot of the committed kernel from the SATA machine in the same
session logged none either. So the console's keys reach VMware's PS/2 keyboard too, with `keyboard.vusb.enable = "TRUE"`
and `guestOS = "other-64"`.

WitOS on VMware therefore needs a PS/2 keyboard, against [ADR 0029](ADR-0029-PC-Interrupts-Power-and-Device-Table.md)'s
USB keyboard on every target and its kernel without port access in the release. **The author decided on 2026-10-26** to
close H7 first and then come back to VMware until WitOS runs there well enough for the author to use it. The keyboard is then either an ADR
for the 8042's port region as a device with an i8042 driver service on the input protocol (which laptops' built-in
keyboards need too), or VMware settings the author checks by hand.

## 2. The session by hand

To be measured by the author:

- typing at the prompt;
- `Get-ChildItem /data`, a command that uses the network (`Invoke-WebRequest` through VMware's NAT);
- the screen console's latency, which no gate measures (the author's decision of 2026-10-20);
- the soft-off at `Stop-Computer` or the end of the session, and the reset (ADR 0029).
