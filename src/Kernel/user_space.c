#include "user.h"
#include "witos/platform.h"

/* User address-space policy: ownership accounting, reservations, commitments, code views and user copies.
 * Entry encoding, table walks and translation caches belong to the architecture (witos/arch.h). */

#if defined(WITOS_TEST_RUNTIME_BOOT)
/* The runtime acceptance prints the last user memory operations after an unexpected runtime failure (D1). */
#define JOURNAL_CAPACITY 256U
static WitMemoryJournalEntry journal_entries[JOURNAL_CAPACITY];
static WitU64 journal_count;

static WitU64 journal(WitU32 kind, WitU64 address, WitU64 size, WitU64 status)
{
    WitMemoryJournalEntry *entry = &journal_entries[journal_count % JOURNAL_CAPACITY];
    entry->Sequence = ++journal_count;
    entry->Kind = kind;
    entry->Status = (WitU32)status;
    entry->Address = address;
    entry->Size = size;
    return status;
}

const WitMemoryJournalEntry *wit_user_memory_journal(WitU64 *count, WitU32 *capacity)
{
    *count = journal_count;
    *capacity = JOURNAL_CAPACITY;
    return journal_entries;
}
#else
#define journal(kind, address, size, status) (status)
#endif

static WitU64 allocate(WitUserSpace *space, WitU64 address)
{
    WitU64 page = 0;
    if (space->OwnedCount >= space->PageLimit || !wit_page_allocate(space->Allocator, &page)) {
        return 0;
    }
    space->OwnedPages[space->OwnedCount] = page;
    space->OwnedVirtual[space->OwnedCount++] = address;
    for (WitU32 i = 0; i < 512; ++i) {
        ((WitU64 *)page)[i] = 0;
    }
    return page;
}

static void free_owned(WitUserSpace *space, WitU64 page)
{
    for (WitU32 i = 0; i < space->OwnedCount; ++i) {
        if (space->OwnedPages[i] != page) {
            continue;
        }
        if (!wit_page_free(space->Allocator, page)) {
            wit_panic("User page ownership corrupted");
        }
        --space->OwnedCount;
        space->OwnedPages[i] = space->OwnedPages[space->OwnedCount];
        space->OwnedVirtual[i] = space->OwnedVirtual[space->OwnedCount];
        return;
    }
    wit_panic("Freeing unowned user page");
}

WitU64 wit_user_space_take_table(WitUserSpace *space)
{
    return allocate(space, 0);
}

void wit_user_space_release_table(WitUserSpace *space, WitU64 page)
{
    free_owned(space, page);
}

static void invalidate(const WitUserSpace *space, WitU64 address)
{
    wit_arch_page_invalidate(space, address);
}

static WitU32 entry_flags(WitU64 entry)
{
    return wit_arch_page_entry_flags(entry);
}

static WitU64 entry_physical(WitU64 entry)
{
    return wit_arch_page_entry_physical(entry);
}

static WitU64 *leaf(WitUserSpace *space, WitU64 address, int create)
{
    return wit_arch_page_entry(space, address, create);
}

static void prune(WitUserSpace *space, WitU64 address)
{
    wit_arch_page_prune(space, address);
}

static WitU32 protection_flags(WitU64 protection)
{
    return WIT_PAGE_OWNED |
        ((protection & WIT_CODE_EXECUTE) ? WIT_PAGE_EXECUTE : 0) |
        (protection & WIT_MEMORY_READ ? WIT_PAGE_READ : 0) |
        (protection & WIT_MEMORY_WRITE ? WIT_PAGE_WRITE : 0);
}

static int map_page(WitUserSpace *space, WitU64 address, WitU32 flags)
{
    WitU64 *entry = leaf(space, address, 1);
    WitU64 page;
    if (!entry) {
        prune(space, address);
        return 0;
    }
    if (*entry != 0) {
        return 0;
    }
    page = allocate(space, address);
    if (!page) {
        prune(space, address);
        return 0;
    }
    *entry = wit_arch_page_entry_make(page, flags);
    invalidate(space, address);
    return 1;
}

static int overlaps(WitU64 base, WitU64 size, WitU64 other, WitU64 length)
{
    return size && length && base < other + length && other < base + size;
}

static int source_view(const WitUserSpace *space, WitU64 address, WitU64 size)
{
    for (WitU32 i = 0; i < WIT_CODE_VIEW_CAPACITY; ++i) {
        const WitCodeView *v = &space->CodeViews[i];
        if (overlaps(address, size, v->Source, v->Size)) {
            return 1;
        }
    }
    return 0;
}

static int destination_view(const WitUserSpace *space, WitU64 address, WitU64 size)
{
    for (WitU32 i = 0; i < WIT_CODE_VIEW_CAPACITY; ++i) {
        const WitCodeView *v = &space->CodeViews[i];
        if (overlaps(address, size, v->Destination, v->Size)) {
            return 1;
        }
    }
    return 0;
}

static int map_alias_page(WitUserSpace *space, WitU64 destination, WitU64 physical, WitU64 protection)
{
    if (space->AliasCount >= space->PageLimit) {
        return 0;
    }
    WitU64 *output = leaf(space, destination, 1);
    if (!output) {
        prune(space, destination);
        return 0;
    }
    if (*output) {
        return 0;
    }
    *output = wit_arch_page_entry_make(physical, (protection_flags(protection) & ~WIT_PAGE_OWNED) | WIT_PAGE_ALIAS);
    space->AliasVirtual[space->AliasCount] = destination;
    space->AliasPhysical[space->AliasCount++] = physical;
    invalidate(space, destination);
    return 1;
}

static int aliased(const WitUserSpace *space, WitU64 physical)
{
    for (WitU32 i = 0; i < space->AliasCount; ++i) {
        if (space->AliasPhysical[i] == physical) {
            return 1;
        }
    }
    return 0;
}

static int aliased_range(const WitUserSpace *space, WitU64 address, WitU64 size)
{
    for (WitU32 i = 0; i < space->OwnedCount; ++i) {
        const WitU64 p = space->OwnedVirtual[i];
        if (p >= address && p - address < size && aliased(space, space->OwnedPages[i])) {
            return 1;
        }
    }
    return 0;
}

static void unmap_page(WitUserSpace *space, WitU64 address)
{
    WitU64 *entry = leaf(space, address, 0);
    if (!entry || !(entry_flags(*entry) & (WIT_PAGE_OWNED | WIT_PAGE_ALIAS))) {
        wit_panic("Missing owned user mapping");
    }
    const WitU64 page = entry_physical(*entry);
    const int alias = (entry_flags(*entry) & WIT_PAGE_ALIAS) != 0;
    if (!alias && aliased(space, page)) {
        wit_panic("Freeing backing with live code aliases");
    }
    *entry = 0;
    invalidate(space, address);
    if (alias) {
        WitU32 i = 0;
        while (i < space->AliasCount && space->AliasVirtual[i] != address) {
            ++i;
        }
        if (i == space->AliasCount || space->AliasPhysical[i] != page) {
            wit_panic("Code alias ownership lost");
        }
        --space->AliasCount;
        space->AliasVirtual[i] = space->AliasVirtual[space->AliasCount];
        space->AliasPhysical[i] = space->AliasPhysical[space->AliasCount];
    } else {
        free_owned(space, page);
    }
    prune(space, address);
}

static int library_range(const WitUserSpace *space, WitU64 address, WitU64 bytes)
{
    if (!bytes) {
        return 0;
    }
    for (WitU32 i = 0; i <= WIT_LIBRARY_CAPACITY; ++i) {
        const WitVirtualRange *range = &space->LibraryRanges[i];
        if (range->Size &&
            address < range->Base + range->Size &&
            (address >= range->Base || bytes > range->Base - address)) {
            return 1;
        }
    }
    return 0;
}

static WitU64 dynamic_limit(WitU64 address)
{
    if (address >= WIT_USER_MEMORY_BASE && address < WIT_USER_MEMORY_LIMIT) {
        return WIT_USER_MEMORY_LIMIT;
    }
    if (address >= WIT_USER_CODE_BASE && address < WIT_USER_CODE_LIMIT) {
        return WIT_USER_CODE_LIMIT;
    }
    return 0;
}

static WitU64 address_limit(const WitUserSpace *space, WitU64 address)
{
    if (address >= WIT_USER_BASE && address < space->FixedLimit) {
        return space->FixedLimit;
    }
    return dynamic_limit(address);
}

int wit_user_space_create_profile(WitUserSpace *space, WitPageAllocator *allocator, int full)
{
    space->Allocator = allocator;
    space->PageLimit = full ? WIT_RUNTIME_PAGE_CAPACITY : WIT_USER_PAGE_CAPACITY;
    space->ReservationLimit = full ? WIT_RUNTIME_RESERVATION_CAPACITY : WIT_USER_RESERVATION_CAPACITY;
    space->FixedLimit = full ? WIT_RUNTIME_USER_LIMIT : WIT_USER_LIMIT;
    space->OwnedCount = 0;
    space->AliasCount = 0;
    for (WitU32 i = 0; i <= WIT_LIBRARY_CAPACITY; ++i) {
        space->LibraryRanges[i] = (WitVirtualRange){0};
    }
    for (WitU32 i = 0; i < WIT_CODE_VIEW_CAPACITY; ++i) {
        space->CodeViews[i] = (WitCodeView){0};
    }
    space->Root = 0;
    for (WitU32 i = 0; i < WIT_RUNTIME_RESERVATION_CAPACITY; ++i) {
        space->Reservations[i].Size = 0;
        space->MappedObjects[i] = 0;
        space->MappedRights[i] = 0;
    }
    if (!wit_arch_space_kernel_ready()) {
        return 0;
    }
    space->Root = allocate(space, 0);
    if (space->Root == 0) {
        return 0;
    }
    wit_arch_space_install_kernel(space->Root);
    return 1;
}

int wit_user_space_create(WitUserSpace *space, WitPageAllocator *allocator)
{
    return wit_user_space_create_profile(space, allocator, 0);
}

int wit_user_space_map(WitUserSpace *space, WitU64 address, int writable, int executable)
{
    if (!space->Root ||
        address < WIT_USER_BASE ||
        address >= space->FixedLimit ||
        (address & 4095) ||
        (writable && executable)) {
        return 0;
    }
    return map_page(space, address,
        WIT_PAGE_OWNED | WIT_PAGE_READ | (writable ? WIT_PAGE_WRITE : 0) | (executable ? WIT_PAGE_EXECUTE : 0));
}

int wit_user_space_unmap_fixed(WitUserSpace *space, WitU64 address)
{
    const WitU64 *entry;
    if (!space->Root || address < WIT_USER_BASE || address >= space->FixedLimit || (address & 4095)) {
        return 0;
    }
    entry = leaf(space, address, 0);
    if (!entry || !(entry_flags(*entry) & WIT_PAGE_OWNED)) {
        return 0;
    }
    unmap_page(space, address);
    return 1;
}

WitU64 wit_user_space_physical(const WitUserSpace *space, WitU64 address, int write, int execute)
{
    if (!space->Root || !address_limit(space, address)) {
        return 0;
    }
    return wit_arch_page_translate(space->Root, address, write, execute);
}

int wit_user_buffer_readable(const WitUserSpace *space, WitU64 address, WitU32 size)
{
    const WitU64 limit = address_limit(space, address);
    if (size == 0) {
        return 1;
    }
    if (!limit || size > limit - address) {
        return 0;
    }
    /* Validate everything before output; operations are serialized with IF clear. */
    for (WitU64 p = address & ~4095ULL; p <= ((address + size - 1) & ~4095ULL); p += 4096) {
        if (!wit_user_space_physical(space, p, 0, 0)) {
            return 0;
        }
    }
    return 1;
}

int wit_user_copy_from(const WitUserSpace *space, WitU64 address, WitU8 *buffer, WitU32 size)
{
    if (!wit_user_buffer_readable(space, address, size)) {
        return 0;
    }
    /* One translation per page: a page is contiguous in the kernel's view of physical memory. */
    for (WitU32 done = 0; done < size;) {
        const WitU8 *source = (const WitU8 *)wit_user_space_physical(space, address + done, 0, 0);
        const WitU32 span = (WitU32)(4096 - ((address + done) & 4095));
        for (WitU32 i = 0; i < span && done < size; ++i) {
            buffer[done++] = source[i];
        }
    }
    return 1;
}

int wit_user_buffer_writable(const WitUserSpace *space, WitU64 address, WitU32 size)
{
    const WitU64 limit = address_limit(space, address);
    if (!size) {
        return 1;
    }
    if (!limit || size > limit - address) {
        return 0;
    }
    /* Mapping and copy operations stay serialized with IF clear. */
    for (WitU64 p = address & ~4095ULL; p <= ((address + size - 1) & ~4095ULL); p += 4096) {
        if (!wit_user_space_physical(space, p, 1, 0)) {
            return 0;
        }
    }
    return 1;
}

int wit_user_copy_to(const WitUserSpace *space, WitU64 address, const WitU8 *buffer, WitU32 size)
{
    if (!wit_user_buffer_writable(space, address, size)) {
        return 0;
    }
    for (WitU32 done = 0; done < size;) {
        WitU8 *target = (WitU8 *)wit_user_space_physical(space, address + done, 1, 0);
        const WitU32 span = (WitU32)(4096 - ((address + done) & 4095));
        for (WitU32 i = 0; i < span && done < size; ++i) {
            target[i] = buffer[done++];
        }
    }
    return 1;
}

WitU64 wit_user_memory_query(const WitUserSpace *space, WitU64 address, WitU64 size, WitU64 version)
{
    WitUserMemoryInfo info;
    if (version != WIT_MEMORY_INFO_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (size != sizeof(info)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    info.Version = WIT_MEMORY_INFO_VERSION;
    info.Size = sizeof(info);
    info.PageSize = (WitU32)WIT_PAGE_SIZE;
    info.ProcessorCount = WIT_USER_PROCESSOR_COUNT; /* Only the bootstrap CPU is online in this backend. */
    info.PhysicalTotalBytes = space->Allocator->TotalPages * WIT_PAGE_SIZE;
    info.PhysicalAvailableBytes = wit_pages_free_count(space->Allocator) * WIT_PAGE_SIZE;
    info.OwnedLimitBytes = space->PageLimit * WIT_PAGE_SIZE;
    info.OwnedBytes = space->OwnedCount * WIT_PAGE_SIZE;
    info.VirtualBase = WIT_USER_MEMORY_BASE;
    info.VirtualBytes = WIT_USER_MEMORY_LIMIT - WIT_USER_MEMORY_BASE;
    info.CodeVirtualBase = WIT_USER_CODE_BASE;
    info.CodeVirtualBytes = WIT_USER_CODE_LIMIT - WIT_USER_CODE_BASE;
    info.ReservedBytes = 0;
    info.DynamicCommittedBytes = 0;
    info.PrivatePageTableBytes = 0;
    info.ReservationCount = 0;
    info.ReservationCapacity = space->ReservationLimit;
    for (WitU32 i = 0; i < space->ReservationLimit; ++i) {
        info.ReservedBytes += space->Reservations[i].Size;
        if (space->Reservations[i].Size) {
            ++info.ReservationCount;
        }
    }
    for (WitU32 i = 0; i < space->OwnedCount; ++i) {
        const WitU64 address_owned = space->OwnedVirtual[i];
        if (!address_owned) {
            info.PrivatePageTableBytes += WIT_PAGE_SIZE;
        } else if (dynamic_limit(address_owned)) {
            info.DynamicCommittedBytes += WIT_PAGE_SIZE;
        }
    }
    return wit_user_copy_to(space, address, (const WitU8 *)&info, sizeof(info)) ? WIT_STATUS_OK
                                                                                : WIT_STATUS_BAD_ADDRESS;
}

static int valid_protection(WitU64 protection)
{
    return protection == WIT_MEMORY_NONE ||
        protection == WIT_MEMORY_READ ||
        protection == (WIT_MEMORY_READ | WIT_MEMORY_WRITE);
}

/* The reservation a page-aligned range lies within, or the limit. */
static WitU32 reservation_of(const WitUserSpace *space, WitU64 address, WitU64 size)
{
    for (WitU32 i = 0; i < space->ReservationLimit; ++i) {
        const WitUserReservation *r = &space->Reservations[i];
        if (r->Size && address >= r->Base && address - r->Base < r->Size && size <= r->Size - (address - r->Base)) {
            return i;
        }
    }
    return space->ReservationLimit;
}

/* A range within a mapping of a memory object: its pages are the object's, so commit, decommit and reset are
 * refused, and a protection change stays within the rights the mapping was made with. */
static int object_mapping(const WitUserSpace *space, WitU64 address, WitU64 size, WitU32 *rights)
{
    const WitU32 slot = reservation_of(space, address, size);
    *rights = 0;
    if (slot == space->ReservationLimit || !space->MappedObjects[slot]) {
        return 0;
    }
    *rights = space->MappedRights[slot];
    return 1;
}

static WitU64 reserved_range(const WitUserSpace *space, WitU64 address, WitU64 size)
{
    if (!size || (address & 4095) || (size & 4095)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 limit = dynamic_limit(address);
    if (!limit || size > limit - address) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    for (WitU32 i = 0; i < space->ReservationLimit; ++i) {
        const WitUserReservation *r = &space->Reservations[i];
        if (r->Size && address >= r->Base && address - r->Base < r->Size && size <= r->Size - (address - r->Base)) {
            return WIT_STATUS_OK;
        }
    }
    return WIT_STATUS_NOT_RESERVED;
}

static WitU64 reserve_within(
    WitUserSpace *space, WitU64 size, WitU64 alignment, WitU64 low, WitU64 high, WitU64 *result)
{
    const WitU64 arena_size = WIT_USER_MEMORY_LIMIT - WIT_USER_MEMORY_BASE;
    *result = 0;
    if (!space->Root ||
        !size ||
        (size & 4095) ||
        alignment < 4096 ||
        alignment > arena_size ||
        (alignment & (alignment - 1))) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!dynamic_limit(low) || high > dynamic_limit(low)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (low >= high || size > high - low) {
        return WIT_STATUS_NO_MEMORY;
    }
    WitU32 slot = space->ReservationLimit;
    for (WitU32 i = 0; i < space->ReservationLimit; ++i) {
        if (!space->Reservations[i].Size) {
            slot = i;
            break;
        }
    }
    if (slot == space->ReservationLimit) {
        return WIT_STATUS_NO_MEMORY;
    }
    WitU64 candidate;
    if (!wit_virtual_gap(space->Reservations, space->ReservationLimit, low, high, size, alignment, &candidate)) {
        return WIT_STATUS_NO_MEMORY;
    }
    space->Reservations[slot] = (WitUserReservation){candidate, size};
    *result = candidate;
    return WIT_STATUS_OK;
}

static WitU64 memory_reserve(WitUserSpace *space, WitU64 size, WitU64 alignment, WitU64 *result)
{
    return reserve_within(space, size, alignment, WIT_USER_MEMORY_BASE, WIT_USER_MEMORY_LIMIT, result);
}

/* Bounds are explicit and upper-exclusive, clipped to the separate near arena. */
WitU64 wit_user_code_reserve(
    WitUserSpace *space, WitU64 size, WitU64 alignment, WitU64 low, WitU64 high, WitU64 *result)
{
    if (low < WIT_USER_CODE_BASE) {
        low = WIT_USER_CODE_BASE;
    }
    if (high > WIT_USER_CODE_LIMIT) {
        high = WIT_USER_CODE_LIMIT;
    }
    return reserve_within(space, size, alignment, low, high, result);
}

static WitU64 memory_commit(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection)
{
    WitU32 rights;
    if (library_range(space, address, size)) {
        return WIT_STATUS_DENIED;
    }
    WitU64 added[WIT_RUNTIME_PAGE_CAPACITY], mapped[WIT_RUNTIME_PAGE_CAPACITY];
    WitU32 count = 0, mappingCount = 0;
    WitU64 status = reserved_range(space, address, size);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (object_mapping(space, address, size, &rights)) {
        return WIT_STATUS_DENIED;
    }
    if (!valid_protection(protection)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (size / 4096 > space->PageLimit) {
        return WIT_STATUS_NO_MEMORY;
    }
    if (destination_view(space, address, size)) {
        return WIT_STATUS_DENIED;
    }
    // Validate every prospective destination before committing any backing.
    for (WitU32 i = 0; i < WIT_CODE_VIEW_CAPACITY; ++i) {
        const WitCodeView *v = &space->CodeViews[i];
        if (!overlaps(address, size, v->Source, v->Size)) {
            continue;
        }
        const WitU64 low = address > v->Source ? address : v->Source;
        const WitU64 high = address + size < v->Source + v->Size ? address + size : v->Source + v->Size;
        for (WitU64 p = low; p < high; p += 4096) {
            const WitU64 *input = leaf(space, p, 0);
            const WitU64 *output = leaf(space, v->Destination + p - v->Source, 0);
            if (input && *input && !(entry_flags(*input) & WIT_PAGE_OWNED)) {
                return WIT_STATUS_DENIED;
            }
            if (output &&
                *output &&
                (!input ||
                    !(entry_flags(*input) & WIT_PAGE_OWNED) ||
                    !(entry_flags(*output) & WIT_PAGE_ALIAS) ||
                    entry_physical(*output) != entry_physical(*input))) {
                return WIT_STATUS_BUSY;
            }
        }
    }
    for (WitU64 p = address; p < address + size; p += 4096) {
        WitU64 *entry = leaf(space, p, 0);
        if (!entry || !(entry_flags(*entry) & (WIT_PAGE_OWNED | WIT_PAGE_ALIAS))) {
            if (!map_page(space, p, protection_flags(protection))) {
                goto failed;
            }
            added[count++] = p;
            entry = leaf(space, p, 0);
        }
        const WitU64 physical = entry_physical(*entry);
        for (WitU32 i = 0; i < WIT_CODE_VIEW_CAPACITY; ++i) {
            const WitCodeView *v = &space->CodeViews[i];
            if (!v->Size || p < v->Source || p - v->Source >= v->Size) {
                continue;
            }
            const WitU64 target = v->Destination + p - v->Source;
            const WitU64 *present = leaf(space, target, 0);
            if (present && (entry_flags(*present) & WIT_PAGE_ALIAS)) {
                continue; // Preserve existing protection and bytes.
            }
            if (mappingCount == WIT_RUNTIME_PAGE_CAPACITY || !map_alias_page(space, target, physical, v->Protection)) {
                goto failed;
            }
            mapped[mappingCount++] = target;
        }
    }
    return WIT_STATUS_OK;
failed:
    while (mappingCount) {
        unmap_page(space, mapped[--mappingCount]);
    }
    while (count) {
        unmap_page(space, added[--count]);
    }
    return WIT_STATUS_NO_MEMORY;
}

static void decommit_range(WitUserSpace *space, WitU64 address, WitU64 size)
{
    /* Walk committed frames, not potentially billions of reserved pages.
     * Unmapping may compact ownership records for both frames and tables. */
    WitU32 i = 0;
    while (i < space->AliasCount) {
        const WitU64 p = space->AliasVirtual[i];
        if (p >= address && p - address < size) {
            unmap_page(space, p);
            i = 0;
        } else {
            ++i;
        }
    }
    i = 0;
    while (i < space->OwnedCount) {
        const WitU64 p = space->OwnedVirtual[i];
        if (p >= address && p - address < size) {
            unmap_page(space, p);
            i = 0;
        } else {
            ++i;
        }
    }
}

static WitU64 memory_decommit(WitUserSpace *space, WitU64 address, WitU64 size)
{
    WitU32 rights;
    if (object_mapping(space, address, size, &rights)) {
        return WIT_STATUS_DENIED;
    }
    if (library_range(space, address, size)) {
        return WIT_STATUS_DENIED;
    }
    const WitU64 status = reserved_range(space, address, size);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (source_view(space, address, size) ||
        destination_view(space, address, size) ||
        aliased_range(space, address, size)) {
        return WIT_STATUS_BUSY;
    }
    decommit_range(space, address, size);
    return WIT_STATUS_OK;
}

static WitU64 memory_reset(WitUserSpace *space, WitU64 address, WitU64 size)
{
    WitU32 rights;
    if (object_mapping(space, address, size, &rights)) {
        return WIT_STATUS_DENIED;
    }
    if (library_range(space, address, size)) {
        return WIT_STATUS_DENIED;
    }
    const WitU64 status = reserved_range(space, address, size);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    /* Bound work even for sparse multi-gigabyte reservations. No allocation. */
    if (size / 4096 > space->PageLimit) {
        return WIT_STATUS_NOT_COMMITTED;
    }
    for (WitU64 p = address; p < address + size; p += 4096) {
        const WitU64 *entry = leaf(space, p, 0);
        if (!entry || !(entry_flags(*entry) & WIT_PAGE_OWNED)) {
            return WIT_STATUS_NOT_COMMITTED;
        }
        if (entry_flags(*entry) & WIT_PAGE_EXECUTE) {
            return WIT_STATUS_DENIED;
        }
    }
    /* IF stays clear across validation and mutation. Owned physical backing
     * lets read-only/no-access commitments retain their original protection. */
    for (WitU64 p = address; p < address + size; p += 4096) {
        volatile WitU64 *data = (volatile WitU64 *)entry_physical(*leaf(space, p, 0));
        for (WitU32 i = 0; i < 512; ++i) {
            data[i] = 0;
        }
    }
    return WIT_STATUS_OK;
}

static WitU64 protect(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection, int code)
{
    WitU32 rights = 0;
    if (library_range(space, address, size)) {
        return WIT_STATUS_DENIED;
    }
    WitU64 status = reserved_range(space, address, size);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const int mapping = object_mapping(space, address, size, &rights);
    if (!valid_protection(protection) && !((code || mapping) && protection == (WIT_MEMORY_READ | WIT_CODE_EXECUTE))) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (mapping &&
        (((protection & WIT_MEMORY_WRITE) && !(rights & WIT_RIGHT_WRITE)) ||
            ((protection & WIT_CODE_EXECUTE) && !(rights & WIT_RIGHT_EXECUTE)))) {
        return WIT_STATUS_DENIED;
    }
    if (size / 4096 > space->PageLimit) {
        return WIT_STATUS_NOT_COMMITTED;
    }
    for (WitU64 p = address; p < address + size; p += 4096) {
        const WitU64 *entry = leaf(space, p, 0);
        if (!entry || !(entry_flags(*entry) & (WIT_PAGE_OWNED | WIT_PAGE_ALIAS))) {
            return WIT_STATUS_NOT_COMMITTED;
        }
    }
    for (WitU64 p = address; p < address + size; p += 4096) {
        WitU64 *entry = leaf(space, p, 0);
        const WitU32 kept = entry_flags(*entry) & (WIT_PAGE_OWNED | WIT_PAGE_ALIAS);
        *entry =
            wit_arch_page_entry_make(entry_physical(*entry), kept | (protection_flags(protection) & ~WIT_PAGE_OWNED));
        invalidate(space, p);
    }
    return WIT_STATUS_OK;
}

WitU64 wit_user_code_validate(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection)
{
    if (!size || address > ~0ULL - size) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (protection == 0) {
        const WitU64 low = address & ~4095ULL;
        if (address + size > ~0ULL - 4095) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        return reserved_range(space, low, ((address + size + 4095) & ~4095ULL) - low);
    }
    if (protection != 1 && protection != 3 && protection != 5) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (size > space->PageLimit * 4096ULL) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (protection == 3 ? !wit_user_buffer_writable(space, address, (WitU32)size)
                        : !wit_user_buffer_readable(space, address, (WitU32)size)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (protection == 5) {
        for (WitU64 p = address & ~4095ULL; p <= ((address + size - 1) & ~4095ULL); p += 4096) {
            if (!wit_user_space_physical(space, p, 0, 1)) {
                return WIT_STATUS_DENIED;
            }
        }
    }
    return WIT_STATUS_OK;
}

/* Unmapped code-block recycle: zero existing private backing only. Untouched
 * sparse pages remain uncommitted and will be zero-filled on a later commit. */
WitU64 wit_user_code_reset_sparse(WitUserSpace *space, WitU64 address, WitU64 size)
{
    if (library_range(space, address, size)) {
        return WIT_STATUS_DENIED;
    }
    const WitU64 status = reserved_range(space, address, size);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (source_view(space, address, size) ||
        destination_view(space, address, size) ||
        aliased_range(space, address, size)) {
        return WIT_STATUS_BUSY;
    }
    for (WitU32 i = 0; i < space->OwnedCount; ++i) {
        const WitU64 p = space->OwnedVirtual[i];
        if (p < address || p - address >= size) {
            continue;
        }
        const WitU64 *entry = leaf(space, p, 0);
        if (!entry || !(entry_flags(*entry) & WIT_PAGE_OWNED) || (entry_flags(*entry) & WIT_PAGE_EXECUTE)) {
            return WIT_STATUS_DENIED;
        }
    }
    for (WitU32 i = 0; i < space->OwnedCount; ++i) {
        const WitU64 p = space->OwnedVirtual[i];
        if (p < address || p - address >= size) {
            continue;
        }
        volatile WitU64 *data = (volatile WitU64 *)space->OwnedPages[i];
        for (WitU32 word = 0; word < 512; ++word) {
            data[word] = 0;
        }
    }
    return WIT_STATUS_OK;
}

/* Register an initially sparse view. Existing backing is mapped now; later
 * commits publish all overlapping views in the same IF-disabled transaction. */
WitU64 wit_user_code_map_sparse(WitUserSpace *space, WitU64 destination, WitU64 source, WitU64 size, WitU64 protection)
{
    if (library_range(space, source, size) || library_range(space, destination, size)) {
        return WIT_STATUS_DENIED;
    }
    WitU64 status = reserved_range(space, source, size);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    status = reserved_range(space, destination, size);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (protection != 3 && protection != 5) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (overlaps(source, size, destination, size) ||
        destination_view(space, source, size) ||
        destination_view(space, destination, size) ||
        source_view(space, destination, size)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitU32 slot = WIT_CODE_VIEW_CAPACITY;
    for (WitU32 i = 0; i < WIT_CODE_VIEW_CAPACITY; ++i) {
        if (!space->CodeViews[i].Size) {
            slot = i;
            break;
        }
    }
    if (slot == WIT_CODE_VIEW_CAPACITY) {
        return WIT_STATUS_NO_MEMORY;
    }
    // Scan settled ownership, not the potentially huge uncommitted VA span.
    for (WitU32 i = 0; i < space->AliasCount; ++i) {
        if (overlaps(space->AliasVirtual[i], 4096, source, size) ||
            overlaps(space->AliasVirtual[i], 4096, destination, size)) {
            return WIT_STATUS_BUSY;
        }
    }
    for (WitU32 i = 0; i < space->OwnedCount; ++i) {
        if (space->OwnedVirtual[i] && overlaps(space->OwnedVirtual[i], 4096, destination, size)) {
            return WIT_STATUS_BUSY;
        }
    }
    WitU64 mapped[WIT_RUNTIME_PAGE_CAPACITY];
    WitU32 count = 0;
    // New private tables may append ownership entries; their virtual tag is zero.
    for (WitU32 i = 0; i < space->OwnedCount; ++i) {
        const WitU64 address = space->OwnedVirtual[i];
        if (address < source || address - source >= size) {
            continue;
        }
        const WitU64 target = destination + address - source;
        if (!map_alias_page(space, target, space->OwnedPages[i], protection)) {
            while (count) {
                unmap_page(space, mapped[--count]);
            }
            return WIT_STATUS_NO_MEMORY;
        }
        mapped[count++] = target;
    }
    space->CodeViews[slot] = (WitCodeView){destination, source, size, protection};
    return WIT_STATUS_OK;
}

/* Private same-component aliases: source backing stays uniquely owned. No
 * physical address, fixed image or supervisor mapping is accepted from callers.
 * Destination reservation remains sparse on failure; empty tables are pruned. */
WitU64 wit_user_code_alias(WitUserSpace *space, WitU64 destination, WitU64 source, WitU64 size, WitU64 protection)
{
    if (library_range(space, source, size) || library_range(space, destination, size)) {
        return WIT_STATUS_DENIED;
    }
    WitU64 status = reserved_range(space, source, size);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    status = reserved_range(space, destination, size);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (protection != 3 && protection != 5) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (destination < source + size && source < destination + size) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (space->AliasCount > space->PageLimit || size / 4096 > space->PageLimit - space->AliasCount) {
        return WIT_STATUS_NO_MEMORY;
    }
    for (WitU64 offset = 0; offset < size; offset += 4096) {
        const WitU64 *input = leaf(space, source + offset, 0);
        const WitU64 *output = leaf(space, destination + offset, 0);
        if (!input || !(entry_flags(*input) & WIT_PAGE_OWNED) || (entry_flags(*input) & WIT_PAGE_ALIAS)) {
            return WIT_STATUS_NOT_COMMITTED;
        }
        if (output && *output) {
            return WIT_STATUS_BUSY;
        }
    }
    WitU64 added = 0;
    for (; added < size; added += 4096) {
        WitU64 *output = leaf(space, destination + added, 1);
        if (!output) {
            prune(space, destination + added);
            while (added) {
                added -= 4096;
                unmap_page(space, destination + added);
            }
            return WIT_STATUS_NO_MEMORY;
        }
        const WitU64 physical = entry_physical(*leaf(space, source + added, 0));
        *output = wit_arch_page_entry_make(physical, (protection_flags(protection) & ~WIT_PAGE_OWNED) | WIT_PAGE_ALIAS);
        space->AliasVirtual[space->AliasCount] = destination + added;
        space->AliasPhysical[space->AliasCount++] = physical;
        invalidate(space, destination + added);
    }
    return WIT_STATUS_OK;
}

/* Code backend only. The ordinary memory syscall still rejects EXECUTE.
 * This is the first building block for the component-owned JIT mapper. */
static WitU64 memory_protect(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection)
{
    return protect(space, address, size, protection, 0);
}

WitU64 wit_user_code_protect(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection)
{
    return protect(space, address, size, protection, 1);
}

/* Makes instructions written to the executable owned or aliased pages of [address, address + size) visible to
 * instruction fetch. Other pages hold no code and are skipped. */
void wit_user_space_publish_code(WitUserSpace *space, WitU64 address, WitU64 size)
{
    for (WitU64 page = address & ~4095ULL; page < address + size; page += 4096) {
        const WitU64 *entry = leaf(space, page, 0);
        if (entry &&
            (entry_flags(*entry) & WIT_PAGE_EXECUTE) &&
            (entry_flags(*entry) & (WIT_PAGE_OWNED | WIT_PAGE_ALIAS))) {
            wit_arch_publish_code_page(wit_arch_page_entry_physical(*entry));
        }
    }
    wit_arch_publish_code();
}

WitU64 wit_user_code_publish(WitUserSpace *space, WitU64 address, WitU64 size)
{
    if (WIT_USER_PROCESSOR_COUNT != 1) {
        return WIT_STATUS_UNSUPPORTED;
    }
    const WitU64 limit = dynamic_limit(address);
    if (!size || !limit || size > limit - address) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    const WitU64 low = address & ~4095ULL, high = (address + size + 4095) & ~4095ULL;
    const WitU64 status = reserved_range(space, low, high - low);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if ((high - low) / 4096 > space->PageLimit) {
        return WIT_STATUS_NOT_COMMITTED;
    }
    for (WitU64 page = low; page < high; page += 4096) {
        const WitU64 *entry = leaf(space, page, 0);
        if (!entry ||
            !(entry_flags(*entry) & WIT_PAGE_READ) ||
            !(entry_flags(*entry) & (WIT_PAGE_OWNED | WIT_PAGE_ALIAS))) {
            return WIT_STATUS_NOT_COMMITTED;
        }
        if ((entry_flags(*entry) & WIT_PAGE_WRITE) || !(entry_flags(*entry) & WIT_PAGE_EXECUTE)) {
            return WIT_STATUS_DENIED;
        }
    }
    wit_user_space_publish_code(space, low, high - low);
    return WIT_STATUS_OK;
}

static WitU64 release_reservation(WitUserSpace *space, WitU64 address, int library)
{
    if (address & 4095) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!dynamic_limit(address)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    for (WitU32 i = 0; i < space->ReservationLimit; ++i) {
        WitUserReservation *r = &space->Reservations[i];
        if (!r->Size || r->Base != address) {
            continue;
        }
        if (!library && library_range(space, r->Base, r->Size)) {
            return WIT_STATUS_DENIED;
        }
        if (source_view(space, r->Base, r->Size) || aliased_range(space, r->Base, r->Size)) {
            return WIT_STATUS_BUSY;
        }
        for (WitU32 v = 0; v < WIT_CODE_VIEW_CAPACITY; ++v) {
            WitCodeView *view = &space->CodeViews[v];
            if (view->Size && view->Destination >= r->Base && view->Destination - r->Base < r->Size) {
                *view = (WitCodeView){0};
            }
        }
        decommit_range(space, r->Base, r->Size);
        r->Size = 0;
        space->MappedObjects[i] = 0;
        space->MappedRights[i] = 0;
        return WIT_STATUS_OK;
    }
    return WIT_STATUS_NOT_RESERVED;
}

/* Memory objects (plan step K5.1): pages a component owns without a virtual address of their own (tagged zero like
 * the table pages), mapped as alias entries of a reservation that remembers the object's nonzero number and the
 * rights it may take. */
int wit_user_space_allocate_pages(WitUserSpace *space, WitU64 *pages, WitU32 count)
{
    for (WitU32 i = 0; i < count; ++i) {
        pages[i] = allocate(space, 0);
        if (!pages[i]) {
            while (i) {
                free_owned(space, pages[--i]);
            }
            return 0;
        }
    }
    return 1;
}

void wit_user_space_free_pages(WitUserSpace *space, const WitU64 *pages, WitU32 count)
{
    for (WitU32 i = 0; i < count; ++i) {
        if (aliased(space, pages[i])) {
            wit_panic("Freeing memory object pages with live mappings");
        }
        free_owned(space, pages[i]);
    }
}

WitU64 wit_user_space_map_object(WitUserSpace *space, WitU64 address, WitU64 size, const WitU64 *pages,
    WitU64 protection, WitU32 object, WitU32 rights, WitU64 *result)
{
    WitU64 base = 0;
    WitU32 slot;
    *result = 0;
    if (!space->Root || !size || (size & 4095) || (address & 4095)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (address) {
        const WitU64 limit = dynamic_limit(address);
        if (!limit || size > limit - address) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        if (library_range(space, address, size)) {
            return WIT_STATUS_DENIED;
        }
        slot = space->ReservationLimit;
        for (WitU32 i = 0; i < space->ReservationLimit; ++i) {
            const WitUserReservation *r = &space->Reservations[i];
            if (r->Size && overlaps(address, size, r->Base, r->Size)) {
                return WIT_STATUS_BUSY;
            }
            if (!r->Size && slot == space->ReservationLimit) {
                slot = i;
            }
        }
        if (slot == space->ReservationLimit) {
            return WIT_STATUS_NO_MEMORY;
        }
        space->Reservations[slot] = (WitUserReservation){address, size};
        base = address;
    } else {
        const WitU64 status = reserve_within(space, size, 4096, WIT_USER_MEMORY_BASE, WIT_USER_MEMORY_LIMIT, &base);
        if (status != WIT_STATUS_OK) {
            return status;
        }
        slot = reservation_of(space, base, size);
    }
    if (space->AliasCount > space->PageLimit || size / 4096 > space->PageLimit - space->AliasCount) {
        space->Reservations[slot].Size = 0;
        return WIT_STATUS_NO_MEMORY;
    }
    for (WitU64 offset = 0; offset < size; offset += 4096) {
        if (!map_alias_page(space, base + offset, pages[offset / 4096], protection)) {
            while (offset) {
                offset -= 4096;
                unmap_page(space, base + offset);
            }
            space->Reservations[slot].Size = 0;
            return WIT_STATUS_NO_MEMORY;
        }
    }
    space->MappedObjects[slot] = object;
    space->MappedRights[slot] = rights;
    *result = base;
    return WIT_STATUS_OK;
}

int wit_user_space_mapping_object(const WitUserSpace *space, WitU64 base, WitU64 *object)
{
    *object = 0;
    for (WitU32 i = 0; i < space->ReservationLimit; ++i) {
        const WitUserReservation *r = &space->Reservations[i];
        if (r->Size && r->Base == base && space->MappedObjects[i]) {
            *object = space->MappedObjects[i];
            return 1;
        }
    }
    return 0;
}

static WitU64 memory_release(WitUserSpace *space, WitU64 address)
{
    return release_reservation(space, address, 0);
}

WitU64 wit_user_library_release(WitUserSpace *space, WitU64 address)
{
    return release_reservation(space, address, 1);
}

void wit_user_space_destroy(WitUserSpace *space)
{
    if (space->Root && wit_arch_space_active(space->Root)) {
        wit_panic("Destroying active user address space");
    }
    while (space->OwnedCount != 0) {
        const WitU64 page = space->OwnedPages[--space->OwnedCount];
        if (!wit_page_free(space->Allocator, page)) {
            wit_panic("User page ownership corrupted");
        }
    }
    space->Root = 0;
    space->AliasCount = 0;
    for (WitU32 i = 0; i <= WIT_LIBRARY_CAPACITY; ++i) {
        space->LibraryRanges[i] = (WitVirtualRange){0};
    }
    for (WitU32 i = 0; i < WIT_CODE_VIEW_CAPACITY; ++i) {
        space->CodeViews[i] = (WitCodeView){0};
    }
    for (WitU32 i = 0; i < WIT_RUNTIME_RESERVATION_CAPACITY; ++i) {
        space->Reservations[i].Size = 0;
        space->MappedObjects[i] = 0;
        space->MappedRights[i] = 0;
    }
}

WitU64 wit_user_memory_reserve(WitUserSpace *space, WitU64 size, WitU64 alignment, WitU64 *result)
{
    const WitU64 status = memory_reserve(space, size, alignment, result);
    return journal(WIT_MEMORY_JOURNAL_RESERVE, status == WIT_STATUS_OK ? *result : 0, size, status);
}

WitU64 wit_user_memory_commit(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection)
{
    return journal(WIT_MEMORY_JOURNAL_COMMIT, address, size, memory_commit(space, address, size, protection));
}

WitU64 wit_user_memory_decommit(WitUserSpace *space, WitU64 address, WitU64 size)
{
    return journal(WIT_MEMORY_JOURNAL_DECOMMIT, address, size, memory_decommit(space, address, size));
}

WitU64 wit_user_memory_reset(WitUserSpace *space, WitU64 address, WitU64 size)
{
    return journal(WIT_MEMORY_JOURNAL_RESET, address, size, memory_reset(space, address, size));
}

WitU64 wit_user_memory_protect(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection)
{
    return journal(WIT_MEMORY_JOURNAL_PROTECT, address, size, memory_protect(space, address, size, protection));
}

WitU64 wit_user_memory_release(WitUserSpace *space, WitU64 address)
{
    return journal(WIT_MEMORY_JOURNAL_RELEASE, address, 0, memory_release(space, address));
}
