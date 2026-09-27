#ifndef _CRT_SECURE_NO_WARNINGS
#define _CRT_SECURE_NO_WARNINGS
#endif
#include "tls.h"
#include "error.h"
#include "protocol.h"
#include <string.h>
#include <stdlib.h>
#include <errno.h>
#include <limits.h>

static void* (*volatile move_bytes)(void*, const void*, size_t) = &memmove;
static void* (*volatile fill_bytes)(void*, int, size_t) = &memset;
static int (*volatile compare_bytes)(const void*, const void*, size_t) = &memcmp;
static char* (*volatile copy_string)(char*, const char*) = &strcpy;
static const char* (*volatile find_string)(const char*, const char*) = &strstr;
static unsigned long (*volatile parse32)(const char*, char**, int) = &strtoul;
static unsigned char pattern(size_t index) { return (unsigned char)(index * 37 + 11); }
static bool overlap()
{
    unsigned char data[96];
    const size_t offsets[] = {0, 1, 7, 16, 31, 48};
    const size_t lengths[] = {0, 1, 2, 7, 15, 16, 17, 31, 48};
    for (size_t from : offsets) for (size_t to : offsets) for (size_t n : lengths) {
        for (size_t i = 0; i < sizeof(data); ++i) data[i] = pattern(i);
        if (move_bytes(data + to, data + from, n) != data + to) return false;
        for (size_t i = 0; i < sizeof(data); ++i)
            if (data[i] != pattern(i >= to && i - to < n ? from + i - to : i)) return false;
    }
    const int values[] = {-1, 0, 0x180};
    for (int value : values) {
        for (size_t i = 0; i < sizeof(data); ++i) data[i] = pattern(i);
        if (fill_bytes(data + 7, value, 65) != data + 7) return false;
        for (size_t i = 0; i < sizeof(data); ++i)
            if (data[i] != (i >= 7 && i < 72 ? (unsigned char)value : pattern(i))) return false;
    }
    const unsigned char a[] = {0, 128, 255}, b[] = {0, 128, 0};
    return compare_bytes(a, b, 2) == 0 && compare_bytes(a, b, 3) > 0 && compare_bytes(b, a, 3) < 0 &&
        move_bytes(nullptr, nullptr, 0) == nullptr && fill_bytes(nullptr, 1, 0) == nullptr &&
        compare_bytes(nullptr, nullptr, 0) == 0;
}
static bool guarded()
{
    WitU64 source = 0, target = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 3 * 4096, 4096, 0, &source) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_RESERVE, 3 * 4096, 4096, 0, &target) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, source + 4096, 4096, 3, nullptr) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, target + 4096, 4096, 3, nullptr) != WIT_STATUS_OK) return false;
    auto src = (unsigned char*)(source + 4096), dst = (unsigned char*)(target + 4096);
    for (size_t i = 0; i < 4096; ++i) src[i] = pattern(i);
    src[0] = 0x80; src[1] = 0xff; src[2] = 0;
    const char sample[] = "ababaXYZ";
    for (size_t i = 0; i < sizeof(sample); ++i) src[4096 - sizeof(sample) + i] = (unsigned char)sample[i];
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, source + 4096, 4096, WIT_MEMORY_READ, nullptr) != WIT_STATUS_OK) return false;
    const size_t lengths[] = {1, 7, 31, 33, 63, 65, 127};
    for (size_t n : lengths) {
        dst[4096 - n - 1] = 91;
        if (fill_bytes(dst + 4096 - n, -1, n) != dst + 4096 - n || dst[4096 - n - 1] != 91) return false;
        for (size_t i = 4096 - n; i < 4096; ++i) if (dst[i] != 255) return false;
        if (move_bytes(dst + 4096 - n, src + 4096 - n, n) != dst + 4096 - n ||
            compare_bytes(src + 4096 - n, dst + 4096 - n, n) != 0 || dst[4096 - n - 1] != 91) return false;
    }
    char* hay = (char*)src + 4096 - sizeof(sample);
    char* needle = (char*)dst + 4096 - 5;
    const char match[] = "abaX";
    for (size_t i = 0; i < sizeof(match); ++i) needle[i] = match[i];
    if (find_string(hay, needle) != hay + 2 || find_string(hay, "XYZ") != hay + 5 || find_string(hay, "XYZ!") ||
        find_string(hay, "ababaXYZ!") || find_string(hay, (char*)src + 4095) != hay ||
        find_string((char*)src + 4095, "x") || find_string(hay, hay) != hay) return false;
    char* copied = (char*)dst + 4096 - sizeof(sample);
    fill_bytes(copied, 0xa5, sizeof(sample));
    dst[4096 - sizeof(sample) - 1] = 91;
    if (copy_string(copied, hay) != copied || compare_bytes(copied, hay, sizeof(sample)) != 0 ||
        dst[4096 - sizeof(sample) - 1] != 91) return false;
    dst[4095] = 73;
    if (copy_string((char*)dst + 4095, (char*)src + 4095) != (char*)dst + 4095 || dst[4095] != 0) return false;
    dst[0] = dst[1] = dst[2] = dst[3] = 77;
    if (copy_string((char*)dst, (char*)src) != (char*)dst || dst[0] != 0x80 || dst[1] != 0xff ||
        dst[2] != 0 || dst[3] != 77 || find_string((char*)src, "\xFF") != (char*)src + 1) return false;
    return wit_native_call(WIT_CALL_MEMORY_RELEASE, source, 0, 0, nullptr) == WIT_STATUS_OK &&
        wit_native_call(WIT_CALL_MEMORY_RELEASE, target, 0, 0, nullptr) == WIT_STATUS_OK;
}
extern "C" bool wit_test_crt_memory()
{
    wit_native_error_set(0x81234567);
    return overlap() && guarded() && wit_native_error_get() == 0x81234567;
}
static bool numbers()
{
    struct Case { const char* text; int base; unsigned long value; size_t end; int error; };
    const Case cases[] = {
        {"4294967295!",10,ULONG_MAX,10,71}, {"4294967296tail",10,ULONG_MAX,10,ERANGE},
        {"-1",10,ULONG_MAX,2,71}, {"-4294967295",10,1,11,71}, {"-4294967296",10,ULONG_MAX,11,ERANGE},
        {" \t+0x2a!",0,42,7,71}, {"0x",0,0,1,71}, {"09",0,0,1,71}, {"-",10,0,0,71},
        {"",10,0,0,71}, {"xyz",36,44027,3,71}, {"1012",2,5,3,71},
        {"ffffffff",16,ULONG_MAX,8,71}, {"100000000",16,ULONG_MAX,9,ERANGE},
        {"184467440737095516160000x",10,ULONG_MAX,24,ERANGE},
        {"12",1,0,0,EINVAL}, {"12",37,0,0,EINVAL}, {"12",-1,0,0,EINVAL}
    };
    for (const auto& item : cases) {
        errno = 71;
        char* end = nullptr;
        if (parse32(item.text, &end, item.base) != item.value || end != item.text + item.end || errno != item.error) return false;
    }
    errno = 71;
    return parse32("42", nullptr, 10) == 42 && errno == 71;
}
static WitU64 worker(WitU64 index)
{
    wit_native_error_set((WitU32)(700 + index));
    if (!numbers() || wit_native_error_get() != 700 + index) return 1770;
    errno = (int)(100 + index);
    if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK || errno != 100 + index) return 1771;
    return WIT_TEST_EXIT_CODE;
}
extern "C" bool wit_test_crt_numbers()
{
    if (!numbers() || wit_native_error_get() != 0x81234567) return false;
    errno = 99;
    for (unsigned round = 0; round < 2; ++round) {
        WitU64 handles[3], result;
        for (WitU64 i = 0; i < 3; ++i) if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) return false;
        for (unsigned i = 0; i < 3; ++i)
            if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE) return false;
    }
    return errno == 99 && wit_native_error_get() == 0x81234567;
}
