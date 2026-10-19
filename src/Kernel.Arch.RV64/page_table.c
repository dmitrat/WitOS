#include "witos/arch.h"
#include "witos/platform.h"
#include "witos/virtual.h"
#include "user.h"
#include "rv64.h"

/* Sv48 translation tables of user address spaces (plan step V1.3): entry encoding, walks, pruning and TLB coherence.
 * Table pages are owned and accounted by the common address-space code through its table callbacks. Root entry 0 and
 * the high half are the kernel's branches, global, which walks of user tables never enter. A user leaf carries U, A
 * and, when writable, D, since the kernel never relies on hardware updates of those bits. A leaf without READ is an
 * invalid entry (V clear) that keeps its frame and its software bits: the walker ignores it, and the kernel still knows
 * the page is committed. The two software bits (RSW, 9:8) hold what the leaf is: a plain page, an OWNED frame, an ALIAS
 * of a memory object's page or an ALIAS of a device region's page (DEVICE, which the common kernel always marks ALIAS
 * too); the base ISA gives a page no memory attribute, so a device page is a plain mapping. */

#define PTE_VALID 1ULL
#define PTE_READ 2ULL
#define PTE_WRITE 4ULL
#define PTE_EXECUTE 8ULL
#define PTE_USER 16ULL
#define PTE_GLOBAL 32ULL
#define PTE_ACCESSED 64ULL
#define PTE_DIRTY 128ULL
#define PTE_LEAF (PTE_READ | PTE_WRITE | PTE_EXECUTE)
#define PTE_KIND (3ULL << 8) /* RSW: 0 plain, 1 OWNED, 2 ALIAS, 3 ALIAS | DEVICE */
#define PTE_KIND_OWNED (1ULL << 8)
#define PTE_KIND_ALIAS (2ULL << 8)
#define PTE_KIND_DEVICE (3ULL << 8)
#define PTE_PPN_SHIFT 10
#define PTE_ADDRESS(entry) ((((entry) >> PTE_PPN_SHIFT) & 0x0FFFFFFFFFFFULL) << 12)
#define USER_LIMIT (1ULL << 47) /* Sv48's low half */
#define SATP_PPN 0x00000FFFFFFFFFFFULL

WitU64 wit_arch_page_entry_make(WitU64 physical, WitU32 flags)
{
    const WitU64 kind = (flags & WIT_PAGE_DEVICE) ? PTE_KIND_DEVICE
        : (flags & WIT_PAGE_ALIAS)                ? PTE_KIND_ALIAS
        : (flags & WIT_PAGE_OWNED)                ? PTE_KIND_OWNED
                                                  : 0;
    return (physical >> 12 << PTE_PPN_SHIFT) |
        kind |
        ((flags & WIT_PAGE_READ) ? PTE_VALID | PTE_READ | PTE_USER | PTE_ACCESSED : 0) |
        ((flags & WIT_PAGE_WRITE) ? PTE_WRITE | PTE_DIRTY : 0) |
        ((flags & WIT_PAGE_EXECUTE) ? PTE_EXECUTE : 0);
}

WitU32 wit_arch_page_entry_flags(WitU64 entry)
{
    const WitU64 kind = entry & PTE_KIND;
    if (entry == 0) {
        return 0;
    }
    return ((entry & PTE_VALID) ? WIT_PAGE_READ : 0) |
        ((entry & PTE_WRITE) ? WIT_PAGE_WRITE : 0) |
        ((entry & PTE_EXECUTE) ? WIT_PAGE_EXECUTE : 0) |
        (kind == PTE_KIND_OWNED ? WIT_PAGE_OWNED : 0) |
        (kind == PTE_KIND_ALIAS ? WIT_PAGE_ALIAS : 0) |
        (kind == PTE_KIND_DEVICE ? WIT_PAGE_ALIAS | WIT_PAGE_DEVICE : 0);
}

WitU64 wit_arch_page_entry_physical(WitU64 entry)
{
    return PTE_ADDRESS(entry);
}

void wit_arch_page_invalidate(const WitUserSpace *space, WitU64 address)
{
    /* No ASIDs: an inactive root is flushed when it is installed. SFENCE.VMA is local, so every other hart that has the
     * space active drops the translation by request (P1.3b). */
    if ((wit_rv64_read_satp() & SATP_PPN) == (space->Root >> 12)) {
        wit_rv64_fence_vma_address(address);
    }
    wit_user_space_shootdown(space, address);
}

/* A branch of the user's own: valid, no leaf bits and not global, which the kernel's branches are. */
static int user_table(WitU64 entry)
{
    return (entry & (PTE_VALID | PTE_LEAF | PTE_GLOBAL)) == PTE_VALID;
}

WitU64 *wit_arch_page_entry(WitUserSpace *space, WitU64 address, int create)
{
    const WitU32 shifts[3] = {39, 30, 21};
    WitU64 *table = (WitU64 *)space->Root;
    if (!table || address >= USER_LIMIT) {
        return 0;
    }
    for (WitU32 level = 0; level < 3; ++level) {
        WitU64 *entry = &table[(address >> shifts[level]) & 511];
        if (!(*entry & PTE_VALID)) {
            WitU64 page;
            if (!create || *entry != 0) {
                return 0;
            }
            page = wit_user_space_take_table(space);
            if (!page) {
                return 0;
            }
            wit_rv64_fence(); /* The zeroed table reaches memory before the walker can see it. */
            *entry = (page >> 12 << PTE_PPN_SHIFT) | PTE_VALID;
        }
        if (!user_table(*entry)) {
            return 0;
        }
        table = (WitU64 *)PTE_ADDRESS(*entry);
    }
    return &table[(address >> 12) & 511];
}

/* Removes partially built paths after allocation failure. Never frees the root or visits the kernel's branches. Call
 * only for validated user addresses. */
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
        tables[depth + 1] = (WitU64 *)PTE_ADDRESS(*entry);
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

WitU64 wit_arch_page_next(const WitUserSpace *space, WitU64 address, WitU64 end)
{
    const WitU32 shifts[4] = {39, 30, 21, 12};
    address &= ~4095ULL;
    if (end > USER_LIMIT) {
        end = USER_LIMIT;
    }
    while (space->Root && address < end) {
        const WitU64 *table = (const WitU64 *)space->Root;
        WitU32 level = 0;
        for (;; ++level) {
            const WitU64 entry = table[(address >> shifts[level]) & 511];
            if (level == 3) {
                if (entry) {
                    return address;
                }
                break;
            }
            if (!user_table(entry)) {
                break;
            }
            table = (const WitU64 *)PTE_ADDRESS(entry);
        }
        const WitU64 span = 1ULL << shifts[level];
        const WitU64 next = (address & ~(span - 1)) + span;
        if (next <= address) {
            break;
        }
        address = next;
    }
    return end;
}

WitU64 wit_arch_page_translate(WitU64 root, WitU64 address, int write, int execute)
{
    const WitU32 shifts[4] = {39, 30, 21, 12};
    WitU64 *table = (WitU64 *)root;
    if (address >= USER_LIMIT) {
        return 0;
    }
    for (WitU32 level = 0; level < 3; ++level) {
        const WitU64 entry = table[(address >> shifts[level]) & 511];
        if (!user_table(entry)) {
            return 0;
        }
        table = (WitU64 *)PTE_ADDRESS(entry);
    }
    const WitU64 entry = table[(address >> shifts[3]) & 511];
    if ((entry & (PTE_VALID | PTE_READ | PTE_USER)) != (PTE_VALID | PTE_READ | PTE_USER) ||
        (write && !(entry & PTE_WRITE)) ||
        (execute && !(entry & PTE_EXECUTE))) {
        return 0;
    }
    return PTE_ADDRESS(entry) | (address & 4095);
}
