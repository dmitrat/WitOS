#include "witos/clock.h"
#include "witos/arch.h"
#include "witos/platform.h"

/* UTC (RFC 0011 section 7.10, plan step K6) over the platform's two clocks: the real-time clock read once at boot
 * and the monotonic counter since. The elapsed time is converted in two parts, whole seconds and the remainder, so
 * that no product exceeds 64 bits for any counter frequency up to 10^9. */

static WitU64 base_nanoseconds; /* UTC at the moment the monotonic origin was taken */
static WitU64 origin; /* the monotonic counter at that moment */
static int available;

static WitU64 elapsed_nanoseconds(void)
{
    const WitU64 frequency = wit_platform_monotonic_frequency();
    const WitU64 now = wit_platform_monotonic_read();
    const WitU64 delta = now >= origin ? now - origin : 0;
    return (delta / frequency) * WIT_CLOCK_UTC_FREQUENCY + ((delta % frequency) * WIT_CLOCK_UTC_FREQUENCY) / frequency;
}

void wit_clock_initialize(void)
{
    WitU64 seconds = 0;
    if (wit_arch_interrupts_enabled() || available) {
        wit_panic("Invalid UTC clock initialization context");
    }
    origin = wit_platform_monotonic_read();
    if (wit_platform_realtime_seconds(&seconds) && seconds <= WIT_CLOCK_UTC_MAX / WIT_CLOCK_UTC_FREQUENCY) {
        base_nanoseconds = seconds * WIT_CLOCK_UTC_FREQUENCY;
        available = 1;
        wit_console_write("UTC at boot: ");
        wit_console_write_u64(seconds);
        wit_console_write(" s since 1970\n");
    } else {
        wit_console_write("UTC: no real-time clock\n");
    }
}

int wit_clock_utc_available(void)
{
    return available;
}

WitU64 wit_clock_utc_read(void)
{
    if (!available) {
        wit_panic("UTC read without a real-time clock");
    }
    const WitU64 value = base_nanoseconds + elapsed_nanoseconds();
    return value > WIT_CLOCK_UTC_MAX ? WIT_CLOCK_UTC_MAX : value;
}

int wit_clock_utc_set(WitU64 nanoseconds)
{
    const WitU64 elapsed = elapsed_nanoseconds();
    if (nanoseconds > WIT_CLOCK_UTC_MAX || nanoseconds < elapsed) {
        return 0;
    }
    base_nanoseconds = nanoseconds - elapsed;
    available = 1;
    return 1;
}
