#ifndef WITOS_ARCH_TYPES_H
#define WITOS_ARCH_TYPES_H

#include "witos/types.h"

/*
 * x64 types that the common kernel embeds without reading them. This directory is the only
 * architecture include path of the common kernel; witos/arch.h declares the operations.
 */

/* Code location and privilege selectors of one user fault. */
struct WitArchFaultState {
    WitU64 Rip;
    WitU64 Cs;
    WitU64 Ss;
};

#endif
