#include "witos/virtual_gap.h"

static int align_up(WitU64 value, WitU64 alignment, WitU64 *result)
{
    if (value > ~0ULL - (alignment - 1)) {
        return 0;
    }
    *result = (value + alignment - 1) & ~(alignment - 1);
    return 1;
}

int wit_virtual_gap(
    const WitVirtualRange *ranges, WitU32 count, WitU64 low, WitU64 high, WitU64 size, WitU64 alignment, WitU64 *result)
{
    if (!result) {
        return 0;
    }
    *result = 0;
    if ((count && !ranges) ||
        !size ||
        !alignment ||
        (alignment & (alignment - 1)) ||
        low >= high ||
        size > high - low) {
        return 0;
    }
    for (WitU32 i = 0; i < count; ++i) {
        if (ranges[i].Size > ~0ULL - ranges[i].Base) {
            return 0;
        }
    }
    WitU64 candidate;
    if (!align_up(low, alignment, &candidate)) {
        return 0;
    }
    // Each collision moves beyond a distinct range; no range can collide twice.
    // Using a 64-bit pass count also makes UINT32_MAX count non-wrapping.
    for (WitU64 pass = 0; pass <= (WitU64)count; ++pass) {
        if (candidate >= high || size > high - candidate) {
            return 0;
        }
        int collision = 0;
        for (WitU32 i = 0; i < count; ++i) {
            const WitVirtualRange *r = &ranges[i];
            if (!r->Size || candidate >= r->Base + r->Size || candidate + size <= r->Base) {
                continue;
            }
            if (!align_up(r->Base + r->Size, alignment, &candidate)) {
                return 0;
            }
            collision = 1;
            break;
        }
        if (!collision) {
            *result = candidate;
            return 1;
        }
    }
    return 0;
}
