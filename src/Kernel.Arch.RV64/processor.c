#include "witos/arch.h"
#include "witos/platform.h"
#include "rv64.h"

/* The identity and the features of the running RISC-V processor (plan step V1.2). S-mode reads no identification
 * register: mhartid and misa are the machine mode's. The boot hart is the one the firmware's hart state machine
 * reports started while every other hart is parked (OpenSBI starts the next stage on one hart and keeps the rest
 * stopped until SBI HSM starts them); the probe runs once and is cached. A started secondary hart (V1.4) knows its
 * identity by the kernel stack it runs on. The features word stays zero: the ISA string is the device tree's, and no
 * feature of it is a kernel's concern yet. */

#define HART_PROBE_LIMIT 64U

/* The record of each hart (P1.1): tp points to it while the hart runs in S-mode, and sscratch while U-mode runs, so that
 * a trap from U-mode finds the kernel stack top of the thread the hart runs (KernelSp) and keeps the thread's sp
 * (UserSp) while it switches stacks; entry.S reaches those two words by their offsets, 0 and 8. */
typedef struct WitRv64Hart {
    WitU64 KernelSp;
    WitU64 UserSp;
    WitU64 Index;
    WitU64 Reserved;
} WitRv64Hart;

_Static_assert(__builtin_offsetof(WitRv64Hart, KernelSp) == 0 && __builtin_offsetof(WitRv64Hart, UserSp) == 8,
    "entry.S reaches the hart record's stack words by offset");

static WitRv64Hart harts[WIT_PROCESSOR_CAPACITY];
static WitU64 boot_hart = ~0ULL;

void wit_arch_processor_set_index(WitU32 index)
{
    if (index >= WIT_PROCESSOR_CAPACITY) {
        wit_panic("Processor number outside the table");
    }
    WitRv64Hart *const hart = &harts[index];
    hart->Index = index;
    __asm__ volatile("mv tp, %0" : : "r"(hart) : "memory");
}

WitU32 wit_arch_processor_index(void)
{
    const WitRv64Hart *hart;
    __asm__ volatile("mv %0, tp" : "=r"(hart));
    return (WitU32)hart->Index;
}

WitU64 wit_arch_processor_id(void)
{
    WitU64 hart;
    if (wit_rv64_secondary_hart(&hart)) {
        return hart;
    }
    if (boot_hart != ~0ULL) {
        return boot_hart;
    }
    for (WitU64 hart = 0; hart < HART_PROBE_LIMIT; ++hart) {
        WitU64 status = 0;
        if (wit_rv64_sbi(WIT_RV64_SBI_HSM, WIT_RV64_SBI_HSM_STATUS, hart, 0, 0, &status) == 0 &&
            status == WIT_RV64_SBI_HSM_STARTED) {
            boot_hart = hart;
            return boot_hart;
        }
    }
    wit_panic("No started hart in the firmware's hart state machine");
}

WitU64 wit_arch_processor_features(void)
{
    return 0;
}

/* The size of the data cache is not reported: the base ISA has no cache identification register. */
WitU64 wit_arch_cache_size(void)
{
    return 0;
}
