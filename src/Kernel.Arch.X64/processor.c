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

/* Port I/O for DEVICE_PORT (ABI-1 1.7, H5.2, ADR 0031): the kernel validated the port, the width and the value. */
int wit_arch_ports_supported(void)
{
    return 1;
}

WitU32 wit_arch_port_read(WitU32 port, WitU32 width)
{
    return width == 1 ? wit_x64_in8((WitU16)port)
        : width == 2  ? wit_x64_in16((WitU16)port)
                      : wit_x64_in32((WitU16)port);
}

void wit_arch_port_write(WitU32 port, WitU32 width, WitU32 value)
{
    if (width == 1) {
        wit_x64_out8((WitU16)port, (WitU8)value);
    } else if (width == 2) {
        wit_x64_out16((WitU16)port, (WitU16)value);
    } else {
        wit_x64_out32((WitU16)port, value);
    }
}
