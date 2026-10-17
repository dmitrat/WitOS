#ifndef WITOS_RV64_SBI_H
#define WITOS_RV64_SBI_H

#include "witos/types.h"

/* The supervisor binary interface of the machine-mode firmware (OpenSBI on the virt board; plan step V1.2): one
 * ecall with the extension in a7 and the function in a6, the arguments in a0–a2, the error back in a0 and the value in
 * a1. The architecture asks it for the boot hart's identity (the hart state machine), the board for the timer where a
 * hart lacks sstc, and V1.4 for the start of the other harts. */

WitU64 wit_rv64_sbi(WitU64 extension, WitU64 function, WitU64 a0, WitU64 a1, WitU64 a2, WitU64 *value);

#define WIT_RV64_SBI_HSM 0x48534DULL /* the hart state management extension */
#define WIT_RV64_SBI_HSM_STATUS 2ULL /* hart_get_status: the state in the value */
#define WIT_RV64_SBI_HSM_STARTED 0ULL
#define WIT_RV64_SBI_TIME 0x54494D45ULL /* the timer extension */
#define WIT_RV64_SBI_TIME_SET 0ULL /* set_timer: the absolute time of the next supervisor timer interrupt */

#endif
