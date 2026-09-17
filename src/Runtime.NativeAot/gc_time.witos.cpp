#include "gcenv.witos.h"

int64_t GCToOSInterface::QueryPerformanceCounter()
{
    WitU64 value = 0;
    if (wit_native_call(WIT_CALL_MONOTONIC_READ, 0, 0, 0, &value) != WIT_STATUS_OK || value > WIT_MONOTONIC_MAX)
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return (int64_t)value;
}
int64_t GCToOSInterface::QueryPerformanceFrequency()
{
    WitU64 value = 0;
    if (wit_native_call(WIT_CALL_MONOTONIC_FREQUENCY, 0, 0, 0, &value) != WIT_STATUS_OK ||
        value < 1000 || value > 1000000000)
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return (int64_t)value;
}
uint64_t GCToOSInterface::GetLowPrecisionTimeStamp()
{
    const uint64_t now = (uint64_t)QueryPerformanceCounter();
    const uint64_t frequency = (uint64_t)QueryPerformanceFrequency();
    return (now / frequency) * 1000 + (now % frequency) * 1000 / frequency;
}
WitU64 wit_gc_deadline_at(uint32_t milliseconds, WitU64 now, WitU64 frequency)
{
    if (milliseconds == INFINITE) return WIT_WAIT_INFINITE;
    if (!milliseconds) return 0;
    // Caller frequency is checked: uint32 milliseconds * <=1GHz fits int64.
    const WitU64 delta = ((WitU64)milliseconds * frequency + 999) / 1000;
    return delta > WIT_MONOTONIC_MAX - now ? WIT_MONOTONIC_MAX : now + delta;
}
WitU64 wit_gc_deadline(uint32_t milliseconds)
{
    if (!milliseconds || milliseconds == INFINITE)
        return milliseconds ? WIT_WAIT_INFINITE : 0;
    const auto now = (WitU64)GCToOSInterface::QueryPerformanceCounter();
    const auto frequency = (WitU64)GCToOSInterface::QueryPerformanceFrequency();
    return wit_gc_deadline_at(milliseconds, now, frequency);
}
void GCToOSInterface::Sleep(uint32_t milliseconds)
{
    if (!milliseconds) { YieldThread(0); return; }
    if (wit_native_call(WIT_CALL_SLEEP_UNTIL, wit_gc_deadline(milliseconds), 0, 0, nullptr) != WIT_STATUS_OK)
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
