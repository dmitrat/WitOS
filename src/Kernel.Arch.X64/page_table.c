#include "x64.h"
#include "user.h"
#include "witos/platform.h"
#include "witos/x64_instructions.h"

/* x64 four-level page tables of user address spaces: entry encoding, walks, pruning and TLB coherence.
 * Table pages are owned and accounted by the common address-space code through its table callbacks. */

#define PAGE_PRESENT 1ULL
#define PAGE_WRITE 2ULL
#define PAGE_USER 4ULL
#define PAGE_LARGE 0x80ULL
/* Software bit: a committed leaf retains its frame even with no access. */
#define PAGE_OWNED 0x200ULL
#define PAGE_ALIAS 0x400ULL
#define PAGE_DEVICE 0x18ULL /* PCD|PWT: PAT entry 3, verified UC when paging starts. */
#define PAGE_NX (1ULL << 63)
#define PAGE_ADDRESS 0x000FFFFFFFFFF000ULL

WitU64 wit_arch_page_entry_make(WitU64 physical, WitU32 flags)
{
    return physical |
        PAGE_USER |
        ((flags & WIT_PAGE_READ) ? PAGE_PRESENT : 0) |
        ((flags & WIT_PAGE_WRITE) ? PAGE_WRITE : 0) |
        ((flags & WIT_PAGE_EXECUTE) ? 0 : PAGE_NX) |
        ((flags & WIT_PAGE_OWNED) ? PAGE_OWNED : 0) |
        ((flags & WIT_PAGE_ALIAS) ? PAGE_ALIAS : 0) |
        ((flags & WIT_PAGE_DEVICE) ? PAGE_DEVICE : 0);
}

WitU32 wit_arch_page_entry_flags(WitU64 entry)
{
    return ((entry & (PAGE_PRESENT | PAGE_USER)) == (PAGE_PRESENT | PAGE_USER) ? WIT_PAGE_READ : 0) |
        ((entry & PAGE_WRITE) ? WIT_PAGE_WRITE : 0) |
        ((entry & PAGE_NX) ? 0 : WIT_PAGE_EXECUTE) |
        ((entry & PAGE_OWNED) ? WIT_PAGE_OWNED : 0) |
        ((entry & PAGE_ALIAS) ? WIT_PAGE_ALIAS : 0) |
        ((entry & PAGE_DEVICE) == PAGE_DEVICE ? WIT_PAGE_DEVICE : 0);
}

WitU64 wit_arch_page_entry_physical(WitU64 entry)
{
    return entry & PAGE_ADDRESS;
}

void wit_arch_page_invalidate(const WitUserSpace *space, WitU64 address)
{
    /* No PCID, global user mappings or second CPU. Inactive CR3s are flushed on entry. */
    if ((wit_x64_read_cr3() & PAGE_ADDRESS) == space->Root) {
        wit_x64_invlpg((void *)address);
    }
}

WitU64 *wit_arch_page_entry(WitUserSpace *space, WitU64 address, int create)
{
    const WitU32 shifts[3] = {39, 30, 21};
    WitU64 *table = (WitU64 *)space->Root;
    if (!table) {
        return 0;
    }
    for (WitU32 level = 0; level < 3; ++level) {
        WitU64 *entry = &table[(address >> shifts[level]) & 511];
        if (!(*entry & PAGE_PRESENT)) {
            WitU64 page;
            if (!create || *entry != 0) {
                return 0;
            }
            page = wit_user_space_take_table(space);
            if (!page) {
                return 0;
            }
            *entry = page | PAGE_PRESENT | PAGE_WRITE | PAGE_USER;
        }
        if ((*entry & (PAGE_PRESENT | PAGE_USER | PAGE_LARGE)) != (PAGE_PRESENT | PAGE_USER)) {
            return 0;
        }
        table = (WitU64 *)(*entry & PAGE_ADDRESS);
    }
    return &table[(address >> 12) & 511];
}

/* Removes partially built paths after allocation failure. Never frees root or
 * visits the shared supervisor branch. Call only for validated user addresses. */
void wit_arch_page_prune(WitUserSpace *space, WitU64 address)
{
    const WitU32 shifts[3] = {39, 30, 21};
    WitU64 *tables[4], *parents[3];
    WitU32 depth = 0;
    tables[0] = (WitU64 *)space->Root;
    for (; depth < 3; ++depth) {
        WitU64 *entry = &tables[depth][(address >> shifts[depth]) & 511];
        if ((*entry & (PAGE_PRESENT | PAGE_USER | PAGE_LARGE)) != (PAGE_PRESENT | PAGE_USER)) {
            break;
        }
        parents[depth] = entry;
        tables[depth + 1] = (WitU64 *)(*entry & PAGE_ADDRESS);
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
            if ((entry & (PAGE_PRESENT | PAGE_USER | PAGE_LARGE)) != (PAGE_PRESENT | PAGE_USER)) {
                break;
            }
            table = (const WitU64 *)(entry & PAGE_ADDRESS);
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
    const WitU64 required = PAGE_PRESENT | PAGE_USER | (write ? PAGE_WRITE : 0);
    WitU64 *table = (WitU64 *)root;
    for (WitU32 level = 0; level < 4; ++level) {
        const WitU64 entry = table[(address >> shifts[level]) & 511];
        if ((entry & required) != required || (execute && (entry & PAGE_NX))) {
            return 0;
        }
        if (level == 3) {
            return (entry & PAGE_ADDRESS) | (address & 4095);
        }
        if (entry & PAGE_LARGE) {
            return 0;
        }
        table = (WitU64 *)(entry & PAGE_ADDRESS);
    }
    return 0;
}

int wit_arch_space_kernel_ready(void)
{
    const WitU64 shared = ((WitU64 *)wit_virtual_kernel_root())[0];
    const WitU64 storage = ((WitU64 *)wit_virtual_kernel_root())[WIT_X64_STORAGE_SLOT];
    return (shared & PAGE_PRESENT) && !(shared & PAGE_USER) && (storage & PAGE_PRESENT) && !(storage & PAGE_USER);
}

void wit_arch_space_install_kernel(WitU64 root)
{
    /* Slots 1 (fixed image) and 2 (dynamic memory) are private. */
    ((WitU64 *)root)[0] = ((WitU64 *)wit_virtual_kernel_root())[0];
    ((WitU64 *)root)[WIT_X64_STORAGE_SLOT] =
        ((WitU64 *)wit_virtual_kernel_root())[WIT_X64_STORAGE_SLOT]; // Shared supervisor branch, never a user table.
}

int wit_arch_space_active(WitU64 root)
{
    return (wit_x64_read_cr3() & PAGE_ADDRESS) == root;
}

/* Another process's root (or the kernel's) within a run: loading CR3 flushes the previous non-global translations;
 * the kernel's own mappings are in every root (wit_arch_space_install_kernel). */
void wit_arch_space_switch(WitU64 root)
{
    if ((wit_x64_read_cr3() & PAGE_ADDRESS) != root) {
        wit_x64_write_cr3(root);
    }
}

void wit_arch_publish_code_page(WitU64 physical)
{
    (void)physical; /* x64 instruction fetch observes earlier data writes; only serialization remains. */
}

void wit_arch_publish_code(void)
{
    // CPUID is a real serializing instruction on the sole online x64 CPU.
    // This is instruction publication; the existing data-fence API is separate.
    int cpu[4];
    wit_x64_cpuid(cpu, 0);
}
