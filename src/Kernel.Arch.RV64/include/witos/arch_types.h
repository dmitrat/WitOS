#ifndef WITOS_ARCH_TYPES_H
#define WITOS_ARCH_TYPES_H

#include "witos/types.h"

/*
 * RISC-V types that the common kernel embeds without reading them (plan step V1). This directory is the only
 * architecture include path of the common kernel; witos/arch.h declares the operations.
 */

/* Exception program counter, status and the cause and value of one user fault. */
struct WitArchFaultState {
    WitU64 Sepc;
    WitU64 Sstatus;
    WitU64 Scause;
    WitU64 Stval;
};

#endif
