#include "witos/devices.h"
#include "witos/boot.h"
#include "witos/limits.h"
#include "witos/platform.h"

/* The device descriptor table (RFC 0011 section 7.7, plan step K3.1): one page the kernel owns, filled once by the
 * platform after paging is active, then read-only for everyone. Ownership of a descriptor is one token at a time;
 * the token is the acquiring component's, and the component's last device handle gives the descriptor back. */

static WitU64 table_page;
static WitU32 count;
static WitU64 owners[WIT_DEVICE_CAPACITY];

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_devices_initialize(const WitBootInfo *boot, WitPageAllocator *allocator)
{
    require(table_page == 0, "Device table initialized twice");
    require(sizeof(WitDeviceTable) + (WitU64)WIT_DEVICE_CAPACITY * sizeof(WitDeviceDescriptor) <= 4096,
        "Device table exceeds its page");
    require(wit_page_allocate(allocator, &table_page), "Device table page allocation failed");
    for (WitU32 i = 0; i < 512; ++i) {
        ((WitU64 *)table_page)[i] = 0;
    }
    WitDeviceTable *table = (WitDeviceTable *)table_page;
    WitDeviceDescriptor *descriptors = (WitDeviceDescriptor *)(table_page + sizeof(WitDeviceTable));
    count = wit_platform_devices(boot, descriptors, WIT_DEVICE_CAPACITY);
    require(count <= WIT_DEVICE_CAPACITY, "Platform enumerated beyond the device table");
    for (WitU32 i = 0; i < count; ++i) {
        const WitDeviceDescriptor *d = &descriptors[i];
        require(d->Bus != 0 && d->RegionCount <= WIT_DEVICE_REGIONS && d->LineCount <= WIT_DEVICE_LINES,
            "Platform published an invalid device descriptor");
        for (WitU32 r = 0; r < d->RegionCount; ++r) {
            const WitDeviceRegion *region = &d->Regions[r];
            require(region->Size != 0 &&
                    region->Base <= ~0ULL - region->Size &&
                    (!(region->Flags & WIT_DEVICE_REGION_MEMORY) || (!(region->Base & 4095) && !(region->Size & 4095))),
                "Platform published an invalid device region");
        }
        owners[i] = 0;
    }
    table->Version = WIT_DEVICE_TABLE_VERSION;
    table->Size = sizeof(WitDeviceTable);
    table->Count = count;
    table->DescriptorSize = sizeof(WitDeviceDescriptor);
    wit_console_write("Devices: ");
    wit_console_write_u64(count);
    wit_console_write("\n");
}

WitU64 wit_devices_table_page(void)
{
    return table_page;
}

WitU32 wit_devices_count(void)
{
    return count;
}

const WitDeviceDescriptor *wit_devices_descriptor(WitU32 index)
{
    if (!table_page || index >= count) {
        return 0;
    }
    return (const WitDeviceDescriptor *)(table_page + sizeof(WitDeviceTable)) + index;
}

int wit_devices_acquire(WitU32 index, WitU64 owner)
{
    if (!owner || index >= count || owners[index]) {
        return 0;
    }
    owners[index] = owner;
    return 1;
}

void wit_devices_release(WitU32 index, WitU64 owner)
{
    require(index < count && owners[index] == owner, "Device released by a component that does not hold it");
    owners[index] = 0;
}

WitU64 wit_devices_owner(WitU32 index)
{
    return index < count ? owners[index] : 0;
}
