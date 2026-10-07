#include "minipal_time.witos.h"

extern "C" int64_t minipal_hires_ticks(void)
{
    WitU64 value = 0;
    if (wit_native_call(WIT_CALL_MONOTONIC_READ, 0, 0, 0, &value) != WIT_STATUS_OK || value > WIT_MONOTONIC_MAX) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return (int64_t)value;
}

extern "C" int64_t minipal_hires_tick_frequency(void)
{
    WitU64 frequency = 0;
    if (wit_native_call(WIT_CALL_MONOTONIC_FREQUENCY, 0, 0, 0, &frequency) != WIT_STATUS_OK ||
        frequency < 1000 ||
        frequency > 1000000000) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return (int64_t)frequency;
}

extern "C" int64_t minipal_lowres_ticks(void)
{
    const WitU64 now = (WitU64)minipal_hires_ticks();
    const WitU64 frequency = (WitU64)minipal_hires_tick_frequency();
    // Dividing first avoids overflow. Frequency >= 1000 keeps ms <= INT64_MAX.
    return (int64_t)((now / frequency) * 1000 + (now % frequency) * 1000 / frequency);
}

WitU64 wit_minipal_deadline_at(uint32_t usecs, WitU64 now, WitU64 frequency)
{
    if (now > WIT_MONOTONIC_MAX || frequency < 1000 || frequency > 1000000000) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    // uint32 usecs * <= 1 GHz plus rounding fits uint64. Always round upward.
    const WitU64 delta = ((WitU64)usecs * frequency + 999999) / 1000000;
    return delta > WIT_MONOTONIC_MAX - now ? WIT_MONOTONIC_MAX : now + delta;
}

extern "C" void minipal_microdelay(uint32_t usecs, uint32_t *usecsSinceYield)
{
    if (!usecs) {
        return;
    }
    const WitU64 frequency = (WitU64)minipal_hires_tick_frequency();
    const WitU64 deadline = wit_minipal_deadline_at(usecs, (WitU64)minipal_hires_ticks(), frequency);
    if (usecs > WIT_MINIPAL_SPIN_MAX_US) {
        if (wit_native_sleep_until(deadline) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        // As in upstream, the sleeping branch clears busy-loop accounting.
        // An absolute deadline may already have expired; no idle is promised.
        if (usecsSinceYield) {
            *usecsSinceYield = 0;
        }
    } else {
        // Small, bounded busy delays use the kernel clock; no architecture
        // instructions, compiler TLS or shared lock live in this adapter.
        while ((WitU64)minipal_hires_ticks() < deadline) {
        }
        if (usecsSinceYield) {
            const uint32_t previous = *usecsSinceYield;
            *usecsSinceYield = usecs > UINT32_MAX - previous ? UINT32_MAX : previous + usecs;
        }
    }
}
