#ifndef WITOS_VIRTUAL_GAP_H
#define WITOS_VIRTUAL_GAP_H
#include "types.h"

typedef struct WitVirtualRange {
    WitU64 Base, Size;
} WitVirtualRange;

/* Pure first-fit selection within [low,high). No allocation/mutation of ranges.
 * The caller supplies its authoritative reserved/fixed regions and policy bounds, and scratch room for count ranges,
 * which the search sorts (O(n log n) in the ranges in use, P4).
 * Result is zero on failure; alignment is a nonzero power of two. */
int wit_virtual_gap(const WitVirtualRange *ranges, WitU32 count, WitU64 low, WitU64 high, WitU64 size, WitU64 alignment,
    WitVirtualRange *scratch, WitU64 *result);
#endif
