#include "witos/arch.h"
#include "witos/platform.h"
#include "rv64.h"

/* The identity and the features of the running RISC-V processor (plan step V1.2). S-mode reads no identification
 * register: mhartid and misa are the machine mode's. The boot hart is the one the firmware's hart state machine
 * reports started while every other hart is parked (OpenSBI starts the next stage on one hart and keeps the rest
 * stopped until SBI HSM starts them); the probe runs once and is cached. A started secondary hart (V1.4) records its
 * identity in the record tp names at its start (wit_rv64_hart_identify), where it knew it by the kernel stack it ran on
 * until 2026-10-22: a trap on a thread's kernel stack, which lies in the kernel's stack window since P1.5, found no
 * start stack and took the boot hart's identity, so a doorbell there read the boot hart's pending kind and panicked when
 * none had been sent yet (W4.1's CI, riscv64-libc-smp2). The features word stays zero: the ISA string is the device
 * tree's, and no feature of it is a kernel's concern yet. */

#define HART_PROBE_LIMIT 64U

/* The record of each hart (P1.1): tp points to it while the hart runs in S-mode, and sscratch while U-mode runs, so that
 * a trap from U-mode finds the kernel stack top of the thread the hart runs (KernelSp) and keeps the thread's sp
 * (UserSp) while it switches stacks; entry.S reaches those two words by their offsets, 0 and 8. */
typedef struct WitRv64Hart {
    WitU64 KernelSp;
    WitU64 UserSp;
    WitU64 Index;
    WitU64 Id; /* the hart's identity, once Known */
    WitU64 Known;
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

/* The record tp names, when it is one of the kernel's: zero before the hart set its index, while tp holds whatever the
 * firmware left. */
static WitRv64Hart *record(void)
{
    WitRv64Hart *hart;
    __asm__ volatile("mv %0, tp" : "=r"(hart));
    for (WitU32 i = 0; i < WIT_PROCESSOR_CAPACITY; ++i) {
        if (hart == &harts[i]) {
            return hart;
        }
    }
    return 0;
}

/* A started hart's identity into its record (V1.4): hart_start gave the entry the hart it started, and the record keeps
 * it for every stack the hart runs on. */
void wit_rv64_hart_identify(WitU64 id)
{
    WitRv64Hart *const hart = record();
    if (!hart) {
        wit_panic("A hart identifies itself before its record");
    }
    hart->Id = id;
    hart->Known = 1;
}

WitU32 wit_arch_processor_index(void)
{
    const WitRv64Hart *hart;
    __asm__ volatile("mv %0, tp" : "=r"(hart));
    return (WitU32)hart->Index;
}

WitU64 wit_arch_processor_id(void)
{
    WitRv64Hart *const hart = record();
    if (hart && hart->Known) {
        return hart->Id;
    }
    /* Before a record knows: the boot hart, found through the firmware's hart state machine once; a secondary records
     * its identity first thing at its start and never asks before. */
    if (hart && hart->Index != 0) {
        wit_panic("A secondary hart asks its identity before its start recorded it");
    }
    if (boot_hart == ~0ULL) {
        for (WitU64 probe = 0; probe < HART_PROBE_LIMIT && boot_hart == ~0ULL; ++probe) {
            WitU64 status = 0;
            if (wit_rv64_sbi(WIT_RV64_SBI_HSM, WIT_RV64_SBI_HSM_STATUS, probe, 0, 0, &status) == 0 &&
                status == WIT_RV64_SBI_HSM_STARTED) {
                boot_hart = probe;
            }
        }
        if (boot_hart == ~0ULL) {
            wit_panic("No started hart in the firmware's hart state machine");
        }
    }
    if (hart) {
        hart->Id = boot_hart;
        hart->Known = 1;
    }
    return boot_hart;
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
