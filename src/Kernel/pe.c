#include "witos/pe.h"

static WitU16 u16(const WitU8 *p) { return (WitU16)(p[0] | ((WitU16)p[1] << 8)); }
static WitU32 u32(const WitU8 *p) { return (WitU32)u16(p) | ((WitU32)u16(p + 2) << 16); }
static WitU64 u64(const WitU8 *p) { return (WitU64)u32(p) | ((WitU64)u32(p + 4) << 32); }
static int range(WitU32 offset, WitU32 length, WitU32 bound)
{
    return offset <= bound && length <= bound - offset;
}
static int overlap(WitU32 a, WitU32 a_size, WitU32 b, WitU32 b_size)
{
    return (WitU64)a < (WitU64)b + b_size && (WitU64)b < (WitU64)a + a_size;
}

int wit_pe_file_range(const WitPeImage *plan, WitU32 rva, WitU32 size, WitU32 *offset)
{
    for (WitU32 i = 0; i < plan->SectionCount; ++i) {
        const WitPeSection *s = &plan->Sections[i];
        if (rva >= s->Rva && range(rva - s->Rva, size, s->VirtualSize) &&
            range(rva - s->Rva, size, s->RawSize)) {
            *offset = s->RawOffset + (rva - s->Rva);
            return 1;
        }
    }
    return 0;
}

static WitPeStatus relocations(const WitU8 *file, const WitPeImage *plan)
{
    WitU32 raw, consumed = 0, entries = 0, previous_page = 0, previous_target = 0;
    int have_page = 0, have_target = 0;
    if (!plan->RelocSize) return WitPeOk;
    if (!wit_pe_file_range(plan, plan->RelocRva, plan->RelocSize, &raw)) return WitPeInvalidImage;
    while (consumed < plan->RelocSize) {
        const WitU8 *block = file + raw + consumed;
        WitU32 page, length;
        if (!range(consumed, 8, plan->RelocSize)) return WitPeInvalidImage;
        page = u32(block);
        length = u32(block + 4);
        if ((page & 4095) || page >= plan->ImageSize || (have_page && page <= previous_page) ||
            length < 8 || (length & 3) || !range(consumed, length, plan->RelocSize)) return WitPeInvalidImage;
        previous_page = page;
        have_page = 1;
        if ((length - 8) / 2 > WIT_PE_MAX_RELOCATIONS - entries) return WitPeTooLarge;
        entries += (length - 8) / 2;
        for (WitU32 position = 8; position < length; position += 2) {
            const WitU16 relocation = u16(block + position);
            const WitU32 kind = relocation >> 12;
            const WitU32 target = page + (relocation & 4095);
            WitU32 target_raw;
            WitU64 value;
            if (kind == 0) continue;
            if (kind != 10) return WitPeUnsupportedImage;
            /* Sorted, nonoverlapping internal DIR64 pointers only. Read source
             * bytes, never a relocation table that earlier fixups could mutate. */
            if ((have_target && (WitU64)target < (WitU64)previous_target + 8) ||
                overlap(target, 8, plan->RelocRva, plan->RelocSize) ||
                !wit_pe_file_range(plan, target, 8, &target_raw)) return WitPeInvalidImage;
            value = u64(file + target_raw);
            if (value < plan->PreferredBase || value - plan->PreferredBase > plan->ImageSize)
                return WitPeInvalidImage;
            previous_target = target;
            have_target = 1;
        }
        consumed += length;
    }
    return WitPeOk;
}

WitPeStatus wit_pe_validate(const WitU8 *file, WitU32 size, WitPeImage *plan)
{
    WitU32 nt, optional, sections, alignment, maximum, debug_rva, debug_size;
    WitU16 characteristics;
    int entry_valid = 0;
    if (!file || !plan || size < 64) return WitPeInvalidImage;
    if (size > WIT_PE_MAX_FILE_SIZE) return WitPeTooLarge;
    if (u16(file) != 0x5A4D) return WitPeInvalidImage;
    nt = u32(file + 60);
    if (nt < 64 || !range(nt, 24, size) || u32(file + nt) != 0x4550) return WitPeInvalidImage;
    if (u16(file + nt + 4) != 0x8664) return WitPeUnsupportedImage;
    characteristics = u16(file + nt + 22);
    if (!(characteristics & 2)) return WitPeInvalidImage;
    if ((characteristics & 0x2000) || u32(file + nt + 12) || u32(file + nt + 16))
        return WitPeUnsupportedImage;
    plan->SectionCount = u16(file + nt + 6);
    if (!plan->SectionCount) return WitPeInvalidImage;
    if (plan->SectionCount > WIT_PE_MAX_SECTIONS) return WitPeTooLarge;
    if (u16(file + nt + 20) != 240) return WitPeUnsupportedImage;
    optional = nt + 24;
    if (!range(optional, 240, size)) return WitPeInvalidImage;
    if (u16(file + optional) != 0x20B || u16(file + optional + 68) != 1)
        return WitPeUnsupportedImage;
    if (u32(file + optional + 32) != 4096 || u32(file + optional + 108) != 16 ||
        (u16(file + optional + 70) & 0x4000)) return WitPeUnsupportedImage;
    if (u32(file + optional + 52) || u32(file + optional + 104)) return WitPeInvalidImage;
    alignment = u32(file + optional + 36);
    if (alignment < 512 || alignment > 4096 || (alignment & (alignment - 1))) return WitPeUnsupportedImage;
    plan->PreferredBase = u64(file + optional + 24);
    plan->ImageSize = u32(file + optional + 56);
    plan->HeadersSize = u32(file + optional + 60);
    plan->EntryRva = u32(file + optional + 16);
    if (!plan->ImageSize || (plan->ImageSize & 4095)) return WitPeInvalidImage;
    if (plan->ImageSize > WIT_PE_MAX_IMAGE_SIZE) return WitPeTooLarge;
    if (!plan->PreferredBase || (plan->PreferredBase & 65535) ||
        plan->PreferredBase > 0x0000800000000000ULL - plan->ImageSize) return WitPeInvalidImage;
    if (!plan->HeadersSize || plan->HeadersSize > 4096 ||
        (plan->HeadersSize & (alignment - 1)) || !range(0, plan->HeadersSize, size)) return WitPeInvalidImage;
    sections = optional + 240;
    if (!range(sections, plan->SectionCount * 40, plan->HeadersSize)) return WitPeInvalidImage;

    plan->RelocRva = u32(file + optional + 112 + 5 * 8);
    plan->RelocSize = u32(file + optional + 116 + 5 * 8);
    debug_rva = u32(file + optional + 112 + 6 * 8);
    debug_size = u32(file + optional + 116 + 6 * 8);
    for (WitU32 i = 0; i < 16; ++i) {
        const WitU32 rva = u32(file + optional + 112 + i * 8);
        const WitU32 length = u32(file + optional + 116 + i * 8);
        if (i != 5 && i != 6 && (rva || length)) return WitPeUnsupportedImage;
        if ((!rva) != (!length)) return WitPeInvalidImage;
    }
    if ((characteristics & 1) && plan->RelocSize) return WitPeInvalidImage;
    maximum = 4096;
    for (WitU32 i = 0; i < plan->SectionCount; ++i) {
        const WitU8 *header = file + sections + i * 40;
        const WitU32 flags = u32(header + 36);
        WitPeSection *s = &plan->Sections[i];
        WitU32 extent;
        s->VirtualSize = u32(header + 8);
        s->Rva = u32(header + 12);
        s->RawSize = u32(header + 16);
        s->RawOffset = u32(header + 20);
        if (!s->VirtualSize) s->VirtualSize = s->RawSize;
        if (!(flags & 0x40000000U) || (flags & 0x1C000000U) ||
            (flags & 0xA0000000U) == 0xA0000000U) return WitPeUnsupportedImage;
        if (u32(header + 24) || u32(header + 28) || u32(header + 32)) return WitPeUnsupportedImage;
        s->Flags = WIT_PE_READ | (flags & 0x80000000U ? WIT_PE_WRITE : 0) |
            (flags & 0x20000000U ? WIT_PE_EXECUTE : 0);
        extent = s->VirtualSize > s->RawSize ? s->VirtualSize : s->RawSize;
        if (!extent || s->Rva < 4096 || (s->Rva & 4095) ||
            !range(s->Rva, extent, plan->ImageSize)) return WitPeInvalidImage;
        s->MapSize = (extent + 4095) & ~4095U;
        if (!range(s->Rva, s->MapSize, plan->ImageSize)) return WitPeInvalidImage;
        if (s->RawSize) {
            if (s->RawOffset < plan->HeadersSize || (s->RawOffset & (alignment - 1)) ||
                (s->RawSize & (alignment - 1)) || !range(s->RawOffset, s->RawSize, size))
                return WitPeInvalidImage;
        } else if (s->RawOffset) return WitPeInvalidImage;
        for (WitU32 j = 0; j < i; ++j) {
            const WitPeSection *other = &plan->Sections[j];
            if (overlap(s->Rva, s->MapSize, other->Rva, other->MapSize) ||
                (s->RawSize && other->RawSize &&
                    overlap(s->RawOffset, s->RawSize, other->RawOffset, other->RawSize)))
                return WitPeInvalidImage;
        }
        if ((s->Flags & WIT_PE_EXECUTE) && plan->EntryRva >= s->Rva &&
            plan->EntryRva - s->Rva < s->VirtualSize && plan->EntryRva - s->Rva < s->RawSize)
            entry_valid = 1;
        if (s->Rva + s->MapSize > maximum) maximum = s->Rva + s->MapSize;
    }
    if (!entry_valid || maximum != plan->ImageSize) return WitPeInvalidImage;
    if (debug_size) {
        WitU32 ignored;
        /* Reproducible-link debug bytes are opaque data, never interpreted or invoked. */
        if (debug_size % 28 || !wit_pe_file_range(plan, debug_rva, debug_size, &ignored))
            return WitPeInvalidImage;
    }
    return relocations(file, plan);
}
