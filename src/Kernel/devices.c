#include "witos/devices.h"
#include "witos/boot.h"
#include "witos/limits.h"
#include "witos/platform.h"

/* The device descriptor table (RFC 0011 section 7.7, plan steps K3.1 and H1.2a): the platform fills the kernel's own
 * array once after paging is active, the kernel validates it and copies the header and the descriptors into as many
 * pages as they take (one for up to 17 descriptors, four for 64), which components read through one read-only memory
 * object. The kernel reads its own array; ownership of a descriptor is one token at a time, the acquiring component's,
 * and the component's last device handle gives the descriptor back. */

#define TABLE_PAGE_LIMIT ((sizeof(WitDeviceTable) + WIT_DEVICE_CAPACITY * sizeof(WitDeviceDescriptor) + 4095) / 4096)

static WitDeviceDescriptor descriptors[WIT_DEVICE_CAPACITY];
static WitU64 table_pages[TABLE_PAGE_LIMIT];
static WitU32 table_page_count;
static WitU32 count;
static WitU64 owners[WIT_DEVICE_CAPACITY];

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

/* The byte at an offset of the published table: the header, then the descriptors. */
static WitU8 table_byte(const WitDeviceTable *header, WitU64 offset)
{
    if (offset < sizeof(WitDeviceTable)) {
        return ((const WitU8 *)header)[offset];
    }
    return ((const WitU8 *)descriptors)[offset - sizeof(WitDeviceTable)];
}

void wit_devices_initialize(const WitBootInfo *boot, WitPageAllocator *allocator)
{
    require(table_page_count == 0, "Device table initialized twice");
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
    WitDeviceTable header;
    header.Version = WIT_DEVICE_TABLE_VERSION;
    header.Size = sizeof(WitDeviceTable);
    header.Count = count;
    header.DescriptorSize = sizeof(WitDeviceDescriptor);
    const WitU64 bytes = sizeof(WitDeviceTable) + (WitU64)count * sizeof(WitDeviceDescriptor);
    const WitU32 pages = (WitU32)((bytes + 4095) / 4096);
    require(pages >= 1 && pages <= TABLE_PAGE_LIMIT, "Device table exceeds its pages");
    for (WitU32 p = 0; p < pages; ++p) {
        require(wit_page_allocate(allocator, &table_pages[p]), "Device table page allocation failed");
        WitU8 *page = (WitU8 *)table_pages[p];
        for (WitU32 i = 0; i < 4096; ++i) {
            const WitU64 offset = (WitU64)p * 4096 + i;
            page[i] = offset < bytes ? table_byte(&header, offset) : 0;
        }
    }
    table_page_count = pages;
    wit_console_write("Devices: ");
    wit_console_write_u64(count);
    wit_console_write(" in ");
    wit_console_write_u64(pages);
    wit_console_write(pages == 1 ? " page\n" : " pages\n");
}

WitU32 wit_devices_table_pages(const WitU64 **pages)
{
    *pages = table_pages;
    return table_page_count;
}

WitU32 wit_devices_count(void)
{
    return count;
}

const WitDeviceDescriptor *wit_devices_descriptor(WitU32 index)
{
    if (!table_page_count || index >= count) {
        return 0;
    }
    return &descriptors[index];
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
