#ifndef WITOS_MEMORY_H
#define WITOS_MEMORY_H

#include "boot.h"

#define WIT_PAGE_SIZE 4096ULL
/* Temporary M1 bound: explicit failure if usable RAM exceeds 4 GiB.
 * Reserved firmware/MMIO ranges may extend beyond this limit. */
#define WIT_PHYSICAL_LIMIT 0x100000000ULL
#define WIT_PAGE_COUNT (WIT_PHYSICAL_LIMIT / WIT_PAGE_SIZE)
#define WIT_BITMAP_WORDS (WIT_PAGE_COUNT / 64ULL)

typedef struct WitPageAllocator {
    WitU64 Eligible[WIT_BITMAP_WORDS];
    WitU64 Allocated[WIT_BITMAP_WORDS];
    WitU64 TotalPages;
    WitU64 FreePages;
    WitU32 Cursor;
    WitU32 Initialized;
} WitPageAllocator;

int wit_memory_map_valid(const WitMemoryRegion *regions, WitU32 count);
int wit_pages_initialize(WitPageAllocator *allocator, const WitMemoryRegion *regions, WitU32 count);
int wit_page_allocate(WitPageAllocator *allocator, WitU64 *physical_address);
int wit_page_free(WitPageAllocator *allocator, WitU64 physical_address);
int wit_page_is_allocated(const WitPageAllocator *allocator, WitU64 physical_address);
WitU64 wit_pages_free_count(const WitPageAllocator *allocator);
void wit_memory_self_test(const WitBootInfo *boot, WitPageAllocator *allocator);

#endif
