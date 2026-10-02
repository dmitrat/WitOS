#include "witos/memory.h"

int wit_memory_map_valid(const WitMemoryRegion *regions, WitU32 count)
{
    if (regions == 0 || count == 0 || count > WIT_MAX_MEMORY_REGIONS) {
        return 0;
    }
    for (WitU32 i = 0; i < count; ++i) {
        const WitMemoryRegion *region = &regions[i];
        if (region->Length == 0 ||
            region->Base > ~0ULL - region->Length ||
            (region->Base & (WIT_PAGE_SIZE - 1)) != 0 ||
            (region->Length & (WIT_PAGE_SIZE - 1)) != 0 ||
            region->Kind > WIT_MEMORY_USABLE ||
            region->Reserved != 0) {
            return 0;
        }
    }
    /* Firmware maps need not be sorted. Reject overlap before allocating. */
    for (WitU32 i = 0; i < count; ++i) {
        for (WitU32 j = i + 1; j < count; ++j) {
            if (regions[i].Base < regions[j].Base + regions[j].Length &&
                regions[j].Base < regions[i].Base + regions[i].Length) {
                return 0;
            }
        }
    }
    return 1;
}

int wit_pages_initialize(WitPageAllocator *allocator, const WitMemoryRegion *regions, WitU32 count)
{
    if (allocator == 0) {
        return 0;
    }
    allocator->Initialized = 0;
    allocator->TotalPages = 0;
    allocator->FreePages = 0;
    allocator->Cursor = 0;
    if (!wit_memory_map_valid(regions, count)) {
        return 0;
    }
    for (WitU32 i = 0; i < count; ++i) {
        if (regions[i].Kind == WIT_MEMORY_USABLE && regions[i].Base + regions[i].Length > WIT_PHYSICAL_LIMIT) {
            return 0;
        }
    }
    for (WitU32 word = 0; word < WIT_BITMAP_WORDS; ++word) {
        allocator->Eligible[word] = 0;
        allocator->Allocated[word] = 0;
    }
    for (WitU32 i = 0; i < count; ++i) {
        if (regions[i].Kind == WIT_MEMORY_USABLE) {
            const WitU64 first = regions[i].Base / WIT_PAGE_SIZE;
            const WitU64 end = (regions[i].Base + regions[i].Length) / WIT_PAGE_SIZE;
            for (WitU64 page = first; page < end; ++page) {
                if (page != 0) { /* Physical page zero is never handed out. */
                    allocator->Eligible[page / 64] |= 1ULL << (page % 64);
                    ++allocator->TotalPages;
                }
            }
        }
    }
    allocator->FreePages = allocator->TotalPages;
    allocator->Initialized = 1;
    return 1;
}

int wit_page_allocate(WitPageAllocator *allocator, WitU64 *physical_address)
{
    if (physical_address == 0) {
        return 0;
    }
    *physical_address = 0;
    if (allocator == 0 || !allocator->Initialized || allocator->FreePages == 0) {
        return 0;
    }

    /* Scan from the last word, wrapping once. No allocator-owned pointers
     * are stored in free physical pages. Firmware mappings stay untouched. */
    for (WitU32 scanned = 0; scanned < WIT_BITMAP_WORDS; ++scanned) {
        const WitU32 word = (allocator->Cursor + scanned) % (WitU32)WIT_BITMAP_WORDS;
        const WitU64 available = allocator->Eligible[word] & ~allocator->Allocated[word];
        if (available != 0) {
            WitU32 bit = 0;
            while ((available & (1ULL << bit)) == 0) {
                ++bit;
            }
            allocator->Allocated[word] |= 1ULL << bit;
            allocator->Cursor = word;
            --allocator->FreePages;
            *physical_address = ((WitU64)word * 64 + bit) * WIT_PAGE_SIZE;
            return 1;
        }
    }
    return 0;
}

int wit_page_free(WitPageAllocator *allocator, WitU64 physical_address)
{
    WitU64 page;
    WitU64 mask;
    if (allocator == 0 ||
        !allocator->Initialized ||
        physical_address == 0 ||
        physical_address >= WIT_PHYSICAL_LIMIT ||
        (physical_address & (WIT_PAGE_SIZE - 1)) != 0) {
        return 0;
    }
    page = physical_address / WIT_PAGE_SIZE;
    mask = 1ULL << (page % 64);
    if ((allocator->Eligible[page / 64] & mask) == 0 || (allocator->Allocated[page / 64] & mask) == 0) {
        return 0;
    }
    allocator->Allocated[page / 64] &= ~mask;
    ++allocator->FreePages;
    allocator->Cursor = (WitU32)(page / 64);
    return 1;
}

int wit_page_is_allocated(const WitPageAllocator *allocator, WitU64 address)
{
    WitU64 page;
    WitU64 mask;
    if (allocator == 0 ||
        !allocator->Initialized ||
        address == 0 ||
        address >= WIT_PHYSICAL_LIMIT ||
        (address & 4095) != 0) {
        return 0;
    }
    page = address / WIT_PAGE_SIZE;
    mask = 1ULL << (page % 64);
    return (allocator->Eligible[page / 64] & mask) != 0 && (allocator->Allocated[page / 64] & mask) != 0;
}

WitU64 wit_pages_free_count(const WitPageAllocator *allocator)
{
    return allocator != 0 && allocator->Initialized ? allocator->FreePages : 0;
}
