#include "witos/arch.h"
#include "witos/platform.h"
#include "witos/virtual.h"
#include "a64.h"

/* ARM64 kernel translation tables: 4 KiB granule, 48-bit addresses in both halves, four levels. TTBR0 maps
 * usable RAM, the kernel image by section and the board's boot devices at their physical addresses; TTBR1 holds
 * the boot storage window. Stack guards, page 0 and firmware memory stay unmapped. Kernel pages are global and
 * EL1-only; EL0 executes none of them. */

#define DESC_VALID 1ULL
#define DESC_TABLE 3ULL /* Table descriptor at levels 0 to 2. */
#define DESC_PAGE 3ULL /* Page descriptor at level 3. */
#define DESC_DEVICE (1ULL << 2) /* AttrIndx 1; AttrIndx 0 is normal memory. */
#define DESC_READ_ONLY (2ULL << 6) /* AP[2]; EL0 has no access either way. */
#define DESC_INNER_SHAREABLE (3ULL << 8)
#define DESC_ACCESSED (1ULL << 10)
#define DESC_PXN (1ULL << 53)
#define DESC_UXN (1ULL << 54)
#define DESC_ADDRESS 0x0000FFFFFFFFF000ULL
/* Kernel level-0 entries deny EL0 access and EL0 execution for everything below them, so the kernel branch
 * that user roots share stays out of reach whatever its leaves say. */
#define TABLE_KERNEL ((1ULL << 61) | (1ULL << 60))

/* MAIR_EL1: Attr0 normal write-back read/write-allocate, Attr1 device-nGnRnE. */
#define MAIR_ATTRIBUTES 0x00FFULL

/* TCR_EL1 for both halves: 48-bit regions, 4 KiB granule, write-back inner-shareable table walks. */
#define TCR_T0SZ 16ULL
#define TCR_WALK0 ((1ULL << 8) | (1ULL << 10) | (3ULL << 12))
#define TCR_T1SZ (16ULL << 16)
#define TCR_WALK1 ((1ULL << 24) | (1ULL << 26) | (3ULL << 28))
#define TCR_TG1_4K (2ULL << 30)
#define TCR_IPS_SHIFT 32

#define BOOT_DEVICE_CAPACITY 8U

static WitPageAllocator *pages;
static WitU64 low_root;
static WitU64 high_root;
static int active;
#if defined(WITOS_SELFTEST)
/* Executable-data probe of the execute-data fault test: a RET that must never run. */
__declspec(align(4096)) static WitU32 nx_probe[1024] = {0xD65F03C0U};
#endif

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitU64 new_table(void)
{
    WitU64 address = 0;
    require(wit_page_allocate(pages, &address), "Page table allocation failed");
    for (WitU32 i = 0; i < 512; ++i) {
        ((WitU64 *)address)[i] = 0;
    }
    return address;
}

static WitU64 *leaf(WitU64 address, int create)
{
    WitU64 *table;
    const WitU32 shifts[3] = {39, 30, 21};
    if ((address >> 48) == 0) {
        table = (WitU64 *)low_root;
    } else if ((address >> 48) == 0xFFFF) {
        table = (WitU64 *)high_root;
    } else {
        return 0;
    }
    for (WitU32 level = 0; level < 3; ++level) {
        WitU64 *entry = &table[(address >> shifts[level]) & 511];
        if ((*entry & DESC_VALID) == 0) {
            if (!create) {
                return 0;
            }
            *entry = new_table() | DESC_TABLE | (level == 0 ? TABLE_KERNEL : 0);
        }
        require((*entry & 3) == DESC_TABLE, "Unexpected block mapping");
        table = (WitU64 *)(*entry & DESC_ADDRESS);
    }
    return &table[(address >> 12) & 511];
}

static WitU64 page_descriptor(WitU64 physical, int writable, int executable, int device)
{
    WitU64 descriptor = physical | DESC_PAGE | DESC_ACCESSED | DESC_UXN;
    descriptor |= device ? DESC_DEVICE : DESC_INNER_SHAREABLE;
    if (!writable) {
        descriptor |= DESC_READ_ONLY;
    }
    if (!executable) {
        descriptor |= DESC_PXN;
    }
    return descriptor;
}

static void set_page(WitU64 virtual_address, WitU64 descriptor)
{
    *leaf(virtual_address, 1) = descriptor;
    if (active) {
        wit_a64_invalidate_page(virtual_address);
    }
}

static void remove_page(WitU64 address)
{
    WitU64 *entry = leaf(address, 0);
    if (entry != 0) {
        *entry = 0;
    }
    if (active) {
        wit_a64_invalidate_page(address);
    }
}

static int scratch_address(WitU64 address)
{
    return active &&
        address >= WIT_VM_SCRATCH_BASE &&
        address < WIT_VM_SCRATCH_BASE + WIT_VM_SCRATCH_SIZE &&
        (address & 4095) == 0;
}

int wit_virtual_map(WitU64 address, WitU64 physical, int writable)
{
    WitU64 *entry;
    if (!scratch_address(address) || (writable != 0 && writable != 1) || !wit_page_is_allocated(pages, physical)) {
        return 0;
    }
    entry = leaf(address, 0);
    if (entry != 0 && (*entry & DESC_VALID)) {
        return 0;
    }
    set_page(address, page_descriptor(physical, writable, 0, 0));
    return 1;
}

/* Changes only the access permission of a valid page, which needs no break-before-make. */
int wit_virtual_protect(WitU64 address, int writable)
{
    WitU64 *entry;
    if (!scratch_address(address) || (writable != 0 && writable != 1)) {
        return 0;
    }
    entry = leaf(address, 0);
    if (entry == 0 || !(*entry & DESC_VALID)) {
        return 0;
    }
    *entry = page_descriptor(*entry & DESC_ADDRESS, writable, 0, 0);
    wit_a64_invalidate_page(address);
    return 1;
}

int wit_virtual_unmap(WitU64 address)
{
    WitU64 *entry;
    if (!scratch_address(address)) {
        return 0;
    }
    entry = leaf(address, 0);
    if (entry == 0 || !(*entry & DESC_VALID)) {
        return 0;
    }
    remove_page(address);
    return 1;
}

static int reserved_image_page(const WitBootInfo *boot, WitU64 address)
{
    for (WitU32 i = 0; i < boot->MemoryRegionCount; ++i) {
        const WitMemoryRegion *r = &boot->MemoryRegions[i];
        if (address >= r->Base && address < r->Base + r->Length) {
            return r->Kind == WIT_MEMORY_RESERVED;
        }
    }
    return 0;
}

static int overlaps_usable(const WitBootInfo *boot, WitU64 address)
{
    for (WitU32 i = 0; i < boot->MemoryRegionCount; ++i) {
        const WitMemoryRegion *r = &boot->MemoryRegions[i];
        if (r->Kind == WIT_MEMORY_USABLE && address < r->Base + r->Length && r->Base < address + 4096) {
            return 1;
        }
    }
    return 0;
}

static void map_image(const WitBootInfo *boot)
{
    for (WitU64 p = boot->ImageBase; p < boot->ImageBase + boot->ImageSize; p += 4096) {
        require(reserved_image_page(boot, p), "Image overlaps usable or absent memory");
        set_page(p, page_descriptor(p, 0, 0, 0)); /* Headers and gaps default to read-only, non-executable. */
    }
    for (WitU32 i = 0; i < boot->ImageSectionCount; ++i) {
        const WitImageSection *s = &boot->ImageSections[i];
        require(s->Base >= boot->ImageBase &&
                s->Base < boot->ImageBase + boot->ImageSize &&
                s->Length != 0 &&
                s->Length <= boot->ImageBase + boot->ImageSize - s->Base &&
                (s->Base & 4095) == 0 &&
                (s->Length & 4095) == 0 &&
                s->Reserved == 0 &&
                (s->Flags & WIT_IMAGE_READ) &&
                !(s->Flags & ~7U) &&
                (s->Flags & 6U) != 6U,
            "Invalid image mapping");
        for (WitU32 j = 0; j < i; ++j) {
            require(s->Base >= boot->ImageSections[j].Base + boot->ImageSections[j].Length ||
                    boot->ImageSections[j].Base >= s->Base + s->Length,
                "Overlapping image sections");
        }
        for (WitU64 p = s->Base; p < s->Base + s->Length; p += 4096) {
            set_page(p, page_descriptor(p, (s->Flags & WIT_IMAGE_WRITE) != 0, (s->Flags & WIT_IMAGE_EXECUTE) != 0, 0));
        }
    }
}

static void map_storage(const WitBootInfo *boot)
{
    const WitU64 table = (WitU64)boot->StorageExtents;
    WitU64 mapped = 0;
    require(boot->StorageBytes >= 32 &&
            boot->StorageBytes <= 128ULL * 1024 * 1024 &&
            !boot->StorageReserved &&
            boot->StorageExtentCount &&
            boot->StorageExtentCount <= WIT_MAX_STORAGE_EXTENTS &&
            table >= boot->ImageBase &&
            table - boot->ImageBase <= boot->ImageSize &&
            (WitU64)boot->StorageExtentCount * sizeof(WitBootStorageExtent) <=
                boot->ImageSize - (table - boot->ImageBase),
        "Invalid boot storage descriptor");
    for (WitU32 i = 0; i < boot->StorageExtentCount; ++i) {
        const WitBootStorageExtent *extent = &boot->StorageExtents[i];
        require(extent->Base &&
                !(extent->Base & 4095) &&
                extent->Length &&
                !(extent->Length & 4095) &&
                extent->Length <= 1024 * 1024 &&
                extent->Base < WIT_PHYSICAL_LIMIT &&
                extent->Length <= WIT_PHYSICAL_LIMIT - extent->Base &&
                (extent->Base >= boot->ImageBase + boot->ImageSize ||
                    boot->ImageBase >= extent->Base + extent->Length) &&
                mapped <= ((boot->StorageBytes + 4095) & ~4095ULL) &&
                extent->Length <= ((boot->StorageBytes + 4095) & ~4095ULL) - mapped,
            "Invalid boot storage extent");
        for (WitU32 j = 0; j < i; ++j) {
            require(extent->Base >= boot->StorageExtents[j].Base + boot->StorageExtents[j].Length ||
                    boot->StorageExtents[j].Base >= extent->Base + extent->Length,
                "Overlapping boot storage extents");
        }
        for (WitU64 offset = 0; offset < extent->Length; offset += 4096) {
            require(reserved_image_page(boot, extent->Base + offset), "Boot storage overlaps usable or absent memory");
            set_page(WIT_A64_STORAGE_BASE + mapped + offset, page_descriptor(extent->Base + offset, 0, 0, 0));
        }
        mapped += extent->Length;
    }
    require(mapped == ((boot->StorageBytes + 4095) & ~4095ULL), "Incomplete boot storage mapping");
}

/* Devices the board needs before it can map pages on demand, such as its console. */
static void map_boot_devices(const WitBootInfo *boot)
{
    WitU64 devices[BOOT_DEVICE_CAPACITY];
    const WitU32 count = wit_platform_boot_devices(devices, BOOT_DEVICE_CAPACITY);
    require(count <= BOOT_DEVICE_CAPACITY, "Too many boot devices");
    for (WitU32 i = 0; i < count; ++i) {
        const WitU64 physical = devices[i];
        require((physical & 4095) == 0 && physical != 0 && physical < WIT_PHYSICAL_LIMIT, "Invalid boot device page");
        require(boot->ImageBase >= physical + 4096 || boot->ImageBase + boot->ImageSize <= physical,
            "Boot device overlaps the kernel image");
        require(!overlaps_usable(boot, physical), "Boot device overlaps usable RAM");
        set_page(physical, page_descriptor(physical, 1, 0, 1));
    }
}

void wit_virtual_initialize(const WitBootInfo *boot, WitPageAllocator *allocator)
{
    WitU64 guards[WIT_A64_STACK_GUARD_COUNT];
    const WitU64 features = wit_a64_memory_features();
    const WitU64 physical_range = features & 15;
    const WitU64 old_root = wit_a64_translation_base() & DESC_ADDRESS;

    require(((features >> 28) & 15) != 15, "4 KiB translation granule is unsupported");
    require(boot->ImageBase != 0 &&
            (boot->ImageBase & 4095) == 0 &&
            boot->ImageSize != 0 &&
            (boot->ImageSize & 4095) == 0 &&
            boot->ImageBase < WIT_PHYSICAL_LIMIT &&
            boot->ImageSize <= WIT_PHYSICAL_LIMIT - boot->ImageBase &&
            boot->Reserved == 0 &&
            boot->ImageSections != 0 &&
            boot->ImageSectionCount > 0 &&
            boot->ImageSectionCount <= WIT_MAX_IMAGE_SECTIONS,
        "Invalid kernel image descriptor");

    pages = allocator;
    low_root = new_table();
    high_root = new_table();
    for (WitU32 i = 0; i < boot->MemoryRegionCount; ++i) {
        const WitMemoryRegion *r = &boot->MemoryRegions[i];
        if (r->Kind == WIT_MEMORY_USABLE) {
            for (WitU64 p = r->Base; p < r->Base + r->Length; p += 4096) {
                if (p != 0) {
                    set_page(p, page_descriptor(p, 1, 0, 0));
                }
            }
        }
    }
    map_image(boot);
    map_boot_devices(boot);
    map_storage(boot);
    wit_a64_stack_guards(guards);
    for (WitU32 i = 0; i < WIT_A64_STACK_GUARD_COUNT; ++i) {
        remove_page(guards[i]);
    }

    /* 48-bit physical addresses at most: 52-bit output needs the LPA descriptor format. */
    wit_a64_install_tables(low_root, high_root,
        TCR_T0SZ |
            TCR_WALK0 |
            TCR_T1SZ |
            TCR_WALK1 |
            TCR_TG1_4K |
            ((physical_range > 5 ? 5 : physical_range) << TCR_IPS_SHIFT),
        MAIR_ATTRIBUTES);
    active = 1;
    require((wit_a64_translation_base() & DESC_ADDRESS) == low_root && low_root != old_root,
        "Kernel paging activation failed");
    wit_console_write("Kernel TTBR0: ");
    wit_console_write_hex(low_root);
    wit_console_write(" TTBR1: ");
    wit_console_write_hex(high_root);
    wit_console_write("\n[TEST-PASS] Memory.KernelPaging\n");
    for (WitU32 i = 0; i < WIT_A64_STACK_GUARD_COUNT; ++i) {
        require(leaf(guards[i], 0) != 0 && !(*leaf(guards[i], 0) & DESC_VALID), "Stack guard is mapped");
    }
    require(leaf(0, 0) == 0 || !(*leaf(0, 0) & DESC_VALID), "Null page is mapped");
    wit_console_write("[TEST-PASS] Memory.StackGuards\n");
}

WitU64 wit_virtual_kernel_root(void)
{
    return low_root;
}

static int kernel_branch(WitU64 entry)
{
    return (entry & 3) == DESC_TABLE && (entry & TABLE_KERNEL) == TABLE_KERNEL;
}

int wit_arch_space_kernel_ready(void)
{
    return active &&
        kernel_branch(((WitU64 *)low_root)[0]) &&
        kernel_branch(((WitU64 *)high_root)[(WIT_A64_STORAGE_BASE >> 39) & 511]);
}

/* A user root shares the kernel's identity branch, level-0 entry 0; TTBR1 with the storage window is never
 * switched. The other level-0 entries are private to the component. */
void wit_arch_space_install_kernel(WitU64 root)
{
    ((WitU64 *)root)[0] = ((WitU64 *)low_root)[0];
}

int wit_arch_space_active(WitU64 root)
{
    return wit_a64_translation_base() == root;
}

/* Device pages a board maps on demand once paging is active; not a public mapping API. */
void wit_arch_map_device_page(const WitBootInfo *boot, WitU64 physical)
{
    WitU64 *entry;
    require(active, "Device mapping before kernel paging");
    /* The virt board's ECAM window lies above 4 GiB; the identity map spans 48 bits. */
    require((physical & 4095) == 0 && physical != 0 && physical < (1ULL << 48), "Invalid device page");
    require(boot->ImageBase >= physical + 4096 || boot->ImageBase + boot->ImageSize <= physical,
        "Device page overlaps kernel image or guards");
    require(!overlaps_usable(boot, physical), "Device page overlaps usable RAM");
    entry = leaf(physical, 0);
    require(!entry || !(*entry & DESC_VALID), "Device page already mapped");
    set_page(physical, page_descriptor(physical, 1, 0, 1));
}

#if defined(WITOS_SELFTEST)
void wit_virtual_self_test(WitPageAllocator *allocator)
{
    WitU64 first = 0, second = 0;
    volatile WitU64 *view = (volatile WitU64 *)WIT_VM_SCRATCH_BASE;
    const WitU64 alias = WIT_VM_SCRATCH_BASE + 4096;
    require(wit_page_allocate(allocator, &first) && wit_page_allocate(allocator, &second), "VM test allocation failed");
    *(volatile WitU64 *)first = 0x1122334455667788ULL;
    *(volatile WitU64 *)second = 0x8877665544332211ULL;
    require(wit_virtual_map(WIT_VM_SCRATCH_BASE, first, 1), "VM map failed");
    require(!wit_virtual_map(WIT_VM_SCRATCH_BASE, second, 1), "Duplicate map accepted");
    require(*view == 0x1122334455667788ULL, "Mapped read failed");
    *view = 0xAABBCCDDEEFF0011ULL;
    require(*(volatile WitU64 *)first == *view, "Mapped write failed");
    require(wit_virtual_map(alias, first, 0), "Read-only alias map failed");
    require(*(volatile WitU64 *)alias == *view, "Alias read failed");
    require(wit_virtual_protect(WIT_VM_SCRATCH_BASE, 0) &&
            (*leaf(WIT_VM_SCRATCH_BASE, 0) & (DESC_READ_ONLY | DESC_PXN | DESC_UXN)) ==
                (DESC_READ_ONLY | DESC_PXN | DESC_UXN),
        "Read-only protection failed");
    require(wit_virtual_protect(WIT_VM_SCRATCH_BASE, 1), "Write protection restore failed");
    *view = 0x1020304050607080ULL;
    require(*(volatile WitU64 *)first == *view, "Restored writable mapping failed");
    require(
        wit_virtual_unmap(WIT_VM_SCRATCH_BASE) && !wit_virtual_unmap(WIT_VM_SCRATCH_BASE), "Unmap lifecycle failed");
    require(wit_virtual_map(WIT_VM_SCRATCH_BASE, second, 1) && *view == 0x8877665544332211ULL, "Stale TLB translation");
    require(!wit_virtual_map(0, first, 1) &&
            !wit_virtual_map(alias + 1, first, 1) &&
            !wit_virtual_map(alias + 4096, first + 1, 1) &&
            !wit_virtual_map(alias + 4096, 0, 1),
        "Invalid VM arguments accepted");
    require(wit_virtual_unmap(WIT_VM_SCRATCH_BASE) && wit_virtual_unmap(alias), "VM cleanup failed");
    require(wit_page_free(allocator, first) && wit_page_free(allocator, second), "VM physical cleanup failed");
    wit_console_write("[TEST-PASS] Memory.VirtualMappings\n");
}

void wit_virtual_fault_test(void)
{
#if defined(WITOS_TEST_WRITE_CODE) || \
    defined(WITOS_TEST_EXECUTE_DATA) || \
    defined(WITOS_TEST_GUARD_LOW) || \
    defined(WITOS_TEST_GUARD_HIGH) || \
    defined(WITOS_TEST_READONLY_ALIAS) || \
    defined(WITOS_TEST_UNMAPPED_ALIAS)
    WitU64 target = 0;
    const char *name = "";
#if defined(WITOS_TEST_WRITE_CODE)
    target = (WitU64)wit_kernel_entry;
    name = "Memory.WriteCode";
#elif defined(WITOS_TEST_EXECUTE_DATA)
    target = (WitU64)nx_probe;
    name = "Memory.ExecuteData";
#elif defined(WITOS_TEST_GUARD_LOW)
    target = (WitU64)wit_a64_kernel_stack;
    name = "Memory.GuardLow";
#elif defined(WITOS_TEST_GUARD_HIGH)
    target = (WitU64)wit_a64_kernel_stack + 4096 + WIT_A64_KERNEL_STACK_SIZE;
    name = "Memory.GuardHigh";
#elif defined(WITOS_TEST_READONLY_ALIAS)
    WitU64 physical = 0;
    require(wit_page_allocate(pages, &physical) && wit_virtual_map(WIT_VM_SCRATCH_BASE, physical, 1),
        "Read-only probe setup failed");
    *(volatile WitU64 *)WIT_VM_SCRATCH_BASE = 1;
    require(wit_virtual_protect(WIT_VM_SCRATCH_BASE, 0), "Read-only probe protection failed");
    target = WIT_VM_SCRATCH_BASE;
    name = "Memory.ReadOnlyAlias";
#elif defined(WITOS_TEST_UNMAPPED_ALIAS)
    target = WIT_VM_SCRATCH_BASE;
    name = "Memory.UnmappedAlias";
#endif
    wit_console_write("[TEST-BEGIN] ");
    wit_console_write(name);
    wit_console_write("\n[FAULT-PROBE] address=");
    wit_console_write_hex(target);
    wit_console_write("\n");
#if defined(WITOS_TEST_EXECUTE_DATA)
    ((void (*)(void))target)();
#elif defined(WITOS_TEST_UNMAPPED_ALIAS)
    wit_console_write_hex(*(volatile WitU64 *)target);
#else
    *(volatile WitU8 *)target = 0x90;
#endif
    wit_panic("Memory fault injection returned");
#else
    (void)nx_probe;
#endif
}
#endif

const WitU8 *wit_virtual_boot_storage(void)
{
    return active ? (const WitU8 *)WIT_A64_STORAGE_BASE : 0;
}
