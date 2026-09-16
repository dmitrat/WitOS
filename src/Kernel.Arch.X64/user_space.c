#include "user.h"
#include "witos/platform.h"

unsigned __int64 __readcr3(void);
void __invlpg(void *);
#pragma intrinsic(__readcr3, __invlpg)

#define PAGE_PRESENT 1ULL
#define PAGE_WRITE 2ULL
#define PAGE_USER 4ULL
/* Software bit: a committed leaf retains its frame even with no access. */
#define PAGE_OWNED 0x200ULL
#define PAGE_NX (1ULL << 63)
#define PAGE_ADDRESS 0x000FFFFFFFFFF000ULL

static WitU64 allocate(WitUserSpace *space, WitU64 address)
{
    WitU64 page = 0;
    if (space->OwnedCount == WIT_USER_PAGE_CAPACITY ||
        !wit_page_allocate(space->Allocator, &page)) return 0;
    space->OwnedPages[space->OwnedCount] = page;
    space->OwnedVirtual[space->OwnedCount++] = address;
    for (WitU32 i = 0; i < 512; ++i) ((WitU64 *)page)[i] = 0;
    return page;
}

static void free_owned(WitUserSpace *space, WitU64 page)
{
    for (WitU32 i = 0; i < space->OwnedCount; ++i) {
        if (space->OwnedPages[i] != page) continue;
        if (!wit_page_free(space->Allocator, page)) wit_panic("User page ownership corrupted");
        --space->OwnedCount;
        space->OwnedPages[i] = space->OwnedPages[space->OwnedCount];
        space->OwnedVirtual[i] = space->OwnedVirtual[space->OwnedCount];
        return;
    }
    wit_panic("Freeing unowned user page");
}

static void invalidate(const WitUserSpace *space, WitU64 address)
{
    /* No PCID, global user mappings or second CPU. Inactive CR3s are flushed on entry. */
    if ((__readcr3() & PAGE_ADDRESS) == space->Root) __invlpg((void *)address);
}

static WitU64 *leaf(WitUserSpace *space, WitU64 address, int create)
{
    const WitU32 shifts[3] = { 39, 30, 21 };
    WitU64 *table = (WitU64 *)space->Root;
    if (!table) return 0;
    for (WitU32 level = 0; level < 3; ++level) {
        WitU64 *entry = &table[(address >> shifts[level]) & 511];
        if (!(*entry & PAGE_PRESENT)) {
            WitU64 page;
            if (!create || *entry != 0) return 0;
            page = allocate(space, 0);
            if (!page) return 0;
            *entry = page | PAGE_PRESENT | PAGE_WRITE | PAGE_USER;
        }
        if ((*entry & (PAGE_PRESENT | PAGE_USER | 0x80)) != (PAGE_PRESENT | PAGE_USER)) return 0;
        table = (WitU64 *)(*entry & PAGE_ADDRESS);
    }
    return &table[(address >> 12) & 511];
}

/* Removes partially built paths after allocation failure. Never frees root or
 * visits the shared supervisor branch. Call only for validated user addresses. */
static void prune(WitUserSpace *space, WitU64 address)
{
    const WitU32 shifts[3] = { 39, 30, 21 };
    WitU64 *tables[4], *parents[3];
    WitU32 depth = 0;
    tables[0] = (WitU64 *)space->Root;
    for (; depth < 3; ++depth) {
        WitU64 *entry = &tables[depth][(address >> shifts[depth]) & 511];
        if ((*entry & (PAGE_PRESENT | PAGE_USER | 0x80)) != (PAGE_PRESENT | PAGE_USER)) break;
        parents[depth] = entry;
        tables[depth + 1] = (WitU64 *)(*entry & PAGE_ADDRESS);
    }
    while (depth) {
        for (WitU32 i = 0; i < 512; ++i) if (tables[depth][i] != 0) return;
        *parents[depth - 1] = 0;
        invalidate(space, address);
        free_owned(space, (WitU64)tables[depth--]);
    }
}

static WitU64 protection_flags(WitU64 protection)
{
    return PAGE_OWNED | PAGE_USER | PAGE_NX |
        (protection & WIT_MEMORY_READ ? PAGE_PRESENT : 0) |
        (protection & WIT_MEMORY_WRITE ? PAGE_WRITE : 0);
}

static int map_page(WitUserSpace *space, WitU64 address, WitU64 flags)
{
    WitU64 *entry = leaf(space, address, 1);
    WitU64 page;
    if (!entry) { prune(space, address); return 0; }
    if (*entry != 0) return 0;
    page = allocate(space, address);
    if (!page) { prune(space, address); return 0; }
    *entry = page | flags;
    invalidate(space, address);
    return 1;
}

static void unmap_page(WitUserSpace *space, WitU64 address)
{
    WitU64 *entry = leaf(space, address, 0);
    WitU64 page;
    if (!entry || !(*entry & PAGE_OWNED)) wit_panic("Missing owned user mapping");
    page = *entry & PAGE_ADDRESS;
    *entry = 0;
    invalidate(space, address);
    free_owned(space, page);
    prune(space, address);
}

static WitU64 address_limit(WitU64 address)
{
    if (address >= WIT_USER_BASE && address < WIT_USER_LIMIT) return WIT_USER_LIMIT;
    if (address >= WIT_USER_MEMORY_BASE && address < WIT_USER_MEMORY_LIMIT) return WIT_USER_MEMORY_LIMIT;
    return 0;
}

int wit_user_space_create(WitUserSpace *space, WitPageAllocator *allocator)
{
    const WitU64 shared = ((WitU64 *)wit_virtual_kernel_root())[0];
    space->Allocator = allocator;
    space->OwnedCount = 0;
    space->Root = 0;
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i)
        space->Reservations[i].Size = 0;
    if (!(shared & PAGE_PRESENT) || (shared & PAGE_USER)) return 0;
    space->Root = allocate(space, 0);
    if (space->Root == 0) return 0;
    /* Slots 1 (fixed image) and 2 (dynamic memory) are private. */
    ((WitU64 *)space->Root)[0] = shared;
    return 1;
}

int wit_user_space_map(WitUserSpace *space, WitU64 address, int writable, int executable)
{
    if (!space->Root || address < WIT_USER_BASE || address >= WIT_USER_LIMIT ||
        (address & 4095) || (writable && executable)) return 0;
    return map_page(space, address, PAGE_OWNED | PAGE_PRESENT | PAGE_USER |
        (writable ? PAGE_WRITE : 0) | (executable ? 0 : PAGE_NX));
}

WitU64 wit_user_space_physical(const WitUserSpace *space, WitU64 address, int write, int execute)
{
    const WitU32 shifts[4] = { 39, 30, 21, 12 };
    const WitU64 required = PAGE_PRESENT | PAGE_USER | (write ? PAGE_WRITE : 0);
    WitU64 *table = (WitU64 *)space->Root;
    if (!space->Root || !address_limit(address)) return 0;
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
    const WitU64 limit = address_limit(address);
    if (size == 0) return 1;
    if (!limit || size > limit - address) return 0;
    /* Validate everything before output; operations are serialized with IF clear. */
    for (WitU64 p = address & ~4095ULL; p <= ((address + size - 1) & ~4095ULL); p += 4096)
        if (!wit_user_space_physical(space, p, 0, 0)) return 0;
    for (WitU32 i = 0; i < size; ++i)
        buffer[i] = *(const WitU8 *)wit_user_space_physical(space, address + i, 0, 0);
    return 1;
}

static int valid_protection(WitU64 protection)
{
    return protection == WIT_MEMORY_NONE || protection == WIT_MEMORY_READ ||
        protection == (WIT_MEMORY_READ | WIT_MEMORY_WRITE);
}

static WitU64 reserved_range(const WitUserSpace *space, WitU64 address, WitU64 size)
{
    if (!size || (address & 4095) || (size & 4095)) return WIT_STATUS_INVALID_ARGUMENT;
    if (address < WIT_USER_MEMORY_BASE || address >= WIT_USER_MEMORY_LIMIT ||
        size > WIT_USER_MEMORY_LIMIT - address) return WIT_STATUS_BAD_ADDRESS;
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
        const WitUserReservation *r = &space->Reservations[i];
        if (r->Size && address >= r->Base && address - r->Base < r->Size &&
            size <= r->Size - (address - r->Base)) return WIT_STATUS_OK;
    }
    return WIT_STATUS_NOT_RESERVED;
}

WitU64 wit_user_memory_reserve(WitUserSpace *space, WitU64 size, WitU64 alignment, WitU64 *result)
{
    const WitU64 arena_size = WIT_USER_MEMORY_LIMIT - WIT_USER_MEMORY_BASE;
    WitU64 candidate;
    WitU32 slot = WIT_USER_RESERVATION_CAPACITY;
    *result = 0;
    if (!space->Root || !size || (size & 4095) || alignment < 4096 ||
        alignment > arena_size || (alignment & (alignment - 1))) return WIT_STATUS_INVALID_ARGUMENT;
    if (size > arena_size) return WIT_STATUS_NO_MEMORY;
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i)
        if (!space->Reservations[i].Size) { slot = i; break; }
    if (slot == WIT_USER_RESERVATION_CAPACITY) return WIT_STATUS_NO_MEMORY;
    candidate = (WIT_USER_MEMORY_BASE + alignment - 1) & ~(alignment - 1);
    for (;;) {
        int overlap = 0;
        if (candidate >= WIT_USER_MEMORY_LIMIT || size > WIT_USER_MEMORY_LIMIT - candidate)
            return WIT_STATUS_NO_MEMORY;
        for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
            const WitUserReservation *r = &space->Reservations[i];
            if (!r->Size || candidate >= r->Base + r->Size || candidate + size <= r->Base) continue;
            candidate = (r->Base + r->Size + alignment - 1) & ~(alignment - 1);
            overlap = 1;
            break;
        }
        if (!overlap) break;
    }
    space->Reservations[slot].Base = candidate;
    space->Reservations[slot].Size = size;
    *result = candidate;
    return WIT_STATUS_OK;
}

WitU64 wit_user_memory_commit(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection)
{
    WitU64 added[WIT_USER_PAGE_CAPACITY];
    WitU32 count = 0;
    WitU64 status = reserved_range(space, address, size);
    if (status != WIT_STATUS_OK) return status;
    if (!valid_protection(protection)) return WIT_STATUS_INVALID_ARGUMENT;
    /* Bound work as well as memory when called with hostile lengths. */
    if (size / 4096 > WIT_USER_PAGE_CAPACITY) return WIT_STATUS_NO_MEMORY;
    for (WitU64 p = address; p < address + size; p += 4096) {
        WitU64 *entry = leaf(space, p, 0);
        if (entry && (*entry & PAGE_OWNED)) continue; /* Preserve data and protection. */
        if (!map_page(space, p, protection_flags(protection))) {
            while (count) unmap_page(space, added[--count]);
            return WIT_STATUS_NO_MEMORY;
        }
        added[count++] = p;
    }
    return WIT_STATUS_OK;
}

static void decommit_range(WitUserSpace *space, WitU64 address, WitU64 size)
{
    /* Walk committed frames, not potentially billions of reserved pages.
     * Unmapping may compact ownership records for both frames and tables. */
    WitU32 i = 0;
    while (i < space->OwnedCount) {
        const WitU64 p = space->OwnedVirtual[i];
        if (p >= address && p - address < size) {
            unmap_page(space, p);
            i = 0;
        } else ++i;
    }
}

WitU64 wit_user_memory_decommit(WitUserSpace *space, WitU64 address, WitU64 size)
{
    const WitU64 status = reserved_range(space, address, size);
    if (status != WIT_STATUS_OK) return status;
    decommit_range(space, address, size);
    return WIT_STATUS_OK;
}

WitU64 wit_user_memory_protect(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection)
{
    WitU64 status = reserved_range(space, address, size);
    if (status != WIT_STATUS_OK) return status;
    if (!valid_protection(protection)) return WIT_STATUS_INVALID_ARGUMENT;
    if (size / 4096 > WIT_USER_PAGE_CAPACITY) return WIT_STATUS_NOT_COMMITTED;
    for (WitU64 p = address; p < address + size; p += 4096) {
        const WitU64 *entry = leaf(space, p, 0);
        if (!entry || !(*entry & PAGE_OWNED)) return WIT_STATUS_NOT_COMMITTED;
    }
    for (WitU64 p = address; p < address + size; p += 4096) {
        WitU64 *entry = leaf(space, p, 0);
        *entry = (*entry & PAGE_ADDRESS) | protection_flags(protection);
        invalidate(space, p);
    }
    return WIT_STATUS_OK;
}

WitU64 wit_user_memory_release(WitUserSpace *space, WitU64 address)
{
    if (address & 4095) return WIT_STATUS_INVALID_ARGUMENT;
    if (address < WIT_USER_MEMORY_BASE || address >= WIT_USER_MEMORY_LIMIT) return WIT_STATUS_BAD_ADDRESS;
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
        WitUserReservation *r = &space->Reservations[i];
        if (!r->Size || r->Base != address) continue;
        decommit_range(space, r->Base, r->Size);
        r->Size = 0;
        return WIT_STATUS_OK;
    }
    return WIT_STATUS_NOT_RESERVED;
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
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) space->Reservations[i].Size = 0;
}
