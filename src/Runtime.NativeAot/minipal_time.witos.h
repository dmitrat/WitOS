#ifndef WITOS_MINIPAL_TIME_H
#define WITOS_MINIPAL_TIME_H
#include <minipal/time.h>
extern "C" {
#include "bootstrap.h"
}
#define WIT_MINIPAL_SPIN_MAX_US 1000U
/* Private conversion helper, tested without waiting for extreme durations.
 * UINT32_MAX is a finite microsecond interval, not an infinite timeout. */
WitU64 wit_minipal_deadline_at(uint32_t usecs, WitU64 now, WitU64 frequency);
#endif
