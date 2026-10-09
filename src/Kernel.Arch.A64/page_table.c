#include "witos/arch.h"
#include "witos/platform.h"
#include "witos/virtual.h"
#include "user.h"
#include "a64.h"

/* ARM64 four-level translation tables of user address spaces (TTBR0, 4 KiB granule, 48-bit addresses): entry
 * encoding, walks, pruning and TLB coherence. Table pages are owned and accounted by the common address-space
 * code through its table callbacks. Level 0 entry 0 is the shared kernel branch, which carries the EL0 no-access
 * and UXN table attributes; walks of user tables never enter it. */

#define DESC_VALID 1ULL
#define DESC_TABLE 3ULL
#define DESC_PAGE 3ULL
#define DESC_TYPE 3ULL
#define DESC_INNER_SHAREABLE (3ULL << 8)
#define DESC_EL0 (1ULL << 6) /* AP[1]: EL0 access. */
#define DESC_READ_ONLY (1ULL << 7) /* AP[2]. */
#define DESC_ACCESSED (1ULL << 10)
#define DESC_NOT_GLOBAL (1ULL << 11)
#define DESC_PXN (1ULL << 53) /* The kernel never executes user pages. */
#define DESC_UXN (1ULL << 54)
/* Software bits, ignored by the walker: a committed leaf retains its frame even with no access. */
#define DESC_OWNED (1ULL << 55)
#define DESC_ALIAS (1ULL << 56)
#define DESC_DEVICE (1ULL << 2) /* AttrIndx 1: device-nGnRnE in the MAIR paging.c installs. */
#define TABLE_UXN (1ULL << 60)
#define TABLE_NO_EL0 (1ULL << 61) /* APTable[0]. */
#define DESC_ADDRESS 0x0000FFFFFFFFF000ULL

WitU64 wit_arch_page_entry_make(WitU64 physical, WitU32 flags)
{
    return physical |
        ((flags & WIT_PAGE_READ) ? DESC_PAGE : 0) |
        DESC_INNER_SHAREABLE |
        DESC_ACCESSED |
        DESC_NOT_GLOBAL |
        DESC_EL0 |
        DESC_PXN |
        ((flags & WIT_PAGE_WRITE) ? 0 : DESC_READ_ONLY) |
        ((flags & WIT_PAGE_EXECUTE) ? 0 : DESC_UXN) |
        ((flags & WIT_PAGE_OWNED) ? DESC_OWNED : 0) |
        ((flags & WIT_PAGE_ALIAS) ? DESC_ALIAS : 0) |
        ((flags & WIT_PAGE_DEVICE) ? DESC_DEVICE : 0);
}

WitU32 wit_arch_page_entry_flags(WitU64 entry)
{
    if (!(entry & DESC_EL0)) {
        return 0; /* Empty: every user entry carries AP[1]. */
    }
    return ((entry & DESC_TYPE) == DESC_PAGE ? WIT_PAGE_READ : 0) |
        ((entry & DESC_READ_ONLY) ? 0 : WIT_PAGE_WRITE) |
        ((entry & DESC_UXN) ? 0 : WIT_PAGE_EXECUTE) |
        ((entry & DESC_OWNED) ? WIT_PAGE_OWNED : 0) |
        ((entry & DESC_ALIAS) ? WIT_PAGE_ALIAS : 0) |
        ((entry & DESC_DEVICE) ? WIT_PAGE_DEVICE : 0);
}

WitU64 wit_arch_page_entry_physical(WitU64 entry)
{
    return entry & DESC_ADDRESS;
}

void wit_arch_page_invalidate(const WitUserSpace *space, WitU64 address)
{
    /* One processor and no ASIDs: an inactive root is flushed when it is installed. */
    if (wit_a64_translation_base() == space->Root) {
        wit_a64_invalidate_page(address);
    }
}

static int user_table(WitU64 entry)
{
    return (entry & DESC_TYPE) == DESC_TABLE && !(entry & (TABLE_NO_EL0 | TABLE_UXN));
}

WitU64 *wit_arch_page_entry(WitUserSpace *space, WitU64 address, int create)
{
    const WitU32 shifts[3] = {39, 30, 21};
    WitU64 *table = (WitU64 *)space->Root;
    if (!table || (address >> 48) != 0) {
        return 0;
    }
    for (WitU32 level = 0; level < 3; ++level) {
        WitU64 *entry = &table[(address >> shifts[level]) & 511];
        if (!(*entry & DESC_VALID)) {
            WitU64 page;
            if (!create || *entry != 0) {
                return 0;
            }
            page = wit_user_space_take_table(space);
            if (!page) {
                return 0;
            }
            wit_a64_data_barrier(); /* The zeroed table reaches memory before the walker can see it. */
            *entry = page | DESC_TABLE;
        }
        if (!user_table(*entry)) {
            return 0;
        }
        table = (WitU64 *)(*entry & DESC_ADDRESS);
    }
    return &table[(address >> 12) & 511];
}

/* Removes partially built paths after allocation failure. Never frees the root or visits the shared kernel
 * branch. Call only for validated user addresses. */
void wit_arch_page_prune(WitUserSpace *space, WitU64 address)
{
    const WitU32 shifts[3] = {39, 30, 21};
    WitU64 *tables[4], *parents[3];
    WitU32 depth = 0;
    tables[0] = (WitU64 *)space->Root;
    for (; depth < 3; ++depth) {
        WitU64 *entry = &tables[depth][(address >> shifts[depth]) & 511];
        if (!user_table(*entry)) {
            break;
        }
        parents[depth] = entry;
        tables[depth + 1] = (WitU64 *)(*entry & DESC_ADDRESS);
    }
    while (depth) {
        for (WitU32 i = 0; i < 512; ++i) {
            if (tables[depth][i] != 0) {
                return;
            }
        }
        *parents[depth - 1] = 0;
        wit_arch_page_invalidate(space, address);
        wit_user_space_release_table(space, (WitU64)tables[depth--]);
    }
}

WitU64 wit_arch_page_translate(WitU64 root, WitU64 address, int write, int execute)
{
    const WitU32 shifts[4] = {39, 30, 21, 12};
    WitU64 *table = (WitU64 *)root;
    if ((address >> 48) != 0) {
        return 0;
    }
    for (WitU32 level = 0; level < 3; ++level) {
        const WitU64 entry = table[(address >> shifts[level]) & 511];
        if (!user_table(entry)) {
            return 0;
        }
        table = (WitU64 *)(entry & DESC_ADDRESS);
    }
    const WitU64 entry = table[(address >> shifts[3]) & 511];
    if ((entry & DESC_TYPE) != DESC_PAGE ||
        !(entry & DESC_EL0) ||
        (write && (entry & DESC_READ_ONLY)) ||
        (execute && (entry & DESC_UXN))) {
        return 0;
    }
    return (entry & DESC_ADDRESS) | (address & 4095);
}

void wit_arch_publish_code_page(WitU64 physical)
{
    /* The kernel's view of the frame (S6.2): the identity map, or the storage window for a page of the boot package. */
    const WitU8 *view = wit_virtual_view(physical);
    if (view) {
        wit_a64_clean_data((WitU64)view, 4096);
    }
}

void wit_arch_publish_code(void)
{
    wit_a64_invalidate_instructions();
}
