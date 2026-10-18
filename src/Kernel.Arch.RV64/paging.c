#include "witos/arch.h"
#include "witos/kernel_stacks.h"
#include "witos/platform.h"
#include "witos/virtual.h"
#include "witos/rv64_csr.h"
#include "rv64.h"

/* RISC-V kernel translation tables (plan step V1.2): Sv48, 4 KiB pages, four levels, one root for both halves of
 * the address space. The low half's first root entry (512 GiB) maps usable RAM, the kernel image by section and the
 * board's boot devices at their physical addresses; the high half holds the boot storage window and the root task
 * image. Stack guards, page 0 and firmware memory stay unmapped. Kernel pages are global and supervisor-only; U-mode
 * executes none of them. Every kernel leaf carries A (and D when writable), since an implementation without hardware
 * updates of those bits faults on a page that lacks them. A user root (V1.3) copies the kernel's root entries. */

#define PTE_VALID 1ULL
#define PTE_READ 2ULL
#define PTE_WRITE 4ULL
#define PTE_EXECUTE 8ULL
#define PTE_USER 16ULL
#define PTE_GLOBAL 32ULL
#define PTE_ACCESSED 64ULL
#define PTE_DIRTY 128ULL
#define PTE_LEAF (PTE_READ | PTE_WRITE | PTE_EXECUTE)
#define PTE_PPN_SHIFT 10
#define PTE_ADDRESS(entry) (((entry) >> PTE_PPN_SHIFT) << 12)
#define SATP_PPN 0x00000FFFFFFFFFFFULL
#define TABLE_KERNEL (PTE_VALID | PTE_GLOBAL) /* a kernel branch: global for every space, a pointer to the next level */

#define BOOT_DEVICE_CAPACITY 8U
#define HIGH_HALF_FIRST 256U /* root entries of the high half */

static WitPageAllocator *pages;
static WitU64 root;
static int active;
#if defined(WITOS_SELFTEST) && defined(WITOS_TEST_EXECUTE_DATA)
/* Executable-data probe of the execute-data fault test: a RET that must never run. */
__attribute__((aligned(4096))) static WitU32 nx_probe[1024] = {0x00008067U};
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

/* Sv48 names the low half with bits 63:47 zero and the high half with them set. */
static int canonical(WitU64 address)
{
    const WitU64 high = address >> 47;
    return high == 0 || high == 0x1FFFF;
}

static WitU64 *leaf(WitU64 address, int create)
{
    WitU64 *table = (WitU64 *)root;
    const WitU32 shifts[3] = {39, 30, 21};
    if (!canonical(address)) {
        return 0;
    }
    for (WitU32 level = 0; level < 3; ++level) {
        WitU64 *entry = &table[(address >> shifts[level]) & 511];
        if ((*entry & PTE_VALID) == 0) {
            if (!create) {
                return 0;
            }
            *entry = (new_table() >> 12 << PTE_PPN_SHIFT) | TABLE_KERNEL;
        }
        require((*entry & PTE_LEAF) == 0, "Unexpected block mapping");
        table = (WitU64 *)PTE_ADDRESS(*entry);
    }
    return &table[(address >> 12) & 511];
}

static WitU64 page_descriptor(WitU64 physical, int writable, int executable, int device)
{
    WitU64 descriptor = (physical >> 12 << PTE_PPN_SHIFT) | PTE_VALID | PTE_READ | PTE_GLOBAL | PTE_ACCESSED;
    (void)device; /* the base ISA gives a page no memory attribute: the platform's physical attributes decide */
    if (writable) {
        descriptor |= PTE_WRITE | PTE_DIRTY;
    }
    if (executable) {
        descriptor |= PTE_EXECUTE;
    }
    return descriptor;
}

static void set_page(WitU64 virtual_address, WitU64 descriptor)
{
    *leaf(virtual_address, 1) = descriptor;
    if (active) {
        wit_rv64_fence_vma_address(virtual_address);
    }
}

static void remove_page(WitU64 address)
{
    WitU64 *entry = leaf(address, 0);
    if (entry != 0) {
        *entry = 0;
    }
    if (active) {
        wit_rv64_fence_vma_address(address);
    }
}

/* The stack window's pages (P1.5): its tables exist since boot under root entry 320, which every user root copies
 * with the rest of the high half; SFENCE.VMA is local, so the caller asks the other harts after a clear. */
static int window_address(WitU64 address)
{
    return address >= WIT_KERNEL_STACK_WINDOW &&
        address < WIT_KERNEL_STACK_WINDOW + WIT_KERNEL_STACK_WINDOW_SIZE &&
        (address & 4095) == 0;
}

void wit_arch_kernel_window_set(WitU64 address, WitU64 physical)
{
    WitU64 *const entry = window_address(address) ? leaf(address, 0) : 0;
    require(entry != 0 && !(*entry & PTE_VALID) && (physical & 4095) == 0, "A stack window page out of place");
    *entry = page_descriptor(physical, 1, 0, 0);
    wit_rv64_fence_vma_address(address);
}

WitU64 wit_arch_kernel_window_clear(WitU64 address)
{
    WitU64 *const entry = window_address(address) ? leaf(address, 0) : 0;
    require(entry != 0 && (*entry & PTE_VALID), "Clearing a stack window page that is not set");
    const WitU64 physical = PTE_ADDRESS(*entry);
    *entry = 0;
    wit_rv64_fence_vma_address(address);
    return physical;
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
    if (entry != 0 && (*entry & PTE_VALID)) {
        return 0;
    }
    set_page(address, page_descriptor(physical, writable, 0, 0));
    return 1;
}

/* Changes only the permission bits of a valid page. */
int wit_virtual_protect(WitU64 address, int writable)
{
    WitU64 *entry;
    if (!scratch_address(address) || (writable != 0 && writable != 1)) {
        return 0;
    }
    entry = leaf(address, 0);
    if (entry == 0 || !(*entry & PTE_VALID)) {
        return 0;
    }
    *entry = page_descriptor(PTE_ADDRESS(*entry), writable, 0, 0);
    wit_rv64_fence_vma_address(address);
    return 1;
}

int wit_virtual_unmap(WitU64 address)
{
    WitU64 *entry;
    if (!scratch_address(address)) {
        return 0;
    }
    entry = leaf(address, 0);
    if (entry == 0 || !(*entry & PTE_VALID)) {
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

static int root_task_mapped;

/* The windows of the boot package's extents, for the kernel's view of a package page a user mapping shows (S6.2). */
static struct {
    WitU64 Base, Length, Window;
} storage_windows[WIT_MAX_STORAGE_EXTENTS];

static WitU32 storage_window_count;

WitU8 *wit_virtual_view(WitU64 physical)
{
    if (!physical) {
        return 0;
    }
    for (WitU32 i = 0; i < storage_window_count; ++i) {
        if (physical - storage_windows[i].Base < storage_windows[i].Length) {
            return (WitU8 *)(storage_windows[i].Window + (physical - storage_windows[i].Base));
        }
    }
    const WitU64 *entry = leaf(physical, 0);
    return entry && (*entry & PTE_VALID) && PTE_ADDRESS(*entry) == (physical & ~4095ULL) ? (WitU8 *)physical : 0;
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
            set_page(WIT_RV64_STORAGE_BASE + mapped + offset, page_descriptor(extent->Base + offset, 0, 0, 0));
        }
        storage_windows[i].Base = extent->Base;
        storage_windows[i].Length = extent->Length;
        storage_windows[i].Window = WIT_RV64_STORAGE_BASE + mapped;
        storage_window_count = i + 1;
        mapped += extent->Length;
    }
    require(mapped == ((boot->StorageBytes + 4095) & ~4095ULL), "Incomplete boot storage mapping");
    /* The root task image (K4): optional, validated like the package, mapped read-only after its window. */
    require(!boot->RootTaskReserved &&
            boot->RootTaskExtentCount <= WIT_MAX_ROOT_EXTENTS &&
            (boot->RootTaskExtentCount == 0) == (boot->RootTaskBytes == 0) &&
            (boot->RootTaskExtentCount == 0) == (boot->RootTaskExtents == 0) &&
            boot->RootTaskBytes <= (4ULL << 20),
        "Invalid root task descriptor");
    if (boot->RootTaskExtentCount) {
        const WitU64 rootTable = (WitU64)boot->RootTaskExtents;
        require(rootTable >= boot->ImageBase &&
                rootTable - boot->ImageBase <= boot->ImageSize &&
                (WitU64)boot->RootTaskExtentCount * sizeof(WitBootStorageExtent) <=
                    boot->ImageSize - (rootTable - boot->ImageBase),
            "Invalid root task descriptor");
        WitU64 root_mapped = 0;
        for (WitU32 i = 0; i < boot->RootTaskExtentCount; ++i) {
            const WitBootStorageExtent *extent = &boot->RootTaskExtents[i];
            require(extent->Base &&
                    !(extent->Base & 4095) &&
                    extent->Length &&
                    !(extent->Length & 4095) &&
                    extent->Length <= 1024 * 1024 &&
                    extent->Base < WIT_PHYSICAL_LIMIT &&
                    extent->Length <= WIT_PHYSICAL_LIMIT - extent->Base &&
                    (extent->Base >= boot->ImageBase + boot->ImageSize ||
                        boot->ImageBase >= extent->Base + extent->Length) &&
                    root_mapped <= ((boot->RootTaskBytes + 4095) & ~4095ULL) &&
                    extent->Length <= ((boot->RootTaskBytes + 4095) & ~4095ULL) - root_mapped,
                "Invalid root task extent");
            for (WitU64 offset = 0; offset < extent->Length; offset += 4096) {
                require(reserved_image_page(boot, extent->Base + offset),
                    "Root task image overlaps usable or absent memory");
                set_page(WIT_RV64_ROOT_BASE + root_mapped + offset, page_descriptor(extent->Base + offset, 0, 0, 0));
            }
            root_mapped += extent->Length;
        }
        require(root_mapped == ((boot->RootTaskBytes + 4095) & ~4095ULL), "Incomplete root task mapping");
        root_task_mapped = 1;
    }
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
    WitU64 guards[WIT_RV64_STACK_GUARD_COUNT];
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
    require((wit_rv64_read_satp() >> 60) == 0, "Translation is active before the kernel's tables");

    pages = allocator;
    root = new_table();
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
    wit_rv64_stack_guards(guards);
    for (WitU32 i = 0; i < WIT_RV64_STACK_GUARD_COUNT; ++i) {
        remove_page(guards[i]);
    }
    /* The stack window's tables under root entry 320, before any user root copies the high half (P1.5): its leaves
     * stay empty until a thread's stack is backed, its guards for good. */
    for (WitU64 address = WIT_KERNEL_STACK_WINDOW; address < WIT_KERNEL_STACK_WINDOW + WIT_KERNEL_STACK_WINDOW_SIZE;
        address += 1ULL << 21) {
        leaf(address, 1);
    }

    /* The table stores reach the walker, the root takes effect, and no translation of the bare space survives. */
    wit_rv64_fence();
    wit_rv64_write_satp(WIT_RV64_SATP_SV48 | (root >> 12));
    wit_rv64_fence_vma();
    active = 1;
    require(wit_rv64_read_satp() == (WIT_RV64_SATP_SV48 | (root >> 12)), "Kernel paging activation failed");
    wit_console_write("Kernel satp: ");
    wit_console_write_hex(wit_rv64_read_satp());
    wit_console_write(" root ");
    wit_console_write_hex(root);
    wit_console_write("\n[TEST-PASS] Memory.KernelPaging\n");
    for (WitU32 i = 0; i < WIT_RV64_STACK_GUARD_COUNT; ++i) {
        require(leaf(guards[i], 0) != 0 && !(*leaf(guards[i], 0) & PTE_VALID), "Stack guard is mapped");
    }
    for (WitU64 low = WIT_KERNEL_STACK_WINDOW + 4096U; low < WIT_KERNEL_STACK_WINDOW + WIT_KERNEL_STACK_WINDOW_SIZE;
        low += WIT_KERNEL_STACK_STRIDE) {
        require(leaf(low - 4096U, 0) != 0 &&
                !(*leaf(low - 4096U, 0) & PTE_VALID) &&
                leaf(low + WIT_KERNEL_STACK_BYTES, 0) != 0 &&
                !(*leaf(low + WIT_KERNEL_STACK_BYTES, 0) & PTE_VALID),
            "A stack window guard is mapped");
    }
    require(leaf(0, 0) == 0 || !(*leaf(0, 0) & PTE_VALID), "Null page is mapped");
    wit_console_write("[TEST-PASS] Memory.StackGuards\n");
}

WitU64 wit_virtual_kernel_root(void)
{
    return root;
}

static int kernel_branch(WitU64 entry)
{
    return (entry & (PTE_VALID | PTE_LEAF)) == PTE_VALID && (entry & PTE_GLOBAL) != 0;
}

int wit_arch_space_kernel_ready(void)
{
    return active &&
        kernel_branch(((WitU64 *)root)[0]) &&
        kernel_branch(((WitU64 *)root)[(WIT_RV64_STORAGE_BASE >> 39) & 511]);
}

/* A user root (V1.3) shares the kernel's identity branch, root entry 0, and the high half; the other low-half entries
 * are private to the component. */
void wit_arch_space_install_kernel(WitU64 space_root)
{
    ((WitU64 *)space_root)[0] = ((WitU64 *)root)[0];
    for (WitU32 i = HIGH_HALF_FIRST; i < 512; ++i) {
        ((WitU64 *)space_root)[i] = ((WitU64 *)root)[i];
    }
}

int wit_arch_space_active(WitU64 space_root)
{
    return (wit_rv64_read_satp() & SATP_PPN) == (space_root >> 12);
}

/* Device pages a board maps on demand once paging is active; not a public mapping API. */
void wit_arch_map_device_page(const WitBootInfo *boot, WitU64 physical)
{
    WitU64 *entry;
    require(active, "Device mapping before kernel paging");
    /* The identity map spans the low half's first root entry: the low 512 GiB. */
    require((physical & 4095) == 0 && physical != 0 && physical < (1ULL << 39), "Invalid device page");
    require(boot->ImageBase >= physical + 4096 || boot->ImageBase + boot->ImageSize <= physical,
        "Device page overlaps kernel image or guards");
    require(!overlaps_usable(boot, physical), "Device page overlaps usable RAM");
    entry = leaf(physical, 0);
    require(!entry || !(*entry & PTE_VALID), "Device page already mapped");
    set_page(physical, page_descriptor(physical, 1, 0, 1));
}

/* Code publication: the data stores reach memory and the instruction fetch sees them (FENCE.I on the one processor
 * that runs threads until phase P). */
void wit_arch_publish_code_page(WitU64 physical)
{
    (void)physical;
}

void wit_arch_publish_code(void)
{
    wit_rv64_fence_instructions();
}

void wit_arch_invalidate_local(WitU64 address)
{
    wit_rv64_fence_vma_address(address);
}

/* All guest threads execute on the sole online processor. This is a full memory fence, not instruction cache
 * maintenance or a GC stop. */
void wit_arch_process_write_barrier(void)
{
    wit_rv64_fence();
}

/* One step of a spin-wait (P1.2): WFI with the hart's own software interrupt pending and enabled returns at once, and
 * QEMU's round-robin TCG ends the hart's slice on every WFI, where PAUSE only ends a translation block. Clearing the
 * bit afterwards loses no doorbell: every spin serves the pending set itself, and a request's sender holds the kernel
 * lock until the request was served, so a hart that takes the lock has nothing left to serve. Without the software
 * interrupt enabled the hart would halt, so it only pauses then. */
void wit_arch_spin_pause(void)
{
    if ((wit_rv64_read_sie() & WIT_RV64_SIE_SOFTWARE) == 0) {
        __asm__ volatile(".insn i 0x0F, 0, x0, x0, 0x010" : : : "memory"); /* PAUSE */
        return;
    }
    wit_rv64_set_sip(WIT_RV64_SIE_SOFTWARE);
    wit_rv64_wait_for_interrupt();
    wit_rv64_clear_sip(WIT_RV64_SIE_SOFTWARE);
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
            (*leaf(WIT_VM_SCRATCH_BASE, 0) & (PTE_WRITE | PTE_EXECUTE | PTE_USER)) == 0,
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
    target = (WitU64)wit_rv64_kernel_stack;
    name = "Memory.GuardLow";
#elif defined(WITOS_TEST_GUARD_HIGH)
    target = (WitU64)wit_rv64_kernel_stack + 4096 + WIT_RV64_KERNEL_STACK_SIZE;
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
#endif
}
#endif

const WitU8 *wit_virtual_boot_storage(void)
{
    return active ? (const WitU8 *)WIT_RV64_STORAGE_BASE : 0;
}

const WitU8 *wit_virtual_root_task(void)
{
    return active && root_task_mapped ? (const WitU8 *)WIT_RV64_ROOT_BASE : 0;
}
