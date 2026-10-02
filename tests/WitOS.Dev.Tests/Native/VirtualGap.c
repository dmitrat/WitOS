#include <stdio.h>
#include "witos/virtual_gap.h"

int main(void)
{
    unsigned tested = 0;
    for (unsigned mask = 0; mask < 256; ++mask) {
        for (unsigned low = 0; low < 9; ++low) {
            for (unsigned high = low; high < 9; ++high) {
                for (unsigned size = 1; size <= 8; ++size) {
                    for (unsigned alignment = 1; alignment <= 8; alignment *= 2) {
                        WitVirtualRange ranges[8];
                        unsigned count = 0;
                        // Reverse ordering deliberately exercises unsorted occupied spans.
                        for (unsigned i = 8; i > 0; --i) {
                            if (mask & (1U << (i - 1))) {
                                ranges[count++] = (WitVirtualRange){i - 1, 1};
                            }
                        }
                        int found = 0;
                        WitU64 expected = 0, actual = 99;
                        for (unsigned at = low; at < high && size <= high - at; ++at) {
                            if (!(at & (alignment - 1)) && !(mask & (((1U << size) - 1) << at))) {
                                found = 1;
                                expected = at;
                                break;
                            }
                        }
                        const int result = wit_virtual_gap(ranges, count, low, high, size, alignment, &actual);
                        if (result != found || actual != expected) {
                            printf("FAIL mask=%u low=%u high=%u size=%u align=%u expected=%d/%llu actual=%d/%llu\n",
                                mask, low, high, size, alignment, found, expected, result, actual);
                            return 1;
                        }
                        ++tested;
                    }
                }
            }
        }
    }
    WitU64 result = 99;
    const WitVirtualRange overflow = {~0ULL - 3, 8};
    if (wit_virtual_gap(&overflow, 1, 0, 32, 1, 1, &result) || result) {
        return 2;
    }
    if (wit_virtual_gap(0, 0, ~0ULL - 4, ~0ULL, 1, 8, &result) || result) {
        return 3;
    }
    if (!wit_virtual_gap(0, 0, ~0ULL - 4, ~0ULL, 4, 1, &result) || result != ~0ULL - 4) {
        return 4;
    }
    if (wit_virtual_gap(0, 1, 0, 32, 1, 1, &result) || result) {
        return 5;
    }
    if (wit_virtual_gap(0, 0, 0, 32, 1, 3, &result) || result) {
        return 6;
    }
    const WitVirtualRange overlapping[] = {{7, 3}, {2, 8}, {12, 2}};
    if (!wit_virtual_gap(overlapping, 3, 0, 32, 4, 4, &result) || result != 16) {
        return 7;
    }
    printf("PASS: %u exhaustive virtual-gap cases plus 6 overflow/contract cases\n", tested);
    return 0;
}
