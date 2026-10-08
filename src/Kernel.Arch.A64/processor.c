#include "witos/arch.h"
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
