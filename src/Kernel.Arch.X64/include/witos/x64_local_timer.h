#ifndef WITOS_X64_LOCAL_TIMER_H
#define WITOS_X64_LOCAL_TIMER_H

#include "witos/types.h"

/* The local APIC's timer of the running processor (P1.3a), the tick of a secondary processor on q35, whose board keeps
 * the PIT for the boot processor: the architecture owns the APIC, the board asks it for a tick. The calibration runs
 * once on the boot processor with secondary processors online, before any start. */
void wit_x64_local_timer_calibrate(WitU32 hz);
void wit_x64_local_timer_start(WitU32 vector);
void wit_x64_local_timer_stop(void);
void wit_x64_local_timer_acknowledge(void);

#endif
