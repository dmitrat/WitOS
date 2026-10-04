#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#define CARD_BUNDLE
#define FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP
#define USE_REGIONS
#define BACKGROUND_GC
#define OS_PAGE_SIZE 4096
#define ALIGN_UP(value, alignment) (((value) + (alignment) - 1) & ~((alignment) - 1))

enum {
    card_table_element,
    brick_table_element,
    card_bundle_table_element,
    software_write_watch_table_element,
    region_to_generation_table_element,
    seg_mapping_table_element,
    mark_array_element,
    total_bookkeeping_elements
};

struct card_table_info {
    uint8_t bytes[64];
};

static size_t input[total_bookkeeping_elements];
// Layout-only fixture: deterministic sizes are inputs to actual pinned methods.
// No substituted collector or OS method is linked into any guest image.
#define DECLARE_LAYOUT \
    struct gc_heap { \
        static void get_card_table_element_sizes(uint8_t *, uint8_t *, size_t *output) \
        { \
            for (int i = 0; i < total_bookkeeping_elements; ++i) \
                output[i] = input[i]; \
        } \
        static void get_card_table_element_layout(uint8_t *, uint8_t *, size_t *); \
    };

namespace original {
DECLARE_LAYOUT
#include "original_layout.inc"
}

namespace corrected {
DECLARE_LAYOUT
#include "corrected_layout.inc"
}

int main()
{
    unsigned exposed = 0;
    for (unsigned scenario = 0; scenario < 128; ++scenario) {
        for (unsigned i = 0; i < total_bookkeeping_elements; ++i) {
            input[i] = (scenario + 3) * (i + 1) * 137;
        }
        input[software_write_watch_table_element] = 0;
        input[mark_array_element] = (scenario & 1) ? (scenario + 1) * 1331 : 0;
        input[seg_mapping_table_element] = (scenario + 9) * 168;
        size_t before[total_bookkeeping_elements + 1], after[total_bookkeeping_elements + 1];
        original::gc_heap::get_card_table_element_layout(nullptr, nullptr, before);
        corrected::gc_heap::get_card_table_element_layout(nullptr, nullptr, after);
        const size_t end = after[seg_mapping_table_element] + input[seg_mapping_table_element];
        if ((after[mark_array_element] & 4095) ||
            (after[total_bookkeeping_elements] & 4095) ||
            (after[mark_array_element] & ~size_t(4095)) < end) {
            return 1;
        }
        for (int i = 1; i <= total_bookkeeping_elements; ++i) {
            if (after[i] < after[i - 1] + input[i - 1]) {
                return 2;
            }
        }
        if ((before[mark_array_element] & ~size_t(4095)) <
            before[seg_mapping_table_element] + input[seg_mapping_table_element]) {
            ++exposed;
        }
    }
    if (!exposed) {
        return 3;
    }
    printf("PASS: 128 pinned GC bookkeeping layouts; original uncovered tails=%u; corrected commit boundaries cover "
           "all segment entries\n",
        exposed);
    return 0;
}
