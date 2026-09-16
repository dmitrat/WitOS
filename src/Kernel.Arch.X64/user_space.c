#include "user.h"
#include "witos/platform.h"

unsigned __int64 __readcr3(void);
#pragma intrinsic(__readcr3)

#define PAGE_PRESENT 1ULL
#define PAGE_WRITE 2ULL
#define PAGE_USER 4ULL
#define PAGE_NX (1ULL << 63)
#define PAGE_ADDRESS 0x000FFFFFFFFFF000ULL

static WitU64 allocate(WitUserSpace *space)
{
    WitU64 page = 0;
    if (space->OwnedCount == WIT_USER_PAGE_CAPACITY ||
        !wit_page_allocate(space->Allocator, &page)) return 0;
    space->OwnedPages[space->OwnedCount++] = page;
    for (WitU32 i = 0; i < 512; ++i) ((WitU64 *)page)[i] = 0;
    return page;
}

int wit_user_space_create(WitUserSpace *space, WitPageAllocator *allocator)
{
    const WitU64 shared = ((WitU64 *)wit_virtual_kernel_root())[0];
    space->Allocator = allocator;
    space->OwnedCount = 0;
    space->Root = 0;
    if (!(shared & PAGE_PRESENT) || (shared & PAGE_USER)) return 0;
    space->Root = allocate(space);
    if (space->Root == 0) return 0;
    /* Share only the supervisor branch for the kernel's below-4-GiB mappings.
     * The user's PML4 slot 1 is always private; kernel scratch mappings are not copied. */
    ((WitU64 *)space->Root)[0] = shared;
    return 1;
}

int wit_user_space_map(WitUserSpace *space, WitU64 address, int writable, int executable)
{
    const WitU32 shifts[3] = { 39, 30, 21 };
    WitU64 *table = (WitU64 *)space->Root;
    WitU64 page;
    if (!space->Root || address < WIT_USER_BASE || address >= WIT_USER_LIMIT ||
        (address & 4095) || (writable && executable)) return 0;
    for (WitU32 level = 0; level < 3; ++level) {
        WitU64 *entry = &table[(address >> shifts[level]) & 511];
        if (!(*entry & PAGE_PRESENT)) {
            page = allocate(space);
            if (page == 0) return 0;
            *entry = page | PAGE_PRESENT | PAGE_WRITE | PAGE_USER;
        }
        if ((*entry & (PAGE_PRESENT | PAGE_USER | 0x80)) != (PAGE_PRESENT | PAGE_USER)) return 0;
        table = (WitU64 *)(*entry & PAGE_ADDRESS);
    }
    if (table[(address >> 12) & 511] & PAGE_PRESENT) return 0;
    page = allocate(space);
    if (page == 0) return 0;
    table[(address >> 12) & 511] = page | PAGE_PRESENT | PAGE_USER |
        (writable ? PAGE_WRITE : 0) | (executable ? 0 : PAGE_NX);
    return 1;
}

WitU64 wit_user_space_physical(const WitUserSpace *space, WitU64 address, int write, int execute)
{
    const WitU32 shifts[4] = { 39, 30, 21, 12 };
    const WitU64 required = PAGE_PRESENT | PAGE_USER | (write ? PAGE_WRITE : 0);
    WitU64 *table = (WitU64 *)space->Root;
    if (!space->Root || address < WIT_USER_BASE || address >= WIT_USER_LIMIT) return 0;
    for (WitU32 level = 0; level < 4; ++level) {
        const WitU64 entry = table[(address >> shifts[level]) & 511];
        if ((entry & required) != required || (execute && (entry & PAGE_NX))) return 0;
        if (level == 3) return (entry & PAGE_ADDRESS) | (address & 4095);
        if (entry & 0x80) return 0;
        table = (WitU64 *)(entry & PAGE_ADDRESS);
    }
    return 0;
}

int wit_user_copy_from(const WitUserSpace *space, WitU64 address, WitU8 *buffer, WitU32 size)
{
    if (size == 0) return 1;
    if (address < WIT_USER_BASE || address >= WIT_USER_LIMIT ||
        size > WIT_USER_LIMIT - address) return 0;
    /* Validate the entire range before any output. Mapping changes cannot race:
     * one CPU, one user thread, interrupt gate entered with IF cleared. */
    for (WitU64 p = address & ~4095ULL; p <= ((address + size - 1) & ~4095ULL); p += 4096)
        if (!wit_user_space_physical(space, p, 0, 0)) return 0;
    for (WitU32 i = 0; i < size; ++i)
        buffer[i] = *(const WitU8 *)wit_user_space_physical(space, address + i, 0, 0);
    return 1;
}

void wit_user_space_destroy(WitUserSpace *space)
{
    if (space->Root && (__readcr3() & PAGE_ADDRESS) == space->Root)
        wit_panic("Destroying active user address space");
    while (space->OwnedCount != 0) {
        const WitU64 page = space->OwnedPages[--space->OwnedCount];
        if (!wit_page_free(space->Allocator, page)) wit_panic("User page ownership corrupted");
    }
    space->Root = 0;
}
