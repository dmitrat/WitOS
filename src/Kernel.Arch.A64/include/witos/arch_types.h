#ifndef WITOS_ARCH_TYPES_H
#define WITOS_ARCH_TYPES_H

#include "witos/types.h"

/*
 * ARM64 types that the common kernel embeds without reading them. This directory is the only
 * architecture include path of the common kernel; witos/arch.h declares the operations.
 */

/* Return address, saved program status and syndrome of one user fault. */
struct WitArchFaultState {
    WitU64 Elr;
    WitU64 Spsr;
    WitU64 Esr;
};

#endif
