#include "pal.witos.h"
#include "tls.h"
#include "protocol.h"
#include <math.h>
#include <initializer_list>
#include <errno.h>
#include "math_log_vectors.h"
#pragma function(log)
extern "C" int _fltused;
extern "C" unsigned wit_math_fp_state();
extern "C" void wit_math_clear_fp_flags();

static double value(uint64_t bits)
{
    union {
        uint64_t bits;
        double value;
    } x = {bits};

    return x.value;
}

static uint64_t bits(double value)
{
    union {
        double value;
        uint64_t bits;
    } x = {value};

    return x.bits;
}

static uint64_t ordered(uint64_t x)
{
    return (x >> 63) ? ~x : x | (1ULL << 63);
}

static bool cases(int saved)
{
    if (_fltused != 0x9875 || (wit_math_fp_state() & 0xE040)) {
        return false;
    }
    errno = saved;
    for (const auto &v : log_vectors) {
        const auto actual = bits(log(value(v.input)));
        const auto a = ordered(actual), e = ordered(v.expected);
        const auto distance = a > e ? a - e : e - a;
        if (distance > 1 || errno != saved) {
            return false;
        }
    }
    if (bits(log(1.0)) != 0 || errno != saved) {
        return false;
    }
    for (auto x : {0ULL, 0x8000000000000000ULL}) {
        errno = saved;
        wit_math_clear_fp_flags();
        if (bits(log(value(x))) != 0xfff0000000000000ULL || errno != ERANGE || !(wit_math_fp_state() & 4)) {
            return false;
        }
    }
    for (auto x : {0xbff0000000000000ULL, 0xfff0000000000000ULL}) {
        errno = saved;
        wit_math_clear_fp_flags();
        if ((bits(log(value(x))) & 0x7fffffffffffffffULL) <= 0x7ff0000000000000ULL ||
            errno != EDOM ||
            !(wit_math_fp_state() & 1)) {
            return false;
        }
    }
    errno = saved;
    if (bits(log(value(0x7ff0000000000000ULL))) != 0x7ff0000000000000ULL || errno != saved) {
        return false;
    }
    for (auto x : {0x7ff8000000000001ULL, 0xfff8000000000001ULL, 0x7ff0000000000001ULL}) {
        if ((bits(log(value(x))) & 0x7fffffffffffffffULL) <= 0x7ff0000000000000ULL || errno != saved) {
            return false;
        }
    }
    return true;
}

static WitU64 worker(WitU64 index)
{
    const DWORD error = (DWORD)(1100 + index);
    const int saved = (int)(1200 + index);
    SetLastError(error);
    errno = saved;
    for (unsigned i = 0; i < 3; ++i) {
        if (!cases(saved) ||
            wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK ||
            errno != saved ||
            GetLastError() != error) {
            return 2101;
        }
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" bool wit_test_math(bool threads)
{
    SetLastError(0x31234567);
    errno = 91;
    if (!cases(91)) {
        return false;
    }
    if (threads) {
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
    }
    return errno == 91 && GetLastError() == 0x31234567;
}
