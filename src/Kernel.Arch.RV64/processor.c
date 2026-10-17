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

static WitU64 boot_hart = ~0ULL;

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
