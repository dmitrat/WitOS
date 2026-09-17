#include "user.h"

static WitU16 u16(const WitU8 *p) { return (WitU16)(p[0] | ((WitU16)p[1] << 8)); }
static WitU32 u32(const WitU8 *p) { return (WitU32)u16(p) | ((WitU32)u16(p + 2) << 16); }
static WitU64 u64(const WitU8 *p) { return (WitU64)u32(p) | ((WitU64)u32(p + 4) << 32); }

static int copy(WitUserSpace *space, WitU64 address, const WitU8 *source, WitU32 count)
{
    /* Fresh inactive space, immutable validated input, IF=0. Final user rights
     * are already installed; only the kernel's supervisor alias writes bytes. */
    for (WitU32 i = 0; i < count; ++i) {
        const WitU64 physical = wit_user_space_physical(space, address + i, 0, 0);
        if (!physical) return 0;
        *(WitU8 *)physical = source[i];
    }
    return 1;
}

/* Internal creation path only: plan was validated from this immutable file.
 * On failure the creator destroys the entire fresh address space. */
int wit_user_image_map(WitUserSpace *space, const WitU8 *file, const WitPeImage *plan, WitU64 base)
{
    WitU32 raw = 0, consumed = 0;
    if (!wit_user_space_map(space, base, 0, 0) ||
        !copy(space, base, file, plan->HeadersSize)) return 0;
    for (WitU32 i = 0; i < plan->SectionCount; ++i) {
        const WitPeSection *s = &plan->Sections[i];
        for (WitU32 p = 0; p < s->MapSize; p += 4096)
            if (!wit_user_space_map(space, base + s->Rva + p,
                (s->Flags & WIT_PE_WRITE) != 0, (s->Flags & WIT_PE_EXECUTE) != 0)) return 0;
        if (!copy(space, base + s->Rva, file + s->RawOffset, s->RawSize)) return 0;
    }
    if (plan->RelocSize && !wit_pe_file_range(plan, plan->RelocRva, plan->RelocSize, &raw)) return 0;
    while (consumed < plan->RelocSize) {
        const WitU8 *block = file + raw + consumed;
        const WitU32 page = u32(block), length = u32(block + 4);
        for (WitU32 position = 8; position < length; position += 2) {
            const WitU16 relocation = u16(block + position);
            WitU32 original;
            WitU64 value;
            WitU8 bytes[8];
            if ((relocation >> 12) == 0) continue;
            if (!wit_pe_file_range(plan, page + (relocation & 4095), 8, &original)) return 0;
            /* Internal-pointer profile avoids signed-delta overflow in either direction. */
            value = base + (u64(file + original) - plan->PreferredBase);
            for (WitU32 b = 0; b < 8; ++b) bytes[b] = (WitU8)(value >> (b * 8));
            if (!copy(space, base + page + (relocation & 4095), bytes, 8)) return 0;
        }
        consumed += length;
    }
    return 1;
}
