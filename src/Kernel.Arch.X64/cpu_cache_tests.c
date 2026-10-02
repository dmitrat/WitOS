#include "cpu_cache.h"
#include "witos/platform.h"

static WitU32 mode, calls;

static void fixture(WitU32 leaf, WitU32 subleaf, WitU32 r[4])
{
    ++calls;
    for (WitU32 i = 0; i < 4; ++i) {
        r[i] = 0;
    }
    if (leaf == 0) {
        r[0] = mode == 8 ? 3 : 4;
        if (mode == 1 || mode == 2 || mode == 9 || mode == 12 || mode == 13 || mode == 15 || mode == 16) {
            r[1] = 0x68747541U;
            r[3] = 0x69746E65U;
            r[2] = 0x444D4163U;
        } else if (mode != 3) {
            r[1] = 0x756E6547U;
            r[3] = 0x49656E69U;
            r[2] = 0x6C65746EU;
        }
    } else if (leaf == 0x80000000U) {
        r[0] = mode == 16 ? 0U : ((mode == 1 || mode == 12 || mode == 13) ? 0x8000001DU : 0x80000006U);
    } else if (leaf == 0x80000001U) {
        r[2] = mode == 12 ? 0 : 1U << 22;
    } else if (leaf == 0x80000006U && (mode == 2 || mode == 12 || mode == 13 || mode == 15)) {
        r[2] = (512U << 16) | (6U << 12) | 64U;
        r[3] = (16U << 18) | (8U << 12) | 64U;
        if (mode == 15) {
            r[2] = (512U << 16) | (3U << 12) | 64U; // Reserved associativity.
        }
    } else if (leaf == 4 || leaf == 0x8000001DU) {
        if (mode == 4 || (mode == 11 && subleaf)) {
            return;
        }
        if (subleaf >= 2 && mode != 6) {
            return;
        }
        r[0] = subleaf == 0 ? 33U : 99U; // L1 data / L3 unified.
        r[1] = subleaf == 0 ? (7U << 22) | 63U : (15U << 22) | 63U;
        r[2] = subleaf == 0 ? 63 : (mode == 1 ? 16383 : 8191);
        if (mode == 5) {
            r[1] = 0xFFFFFFFFU;
            r[2] = 0xFFFFFFFFU;
        }
        if (mode == 7) {
            r[0] = 36; // Reserved cache type.
        }
        if (mode == 11) {
            r[0] = 34;
            r[1] = (7U << 22) | 63U;
            r[2] = 127;
        }
        if (mode == 13) {
            r[0] = 37; // Malformed deterministic data must not fall back.
        }
        if (mode == 14) {
            r[2] = subleaf == 0 ? 32767 : 63; // Largest size, not highest level.
        }
        if (mode == 10) {
            r[0] = 1; // Missing level.
        }
    }
}

void wit_x64_cache_self_test(void)
{
    static const WitU64 expected[] = {
        8388608, 16777216, 8388608, 0, 0, 0, 0, 0, 0, 0, 0, 65536, 8388608, 0, 16777216, 0, 0};
    for (mode = 0; mode < sizeof(expected) / sizeof(expected[0]); ++mode) {
        calls = 0;
        if (wit_x64_decode_cache(fixture) != expected[mode] || calls > 19) {
            wit_panic("CPUID cache decoder contract failed");
        }
    }
    if (wit_x64_decode_cache(0) || !wit_arch_cache_size()) {
        wit_panic("CPUID cache discovery unavailable in test CPU profile");
    }
    wit_console_write("[CACHE] Largest reported bytes: ");
    wit_console_write_u64(wit_arch_cache_size());
    wit_console_write("\n");
    wit_console_write("[TEST-PASS] User.CpuCacheDiscovery\n");
}
