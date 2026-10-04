#include "cpu_cache.h"

void __cpuidex(int registers[4], int leaf, int subleaf);
#pragma intrinsic(__cpuidex)

static void hardware(WitU32 leaf, WitU32 subleaf, WitU32 registers[4])
{
    int raw[4];
    __cpuidex(raw, (int)leaf, (int)subleaf);
    for (WitU32 i = 0; i < 4; ++i) {
        registers[i] = (WitU32)raw[i];
    }
}

static WitU64 maximum(WitU64 a, WitU64 b)
{
    return a > b ? a : b;
}

static WitU64 deterministic(WitCpuidReader read, WitU32 leaf)
{
    WitU64 largest = 0;
    for (WitU32 index = 0; index < 16; ++index) {
        WitU32 r[4];
        read(leaf, index, r);
        const WitU32 type = r[0] & 31;
        if (!type) {
            return largest;
        }
        if (type > 3 || !((r[0] >> 5) & 7)) {
            return 0;
        }
        const WitU64 ways = ((r[1] >> 22) & 1023) + 1ULL;
        const WitU64 partitions = ((r[1] >> 12) & 1023) + 1ULL;
        const WitU64 line = (r[1] & 4095) + 1ULL;
        const WitU64 sets = (WitU64)r[2] + 1ULL;
        const WitU64 per_set = ways * partitions * line; // At most 2^32.
        if (sets > (~0ULL) / per_set) {
            return 0;
        }
        largest = maximum(largest, per_set * sets);
    }
    return 0; // Unterminated enumeration is not a complete discovery result.
}

static int legacy_associativity(WitU32 field)
{
    return field == 1 || field == 2 || field == 4 || field == 6 || field >= 8 && field != 9;
}

static WitU64 legacy_amd(WitCpuidReader read, WitU32 maximum_leaf)
{
    WitU32 r[4];
    WitU64 largest = 0;
    if (maximum_leaf >= 0x80000005U) {
        read(0x80000005U, 0, r);
        for (WitU32 i = 2; i < 4; ++i) {
            const WitU64 size = (WitU64)(r[i] >> 24) * 1024;
            if (size && (!(r[i] & 255) || !((r[i] >> 16) & 255))) {
                return 0;
            }
            largest = maximum(largest, size);
        }
    }
    if (maximum_leaf >= 0x80000006U) {
        read(0x80000006U, 0, r);
        const WitU64 l2 = (WitU64)(r[2] >> 16) * 1024;
        const WitU64 l3 = (WitU64)(r[3] >> 18) * 512 * 1024;
        if ((l2 && (!(r[2] & 255) || !legacy_associativity((r[2] >> 12) & 15))) ||
            (l3 && (!(r[3] & 255) || !legacy_associativity((r[3] >> 12) & 15)))) {
            return 0;
        }
        largest = maximum(largest, maximum(l2, l3));
    }
    return largest;
}

WitU64 wit_x64_decode_cache(WitCpuidReader read)
{
    WitU32 r[4];
    if (!read) {
        return 0;
    }
    read(0, 0, r);
    if (r[1] == 0x756E6547U && r[3] == 0x49656E69U && r[2] == 0x6C65746EU) {
        return r[0] >= 4 ? deterministic(read, 4) : 0; // GenuineIntel
    }
    if (r[1] != 0x68747541U || r[3] != 0x69746E65U || r[2] != 0x444D4163U) {
        return 0;
    }
    read(0x80000000U, 0, r);
    const WitU32 maximum_leaf = r[0];
    if (maximum_leaf < 0x80000000U) {
        return 0;
    }
    if (maximum_leaf >= 0x8000001DU) {
        read(0x80000001U, 0, r);
        if (r[2] & (1U << 22)) {
            return deterministic(read, 0x8000001DU);
        }
    }
    return legacy_amd(read, maximum_leaf); // AuthenticAMD, older CPUID format.
}

WitU64 wit_arch_cache_size(void)
{
    // Called on the sole online CPU with IF clear; no hotplug or migration.
    static WitU64 bytes;
    static int ready;
    if (!ready) {
        bytes = wit_x64_decode_cache(hardware);
        ready = 1;
    }
    return bytes;
}
