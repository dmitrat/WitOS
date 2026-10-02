#include "minipal_time.witos.h"
#include "error.h"
#include "tls.h"
#include "protocol.h"
#include <errno.h>

static WitU64 milliseconds(WitU64 value, WitU64 frequency)
{
    return value / frequency * 1000 + (value % frequency) * 1000 / frequency;
}

static bool delay(uint32_t usecs, uint32_t *busy)
{
    const WitU64 frequency = (WitU64)minipal_hires_tick_frequency();
    const WitU64 before = (WitU64)minipal_hires_ticks();
    minipal_microdelay(usecs, busy);
    const WitU64 after = (WitU64)minipal_hires_ticks();
    return after >= before && after - before >= ((WitU64)usecs * frequency + 999999) / 1000000;
}

extern "C" bool wit_test_minipal_time_early()
{
    wit_native_error_set(0x71324567);
    WitU64 actual = 0;
    if (wit_native_call(WIT_CALL_MONOTONIC_FREQUENCY, 0, 0, 0, &actual) != WIT_STATUS_OK ||
        minipal_hires_tick_frequency() != (int64_t)actual) {
        return false;
    }
    const auto before = (WitU64)minipal_hires_ticks();
    const auto low = (WitU64)minipal_lowres_ticks();
    const auto after = (WitU64)minipal_hires_ticks();
    if (after < before || low < milliseconds(before, actual) || low > milliseconds(after, actual)) {
        return false;
    }
    if (wit_minipal_deadline_at(0, 7, 1000) != 7 ||
        wit_minipal_deadline_at(1, 7, 1000) != 8 ||
        wit_minipal_deadline_at(1001, 7, 1000) != 9 ||
        wit_minipal_deadline_at(UINT32_MAX, 7, 1000000000) != 4294967295007ULL ||
        wit_minipal_deadline_at(1, WIT_MONOTONIC_MAX - 1, 1000000000) != WIT_MONOTONIC_MAX) {
        return false;
    }
    uint32_t busy = 37;
    minipal_microdelay(0, &busy);
    if (busy != 37 || !delay(1, &busy) || busy != 38 || !delay(1000, &busy) || busy != 1038) {
        return false;
    }
    busy = UINT32_MAX - 1;
    if (!delay(2, &busy) ||
        busy != UINT32_MAX ||
        !delay(1001, &busy) ||
        busy != 0 ||
        !delay(1, nullptr) ||
        !delay(2500, nullptr)) {
        return false;
    }
    return wit_native_error_get() == 0x71324567;
}

static WitU64 time_worker(WitU64 index)
{
    wit_native_error_set((WitU32)(100 + index));
    errno = (int)(200 + index);
    uint32_t busy = 0;
    for (uint32_t i = 1; i <= 4; ++i) {
        if (!delay(i, &busy) || busy != i * (i + 1) / 2) {
            return 1750;
        }
    }
    if (!delay(1500, &busy) || busy || errno != 200 + index || wit_native_error_get() != 100 + index) {
        return 1751;
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" bool wit_test_minipal_time_threads()
{
    errno = 99;
    for (unsigned round = 0; round < 2; ++round) {
        WitU64 handles[3], result;
        for (WitU64 i = 0; i < 3; ++i) {
            if (wit_native_thread_create(time_worker, i, &handles[i]) != WIT_STATUS_OK) {
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
    return errno == 99 && wit_native_error_get() == 0x71324567;
}
