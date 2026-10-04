#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "../User/protocol.h"
#include <bcrypt.h>
#include <errno.h>
extern "C" NTSTATUS WINAPI wit_random_direct(BCRYPT_ALG_HANDLE, PUCHAR, ULONG, ULONG);
extern "C" const void *const __imp_BCryptGenRandom;
extern "C" bool wit_gs_check_abi(uintptr_t);
static volatile WitU64 counts[4][3];
static unsigned char first[3][32];

static void note(unsigned slot, WitU64 bytes)
{
    if (bytes) {
        counts[slot][0]++;
        counts[slot][1] += bytes;
        counts[slot][2] += (bytes + 63) / 64;
    }
}

static bool different(const unsigned char *a, const unsigned char *b, unsigned count)
{
    unsigned value = 0;
    for (unsigned i = 0; i < count; ++i) {
        value |= a[i] ^ b[i];
    }
    return value != 0;
}

static bool generated(unsigned slot, unsigned char *output, ULONG count, bool direct = false)
{
    const auto status = direct ? wit_random_direct(nullptr, output, count, BCRYPT_USE_SYSTEM_PREFERRED_RNG)
                               : BCryptGenRandom(nullptr, output, count, BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    if (status) {
        return false;
    }
    note(slot, count);
    return true;
}

static WitU64 worker(WitU64 index)
{
    SetLastError(DWORD(2100 + index));
    errno = int(2200 + index);
    unsigned char output[32];
    for (unsigned i = 0; i < 4; ++i) {
        if (!generated(unsigned(index), i ? output : first[index], 32) ||
            !wit_gs_check_abi(__security_cookie) ||
            wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK ||
            GetLastError() != 2100 + index ||
            errno != 2200 + index) {
            return 2401;
        }
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" WitU64 wit_test_random(const WitUserStartup *startup, WitU64 mode)
{
    const bool tls = mode == 58;
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[1] = 268435456;
    // This is the production entropy path, before image publication or TLS constructors.
    wit_native_security_initialize_system();
    if (!__security_cookie ||
        (__security_cookie >> 48) ||
        __security_cookie == 0x2B992DDFA232ULL ||
        __security_cookie_complement != ~__security_cookie ||
        !wit_gs_check_abi(__security_cookie)) {
        return 2402;
    }
    wit_native_process_image_initialize(startup);
    if (tls) {
        wit_native_tls_initialize(startup);
    }
    SetLastError(0x14823765);
    if (tls) {
        errno = 83;
    }
    unsigned char a[64], b[64];
    for (unsigned i = 0; i < 64; ++i) {
        a[i] = b[i] = 0xa5;
    }
    if (!generated(3, a, 64) || !generated(3, b, 64, true) || !different(a, b, 64)) {
        return 2403;
    }
    if (BCryptGenRandom((BCRYPT_ALG_HANDLE)1, a, 8, BCRYPT_USE_SYSTEM_PREFERRED_RNG) != (NTSTATUS)0xC0000008UL ||
        BCryptGenRandom(nullptr, a, 8, 0) != (NTSTATUS)0xC00000BBUL ||
        BCryptGenRandom(nullptr, nullptr, 8, BCRYPT_USE_SYSTEM_PREFERRED_RNG) != (NTSTATUS)0xC0000005UL ||
        BCryptGenRandom(nullptr, a, WIT_ABI_MAX_RANDOM + 1, BCRYPT_USE_SYSTEM_PREFERRED_RNG) !=
            (NTSTATUS)0xC0000044UL ||
        BCryptGenRandom(nullptr, nullptr, 0, BCRYPT_USE_SYSTEM_PREFERRED_RNG)) {
        return 2404;
    }
    const void *binding = __imp_BCryptGenRandom;
    if (BCryptGenRandom(nullptr, (PUCHAR)&__imp_BCryptGenRandom, 8, BCRYPT_USE_SYSTEM_PREFERRED_RNG) !=
            (NTSTATUS)0xC0000005UL ||
        binding != __imp_BCryptGenRandom) {
        return 2405;
    }
    WitU64 copied = 99;
    if (wit_native_call(WIT_CALL_RANDOM, (WitU64)a, 1, 1, &copied) != WIT_STATUS_INVALID_ARGUMENT ||
        copied ||
        wit_native_call(WIT_CALL_RANDOM, (WitU64)a, 1ULL << 32, 0, &copied) != WIT_STATUS_TOO_LARGE ||
        copied ||
        wit_native_call(WIT_CALL_RANDOM, ~0ULL - 7, 16, 0, &copied) != WIT_STATUS_BAD_ADDRESS ||
        copied) {
        return 2406;
    }
    WitU64 arena = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return 2407;
    }
    auto edge = (unsigned char *)(arena + 4092);
    for (unsigned i = 0; i < 4; ++i) {
        edge[i] = 0xa5;
    }
    if (BCryptGenRandom(nullptr, edge, 16, BCRYPT_USE_SYSTEM_PREFERRED_RNG) != (NTSTATUS)0xC0000005UL) {
        return 2408;
    }
    for (unsigned i = 0; i < 4; ++i) {
        if (edge[i] != 0xa5) {
            return 2409;
        }
    }
    if (wit_native_call(WIT_CALL_MEMORY_COMMIT, arena + 4096, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return 2410;
    }
    for (unsigned protection = 0; protection <= 1; ++protection) {
        if (wit_native_call(WIT_CALL_MEMORY_PROTECT, arena + 4096, 4096, protection, nullptr) != WIT_STATUS_OK ||
            wit_native_call(WIT_CALL_RANDOM, (WitU64)edge, 16, 0, &copied) != WIT_STATUS_BAD_ADDRESS ||
            copied) {
            return 2411;
        }
        for (unsigned i = 0; i < 4; ++i) {
            if (edge[i] != 0xa5) {
                return 2412;
            }
        }
    }
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, arena + 4096, 4096, 3, nullptr) != WIT_STATUS_OK ||
        !generated(3, edge, 16) ||
        !generated(3, (unsigned char *)arena, 256) ||
        wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
        return 2413;
    }
    // The exact nonpreemptible work boundary is a real supported request,
    // not merely a too-large rejection case.
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, WIT_ABI_MAX_RANDOM, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, WIT_ABI_MAX_RANDOM, 3, nullptr) != WIT_STATUS_OK ||
        !generated(3, (unsigned char *)arena, WIT_ABI_MAX_RANDOM) ||
        wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
        return 2418;
    }
    if (tls) {
        WitU64 handles[3], result;
        for (WitU64 i = 0; i < 3; ++i) {
            if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) {
                return 2414;
            }
        }
        for (unsigned i = 0; i < 3; ++i) {
            if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK ||
                result != WIT_TEST_EXIT_CODE) {
                return 2415;
            }
        }
        if (!different(first[0], first[1], 32) || !different(first[1], first[2], 32)) {
            return 2416;
        }
        wit_native_tls_leave();
    }
    if (GetLastError() != 0x14823765 || (tls && errno != 83)) {
        return 2417;
    }
    for (unsigned j = 0; j < 3; ++j) {
        report[j + 2] = 0;
        for (unsigned i = 0; i < 4; ++i) {
            report[j + 2] += counts[i][j];
        }
    }
    return WIT_TEST_EXIT_CODE;
}
