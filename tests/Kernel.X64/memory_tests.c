#include "witos/memory.h"
#include "witos/platform.h"

static WitPageAllocator test_allocator;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static int page_in_usable_region(const WitBootInfo *boot, WitU64 address)
{
    for (WitU32 i = 0; i < boot->MemoryRegionCount; ++i) {
        const WitMemoryRegion *region = &boot->MemoryRegions[i];
        if (region->Kind == WIT_MEMORY_USABLE && address >= region->Base && address < region->Base + region->Length) {
            return 1;
        }
    }
    return 0;
}

void wit_memory_self_test(const WitBootInfo *boot, WitPageAllocator *allocator)
{
    WitU64 first = 0;
    WitU64 second = 0;
    const WitU64 before = wit_pages_free_count(allocator);
    WitU64 pages[4];
    WitU64 failed = 1;
    WitMemoryRegion fixture[3] = {{0x1000, 0x3000, WIT_MEMORY_USABLE, 0}, {0x4000, 0x2000, WIT_MEMORY_RESERVED, 0},
        {0x6000, 0x1000, WIT_MEMORY_USABLE, 0}};

    wit_console_write("[TEST-BEGIN] Memory.PhysicalPages\n");
    require(wit_page_allocate(allocator, &first), "First allocation failed");
    require(wit_page_allocate(allocator, &second), "Second allocation failed");
    require(first != second && first != 0 && second != 0 && (first & 4095) == 0 && (second & 4095) == 0,
        "Invalid allocated pages");
    require(page_in_usable_region(boot, first) && page_in_usable_region(boot, second),
        "Allocator returned reserved memory");
    require(wit_pages_free_count(allocator) == before - 2, "Allocation accounting failed");

    /* Both pages are live simultaneously. Verify every word to detect aliasing. */
    for (WitU32 i = 0; i < WIT_PAGE_SIZE / sizeof(WitU64); ++i) {
        ((volatile WitU64 *)first)[i] = 0x123456789ABCDEF0ULL ^ i;
        ((volatile WitU64 *)second)[i] = 0xFEDCBA9876543210ULL ^ i;
    }
    for (WitU32 i = 0; i < WIT_PAGE_SIZE / sizeof(WitU64); ++i) {
        require(((volatile WitU64 *)first)[i] == (0x123456789ABCDEF0ULL ^ i) &&
                ((volatile WitU64 *)second)[i] == (0xFEDCBA9876543210ULL ^ i),
            "Physical page contents aliased or corrupted");
    }
    require(!wit_page_free(allocator, first + 1), "Accepted unaligned free");
    require(!wit_page_free(allocator, 0), "Accepted page zero");
    require(!wit_page_free(allocator, (WitU64)boot & ~4095ULL), "Accepted reserved boot metadata");
    require(wit_pages_free_count(allocator) == before - 2, "Invalid free changed accounting");
    require(wit_page_free(allocator, first), "Free failed");
    require(!wit_page_free(allocator, first), "Accepted double free");
    require(wit_page_free(allocator, second), "Second free failed");
    require(wit_pages_free_count(allocator) == before, "Page accounting did not recover");
    wit_console_write("[TEST-PASS] Memory.PhysicalPages\n");

    wit_console_write("[TEST-BEGIN] Memory.Exhaustion\n");
    /* Synthetic addresses are bookkeeping fixtures and are never dereferenced. */
    require(wit_pages_initialize(&test_allocator, fixture, 3), "Fixture initialization failed");
    require(wit_pages_free_count(&test_allocator) == 4, "Fixture capacity wrong");
    for (WitU32 i = 0; i < 4; ++i) {
        require(wit_page_allocate(&test_allocator, &pages[i]), "Fixture allocation failed");
        require((pages[i] >= 0x1000 && pages[i] <= 0x3000) || pages[i] == 0x6000, "Fixture allocated a reserved hole");
        for (WitU32 j = 0; j < i; ++j) {
            require(pages[i] != pages[j], "Duplicate live allocation");
        }
    }
    require(!wit_page_allocate(&test_allocator, &failed) && failed == 0, "Exhaustion was not reported");
    require(wit_pages_free_count(&test_allocator) == 0, "Exhaustion accounting wrong");
    require(wit_page_free(&test_allocator, pages[1]), "Fixture free failed");
    require(wit_page_allocate(&test_allocator, &failed) && failed == pages[1], "Freed page was not reused");
    require(!wit_page_free(&test_allocator, 0x4000), "Reserved hole was freed");
    wit_console_write("[TEST-PASS] Memory.Exhaustion\n");

    wit_console_write("[TEST-BEGIN] Memory.InvalidMaps\n");
    fixture[1].Base = 0x2000;
    require(!wit_pages_initialize(&test_allocator, fixture, 3), "Overlapping map accepted");
    require(!wit_page_allocate(&test_allocator, &failed), "Invalid allocator stayed active");
    fixture[1].Base = 0x4000;
    fixture[0].Length = 0;
    require(!wit_pages_initialize(&test_allocator, fixture, 3), "Zero-length region accepted");
    fixture[0].Length = 0x3001;
    require(!wit_pages_initialize(&test_allocator, fixture, 3), "Unaligned region accepted");
    fixture[0].Base = ~0ULL - 4095;
    fixture[0].Length = 0x2000;
    require(!wit_pages_initialize(&test_allocator, fixture, 3), "Overflowing region accepted");
    fixture[0].Base = WIT_PHYSICAL_LIMIT;
    fixture[0].Length = 0x1000;
    require(!wit_pages_initialize(&test_allocator, fixture, 3), "Unsupported RAM silently accepted");
    wit_console_write("[TEST-PASS] Memory.InvalidMaps\n");
}
