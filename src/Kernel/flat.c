#include "witos/flat.h"
#include "witos/user_abi.h"
#include "witos/user_layout.h"
#include "root_task.h"

/* Validation of a flat image (plan step K4): the whole file is checked before any page is touched. Addresses must
 * lie in the component's fixed region above the kernel-provided pages and below the image limit; segments are
 * page-aligned, within the file, bounded, disjoint, and the entry lies in an executable one. */

static int valid_protection(WitU32 protection)
{
    return protection == WIT_MEMORY_READ ||
        protection == (WIT_MEMORY_READ | WIT_MEMORY_WRITE) ||
        protection == (WIT_MEMORY_READ | WIT_MEMORY_EXECUTE);
}

static WitU64 read64(const WitU8 *p)
{
    WitU64 value = 0;
    for (WitU32 i = 0; i < 8; ++i) {
        value |= (WitU64)p[i] << (i * 8);
    }
    return value;
}

static WitU32 read32(const WitU8 *p)
{
    return (WitU32)p[0] | ((WitU32)p[1] << 8) | ((WitU32)p[2] << 16) | ((WitU32)p[3] << 24);
}

int wit_flat_validate(const WitU8 *file, WitU64 size, WitFlatLayout *layout)
{
    if (!file || size < WIT_FLAT_HEADER_SIZE + WIT_FLAT_SEGMENTS * WIT_FLAT_SEGMENT_SIZE || size > WIT_FLAT_MAX_BYTES) {
        return 0;
    }
    if (read64(file) != WIT_FLAT_MAGIC ||
        read32(file + 8) != WIT_FLAT_VERSION ||
        read32(file + 12) != WIT_FLAT_HEADER_SIZE) {
        return 0;
    }
    const WitU64 entry = read64(file + 16);
    const WitU32 count = read32(file + 24);
    if (read32(file + 28) != 0 || !count || count > WIT_FLAT_SEGMENTS) {
        return 0;
    }
    for (WitU32 i = 32; i < WIT_FLAT_HEADER_SIZE; ++i) {
        if (file[i]) {
            return 0;
        }
    }
    int entry_executable = 0;
    for (WitU32 i = 0; i < count; ++i) {
        const WitU8 *s = file + WIT_FLAT_HEADER_SIZE + i * WIT_FLAT_SEGMENT_SIZE;
        WitFlatSegment *out = &layout->Segments[i];
        out->Address = read64(s);
        out->FileOffset = read64(s + 8);
        out->FileSize = read32(s + 16);
        out->MemorySize = read32(s + 20);
        out->Protection = read32(s + 24);
        out->Reserved = read32(s + 28);
        if (out->Reserved ||
            !valid_protection(out->Protection) ||
            (out->Address & 4095) ||
            (out->FileOffset & 4095) ||
            (out->MemorySize & 4095) ||
            !out->MemorySize ||
            out->MemorySize > WIT_FLAT_MAX_SEGMENT_BYTES ||
            out->FileSize > out->MemorySize ||
            out->FileOffset > size ||
            out->FileSize > size - out->FileOffset) {
            return 0;
        }
        /* The fixed region above the kernel's pages (startup block, data, stacks, TLS) and below its limit. */
        if (out->Address < WIT_USER_FLAT_BASE ||
            out->Address >= WIT_RUNTIME_USER_LIMIT || /* the root task's window: the full profile's (S1.3) */
            out->MemorySize > WIT_RUNTIME_USER_LIMIT - out->Address) {
            return 0;
        }
        for (WitU32 k = 0; k < i; ++k) {
            const WitFlatSegment *other = &layout->Segments[k];
            if (out->Address < other->Address + other->MemorySize && other->Address < out->Address + out->MemorySize) {
                return 0;
            }
        }
        if ((out->Protection & WIT_MEMORY_EXECUTE) && entry >= out->Address && entry - out->Address < out->MemorySize) {
            entry_executable = 1;
        }
    }
    for (WitU32 i = count; i < WIT_FLAT_SEGMENTS; ++i) {
        const WitU8 *s = file + WIT_FLAT_HEADER_SIZE + i * WIT_FLAT_SEGMENT_SIZE;
        for (WitU32 k = 0; k < WIT_FLAT_SEGMENT_SIZE; ++k) {
            if (s[k]) {
                return 0;
            }
        }
    }
    if (!entry_executable) {
        return 0;
    }
    layout->Entry = entry;
    layout->SegmentCount = count;
    layout->File = file;
    layout->Size = size;
    return 1;
}
