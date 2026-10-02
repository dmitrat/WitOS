#include "minipal_cpu.witos.h"
#if !defined(_M_X64) || !defined(HOST_AMD64)
#error This adapter requires the WitOS x64 FXSAVE-only profile
#endif
extern "C" void __cpuidex(int registers[4], int leaf, int subleaf);
#pragma intrinsic(__cpuidex)

static void read(WitU32 leaf, WitU32 registers[4])
{
    int values[4];
    __cpuidex(values, (int)leaf, 0);
    for (unsigned i = 0; i < 4; ++i) {
        registers[i] = (WitU32)values[i];
    }
}

int wit_x64_minipal_decode(WitU32 maximum, WitU32 ecx, WitU32 edx)
{
    const WitU32 baseline = (1U << 24) | (1U << 25) | (1U << 26); // FXSR/SSE/SSE2
    if (maximum < 1 || (edx & baseline) != baseline || (ecx & (1U << 27))) {
        return -1;
    }
    const WitU32 sse42 = (1U << 0) | (1U << 9) | (1U << 19) | (1U << 20) | (1U << 23);
    const WitU32 aes = (1U << 25) | (1U << 1); // AES and PCLMULQDQ, as upstream requires.
    int flags = 0;
    if ((ecx & sse42) == sse42) {
        flags |= XArchIntrinsicConstants_Sse42;
    }
    if ((ecx & aes) == aes) {
        flags |= XArchIntrinsicConstants_Aes;
    }
    // Other optional sets are deliberately withheld in the initial profile.
    // In particular, hardware AVX/XSAVE does not imply preserved YMM/ZMM state.
    return flags;
}

extern "C" int minipal_getcpufeatures(void)
{
    WitU32 values[4];
    read(0, values);
    const WitU32 maximum = values[0];
    if (maximum < 1) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    read(1, values);
    const int result = wit_x64_minipal_decode(maximum, values[2], values[3]);
    if (result < 0) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return result;
}

WitU32 wit_x64_minipal_leaf1_ecx()
{
    WitU32 values[4];
    read(1, values);
    return values[2];
}

bool wit_x64_minipal_brand_match(const unsigned char *brand)
{
    static const unsigned char needle[] = "VirtualApple";
    for (unsigned i = 0; i < 48 && brand[i]; ++i) {
        unsigned j = 0;
        while (j < sizeof(needle) - 1 && i + j < 48 && brand[i + j] == needle[j]) {
            ++j;
        }
        if (j == sizeof(needle) - 1) {
            return true;
        }
    }
    return false;
}

extern "C" bool minipal_detect_rosetta(void)
{
    WitU32 values[4];
    read(0x80000000U, values);
    if (values[0] < 0x80000004U) {
        return false;
    }
    unsigned char brand[48];
    for (unsigned leaf = 0; leaf < 3; ++leaf) {
        read(0x80000002U + leaf, values);
        for (unsigned word = 0; word < 4; ++word) {
            for (unsigned byte = 0; byte < 4; ++byte) {
                brand[leaf * 16 + word * 4 + byte] = (unsigned char)(values[word] >> (byte * 8));
            }
        }
    }
    return wit_x64_minipal_brand_match(brand);
}
