#ifndef WITOS_A64_PSCI_H
#define WITOS_A64_PSCI_H

#include "witos/types.h"

/* Arm's power state coordination interface through HVC, the conduit of QEMU's virt board (plan step H1.2a, ADR 0029):
 * a board's platform turns the machine off or resets it with it at the end of a boot. The secondary processors' start
 * keeps its own call (secondary_entry.S). */

WitU64 wit_a64_psci_hvc(WitU64 function, WitU64 a1, WitU64 a2, WitU64 a3);

#define WIT_PSCI_SYSTEM_OFF 0x84000008ULL
#define WIT_PSCI_SYSTEM_RESET 0x84000009ULL

#endif
