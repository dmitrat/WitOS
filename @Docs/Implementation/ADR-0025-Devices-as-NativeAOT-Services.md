# ADR 0025: Devices as NativeAOT services

**Status:** Accepted 2026-10-12.
**Date:** 2026-10-12.
**Scope:** The service model of phase D. It covers where the device manager and the drivers run, how device
authority reaches them, the C and managed capability API they are written against, and how the limits of the K3
kernel are carried. The plan steps that apply it are D1.1, D1.2 and D2 of `PLAN.md`.

## Context

Phase K gave the kernel the device mechanisms of RFC 0011 §7.7 (K3.1, K3.2):
- a read-only table of the descriptors the platform enumerated;
- `DEVICE_ACQUIRE` and `DEVICE_MEMORY`;
- `INTERRUPT_BIND` and `INTERRUPT_ACK`;
- `DMA_PIN` and `DMA_UNPIN`.

A C fixture proved them by reading a sector of the virtio-blk boot disk. Phase R then made NativeAOT programs of
the boot package run on both ISAs (R2) and built them with the SDK's own targets (R2.3b).

The documents fix what comes next.
- The device manager is a managed service above the kernel. It discovers devices, matches drivers, activates them,
  delegates resources and keeps the device lifecycle (RFC 0007 §156, Implementation Strategy §41).
- A driver receives narrow capabilities of one device: its configuration, its MMIO, its interrupt, DMA and reset
  (RFC 0007 §52, Implementation Strategy §43, RFC 0004 §55). It never receives machine-wide authority (RFC 0007
  §109).
- Device access reaches services through `libwitos` and the `OutWit.OS.*` packages above it (RFC 0011 §9.1, §9.4).
- M4 is done when the virtual block device is discovered, a driver is selected, it receives narrow capabilities,
  and it can issue a basic I/O operation (Implementation Strategy §45).

The documents leave open:
- who starts the device manager;
- how its authority arrives, given that `DEVICE_ACQUIRE` is the root task's (RFC 0011 §7.7, §7.11);
- how a driver the device manager starts receives its capabilities;
- what the managed API looks like before phase N publishes packages;
- what to do where K3 falls short of RFC 0007.

## Decisions

### 1. Two kinds of service process

- **The device manager** is a NativeAOT program of the boot package, `/system/DeviceManager`. It runs in a process
  of the system layer like any other.
- **A driver** is a NativeAOT program of the boot package, such as `/system/Drivers/VirtioBlock`. Each driven device
  gets one driver process.

Isolation is the process (RFC 0007 §105: the isolated-process model). A driver's fault ends its process, not the
device manager or the kernel.

### 2. The root task starts the device manager and moves the device table to it

The root task starts `/system/DeviceManager` before `/bin/init`, whenever the boot package has it. It moves its own
handle of the device table to the device manager, with the rights the kernel gave it, and keeps no device authority
afterwards. It moves rather than attenuates because `HANDLE_DUPLICATE` keeps `ACQUIRE` out of a memory object's
rights.

This keeps "root task authority" (RFC 0011 §7.7) a matter of who decides, not of who runs the device code. The
root task decides once that the device manager is the authority, and the device manager decides every acquisition
after that.

The root task starts `/bin/init` only after the device manager has answered that its devices are discovered and
their drivers started. Its process manager serves spawn requests while it waits, so the device manager can start
drivers through it. A device manager that ends before it answers is reported, and `/bin/init` starts without it.

### 3. A channel granted at the start carries the capabilities

A new process may start with one capability its creator grants it, beside the log, the package and the process
manager.
- **Start message, version 4.** The grant is the fourth handle slot, `WIT_START_HANDLE_GRANT`. A message holds four
  handles, so the start message's capacity is now used up.
- **Spawn request, version 3.** It carries the grant in its third handle slot, so a process the process manager
  starts may receive one too.

A service's grant is always the end of a channel to its creator, and the creator sends the service's capabilities
over that channel. The same protocol runs at two levels.
- **The root task and the device manager:** the root task grants the device manager a channel and sends it the
  device table over it. The device manager answers on it when it is ready.
- **The device manager and a driver:** the device manager grants the driver a channel and sends it what it
  acquired:
  - the device handle, moved with `TRANSFER`, with `BIND` and `QUERY`;
  - the device's descriptor.

  The driver answers when it is ready, or with the reason it is not.

The driver derives the rest from the device handle: `DEVICE_MEMORY` for its regions, `INTERRUPT_BIND` for its
line, and `DMA_PIN` for memory it owns. It holds one device and never the table.

Nothing else is inherited (RFC 0004 §137). A process without a grant starts as before. The channel stays open for
the service's life, so a later request, such as a driver asking again after a restart, has a path to its creator.

**Moving a device needs one kernel change.** K3 made a device handle process-bound. A device's holder record
belonged to the process that acquired it, so the kernel refuses to deliver the handle to another process (K5.2c),
as it refuses events, interrupt bindings and pins. The table crosses, since it is a memory object, so D1.1 needs
nothing of the kernel. D1.2 lets a device handle cross: at delivery, the device's holder becomes the receiving
process, and the device is still held by one process at a time. This is an additive change to ABI-1 and raises its
minor version to 1.2. Interrupt bindings and pins stay process-bound, and the driver makes them itself.

### 4. The C capability API is libwitos

`libwitos` presents the families the services need, with the `witos_` prefix:
- handles: close, duplicate with fewer rights, query;
- events and waits;
- channels: create, send, receive, with handles;
- memory objects: create, map, unmap;
- the device table and devices: map and read the table, acquire, the memory of a region;
- interrupts: bind, acknowledge;
- DMA: pin, unpin;
- the start grant.

A function returns zero or an `errno` value, as `witos_spawn` does. Kernel statuses map onto `errno` values, and no
call number or status appears above layer 2 (RFC 0011 §9.4). Every function validates its arguments before it
calls the kernel, and the kernel validates them again.

### 5. The managed API is one assembly, `OutWit.OS`, for WitOS's own services until phase N

Its namespaces follow the documents' naming:
- `OutWit.OS.Resources`: handles as safe handles, memory objects and their mappings, events, waits;
- `OutWit.OS.Communication`: channels and messages with handles;
- `OutWit.OS.Hardware`: the device table, descriptors, devices, interrupt bindings and DMA pins.

On the host, the services reference the assembly as a project, and its tests run there. For witos, `runtime-witos`
compiles its sources into each service it publishes, in the generated project that keeps the repository's build
properties out. NativeAOT links `libwitos` statically and calls it through direct P/Invoke, as the M3 acceptance
calls the libc.

It holds only what the device manager and the drivers use. It is not published and is not an application API.
Phase N splits it into the packages of RFC 0011 §9.1 with their versions (§10.2). Applications keep seeing .NET,
and no hardware class leaks to them (RFC 0007 §113).

### 6. The device manager translates the platform's descriptors into UHI records

The kernel stays classless (RFC 0011 §7.7). The device manager's descriptor reader turns each descriptor into a UHI
device record (RFC 0007 §47–55):
- **Identity:** the bus address (stable on these boards) with the vendor, device and subsystem words. Identity is
  kept apart from addressing (§49).
- **Class:** the hardware class, from the PCI class code (§48).
- **Provider and contract:** from the vendor and device words. Virtio is vendor `0x1AF4` with modern device
  identifiers from `0x1040` plus the virtio type, giving `BlockStorageController` for virtio-blk, for example (§55).
- **Resources:** the device's regions and lines.
- **Guarantees, as they are:** no IOMMU, so DMA isolation is none (§62, §202).

The descriptors are validated, never trusted (§137). Each record is printed as a discovery line, as Implementation
Strategy §44 asks: `Device discovered: Class: BlockStorageController, Provider: Virtio, DeviceId: ...`.

### 7. A driver table until driver packages

Until driver packages carry their match metadata (RFC 0010, RFC 0007 §110), a table in the device manager maps a
provider and contract to a driver program. A device without a driver is reported as unclaimed and stays in the
table.

The lifecycle:
- A driver's end gives its device back: the kernel frees the devices a component held.
- The device manager acquires the device again and restarts the driver, a bounded number of times (RFC 0007
  §106–107), then reports the device as failed.

Hotplug, power states and a reset capability stay out of phase D. These boards have no hotplug, and a driver resets
its device through the device's own configuration until a reset capability exists.

### 8. K3's limits are carried and reported, not hidden

| Limit | What phase D does |
| --- | --- |
| No `PIN` right and no IOMMU | The holder of a device handle is the DMA trust boundary. RFC 0004 §57's separate right waits for an IOMMU, and the device manager reports isolation as none. |
| No DMA model in the descriptor | The device manager reports what ABI-1 has, which is no IOMMU feature. |
| 16 descriptors | Kept explicit. More devices than the table holds is a boot fact, reported by the platform. |
| INTx only, one binding per line | A device whose line another driven device already holds is reported unclaimed, not driven. |
| No port I/O | Only memory-mapped transports are driven: modern virtio PCI. |
| A device handle bound to its process | D1.2 moves the holder with the handle (decision 3). Interrupt bindings and pins stay bound, and a driver makes its own. |

## Consequences

- **D1.1:** the start grant, libwitos's capability API, the `OutWit.OS` assembly, and the device manager reading the
  table and printing its discovery lines on both ISAs (M4 §45, points 1 and 2).
- **D1.2:** driver matching and activation. The kernel lets a device handle cross a process boundary (ABI-1 1.2).
  The device manager starts a driver, grants it its channel and moves its device to it, and the driver proves the
  capabilities it received (M4 §45, points 3 and 4).
- **D2:** the virtio-blk driver on NativeAOT reads the boot disk through those capabilities (M4 §45, point 5).
- The scenarios of phase D need the runtime's build, as R2's do, so they run where `runtime-witos` runs: on a Linux
  host, and in CI's `linux-host` job.
