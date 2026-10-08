#ifndef WITOS_CLOCK_H
#define WITOS_CLOCK_H
#include "types.h"

/* The UTC domain (RFC 0011 section 7.10, plan step K6): nanoseconds since 1970-01-01 as the board's real-time clock
 * reported it once at boot plus the monotonic time since, at 10^9 per second. The monotonic domain stays the
 * platform's. CLOCK_SET rebases UTC so that later reads continue from the value given; setting is a policy behind
 * the clock capability of the root task. Every call runs with interrupts disabled on the one processor. */
#define WIT_CLOCK_UTC_FREQUENCY 1000000000ULL
#define WIT_CLOCK_UTC_MAX 0x7FFFFFFFFFFFFFFFULL

void wit_clock_initialize(void);
int wit_clock_utc_available(void);
WitU64 wit_clock_utc_read(void);
/* 0 when the value is beyond WIT_CLOCK_UTC_MAX or would place the boot before 1970. */
int wit_clock_utc_set(WitU64 nanoseconds);
#endif
