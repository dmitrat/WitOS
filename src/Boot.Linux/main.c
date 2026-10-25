#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/package.h"
#include "witos/platform.h"
#include "boot_linux.h"

/* The boot contract built from the Linux arm64 boot protocol (plan step H6, ADR 0030): the same WitBootInfo the UEFI
 * loader builds, so that the kernel never learns which path loaded it. The entry (entry.S) runs here at EL1 with the
 * MMU on, the image's gigabytes and the device tree's mapped as normal memory. This layer maps all of the tree's
 * memory as normal memory, takes the memory map from the tree (its memory less the reservations, the tree, the image
 * and the initial ramdisk), the root task and the boot package from the initial ramdisk the image tool writes, and the
 * entropy seed from /chosen, without which, as on the UEFI path, it does not boot. */

#define GIB (1ULL << 30)
#define CHUNK (2ULL << 20)
#define LEVEL2_TABLES 8U
#define BLOCK_NORMAL 0x701ULL
#define BLOCK_DEVICE 0x0060000000000405ULL
#define TABLE 3ULL
#define PAGE 4096ULL

extern WitU64 wit_linux_relocations;

static WitBootInfo boot_info;
static WitMemoryRegion memory_regions[WIT_MAX_MEMORY_REGIONS];
static WitBootStorageExtent package_extents[WIT_MAX_STORAGE_EXTENTS];
static WitBootStorageExtent root_extents[WIT_MAX_ROOT_EXTENTS];
static WitU8 seed[WIT_BOOT_SEED_BYTES];
static WitLinuxTree tree_info;
__declspec(align(4096)) static WitU64 level0[512];
__declspec(align(4096)) static WitU64 level1[512];
__declspec(align(4096)) static WitU64 level2[LEVEL2_TABLES][512];

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static int overlaps(WitU64 base, WitU64 length, WitU64 other, WitU64 other_length)
{
    return base < other + other_length && other < base + length;
}

static int holds_memory(WitU64 base, WitU64 length)
{
    for (WitU32 i = 0; i < tree_info.MemoryCount; ++i) {
        if (overlaps(base, length, tree_info.Memory[i].Base, tree_info.Memory[i].Length)) {
            return 1;
        }
    }
    return 0;
}

static int all_memory(WitU64 base, WitU64 length)
{
    for (WitU32 i = 0; i < tree_info.MemoryCount; ++i) {
        if (tree_info.Memory[i].Base <= base &&
            base + length <= tree_info.Memory[i].Base + tree_info.Memory[i].Length) {
            return 1;
        }
    }
    return 0;
}

/* The second stage's identity map, in the kernel's geometry as the first stage's (entry.S): level 0's first entry is a
 * level-1 table, where a gigabyte wholly memory is one normal block, a gigabyte with some memory a table of 2 MiB
 * blocks, normal where they hold memory, and every other gigabyte device memory; then the switch. */
static void map_memory(void)
{
    WitU32 tables = 0;
    for (WitU64 i = 0; i < 512; ++i) {
        const WitU64 base = i * GIB;
        if (all_memory(base, GIB)) {
            level1[i] = base | BLOCK_NORMAL;
        } else if (holds_memory(base, GIB)) {
            require(tables < LEVEL2_TABLES, "Too many partly mapped gigabytes");
            for (WitU64 j = 0; j < 512; ++j) {
                const WitU64 chunk = base + j * CHUNK;
                level2[tables][j] = chunk | (holds_memory(chunk, CHUNK) ? BLOCK_NORMAL : BLOCK_DEVICE);
            }
            level1[i] = (WitU64)level2[tables] | TABLE;
            ++tables;
        } else {
            level1[i] = base | BLOCK_DEVICE;
        }
    }
    level0[0] = (WitU64)level1 | TABLE;
    wit_linux_switch_tables(level0);
}

static WitU64 page_down(WitU64 value)
{
    return value & ~(PAGE - 1);
}

static WitU64 page_up(WitU64 value)
{
    return (value + PAGE - 1) & ~(PAGE - 1);
}

static void add_region(WitU64 base, WitU64 end, WitU32 kind)
{
    if (end <= base) {
        return;
    }
    require(boot_info.MemoryRegionCount < WIT_MAX_MEMORY_REGIONS, "Too many memory regions");
    WitMemoryRegion *region = &memory_regions[boot_info.MemoryRegionCount++];
    region->Base = base;
    region->Length = end - base;
    region->Kind = kind;
    region->Reserved = 0;
}

/* The memory map: each memory range in whole pages, cut where the reserved ranges (in whole pages, sorted and merged
 * here) lie in it. */
static void build_memory_map(WitLinuxRange *reserved, WitU32 count)
{
    for (WitU32 i = 0; i < count; ++i) {
        reserved[i].Length = page_up(reserved[i].Base + reserved[i].Length) - page_down(reserved[i].Base);
        reserved[i].Base = page_down(reserved[i].Base);
    }
    for (WitU32 i = 1; i < count; ++i) {
        for (WitU32 j = i; j > 0 && reserved[j - 1].Base > reserved[j].Base; --j) {
            const WitLinuxRange swap = reserved[j];
            reserved[j] = reserved[j - 1];
            reserved[j - 1] = swap;
        }
    }
    boot_info.MemoryRegionCount = 0;
    for (WitU32 m = 0; m < tree_info.MemoryCount; ++m) {
        const WitU64 base = page_up(tree_info.Memory[m].Base);
        const WitU64 end = page_down(tree_info.Memory[m].Base + tree_info.Memory[m].Length);
        WitU64 at = base;
        for (WitU32 r = 0; r < count && at < end; ++r) {
            const WitU64 low = reserved[r].Base > at ? reserved[r].Base : at;
            const WitU64 high =
                reserved[r].Base + reserved[r].Length < end ? reserved[r].Base + reserved[r].Length : end;
            if (high <= at || low >= end) {
                continue;
            }
            add_region(at, low, WIT_MEMORY_USABLE);
            add_region(low, high, WIT_MEMORY_RESERVED);
            at = high;
        }
        add_region(at, end, WIT_MEMORY_USABLE);
    }
}

/* The root task and the package, each page-aligned in the initial ramdisk, as extents of a MiB and of two. */
static void take_initrd(const WitU8 *initrd, WitU64 bytes)
{
    const WitInitrdHeader *header = (const WitInitrdHeader *)initrd;
    require(bytes >= WIT_INITRD_HEADER_BYTES &&
            header->Magic == WIT_INITRD_MAGIC &&
            header->Version == WIT_INITRD_VERSION &&
            header->HeaderBytes == WIT_INITRD_HEADER_BYTES,
        "Invalid initial ramdisk");
    require(((WitU64)initrd & (PAGE - 1)) == 0, "Initial ramdisk not page-aligned");
    require(!(header->RootOffset & (PAGE - 1)) &&
            header->RootOffset >= WIT_INITRD_HEADER_BYTES &&
            header->RootBytes &&
            header->RootBytes <= (WitU64)WIT_MAX_ROOT_EXTENTS << 20 &&
            header->RootOffset <= bytes &&
            header->RootBytes <= bytes - header->RootOffset &&
            !(header->PackageOffset & (PAGE - 1)) &&
            header->PackageOffset >= WIT_INITRD_HEADER_BYTES &&
            header->PackageBytes &&
            header->PackageBytes <= WIT_PACKAGE_MAX_BYTES &&
            header->PackageOffset <= bytes &&
            header->PackageBytes <= bytes - header->PackageOffset,
        "Invalid initial ramdisk layout");
    WitU32 count = 0;
    for (WitU64 at = 0; at < header->PackageBytes; at += WIT_PACKAGE_EXTENT_BYTES) {
        require(count < WIT_MAX_STORAGE_EXTENTS, "Boot package too large");
        const WitU64 left = page_up(header->PackageBytes) - at;
        package_extents[count].Base = (WitU64)initrd + header->PackageOffset + at;
        package_extents[count].Length = left < WIT_PACKAGE_EXTENT_BYTES ? left : WIT_PACKAGE_EXTENT_BYTES;
        ++count;
    }
    boot_info.StorageExtents = package_extents;
    boot_info.StorageExtentCount = count;
    boot_info.StorageBytes = header->PackageBytes;
    boot_info.StorageReserved = 0;
    count = 0;
    for (WitU64 at = 0; at < header->RootBytes; at += 1ULL << 20) {
        const WitU64 left = page_up(header->RootBytes) - at;
        root_extents[count].Base = (WitU64)initrd + header->RootOffset + at;
        root_extents[count].Length = left < (1ULL << 20) ? left : 1ULL << 20;
        ++count;
    }
    boot_info.RootTaskExtents = root_extents;
    boot_info.RootTaskExtentCount = count;
    boot_info.RootTaskBytes = header->RootBytes;
    boot_info.RootTaskReserved = 0;
    wit_console_write("[BOOT] Initial ramdisk at ");
    wit_console_write_hex((WitU64)initrd);
    wit_console_write(": root task ");
    wit_console_write_u64(header->RootBytes);
    wit_console_write(" bytes, boot package ");
    wit_console_write_u64(header->PackageBytes);
    wit_console_write(" bytes\n");
    wit_console_write("[BOOT] Root task image read\n");
}

WIT_NORETURN void wit_linux_main(WitU64 tree, WitU64 level)
{
    WitLinuxRange reserved[WIT_LINUX_RANGES + 3];

    wit_platform_console_tree(tree); /* the console the tree names, before the first line (H7.2) */
    wit_console_initialize();
    wit_console_write("[BOOT] Linux ");
    wit_console_write(wit_arch_identity()->Name);
    wit_console_write(" entry at EL");
    wit_console_write_u64(level);
    wit_console_write("\n");
    require(wit_linux_relocations != ~0ULL, "Unsupported base relocation");
    wit_boot_describe_image(&boot_info);
    wit_linux_read_tree((const WitU8 *)tree, &tree_info);
    require(tree_info.MemoryCount > 0, "No memory in the device tree");
    wit_console_write("[BOOT] Device tree at ");
    wit_console_write_hex(tree);
    wit_console_write(", ");
    wit_console_write_u64(tree_info.Bytes);
    wit_console_write(" bytes\n");
    map_memory();

    require(tree_info.InitrdStart && tree_info.InitrdEnd > tree_info.InitrdStart, "No initial ramdisk");
    require(tree_info.SeedBytes >= WIT_BOOT_SEED_BYTES, "No entropy seed in the device tree");
    for (WitU32 i = 0; i < WIT_BOOT_SEED_BYTES; ++i) {
        seed[i] = tree_info.Seed[i];
    }
    boot_info.EntropySeed = seed;
    boot_info.EntropySize = WIT_BOOT_SEED_BYTES;
    boot_info.EntropyReserved = 0;
    wit_console_write("[BOOT] Cryptographic seed acquired\n");

    take_initrd((const WitU8 *)tree_info.InitrdStart, tree_info.InitrdEnd - tree_info.InitrdStart);
    WitU32 count = tree_info.ReservedCount;
    for (WitU32 i = 0; i < count; ++i) {
        reserved[i] = tree_info.Reserved[i];
    }
    reserved[count].Base = tree;
    reserved[count++].Length = tree_info.Bytes;
    reserved[count].Base = boot_info.ImageBase;
    reserved[count++].Length = boot_info.ImageSize;
    reserved[count].Base = tree_info.InitrdStart;
    reserved[count++].Length = tree_info.InitrdEnd - tree_info.InitrdStart;
    build_memory_map(reserved, count);

    boot_info.AcpiRsdp = 0;
    boot_info.DeviceTree = tree;
    boot_info.FramebufferBase = 0;
    boot_info.FramebufferBytes = 0;
    boot_info.FramebufferWidth = 0;
    boot_info.FramebufferHeight = 0;
    boot_info.FramebufferStride = 0;
    boot_info.FramebufferFormat = 0;
    boot_info.Magic = WIT_BOOT_MAGIC;
    boot_info.Version = WIT_BOOT_VERSION;
    boot_info.Size = sizeof(WitBootInfo);
    boot_info.Architecture = wit_arch_identity()->BootArchitecture;
    boot_info.MemoryRegions = memory_regions;
    boot_info.Flags = WIT_BOOT_SERVICES_EXITED; /* no firmware services are left running */
    wit_console_write("[BOOT] Boot contract from the device tree: ");
    wit_console_write_u64(boot_info.MemoryRegionCount);
    wit_console_write(" memory regions\n");
    wit_arch_disable_interrupts();
    wit_arch_enter(&boot_info);
}
