#include "witos/platform.h"

/* What the virt (riscv64) board does not serve yet: the secondary harts (plan step V1.4) — their PLIC contexts, their
 * interrupt enables and the inter-processor interrupts through SBI. Each operation panics by name, so that nothing
 * pretends to work; V1.4 replaces this file with the port. */

#pragma clang diagnostic ignored "-Wunused-parameter"

#define NOT_PORTED(name) wit_panic("QemuVirtRv: " name " is not ported (plan step V1.4)")

void wit_platform_processor_prepare(const struct WitBootInfo *boot, WitU32 index, WitU64 hardware_id)
{
    NOT_PORTED("wit_platform_processor_prepare");
}

void wit_platform_processor_interrupts_enable(WitU32 index, WitU64 hardware_id)
{
    NOT_PORTED("wit_platform_processor_interrupts_enable");
}

void wit_platform_ipi(WitU64 hardware_id, WitU32 kind)
{
    NOT_PORTED("wit_platform_ipi");
}

void wit_platform_ipi_complete(WitU32 number)
{
    NOT_PORTED("wit_platform_ipi_complete");
}
