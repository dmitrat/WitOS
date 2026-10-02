#include "pal.witos.h"
#include "tls.h"
#include "protocol.h"
#include <minipal/time.h>
#include <errno.h>
extern "C" BOOL WINAPI wit_clock_direct_counter(LARGE_INTEGER *);
extern "C" BOOL WINAPI wit_clock_direct_frequency(LARGE_INTEGER *);
extern "C" ULONGLONG WINAPI wit_clock_direct_ticks();
extern "C" const void *const __imp_QueryPerformanceCounter;
extern "C" const void *const __imp_QueryPerformanceFrequency;

static WitU64 load(const unsigned char *p)
{
    WitU64 value = 0;
    for (unsigned i = 0; i < 8; ++i) {
        value |= (WitU64)p[i] << (8 * i);
    }
    return value;
}

static bool values()
{
    LARGE_INTEGER first, second, frequency, directFrequency;
    const auto before = minipal_hires_ticks();
    if (!QueryPerformanceCounter(&first) ||
        !wit_clock_direct_counter(&second) ||
        !QueryPerformanceFrequency(&frequency) ||
        !wit_clock_direct_frequency(&directFrequency)) {
        return false;
    }
    const auto after = minipal_hires_ticks();
    const auto low = (WitU64)minipal_lowres_ticks();
    const auto a = GetTickCount64(), b = wit_clock_direct_ticks();
    const auto high = (WitU64)minipal_lowres_ticks();
    return before <= first.QuadPart &&
        first.QuadPart <= second.QuadPart &&
        second.QuadPart <= after &&
        frequency.QuadPart == minipal_hires_tick_frequency() &&
        frequency.QuadPart == directFrequency.QuadPart &&
        low <= a &&
        a <= b &&
        b <= high;
}

extern "C" bool wit_test_native_clock_early()
{
    SetLastError(0x75432109);
    if (!values() || GetLastError() != 0x75432109) {
        return false;
    }
    LARGE_INTEGER result;
    result.QuadPart = 0x12345678;
    WitU64 copied = 99;
    if (wit_native_call(WIT_CALL_MONOTONIC_QUERY, (WitU64)&result, 7, WIT_MONOTONIC_COUNTER, &copied) !=
            WIT_STATUS_INVALID_ARGUMENT ||
        copied ||
        result.QuadPart != 0x12345678 ||
        wit_native_call(WIT_CALL_MONOTONIC_QUERY, (WitU64)&result, 8, 2, &copied) != WIT_STATUS_UNSUPPORTED ||
        copied ||
        result.QuadPart != 0x12345678 ||
        wit_native_call(WIT_CALL_MONOTONIC_QUERY, (WitU64)&result, (1ULL << 32) + 8, WIT_MONOTONIC_HZ, &copied) !=
            WIT_STATUS_INVALID_ARGUMENT ||
        copied) {
        return false;
    }
    const void *slot = __imp_QueryPerformanceCounter;
    if (QueryPerformanceCounter(nullptr) ||
        GetLastError() != ERROR_INVALID_ADDRESS ||
        wit_clock_direct_frequency((LARGE_INTEGER *)&__imp_QueryPerformanceCounter) ||
        GetLastError() != ERROR_INVALID_ADDRESS ||
        __imp_QueryPerformanceCounter != slot ||
        QueryPerformanceFrequency((LARGE_INTEGER *)(~0ULL - 3)) ||
        GetLastError() != ERROR_INVALID_ADDRESS) {
        return false;
    }
    WitU64 arena = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    auto edge = (unsigned char *)(arena + 4092);
    for (unsigned i = 0; i < 4; ++i) {
        edge[i] = 0xa5;
    }
    if (QueryPerformanceCounter((LARGE_INTEGER *)edge) || GetLastError() != ERROR_INVALID_ADDRESS) {
        return false;
    }
    for (unsigned i = 0; i < 4; ++i) {
        if (edge[i] != 0xa5) {
            return false;
        }
    }
    if (wit_native_call(WIT_CALL_MEMORY_COMMIT, arena + 4096, 4096, 3, nullptr) != WIT_STATUS_OK ||
        !wit_clock_direct_frequency((LARGE_INTEGER *)edge) ||
        load(edge) != (WitU64)minipal_hires_tick_frequency()) {
        return false;
    }
    for (unsigned i = 0; i < 8; ++i) {
        edge[i] = 0xa5;
    }
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, arena + 4096, 4096, WIT_MEMORY_READ, nullptr) != WIT_STATUS_OK ||
        QueryPerformanceFrequency((LARGE_INTEGER *)edge) ||
        GetLastError() != ERROR_INVALID_ADDRESS) {
        return false;
    }
    for (unsigned i = 0; i < 8; ++i) {
        if (edge[i] != 0xa5) {
            return false;
        }
    }
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, arena + 4096, 4096, 0, nullptr) != WIT_STATUS_OK ||
        wit_clock_direct_counter((LARGE_INTEGER *)edge) ||
        GetLastError() != ERROR_INVALID_ADDRESS) {
        return false;
    }
    for (unsigned i = 0; i < 4; ++i) {
        if (edge[i] != 0xa5) {
            return false;
        }
    }
    if (wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    SetLastError(0x75432109);
    return values() && GetLastError() == 0x75432109;
}

static WitU64 worker(WitU64 index)
{
    SetLastError((DWORD)(100 + index));
    errno = (int)(200 + index);
    for (unsigned i = 0; i < 4; ++i) {
        if (!values() ||
            wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK ||
            GetLastError() != 100 + index ||
            errno != 200 + index) {
            return 1801;
        }
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" bool wit_test_native_clock_threads()
{
    errno = 99;
    WitU64 handles[3], result;
    for (WitU64 i = 0; i < 3; ++i) {
        if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) {
            return false;
        }
    }
    for (unsigned i = 0; i < 3; ++i) {
        if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK ||
            result != WIT_TEST_EXIT_CODE) {
            return false;
        }
    }
    return errno == 99 && GetLastError() == 0x75432109;
}
