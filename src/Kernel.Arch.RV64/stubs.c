#include "witos/arch.h"
#include "witos/platform.h"
#include "rv64.h"

/* What the RISC-V port does not implement yet: the secondary harts (plan step V1.4) — their start through SBI HSM and
 * the inter-processor interrupts. Each operation panics by name, so that nothing pretends to work; V1.4 replaces this
 * file with the port. */

#pragma clang diagnostic ignored "-Wunused-parameter"

#define NOT_PORTED_SMP(name) wit_panic("RV64: " name " is not ported (plan step V1.4)")

void wit_arch_secondary_prepare(const struct WitBootInfo *boot, WitU32 index, WitU64 hardware_id)
{
    NOT_PORTED_SMP("wit_arch_secondary_prepare");
}

int wit_arch_secondary_start(WitU32 index, WitU64 hardware_id)
{
    NOT_PORTED_SMP("wit_arch_secondary_start");
}

void wit_arch_ipi(WitU64 hardware_id, WitU32 kind)
{
    NOT_PORTED_SMP("wit_arch_ipi");
}
