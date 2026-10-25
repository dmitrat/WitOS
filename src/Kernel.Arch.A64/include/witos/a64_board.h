#ifndef WITOS_A64_BOARD_H
#define WITOS_A64_BOARD_H

#include "witos/types.h"

struct WitBootInfo;

/* What the ARM64 architecture and the platform of its boards give each other (H7.2): the platform knows from the device
 * tree how a board starts its processors and turns itself off, and the architecture gives it the means. */

/* Arm's power state coordination interface through the conduit the tree's /psci names: HVC (QEMU's virt) or SMC (a
 * board with an EL2 or firmware at EL3). A function that turns the machine off or resets it does not return when it
 * succeeds (plan step H1.2a, ADR 0029). */
WitU64 wit_a64_psci_hvc(WitU64 function, WitU64 a1, WitU64 a2, WitU64 a3);
WitU64 wit_a64_psci_smc(WitU64 function, WitU64 a1, WitU64 a2, WitU64 a3);

#define WIT_PSCI_CPU_ON 0xC4000003ULL
#define WIT_PSCI_SYSTEM_OFF 0x84000008ULL
#define WIT_PSCI_SYSTEM_RESET 0x84000009ULL

/* A secondary processor's entries, at EL2 or EL1 with the MMU off: PSCI's CPU_ON enters wit_a64_secondary_entry with
 * the kernel's number of the processor as its context; a spin table's release enters wit_a64_secondary_spin_entry,
 * which finds that number by its MPIDR among the processors the architecture prepared. */
void wit_a64_secondary_entry(void);
void wit_a64_secondary_spin_entry(void);

/* Starts the prepared processor index by the means the board's tree names; nonzero once the start was given (the
 * platform, H7.2). */
int wit_a64_board_processor_start(WitU32 index, WitU64 hardware_id);

/* Writes a word of memory the kernel does not map, as a spin table's release word in a page the firmware reserved:
 * through a device mapping of its page at its own address, taken away again; never usable memory or the kernel image. */
void wit_a64_write_physical(const struct WitBootInfo *boot, WitU64 physical, WitU64 value);

/* SEV: wakes the processors that wait for an event, as a spin table's do after its release word changed. */
void wit_a64_send_event(void);

#endif
