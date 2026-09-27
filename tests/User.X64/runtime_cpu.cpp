#include "minipal_cpu.witos.h"
#include "tls.h"
#include "error.h"
#include "protocol.h"
#include <errno.h>
extern "C" WitU64 wit_cpu_sse42();
extern "C" WitU64 wit_cpu_aes(WitU64 seed);
static int expected;
static bool decode_tests()
{
    const WitU32 baseline = 7U << 24;
    const WitU32 bundle = (1U << 0) | (1U << 9) | (1U << 19) | (1U << 20) | (1U << 23);
    const WitU32 aes = (1U << 25) | (1U << 1);
    if (wit_x64_minipal_decode(0, 0, baseline) != -1 || wit_x64_minipal_decode(1, 0, baseline) != 0 ||
        wit_x64_minipal_decode(1, 1U << 27, baseline) != -1 ||
        wit_x64_minipal_decode(1, ~0U & ~(1U << 27), baseline) != (XArchIntrinsicConstants_Sse42 | XArchIntrinsicConstants_Aes)) return false;
    for (unsigned bit = 24; bit <= 26; ++bit)
        if (wit_x64_minipal_decode(1, bundle, baseline & ~(1U << bit)) != -1) return false;
    const unsigned sseBits[] = {0, 9, 19, 20, 23};
    for (unsigned bit : sseBits)
        if (wit_x64_minipal_decode(1, (bundle | aes) & ~(1U << bit), baseline) != XArchIntrinsicConstants_Aes) return false;
    if (wit_x64_minipal_decode(1, bundle | (1U << 25), baseline) != XArchIntrinsicConstants_Sse42 ||
        wit_x64_minipal_decode(1, bundle | (1U << 1), baseline) != XArchIntrinsicConstants_Sse42) return false;
    unsigned char brand[48];
    const char needle[] = "VirtualApple";
    const unsigned offsets[] = {0, 17, 36};
    for (unsigned offset : offsets) {
        for (auto& ch : brand) ch = ' ';
        for (unsigned i = 0; i < sizeof(needle) - 1; ++i) brand[offset + i] = (unsigned char)needle[i];
        if (!wit_x64_minipal_brand_match(brand)) return false;
        brand[offset] = 'v';
        if (wit_x64_minipal_brand_match(brand)) return false;
    }
    brand[0] = 0;
    for (unsigned i = 0; i < sizeof(needle) - 1; ++i) brand[1 + i] = (unsigned char)needle[i];
    return !wit_x64_minipal_brand_match(brand);
}
static bool execute(int flags, WitU64 seed)
{
    if (flags & XArchIntrinsicConstants_Sse42) {
        WitU32 crc = 0, data = 0x12345678;
        for (unsigned i = 0; i < 32; ++i) { const bool bit = ((crc ^ data) & 1) != 0; crc >>= 1; if (bit) crc ^= 0x82F63B78; data >>= 1; }
        if (wit_cpu_sse42() != crc) return false;
    }
    return !(flags & XArchIntrinsicConstants_Aes) || wit_cpu_aes(seed) == (0x6363636363636363ULL ^ seed);
}
extern "C" bool wit_test_cpu_early()
{
    wit_native_error_set(0x71529384);
    if (!decode_tests()) return false;
    expected = minipal_getcpufeatures();
    const WitU32 leaf = wit_x64_minipal_leaf1_ecx();
    if ((leaf & (1U << 27)) || (expected & ~(XArchIntrinsicConstants_Sse42 | XArchIntrinsicConstants_Aes)) ||
        minipal_detect_rosetta() || !execute(expected, 1)) return false;
    ((WitU64*)WIT_GC_INFO_REPORT)[2] = (WitU64)expected;
    ((WitU64*)WIT_GC_INFO_REPORT)[3] = (leaf >> 28) & 1;
    return wit_native_error_get() == 0x71529384;
}
static WitU64 worker(WitU64 index)
{
    wit_native_error_set((WitU32)(800 + index)); errno = (int)(900 + index);
    for (unsigned i = 0; i < 4; ++i)
        if (minipal_getcpufeatures() != expected || minipal_detect_rosetta() || !execute(expected, index + 17) ||
            wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK ||
            errno != 900 + index || wit_native_error_get() != 800 + index) return 1791;
    return WIT_TEST_EXIT_CODE;
}
extern "C" bool wit_test_cpu_threads()
{
    errno = 99;
    WitU64 handles[3], result;
    for (WitU64 i = 0; i < 3; ++i) if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) return false;
    for (unsigned i = 0; i < 3; ++i)
        if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE) return false;
    return errno == 99 && wit_native_error_get() == 0x71529384;
}
