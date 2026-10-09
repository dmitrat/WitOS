#include "witos/arch.h"
#include "witos/x64_instructions.h"

/* The identity and the features of the running x64 processor (plan step K7.1), from CPUID: the x2APIC id of leaf
 * 0xB where the leaf exists, else the initial APIC id of leaf 1; the feature words of leaf 1. */

static void cpuid(WitU32 leaf, WitU32 subleaf, WitU32 registers[4])
{
    int raw[4];
    wit_x64_cpuidex(raw, (int)leaf, (int)subleaf);
    for (WitU32 i = 0; i < 4; ++i) {
        registers[i] = (WitU32)raw[i];
    }
}

WitU64 wit_arch_processor_id(void)
{
    WitU32 r[4];
    cpuid(0, 0, r);
    if (r[0] >= 0xB) {
        cpuid(0xB, 0, r);
        if (r[1] != 0) { /* EBX zero means the leaf is not implemented despite the maximum. */
            return r[3];
        }
    }
    cpuid(1, 0, r);
    return r[1] >> 24;
}

WitU64 wit_arch_processor_features(void)
{
    WitU32 r[4];
    cpuid(1, 0, r);
    return (WitU64)r[2] | ((WitU64)r[3] << 32);
}
