#ifndef WITOS_VIRT_H
#define WITOS_VIRT_H

#include "witos/types.h"

/* QEMU virt board: device addresses and the semihosting entry, used only by this directory. */

#define WIT_VIRT_PL011_BASE 0x09000000ULL

/* Arm semihosting call (HLT #0xF000); QEMU serves it when started with -semihosting-config. */
WitU64 wit_virt_semihosting(WitU64 operation, const void *parameter);

/* Generic counter (CNTPCT_EL0, read after an ISB) and its frequency (CNTFRQ_EL0). */
WitU64 wit_virt_counter(void);
WitU64 wit_virt_counter_frequency(void);

#endif
