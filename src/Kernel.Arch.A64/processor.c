#include "witos/arch.h"
#include "witos/platform.h"
#include "a64.h"

/* The identity and the features of the running ARM64 processor (plan step K7.1): the affinity fields of MPIDR_EL1,
 * and the low words of ID_AA64ISAR0_EL1 (instruction set) and ID_AA64PFR0_EL1 (processor features). */

#define MPIDR_AFFINITY 0x000000FF00FFFFFFULL

WitU64 wit_arch_processor_id(void)
{
    return wit_a64_processor_id() & MPIDR_AFFINITY;
}

WitU64 wit_arch_processor_features(void)
{
    return (wit_a64_isa_features() & 0xFFFFFFFFULL) | ((wit_a64_processor_features() & 0xFFFFFFFFULL) << 32);
}

/* No port space (ABI-1 1.7, H5.2, ADR 0031): DEVICE_PORT is UNSUPPORTED, and the kernel never asks for an access. */
int wit_arch_ports_supported(void)
{
    return 0;
}

WitU32 wit_arch_port_read(WitU32 port, WitU32 width)
{
    (void)port;
    (void)width;
    wit_panic("Port read on an architecture without a port space");
}

void wit_arch_port_write(WitU32 port, WitU32 width, WitU32 value)
{
    (void)port;
    (void)width;
    (void)value;
    wit_panic("Port write on an architecture without a port space");
}
