#include "fixture.h"

/* Device fixture over the devices of ABI-1 (RFC 0011 v3 section 7.7, plan steps K3.1 and K8.3): the device table as a
 * read-only memory object, DEVICE_ACQUIRE over it and DEVICE_MEMORY of the acquired device's regions. The fixture knows
 * what the kernel does not: the identity of the board's virtio block function (vendor 0x1AF4, device 0x1001) and that
 * its first region is the function's configuration space, whose first words repeat that identity. */

#define VIRTIO_BLOCK FIXTURE_VIRTIO_BLOCK
#define READ_WRITE (WIT_MEMORY_READ | WIT_MEMORY_WRITE)
#define READ_EXECUTE (WIT_MEMORY_READ | WIT_MEMORY_EXECUTE)

static WitU64 acquire(WitU64 table, WitU64 index, WitU64 expected)
{
    return fixture_expect(WIT_CALL_DEVICE_ACQUIRE, table, index, 0, expected);
}

static WitU64 region(WitU64 device, WitU64 index, WitU64 expected)
{
    return fixture_expect(WIT_CALL_DEVICE_MEMORY, device, index, 0, expected);
}

static void release(WitU64 base)
{
    fixture_expect(WIT_CALL_MEMORY_RELEASE, base, 0, 0, WIT_STATUS_OK);
}

static void table_test(WitU64 table, const volatile WitDeviceDescriptor *block, WitU32 index)
{
    /* The table is read-only: a writable view is refused by the handle's rights, and so is an executable one. */
    fixture_map(table, 0, 4096, READ_WRITE, 0, WIT_STATUS_DENIED);
    fixture_map(table, 0, 4096, READ_EXECUTE, 0, WIT_STATUS_DENIED);
    /* The block function's first region is its configuration space; its words repeat the identity of the table. */
    fixture_check(block->RegionCount >= 1 && (block->Regions[0].Flags & WIT_DEVICE_REGION_CONFIG), 10);
    WitU64 device = acquire(table, index, WIT_STATUS_OK);
    const WitU64 object = region(device, 0, WIT_STATUS_OK);
    const WitU64 view = fixture_map(object, 0, 4096, WIT_MEMORY_READ, 0, WIT_STATUS_OK);
    const volatile WitU32 *config = (const volatile WitU32 *)view;
    fixture_check(config[0] == VIRTIO_BLOCK, 11);
    fixture_check(config[2] == block->Identity[1], 12); /* class and revision */
    /* A writable view is within the region handle's rights; an executable one never is; the view is not committable. */
    const WitU64 writable = fixture_map(object, 0, 4096, READ_WRITE, 0, WIT_STATUS_OK);
    fixture_check(*(const volatile WitU32 *)writable == VIRTIO_BLOCK, 13);
    release(writable);
    fixture_map(object, 0, 4096, READ_EXECUTE, 0, WIT_STATUS_DENIED);
    fixture_expect(WIT_CALL_MEMORY_COMMIT, view, 4096, READ_WRITE, WIT_STATUS_DENIED);
    fixture_expect(WIT_CALL_MEMORY_PROTECT, view, 4096, READ_EXECUTE, WIT_STATUS_DENIED);
    /* The region object outlives the device handle and the view outlives the object's handle; the device is free
     * again only when every handle to it is gone. */
    fixture_close(device);
    device = acquire(table, index, WIT_STATUS_OK);
    fixture_close(object);
    fixture_check(config[0] == VIRTIO_BLOCK, 14);
    /* A call's buffer in a device region is refused: the kernel has no view of device memory (S6.2). */
    fixture_expect(WIT_CALL_MEMORY_OBJECT_MAP, view, sizeof(WitMemoryMapRequest), 0, WIT_STATUS_BAD_ADDRESS);
    release(view);
    fixture_close(device);
}

static void authority_test(WitU64 table, const volatile WitDeviceDescriptor *block, WitU32 index)
{
    /* A table handle without ACQUIRE acquires nothing; indexes and reserved arguments are checked first. */
    const WitU64 reader = fixture_duplicate(table, WIT_RIGHT_MAP | WIT_RIGHT_QUERY, WIT_STATUS_OK);
    acquire(reader, index, WIT_STATUS_DENIED);
    fixture_close(reader);
    acquire(table, 0xFFFF, WIT_STATUS_INVALID_ARGUMENT);
    fixture_expect(WIT_CALL_DEVICE_ACQUIRE, table, index, 1, WIT_STATUS_INVALID_ARGUMENT);
    /* One holder at a time; a region index beyond the descriptor, a reserved argument and a port region are refused. */
    WitU64 device = acquire(table, index, WIT_STATUS_OK);
    acquire(table, index, WIT_STATUS_BUSY);
    region(device, 99, WIT_STATUS_INVALID_ARGUMENT);
    fixture_expect(WIT_CALL_DEVICE_MEMORY, device, 0, 1, WIT_STATUS_INVALID_ARGUMENT);
    for (WitU32 i = 0; i < block->RegionCount; ++i) {
        if (block->Regions[i].Flags & WIT_DEVICE_REGION_PORT) {
            region(device, i, WIT_STATUS_UNSUPPORTED);
        }
    }
    /* A device handle without BIND maps no region; the device stays held through it. */
    const WitU64 query = fixture_duplicate(device, WIT_RIGHT_QUERY, WIT_STATUS_OK);
    region(query, 0, WIT_STATUS_DENIED);
    fixture_close(device);
    acquire(table, index, WIT_STATUS_BUSY);
    fixture_close(query);
    /* The device moves through a channel: in flight it is held, received it works, and a dropped message frees it. */
    WitU64 ends[2] = {0, 0}, moved = 0, received = 0;
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, WIT_STATUS_OK);
    device = acquire(table, index, WIT_STATUS_OK);
    moved = device;
    fixture_send_handle(ends[0], &moved, WIT_STATUS_OK);
    region(device, 0, WIT_STATUS_BAD_HANDLE);
    acquire(table, index, WIT_STATUS_BUSY);
    fixture_receive_handle(ends[1], &received, WIT_STATUS_OK);
    fixture_close(region(received, 0, WIT_STATUS_OK));
    moved = received;
    fixture_send_handle(ends[0], &moved, WIT_STATUS_OK);
    fixture_close(ends[1]); /* the receiving endpoint: its queue is dropped with the device handle */
    fixture_close(acquire(table, index, WIT_STATUS_OK));
    fixture_close(ends[0]);
}

FIXTURE_ENTRY void wit_user_start(const WitRootStartup *startup)
{
    const WitUserTestConfig *config = fixture_config(startup);
    fixture_check(startup->AbiVersion == WIT_ABI_VERSION, 1);
    /* The table, mapped read-only: its header, then the block function among its descriptors. */
    const WitU64 table = config->TableHandle;
    WitU64 mapped = 0;
    WitU32 index = 0;
    const volatile WitDeviceDescriptor *block = fixture_find_device(table, VIRTIO_BLOCK, &mapped, &index);
    if (config->Mode == WIT_DEVICE_TEST_TABLE) {
        table_test(table, block, index);
    } else if (config->Mode == WIT_DEVICE_TEST_AUTHORITY) {
        authority_test(table, block, index);
    } else {
        fixture_failed(5);
    }
    release(mapped);
    fixture_close(table);
    fixture_exit(WIT_TEST_EXIT_CODE);
}
