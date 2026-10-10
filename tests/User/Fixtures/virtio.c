#include "fixture.h"
#include "witos/dma.h"

/* Virtio block fixture (plan steps K3.2 and K8.3): a user-space driver over the modern virtio PCI transport of QEMU's
 * virtio-blk-pci, built on the devices of ABI-1 alone: the device table, DEVICE_ACQUIRE, DEVICE_MEMORY of the
 * configuration page and of the modern BAR, DMA_PIN of the queue and request pages, INTERRUPT_BIND of the INTx line to
 * an event and INTERRUPT_ACK. It reads sector 0 of the boot disk and checks the FAT boot sector the tool wrote (the
 * signature 0xAA55 and the FAT16 type). The kernel knows none of this; the fixture holds all of it. */

#define READ_WRITE (WIT_MEMORY_READ | WIT_MEMORY_WRITE)
#define QUEUE_SIZE 8U
#define PAGES 5U /* the descriptors, the available ring, the used ring, the request and the data */
#define MMIO8(address) (*(volatile WitU8 *)(address))
#define MMIO16(address) (*(volatile WitU16 *)(address))
#define MMIO32(address) (*(volatile WitU32 *)(address))
#define MMIO64(address) (*(volatile WitU64 *)(address))

/* A virtio capability's place: the BAR and the offset in it. */
typedef struct Capability {
    WitU32 Bar, Offset;
} Capability;

/* A split virtqueue's descriptor. */
typedef struct Descriptor {
    WitU64 Address;
    WitU32 Length;
    WitU16 Flags, Next;
} Descriptor;

#define DESCRIPTOR_NEXT 1U
#define DESCRIPTOR_WRITE 2U

static void describe(volatile Descriptor *descriptor, WitU64 address, WitU32 length, WitU16 flags, WitU16 next)
{
    descriptor->Address = address;
    descriptor->Length = length;
    descriptor->Flags = flags;
    descriptor->Next = next;
}

/* DMA_PIN of one page of the object for the device: its physical address. */
static WitU64 pin_page(WitU64 device, WitU64 object, WitU64 offset, WitU64 *handle)
{
    WitDmaRange ranges[2];
    WitDmaPinRequest request;
    request.Version = WIT_DMA_PIN_VERSION;
    request.Size = sizeof(request);
    request.Device = device;
    request.Object = object;
    request.Offset = offset;
    request.Bytes = 4096;
    request.Ranges = (WitU64)ranges;
    request.RangeCapacity = 2;
    request.Flags = 0;
    *handle = fixture_expect(WIT_CALL_DMA_PIN, (WitU64)&request, sizeof(request), 0, WIT_STATUS_OK);
    return ranges[0].Address;
}

static void release(WitU64 base)
{
    fixture_expect(WIT_CALL_MEMORY_RELEASE, base, 0, 0, WIT_STATUS_OK);
}

FIXTURE_ENTRY void wit_user_start(const WitRootStartup *startup)
{
    const WitUserTestConfig *config = fixture_config(startup);
    fixture_check(startup->AbiVersion == WIT_ABI_VERSION && config->Mode == WIT_INTERRUPT_TEST_VIRTIO, 1);
    const WitU64 table = config->TableHandle;
    WitU64 table_view = 0;
    WitU32 index = 0;
    const volatile WitDeviceDescriptor *block = fixture_find_device(table, FIXTURE_VIRTIO_BLOCK, &table_view, &index);
    const WitU64 device = fixture_expect(WIT_CALL_DEVICE_ACQUIRE, table, index, 0, WIT_STATUS_OK);

    /* The configuration page, writable: memory decoding and bus mastering on, then the vendor capabilities. */
    const WitU64 region0 = fixture_expect(WIT_CALL_DEVICE_MEMORY, device, 0, 0, WIT_STATUS_OK);
    const WitU64 pci = fixture_map(region0, 0, 4096, READ_WRITE, 0, WIT_STATUS_OK);
    MMIO16(pci + 4) = (WitU16)(MMIO16(pci + 4) | 6);
    fixture_check(MMIO8(pci + 6) & 0x10, 30);
    Capability capabilities[5] = {{0, 0}, {0, 0}, {0, 0}, {0, 0}, {0, 0}};
    WitU32 found = 0, multiplier = 0;
    for (WitU32 at = MMIO8(pci + 0x34); at != 0; at = MMIO8(pci + at + 1)) {
        const WitU32 type = MMIO8(pci + at + 3); /* cfg_type 1 common, 2 notify, 3 ISR, 4 device */
        if (MMIO8(pci + at) != 9 || type < 1 || type > 4) {
            continue;
        }
        found |= 1U << type;
        capabilities[type].Bar = MMIO8(pci + at + 4);
        capabilities[type].Offset = MMIO32(pci + at + 8);
        if (type == 2) {
            multiplier = MMIO32(pci + at + 16); /* notify_off_multiplier */
        }
    }
    fixture_check(found == 0x1E, 31);
    fixture_check(capabilities[1].Bar == capabilities[2].Bar && capabilities[1].Bar == capabilities[3].Bar, 32);

    /* The BAR the capabilities name, found among the descriptor's regions by its firmware-assigned base. */
    const WitU32 raw = MMIO32(pci + 0x10 + capabilities[1].Bar * 4);
    WitU64 base = raw & 0xFFFFFFF0U;
    if ((raw & 6) == 4) {
        base |= (WitU64)MMIO32(pci + 0x14 + capabilities[1].Bar * 4) << 32;
    }
    WitU32 region = 1;
    while (region < block->RegionCount && block->Regions[region].Base != base) {
        ++region;
    }
    fixture_check(region < block->RegionCount, 33);
    const WitU64 bar_object = fixture_expect(WIT_CALL_DEVICE_MEMORY, device, region, 0, WIT_STATUS_OK);
    const WitU64 bar = fixture_map(bar_object, 0, block->Regions[region].Size, READ_WRITE, 0, WIT_STATUS_OK);

    /* Five pages, pinned one by one. */
    const WitU64 object = fixture_expect(WIT_CALL_MEMORY_OBJECT_CREATE, PAGES * 4096, 0, 0, WIT_STATUS_OK);
    const WitU64 dma = fixture_map(object, 0, PAGES * 4096, READ_WRITE, 0, WIT_STATUS_OK);
    WitU64 pins[PAGES], physical[PAGES];
    for (WitU32 i = 0; i < PAGES; ++i) {
        physical[i] = pin_page(device, object, (WitU64)i * 4096, &pins[i]);
    }

    /* The common configuration: reset, acknowledge, VERSION_1 alone, FEATURES_OK, queue 0 of eight entries on the
     * pinned pages, DRIVER_OK. */
    const WitU64 common = bar + capabilities[1].Offset;
    MMIO8(common + 20) = 0;
    while (MMIO8(common + 20) != 0) {
    }
    MMIO8(common + 20) = 3;
    MMIO32(common + 0) = 1;
    fixture_check(MMIO32(common + 4) & 1, 34); /* VIRTIO_F_VERSION_1 */
    MMIO32(common + 8) = 1;
    MMIO32(common + 12) = 1;
    MMIO32(common + 8) = 0;
    MMIO32(common + 12) = 0;
    MMIO8(common + 20) = 0x0B;
    fixture_check(MMIO8(common + 20) & 8, 35);
    MMIO16(common + 22) = 0;
    fixture_check(MMIO16(common + 24) >= QUEUE_SIZE, 36);
    MMIO16(common + 24) = QUEUE_SIZE;
    MMIO64(common + 32) = physical[0];
    MMIO64(common + 40) = physical[1];
    MMIO64(common + 48) = physical[2];
    const WitU32 notify = MMIO16(common + 30);
    MMIO16(common + 28) = 1;
    MMIO8(common + 20) = 0x0F;

    /* The line to an auto-reset event. */
    const WitU64 event = fixture_expect(WIT_CALL_EVENT_CREATE, 0, 0, 0, WIT_STATUS_OK);
    const WitU64 binding = fixture_expect(WIT_CALL_INTERRUPT_BIND, device, 0, event, WIT_STATUS_OK);

    /* One request: read sector 0 into the data page through three descriptors, then notify the queue. */
    const WitU64 request = dma + 3 * 4096, data = dma + 4 * 4096;
    MMIO32(request) = 0; /* VIRTIO_BLK_T_IN */
    MMIO32(request + 4) = 0;
    MMIO64(request + 8) = 0; /* sector 0 */
    MMIO8(request + 512) = 0xFF;
    volatile Descriptor *descriptors = (volatile Descriptor *)dma;
    describe(&descriptors[0], physical[3], 16, DESCRIPTOR_NEXT, 1);
    describe(&descriptors[1], physical[4], 512, DESCRIPTOR_NEXT | DESCRIPTOR_WRITE, 2);
    describe(&descriptors[2], physical[3] + 512, 1, DESCRIPTOR_WRITE, 0);
    const WitU64 available = dma + 4096, used = dma + 2 * 4096;
    MMIO16(available) = 0;
    MMIO16(available + 4) = 0;
    fixture_device_barrier();
    MMIO16(available + 2) = 1;
    fixture_device_barrier();
    MMIO16(bar + capabilities[2].Offset + notify * multiplier) = 0;

    /* The completion arrives as the bound interrupt; the ISR register says so and the used ring holds the request. */
    fixture_wait(&event, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    fixture_check(MMIO8(bar + capabilities[3].Offset) & 1, 37);
    fixture_check(MMIO16(used + 2) == 1 && MMIO32(used + 4) == 0, 38);
    fixture_check(MMIO8(request + 512) == 0, 39);
    fixture_check(MMIO16(data + 510) == 0xAA55 && MMIO32(data + 54) == 0x31544146U, 40); /* "FAT1" */
    fixture_expect(WIT_CALL_INTERRUPT_ACK, binding, 0, 0, WIT_STATUS_OK);

    /* Quiesce the device, then give everything back. */
    MMIO8(common + 20) = 0;
    fixture_close(binding);
    fixture_close(event);
    for (WitU32 i = 0; i < PAGES; ++i) {
        fixture_expect(WIT_CALL_DMA_UNPIN, pins[i], 0, 0, WIT_STATUS_OK);
    }
    release(dma);
    fixture_close(object);
    release(bar);
    release(pci);
    fixture_close(bar_object);
    fixture_close(region0);
    fixture_close(device);
    release(table_view);
    fixture_close(table);
    fixture_exit(WIT_TEST_EXIT_CODE);
}
