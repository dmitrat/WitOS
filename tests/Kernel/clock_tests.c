#include "witos/clock.h"
#include "witos/platform.h"
#include "self_test.h"

/* The UTC domain (RFC 0011 section 7.10, plan step K6) on both boards: the real-time clock gave a plausible time at
 * boot, two readings never go backwards, setting moves the clock and the clock continues from the value, values
 * beyond the range or before the boot are refused, and the clock is restored to the board's time afterwards. */

#define SECONDS_2026 1767225600ULL /* 2026-01-01T00:00:00Z */
#define SECONDS_2030 1893456000ULL /* 2030-01-01T00:00:00Z */
#define SECONDS_2100 4102444800ULL /* 2100-01-01T00:00:00Z */

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_clock_self_test(void)
{
    require(wit_clock_utc_available(), "The board reported no real-time clock");
    const WitU64 first = wit_clock_utc_read();
    const WitU64 second = wit_clock_utc_read();
    require(second >= first, "UTC went backwards");
    require(first >= SECONDS_2026 * WIT_CLOCK_UTC_FREQUENCY && first < SECONDS_2100 * WIT_CLOCK_UTC_FREQUENCY,
        "UTC at boot is implausible");
    require(!wit_clock_utc_set(WIT_CLOCK_UTC_MAX + 1), "UTC beyond its range was accepted");
    require(!wit_clock_utc_set(0), "UTC before the boot was accepted");
    const WitU64 target = SECONDS_2030 * WIT_CLOCK_UTC_FREQUENCY;
    require(wit_clock_utc_set(target), "UTC refused a value in range");
    const WitU64 moved = wit_clock_utc_read();
    require(moved >= target && moved < target + WIT_CLOCK_UTC_FREQUENCY, "UTC did not continue from the value set");
    /* Back to the board's time: the time that passed since the first reading stays counted. */
    require(wit_clock_utc_set(first + (wit_clock_utc_read() - target)), "UTC could not be restored");
    require(wit_clock_utc_read() >= first && wit_clock_utc_read() < first + WIT_CLOCK_UTC_FREQUENCY,
        "UTC restoration missed");
    wit_console_write("[TEST-PASS] Clock.Utc\n");
}
