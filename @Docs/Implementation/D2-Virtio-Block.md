# D2 — The virtio block driver reads the disk

D2 makes the driver of D1.2 do I/O. The NativeAOT program `/system/Drivers/VirtioBlock` drives the boot disk over the
modern virtio PCI transport, with nothing but the capabilities the device manager moved to it, and reads its first
sector. Both ISAs pass.

```
[VIRTIO-BLOCK] driver of pci/00:02.0/1af4:1001: holds the device, its configuration space reads 1af4:1001, as descriptor 2 names it
[VIRTIO-BLOCK] driver of pci/00:02.0/1af4:1001: read sector 0 through queue 0 and its interrupt: a boot sector (signature aa55, file system FAT16)
[DEVICES] driver /system/Drivers/VirtioBlock of pci/00:02.0/1af4:1001: ready
```

With this, every point of M4's definition of done is met (Implementation Strategy §45):
1. the hardware appears as UHI records (D1.1);
2. the device manager discovers the virtual block device (D1.1);
3. a managed driver is selected (D1.2);
4. it receives narrow capabilities, one device and none of the table (D1.2);
5. it issues a basic I/O operation (D2).

## The capability API

**libwitos** gains:
- `witos_memory_object_create`;
- `witos_event_create`;
- `witos_object_wait`, the one wait, with an absolute deadline that a signal's activation does not end;
- `witos_interrupt_bind` and `witos_interrupt_ack`;
- `witos_dma_pin` and `witos_dma_unpin`, the pin writing its physical ranges;
- `witos_device_barrier`: `MFENCE` on x64, `DSB SY` on ARM64.

**`OutWit.OS`** gains:
- `MemoryObject.Create`;
- `Event.Create` and `Event.Wait`;
- a mapping's `Writable` bytes;
- `MmioRegion`: a writable mapping of a device region read and written one volatile access of each width at a time,
  never merged, split or reordered, with bounds and alignment checked;
- `InterruptBinding`;
- `DmaPin`.

## The driver

The driver ports the C fixture of K3.2 (`tests/User/Fixtures/virtio.c`) to C#, using the regions and handles the
device manager moved to it rather than its own acquisition.
- **`Virtio/VirtioPciTransport.cs`** sets up the transport.
  - It maps the configuration region writable and turns on memory decoding and bus mastering.
  - It walks the vendor capabilities to the common configuration, the notifications, the ISR status and the device
    configuration.
  - It finds the BAR they name among the descriptor's regions by its firmware-assigned base, and maps it.
- **`Virtio/VirtioBlockQueue.cs`** drives the queue.
  - It creates five pages and pins each for the device: the descriptors, the available ring, the used ring, the
    request with its status, and the data.
  - It resets the device, negotiates `VIRTIO_F_VERSION_1` alone and sets queue 0 of eight entries on the pinned
    pages.
  - It binds the device's first line to an auto-reset event.
  - A read is three descriptors (the request, the data the device writes, the status), a barrier, the available
    index, a barrier and a notification. The completion comes as the bound interrupt: the ISR register says so, the
    used ring holds the request, and the line is acknowledged.
  - Disposing it quiesces the device and gives everything back.

`runtime-witos` compiles the driver's `Virtio` folder with it. The `runtime-devices` scenario requires the read's line
on both ISAs.

## Limits kept explicit

- One request at a time, on a queue of eight entries, reading only. There are no writes, no flush and no queue of
  requests; D3's block layer and D6's persistence need them, together with a writable disk.
- The driver serves no requests on its channel yet. D3 gives it the block protocol through which the storage stack
  reaches the disk.
- The device's first interrupt line, INTx, is the one bound. MSI-X stays unused, as in K3.2.
