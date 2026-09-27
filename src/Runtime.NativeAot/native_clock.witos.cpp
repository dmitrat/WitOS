#include "pal.witos.h"
#include <minipal/time.h>
static_assert(sizeof(LARGE_INTEGER) == sizeof(WitU64));
static BOOL query(LARGE_INTEGER* output, WitU64 selector)
{
    WitU64 copied = 0;
    const WitU64 status = wit_native_call(WIT_CALL_MONOTONIC_QUERY, (uintptr_t)output, sizeof(*output), selector, &copied);
    if (status == WIT_STATUS_OK && copied != sizeof(*output)) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return (BOOL)wit_pal_result(status);
}
extern "C" BOOL WINAPI wit_native_query_performance_counter(LARGE_INTEGER* output) { return query(output, WIT_MONOTONIC_COUNTER); }
extern "C" BOOL WINAPI wit_native_query_performance_frequency(LARGE_INTEGER* output) { return query(output, WIT_MONOTONIC_HZ); }
extern "C" ULONGLONG WINAPI wit_native_tick_count64()
{
    const int64_t value = minipal_lowres_ticks();
    if (value < 0) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return (ULONGLONG)value;
}
