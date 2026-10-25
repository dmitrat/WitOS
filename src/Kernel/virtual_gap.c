#include "witos/virtual_gap.h"

static int align_up(WitU64 value, WitU64 alignment, WitU64 *result)
{
    if (value > ~0ULL - (alignment - 1)) {
        return 0;
    }
    *result = (value + alignment - 1) & ~(alignment - 1);
    return 1;
}

/* Restores the max-heap order by base below one entry. */
static void sift_down(WitVirtualRange *heap, WitU32 at, WitU32 count)
{
    for (;;) {
        WitU32 largest = at;
        const WitU32 left = 2 * at + 1;
        const WitU32 right = left + 1;
        if (left < count && heap[left].Base > heap[largest].Base) {
            largest = left;
        }
        if (right < count && heap[right].Base > heap[largest].Base) {
            largest = right;
        }
        if (largest == at) {
            return;
        }
        const WitVirtualRange swap = heap[at];
        heap[at] = heap[largest];
        heap[largest] = swap;
        at = largest;
    }
}

/* Heapsort by base: in place, bounded and without recursion. */
static void sort_by_base(WitVirtualRange *ranges, WitU32 count)
{
    for (WitU32 i = count / 2; i > 0; --i) {
        sift_down(ranges, i - 1, count);
    }
    for (WitU32 end = count; end > 1; --end) {
        const WitVirtualRange swap = ranges[0];
        ranges[0] = ranges[end - 1];
        ranges[end - 1] = swap;
        sift_down(ranges, 0, end - 1);
    }
}

int wit_virtual_gap(const WitVirtualRange *ranges, WitU32 count, WitU64 low, WitU64 high, WitU64 size, WitU64 alignment,
    WitVirtualRange *scratch, WitU64 *result)
{
    if (!result) {
        return 0;
    }
    *result = 0;
    if ((count && (!ranges || !scratch)) ||
        !size ||
        !alignment ||
        (alignment & (alignment - 1)) ||
        low >= high ||
        size > high - low) {
        return 0;
    }
    // The ranges in use, in address order: a sweep then meets each once (P4: a first-fit search that rescanned the whole
    // table after every collision cost a process with 1600 mappings 12 million steps a reservation).
    WitU32 used = 0;
    for (WitU32 i = 0; i < count; ++i) {
        if (ranges[i].Size > ~0ULL - ranges[i].Base) {
            return 0;
        }
        if (ranges[i].Size) {
            scratch[used++] = ranges[i];
        }
    }
    sort_by_base(scratch, used);
    WitU64 candidate;
    if (!align_up(low, alignment, &candidate)) {
        return 0;
    }
    // The candidate only moves beyond the end of a range that overlaps it, to the next aligned address, so it stays the
    // lowest aligned address no range met so far excludes; every later range starts at or above the one that stopped it.
    for (WitU32 i = 0; i < used; ++i) {
        const WitVirtualRange *r = &scratch[i];
        if (candidate >= high || size > high - candidate) {
            return 0;
        }
        if (r->Base + r->Size <= candidate) {
            continue;
        }
        if (candidate + size <= r->Base) {
            break;
        }
        if (!align_up(r->Base + r->Size, alignment, &candidate)) {
            return 0;
        }
    }
    if (candidate >= high || size > high - candidate) {
        return 0;
    }
    *result = candidate;
    return 1;
}
