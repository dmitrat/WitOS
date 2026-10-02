#include "x64.h"
#include "witos/platform.h"
#include "witos/virtual.h"

unsigned __int64 __readcr0(void);
unsigned __int64 __readcr3(void);
unsigned __int64 __readcr4(void);
unsigned __int64 __readmsr(unsigned long);
void __writecr0(unsigned __int64);
void __writecr3(unsigned __int64);
void __writecr4(unsigned __int64);
void __writemsr(unsigned long, unsigned __int64);
void __invlpg(void *);
void __cpuid(int[4], int);
#pragma intrinsic( \
    __readcr0, __readcr3, __readcr4, __readmsr, __writecr0, __writecr3, __writecr4, __writemsr, __invlpg, __cpuid)

#define PTE_PRESENT 1ULL
#define PTE_WRITE 2ULL
#define PTE_NX (1ULL << 63)
#define PTE_ADDRESS 0x000FFFFFFFFFF000ULL

static WitPageAllocator *pages;
static WitU64 root_table;
static int active;
__declspec(align(4096)) static WitU8 nx_probe[4096] = {0xC3};

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
    WitU64 *table = (WitU64 *)root_table;
    const WitU32 shifts[3] = {39, 30, 21};
    for (WitU32 level = 0; level < 3; ++level) {
        WitU64 *entry = &table[(address >> shifts[level]) & 511];
        if ((*entry & PTE_PRESENT) == 0) {
            if (!create) {
                return 0;
            }
            *entry = new_table() | PTE_PRESENT | PTE_WRITE;
        }
        require((*entry & 0x80) == 0, "Unexpected large page");
        table = (WitU64 *)(*entry & PTE_ADDRESS);
    }
    return &table[(address >> 12) & 511];
}

static void set_page(WitU64 virtual_address, WitU64 physical_address, WitU64 flags)
{
    *leaf(virtual_address, 1) = physical_address | flags | PTE_PRESENT;
    if (active) {
        __invlpg((void *)virtual_address);
    }
}

static void remove_page(WitU64 address)
{
    WitU64 *entry = leaf(address, 0);
    if (entry != 0) {
        *entry = 0;
    }
    if (active) {
        __invlpg((void *)address);
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
    if (entry != 0 && (*entry & PTE_PRESENT)) {
        return 0;
    }
    set_page(address, physical, PTE_NX | (writable ? PTE_WRITE : 0));
    return 1;
}

int wit_virtual_protect(WitU64 address, int writable)
{
    WitU64 *entry;
    if (!scratch_address(address) || (writable != 0 && writable != 1)) {
        return 0;
    }
    entry = leaf(address, 0);
    if (entry == 0 || !(*entry & PTE_PRESENT)) {
        return 0;
    }
    *entry = (*entry & PTE_ADDRESS) | PTE_PRESENT | PTE_NX | (writable ? PTE_WRITE : 0);
    __invlpg((void *)address);
    return 1;
}

int wit_virtual_unmap(WitU64 address)
{
    WitU64 *entry;
    if (!scratch_address(address)) {
        return 0;
    }
    entry = leaf(address, 0);
    if (entry == 0 || !(*entry & PTE_PRESENT)) {
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

void wit_virtual_initialize(const WitBootInfo *boot, WitPageAllocator *allocator)
{
    int cpu[4];
    WitU64 guards[WIT_STACK_GUARD_COUNT];
    const WitU64 old_root = __readcr3() & PTE_ADDRESS;
    const WitU64 cr4 = __readcr4();
    __cpuid(cpu, (int)0x80000000U);
    require((WitU32)cpu[0] >= 0x80000001U, "Extended CPU features unavailable");
    __cpuid(cpu, (int)0x80000001U);
    require((cpu[3] & (1 << 20)) != 0, "NX is required");
    require((cr4 & ((1ULL << 22) | (1ULL << 23) | (1ULL << 25))) == 0, "Inherited PKE/CET/UINTR state is unsupported");
    require((cr4 & ((1ULL << 12) | (1ULL << 17))) == 0, "LA57/PCID are unsupported");
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
    root_table = new_table();
    for (WitU32 i = 0; i < boot->MemoryRegionCount; ++i) {
        const WitMemoryRegion *r = &boot->MemoryRegions[i];
        if (r->Kind == WIT_MEMORY_USABLE) {
            for (WitU64 p = r->Base; p < r->Base + r->Length; p += 4096) {
                if (p != 0) {
                    set_page(p, p, PTE_WRITE | PTE_NX);
                }
            }
        }
    }
    for (WitU64 p = boot->ImageBase; p < boot->ImageBase + boot->ImageSize; p += 4096) {
        require(reserved_image_page(boot, p), "Image overlaps usable or absent memory");
        set_page(p, p, PTE_NX); /* Headers and gaps default to read-only NX. */
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
            set_page(p, p, (s->Flags & WIT_IMAGE_WRITE ? PTE_WRITE : 0) | (s->Flags & WIT_IMAGE_EXECUTE ? 0 : PTE_NX));
        }
    }
    const WitU64 storageTable = (WitU64)boot->StorageExtents;
    require(boot->StorageBytes >= 32 &&
            boot->StorageBytes <= 128ULL * 1024 * 1024 &&
            !boot->StorageReserved &&
            boot->StorageExtentCount &&
            boot->StorageExtentCount <= WIT_MAX_STORAGE_EXTENTS &&
            storageTable >= boot->ImageBase &&
            storageTable - boot->ImageBase <= boot->ImageSize &&
            (WitU64)boot->StorageExtentCount * sizeof(WitBootStorageExtent) <=
                boot->ImageSize - (storageTable - boot->ImageBase),
        "Invalid boot storage descriptor");
    WitU64 mapped = 0;
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
            set_page(WIT_X64_STORAGE_BASE + mapped + offset, extent->Base + offset, PTE_NX);
        }
        mapped += extent->Length;
    }
    require(mapped == ((boot->StorageBytes + 4095) & ~4095ULL), "Incomplete boot storage mapping");
    wit_x64_stack_guards(guards);
    for (WitU32 i = 0; i < WIT_STACK_GUARD_COUNT; ++i) {
        remove_page(guards[i]);
    }

    /* Flush inherited global entries as well as ordinary translations. */
    __writemsr(0xC0000080, __readmsr(0xC0000080) | (1ULL << 11));
    __writecr4(cr4 & ~((1ULL << 7) | (1ULL << 16) | (1ULL << 18))); /* No global pages, FSGSBASE or OSXSAVE. */
    require((__readcr4() & ((1ULL << 16) | (1ULL << 18))) == 0, "Unsupported user extended CPU state enabled");
    __writecr3(root_table);
    __writecr0(__readcr0() | (1ULL << 16));
    wit_x64_context_profile_self_test();
    active = 1;
    require((__readcr3() & PTE_ADDRESS) == root_table &&
            root_table != old_root &&
            (__readcr0() & (1ULL << 16)) &&
            (__readmsr(0xC0000080) & (1ULL << 11)),
        "Kernel paging activation failed");
    wit_console_write("Kernel CR3: ");
    wit_console_write_hex(root_table);
    wit_console_write("\n[TEST-PASS] Memory.KernelPaging\n");
    for (WitU32 i = 0; i < WIT_STACK_GUARD_COUNT; ++i) {
        require(leaf(guards[i], 0) != 0 && !(*leaf(guards[i], 0) & 1), "Stack guard is mapped");
    }
    require(leaf(0, 0) == 0 || !(*leaf(0, 0) & 1), "Null page is mapped");
    wit_console_write("[TEST-PASS] Memory.StackGuards\n");
}

/* Controlled q35 device aperture only; not a public mapping API. */
void wit_arch_map_device_page(const WitBootInfo *boot, WitU64 physical)
{
    int cpu[4];
    WitU64 *entry;
    require(active, "Device mapping before kernel paging");
    require(boot->ImageBase >= physical + 4096 || boot->ImageBase + boot->ImageSize <= physical,
        "Device page overlaps kernel image or guards");
    __cpuid(cpu, 1);
    require(
        (cpu[3] & (1 << 16)) && ((__readmsr(0x277) >> 24) & 255) == 0, "Device mapping requires PAT entry 3 to be UC");
    for (WitU32 i = 0; i < boot->MemoryRegionCount; ++i) {
        const WitMemoryRegion *r = &boot->MemoryRegions[i];
        require(r->Kind != WIT_MEMORY_USABLE || r->Base >= physical + 4096 || r->Base + r->Length <= physical,
            "Device page overlaps usable RAM");
    }
    entry = leaf(physical, 0);
    require(!entry || !(*entry & PTE_PRESENT), "Device page already mapped");
    /* PCD|PWT selects verified UC PAT entry 3; supervisor RW/NX, no aliases. */
    set_page(physical, physical, PTE_WRITE | PTE_NX | 0x18);
    require(
        (*leaf(physical, 0) & (PTE_PRESENT | PTE_WRITE | PTE_NX | 0x1C)) == (PTE_PRESENT | PTE_WRITE | PTE_NX | 0x18),
        "Device page permissions failed");
}

WitU64 wit_virtual_kernel_root(void)
{
    return root_table;
}

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
    require(
        wit_virtual_protect(WIT_VM_SCRATCH_BASE, 0) && (*leaf(WIT_VM_SCRATCH_BASE, 0) & (PTE_WRITE | PTE_NX)) == PTE_NX,
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
    target = (WitU64)wit_x64_kernel_stack;
    name = "Memory.GuardLow";
#elif defined(WITOS_TEST_GUARD_HIGH)
    target = (WitU64)wit_x64_kernel_stack + 4096 + WIT_KERNEL_STACK_SIZE;
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

const WitU8 *wit_virtual_boot_storage(void)
{
    return active ? (const WitU8 *)WIT_X64_STORAGE_BASE : 0;
}
