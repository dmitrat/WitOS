#ifndef WITOS_VIRT_H
#define WITOS_VIRT_H

#include "witos/types.h"

/* QEMU virt board: device addresses and the semihosting entry, used only by this directory. */

#define WIT_VIRT_PL011_BASE 0x09000000ULL
#define WIT_VIRT_PL031_BASE 0x09010000ULL /* the real-time clock of the QEMU virt profile (K6) */
#define WIT_VIRT_GICD_BASE 0x08000000ULL
#define WIT_VIRT_GICR_BASE 0x080A0000ULL /* Redistributor of the boot processor. */
#define WIT_VIRT_GICR_SGI_BASE 0x080B0000ULL

struct WitBootInfo;

/* Arm semihosting call (HLT #0xF000); QEMU serves it when started with -semihosting-config. */
WitU64 wit_virt_semihosting(WitU64 operation, const void *parameter);

/* Generic counter (CNTPCT_EL0, read after an ISB) and its frequency (CNTFRQ_EL0). */
WitU64 wit_virt_counter(void);
WitU64 wit_virt_counter_frequency(void);

/* Maps the GIC distributor and redistributor pages; the clock does it once paging is active. */
void wit_virt_interrupts_map(const struct WitBootInfo *boot);

/* GICv3 CPU interface and EL1 virtual timer (gic.asm). */
void wit_virt_gic_cpu_enable(void);
WitU64 wit_virt_gic_acknowledge(void);
void wit_virt_gic_complete(WitU64 interrupt);
void wit_virt_timer_arm(WitU64 ticks);
void wit_virt_timer_disable(void);

#endif
