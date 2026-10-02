#include "witos/pe_imports.h"
#include "witos/pe.h"
#include "witos/unwind_metadata.h"

static WitU16 u16(const WitU8 *p)
{
    return (WitU16)(p[0] | ((WitU16)p[1] << 8));
}

static WitU32 u32(const WitU8 *p)
{
    return (WitU32)u16(p) | ((WitU32)u16(p + 2) << 16);
}

static WitU64 u64(const WitU8 *p)
{
    return (WitU64)u32(p) | ((WitU64)u32(p + 4) << 32);
}

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
        if (rva >= s->Rva && range(rva - s->Rva, size, s->VirtualSize) && range(rva - s->Rva, size, s->RawSize)) {
            *offset = s->RawOffset + (rva - s->Rva);
            return 1;
        }
    }
    return 0;
}

static int file_range_flags(
    const WitPeImage *plan, WitU32 rva, WitU32 size, WitU32 required, WitU32 forbidden, WitU32 *raw)
{
    for (WitU32 i = 0; i < plan->SectionCount; ++i) {
        const WitPeSection *s = &plan->Sections[i];
        if ((s->Flags & required) == required &&
            !(s->Flags & forbidden) &&
            rva >= s->Rva &&
            range(rva - s->Rva, size, s->VirtualSize) &&
            range(rva - s->Rva, size, s->RawSize)) {
            *raw = s->RawOffset + rva - s->Rva;
            return 1;
        }
    }
    return 0;
}

/* Runtime-written data such as the TLS index may live in zero-filled BSS. */
static int memory_range_flags(const WitPeImage *plan, WitU32 rva, WitU32 size, WitU32 required, WitU32 forbidden)
{
    for (WitU32 i = 0; i < plan->SectionCount; ++i) {
        const WitPeSection *s = &plan->Sections[i];
        if ((s->Flags & required) == required &&
            !(s->Flags & forbidden) &&
            rva >= s->Rva &&
            range(rva - s->Rva, size, s->VirtualSize)) {
            return 1;
        }
    }
    return 0;
}

static int nonvolatile_register(WitU32 reg)
{
    return reg == 3 || reg == 5 || reg == 6 || reg == 7 || (reg >= 12 && reg <= 15);
}

static WitPeStatus unwind_info(const WitU8 *file, WitPeImage *plan)
{
    WitU32 raw, previous_end = 0;
    plan->UnwindCount = 0;
    if (!plan->UnwindSize) {
        return WitPeOk;
    }
    if ((plan->UnwindRva & 3) || plan->UnwindSize % 12) {
        return WitPeInvalidImage;
    }
    if (plan->UnwindSize / 12 > WIT_PE_MAX_UNWIND_ENTRIES) {
        return WitPeTooLarge;
    }
    if (!file_range_flags(plan, plan->UnwindRva, plan->UnwindSize, WIT_PE_READ, WIT_PE_WRITE | WIT_PE_EXECUTE, &raw)) {
        return WitPeInvalidImage;
    }
    for (WitU32 i = 0; i < plan->UnwindSize / 12; ++i) {
        const WitU32 begin = u32(file + raw + i * 12);
        const WitU32 end = u32(file + raw + i * 12 + 4);
        const WitU32 info = u32(file + raw + i * 12 + 8);
        WitU32 info_raw, ignored, length, frame_sets = 0, previous_code;
        const WitU8 *data;
        WitU32 slots, frame;
        if (begin >= end ||
            begin < previous_end ||
            (info & 3) ||
            !file_range_flags(plan, begin, end - begin, WIT_PE_EXECUTE, WIT_PE_WRITE, &ignored) ||
            !file_range_flags(plan, info, 4, WIT_PE_READ, WIT_PE_WRITE | WIT_PE_EXECUTE, &info_raw)) {
            return WitPeInvalidImage;
        }
        data = file + info_raw;
        /* Metadata only: version-1 prologs without handler/chained/machine frames. */
        if (data[0] != 1) {
            return WitPeUnsupportedImage;
        }
        slots = data[2];
        frame = data[3] & 15;
        if (data[1] > end - begin || (!frame && data[3]) || (frame && !nonvolatile_register(frame))) {
            return WitPeInvalidImage;
        }
        length = (4 + slots * 2 + 3) & ~3U;
        if (!file_range_flags(plan, info, length, WIT_PE_READ, WIT_PE_WRITE | WIT_PE_EXECUTE, &ignored) ||
            overlap(info, length, plan->UnwindRva, plan->UnwindSize)) {
            return WitPeInvalidImage;
        }
        previous_code = data[1];
        for (WitU32 slot = 0; slot < slots;) {
            const WitU8 *code = data + 4 + slot * 2;
            const WitU32 operation = code[1] & 15, argument = code[1] >> 4;
            WitU32 extra = 0;
            if (code[0] > previous_code) {
                return WitPeInvalidImage;
            }
            previous_code = code[0];
            switch (operation) {
            case 0:
                if (!nonvolatile_register(argument)) {
                    return WitPeInvalidImage;
                }
                break;
            case 1:
                if (argument > 1) {
                    return WitPeInvalidImage;
                }
                extra = argument ? 2 : 1;
                break;
            case 2:
                break;
            case 3:
                if (argument || !frame || ++frame_sets != 1) {
                    return WitPeInvalidImage;
                }
                break;
            case 4:
            case 5:
                if (!nonvolatile_register(argument)) {
                    return WitPeInvalidImage;
                }
                extra = operation == 4 ? 1 : 2;
                break;
            case 8:
            case 9:
                if (argument < 6) {
                    return WitPeInvalidImage;
                }
                extra = operation == 8 ? 1 : 2;
                break;
            default:
                return WitPeUnsupportedImage;
            }
            if (extra > slots - slot - 1) {
                return WitPeInvalidImage;
            }
            if (operation == 1 &&
                ((!argument && !u16(code + 2)) || (argument && (!u32(code + 2) || (u32(code + 2) & 7))))) {
                return WitPeInvalidImage;
            }
            if ((operation == 5 && (u32(code + 2) & 7)) || (operation == 9 && (u32(code + 2) & 15))) {
                return WitPeInvalidImage;
            }
            slot += 1 + extra;
        }
        if (frame && !frame_sets) {
            return WitPeInvalidImage;
        }
        plan->UnwindInfo[i].Rva = info;
        plan->UnwindInfo[i].Size = length;
        ++plan->UnwindCount;
        previous_end = end;
    }
    return WitPeOk;
}

typedef struct WitPeUnwindReader {
    const WitU8 *File;
    WitPeImage *Plan;
} WitPeUnwindReader;

static const WitU8 *runtime_unwind_read(void *context, WitU32 rva, WitU32 size, int code)
{
    const WitPeUnwindReader *reader = (const WitPeUnwindReader *)context;
    WitU32 raw;
    return file_range_flags(reader->Plan, rva, size, WIT_PE_READ | (code ? WIT_PE_EXECUTE : 0),
               WIT_PE_WRITE | (code ? 0 : WIT_PE_EXECUTE), &raw)
        ? reader->File + raw
        : 0;
}

static int runtime_unwind_visit(void *context, WitU32 rva, WitU32 size)
{
    WitPeImage *plan = ((WitPeUnwindReader *)context)->Plan;
    for (WitU32 i = 0; i < plan->UnwindCount; ++i) {
        if (plan->UnwindInfo[i].Rva == rva) {
            return plan->UnwindInfo[i].Size == size;
        }
    }
    if (plan->UnwindCount ==
        ((plan->Profile & WIT_PE_RUNTIME_FULL) ? WIT_PE_FULL_UNWIND_ENTRIES : WIT_PE_MAX_UNWIND_RANGES)) {
        return 0;
    }
    plan->UnwindInfo[plan->UnwindCount++] = (WitPeUnwindRange){rva, size};
    return 1;
}

static WitPeStatus runtime_unwind_info(const WitU8 *file, WitPeImage *plan)
{
    plan->UnwindCount = 0;
    if (!plan->UnwindSize) {
        return WitPeOk;
    }
    WitPeUnwindReader reader = {file, plan};
    const WitUnwindMetadataView view = {&reader, runtime_unwind_read, runtime_unwind_visit, plan->ImageSize,
        plan->UnwindRva, plan->UnwindSize,
        (plan->Profile & WIT_PE_RUNTIME_FULL) ? WIT_PE_FULL_UNWIND_ENTRIES : WIT_PE_RUNTIME_UNWIND_ENTRIES};
    const WitUnwindValidation status = wit_unwind_metadata_image(&view);
    if (status == WitUnwindValid) {
        return WitPeOk;
    }
    if (status == WitUnwindQuota) {
        return WitPeTooLarge;
    }
    return status == WitUnwindUnsupported ? WitPeUnsupportedImage : WitPeInvalidImage;
}

/* Single static TLS module. Addresses in PE32+ TLS directories are VAs. */
static int tls_rva(const WitPeImage *plan, WitU64 va, WitU32 *rva)
{
    if (va < plan->PreferredBase || va - plan->PreferredBase > plan->ImageSize) {
        return 0;
    }
    *rva = (WitU32)(va - plan->PreferredBase);
    return 1;
}

static WitPeStatus tls_info(const WitU8 *file, WitPeImage *plan)
{
    WitU32 raw, ignored, flags, alignment, end;
    WitU64 callbacks;
    plan->TlsTemplateRva = plan->TlsInitialized = plan->TlsZeroFill = plan->TlsIndexRva = plan->TlsCallbacksRva = 0;
    if (!plan->TlsSize) {
        return WitPeOk;
    }
    if (plan->TlsSize != 40 ||
        (plan->TlsRva & 7) ||
        !file_range_flags(plan, plan->TlsRva, 40, WIT_PE_READ, WIT_PE_WRITE | WIT_PE_EXECUTE, &raw)) {
        return WitPeInvalidImage;
    }
    if (!tls_rva(plan, u64(file + raw), &plan->TlsTemplateRva) ||
        !tls_rva(plan, u64(file + raw + 8), &end) ||
        end < plan->TlsTemplateRva ||
        !tls_rva(plan, u64(file + raw + 16), &plan->TlsIndexRva) ||
        (plan->TlsIndexRva & 3)) {
        return WitPeInvalidImage;
    }
    plan->TlsInitialized = end - plan->TlsTemplateRva;
    plan->TlsZeroFill = u32(file + raw + 32);
    if (plan->TlsInitialized > WIT_PE_TLS_MAX_BYTES ||
        plan->TlsZeroFill > WIT_PE_TLS_MAX_BYTES - plan->TlsInitialized) {
        return WitPeTooLarge;
    }
    if (!plan->TlsInitialized && !plan->TlsZeroFill) {
        return WitPeUnsupportedImage;
    }
    flags = u32(file + raw + 36);
    alignment = (flags >> 20) & 15;
    if ((flags & ~0x00F00000U) || alignment > 9) {
        return WitPeUnsupportedImage;
    }
    if (!file_range_flags(plan, plan->TlsTemplateRva, plan->TlsInitialized, WIT_PE_READ, WIT_PE_EXECUTE, &ignored) ||
        !memory_range_flags(plan, plan->TlsIndexRva, 4, WIT_PE_READ | WIT_PE_WRITE, WIT_PE_EXECUTE) ||
        overlap(plan->TlsIndexRva, 4, plan->TlsTemplateRva, plan->TlsInitialized) ||
        overlap(plan->TlsRva, 40, plan->TlsTemplateRva, plan->TlsInitialized)) {
        return WitPeInvalidImage;
    }
    callbacks = u64(file + raw + 24);
    if (callbacks) {
        if (!tls_rva(plan, callbacks, &plan->TlsCallbacksRva) ||
            (plan->TlsCallbacksRva & 7) ||
            !file_range_flags(plan, plan->TlsCallbacksRva, 8, WIT_PE_READ, WIT_PE_WRITE | WIT_PE_EXECUTE, &ignored)) {
            return WitPeInvalidImage;
        }
        if (u64(file + ignored)) {
            return WitPeUnsupportedImage;
        }
    }
    return WitPeOk;
}

static WitPeStatus relocations(const WitU8 *file, const WitPeImage *plan, const WitPeImports *imports)
{
    WitU32 raw, consumed = 0, entries = 0, previous_page = 0, previous_target = 0;
    int have_page = 0, have_target = 0;
    WitU32 tls_fixups = 0;
    if (!plan->RelocSize) {
        return WitPeOk;
    }
    if (!wit_pe_file_range(plan, plan->RelocRva, plan->RelocSize, &raw)) {
        return WitPeInvalidImage;
    }
    while (consumed < plan->RelocSize) {
        const WitU8 *block = file + raw + consumed;
        WitU32 page, length;
        if (!range(consumed, 8, plan->RelocSize)) {
            return WitPeInvalidImage;
        }
        page = u32(block);
        length = u32(block + 4);
        if ((page & 4095) ||
            page >= plan->ImageSize ||
            (have_page && page <= previous_page) ||
            length < 8 ||
            (length & 3) ||
            !range(consumed, length, plan->RelocSize)) {
            return WitPeInvalidImage;
        }
        previous_page = page;
        have_page = 1;
        if ((length - 8) / 2 > WIT_PE_MAX_RELOCATIONS - entries) {
            return WitPeTooLarge;
        }
        entries += (length - 8) / 2;
        for (WitU32 position = 8; position < length; position += 2) {
            const WitU16 relocation = u16(block + position);
            const WitU32 kind = relocation >> 12;
            const WitU32 target = page + (relocation & 4095);
            WitU32 target_raw;
            WitU64 value;
            if (kind == 0) {
                continue;
            }
            if (kind != 10) {
                return WitPeUnsupportedImage;
            }
            /* Sorted, nonoverlapping internal DIR64 pointers only. Read source
             * bytes, never a relocation table that earlier fixups could mutate. */
            if ((have_target && (WitU64)target < (WitU64)previous_target + 8) ||
                overlap(target, 8, plan->RelocRva, plan->RelocSize) ||
                !wit_pe_file_range(plan, target, 8, &target_raw)) {
                return WitPeInvalidImage;
            }
            if (imports && wit_pe_imports_overlap(imports, target, 8)) {
                return WitPeInvalidImage;
            }
            if (overlap(target, 8, plan->IatRva, plan->IatSize)) {
                return WitPeInvalidImage;
            }
            if (overlap(target, 8, plan->ExportRva, plan->ExportSize)) {
                return WitPeInvalidImage;
            }
            if (overlap(target, 8, plan->UnwindRva, plan->UnwindSize)) {
                return WitPeInvalidImage;
            }
            for (WitU32 i = 0; i < plan->UnwindCount; ++i) {
                if (overlap(target, 8, plan->UnwindInfo[i].Rva, plan->UnwindInfo[i].Size)) {
                    return WitPeInvalidImage;
                }
            }
            if (plan->TlsSize) {
                if (overlap(target, 8, plan->TlsIndexRva, 4) ||
                    (plan->TlsCallbacksRva && overlap(target, 8, plan->TlsCallbacksRva, 8))) {
                    return WitPeInvalidImage;
                }
                if (overlap(target, 8, plan->TlsRva, 40)) {
                    if (target < plan->TlsRva || target - plan->TlsRva > 24 || ((target - plan->TlsRva) & 7)) {
                        return WitPeInvalidImage;
                    }
                    tls_fixups |= 1U << ((target - plan->TlsRva) / 8);
                }
            }
            value = u64(file + target_raw);
            if (value < plan->PreferredBase || value - plan->PreferredBase > plan->ImageSize) {
                return WitPeInvalidImage;
            }
            previous_target = target;
            have_target = 1;
        }
        consumed += length;
    }
    if (plan->TlsSize && tls_fixups != (plan->TlsCallbacksRva ? 15U : 7U)) {
        return WitPeInvalidImage;
    }
    return WitPeOk;
}

static WitPeStatus import_relocations(const WitU8 *file, WitU32 bytes, const WitPeImage *plan)
{
    WitPeImports imports;
    WitPeStatus status = wit_pe_imports_validate(file, bytes, plan, plan->ImportRva, plan->ImportSize, &imports);
    if (status != WitPeOk) {
        return status;
    }
    if (plan->IatSize) {
        WitU32 raw;
        int readonly = 0;
        if (!plan->ImportSize ||
            (plan->IatRva & 7) ||
            (plan->IatSize & 7) ||
            !wit_pe_file_range(plan, plan->IatRva, plan->IatSize, &raw)) {
            return WitPeInvalidImage;
        }
        for (WitU32 i = 0; i < plan->SectionCount; ++i) {
            const WitPeSection *section = &plan->Sections[i];
            if (section->Flags == WIT_PE_READ &&
                plan->IatRva >= section->Rva &&
                plan->IatRva - section->Rva <= section->VirtualSize &&
                plan->IatSize <= section->VirtualSize - (plan->IatRva - section->Rva)) {
                readonly = 1;
            }
        }
        if (!readonly) {
            return WitPeInvalidImage;
        }
    }
    for (WitU32 i = 0; i < imports.ModuleCount; ++i) {
        const WitPeImportModule *module = &imports.Modules[i];
        const WitU32 size = (module->SymbolCount + 1) * 8;
        if (plan->IatSize &&
            (module->IatRva < plan->IatRva ||
                module->IatRva - plan->IatRva > plan->IatSize ||
                size > plan->IatSize - (module->IatRva - plan->IatRva))) {
            return WitPeInvalidImage;
        }
        if (overlap(module->IatRva, size, plan->ExportRva, plan->ExportSize) ||
            overlap(module->IatRva, size, plan->UnwindRva, plan->UnwindSize) ||
            overlap(module->IatRva, size, plan->TlsRva, plan->TlsSize) ||
            overlap(module->IatRva, size, plan->TlsTemplateRva, plan->TlsInitialized)) {
            return WitPeInvalidImage;
        }
        for (WitU32 j = 0; j < plan->UnwindCount; ++j) {
            if (overlap(module->IatRva, size, plan->UnwindInfo[j].Rva, plan->UnwindInfo[j].Size)) {
                return WitPeInvalidImage;
            }
        }
    }
    return relocations(file, plan, &imports);
}

WitPeStatus wit_pe_validate_profile(const WitU8 *file, WitU32 size, WitPeImage *plan, WitU32 profile)
{
    if ((profile &
            ~(WIT_PE_UNWIND_RUNTIME |
                WIT_PE_RUNTIME_FULL |
                WIT_PE_LIBRARY |
                WIT_PE_LIBRARY_IMPORTS |
                WIT_PE_LIBRARY_TLS)) ||
        ((profile & WIT_PE_RUNTIME_FULL) && !(profile & WIT_PE_UNWIND_RUNTIME))) {
        return WitPeUnsupportedImage;
    }
    if ((profile & (WIT_PE_LIBRARY_IMPORTS | WIT_PE_LIBRARY_TLS)) && !(profile & WIT_PE_LIBRARY)) {
        return WitPeUnsupportedImage;
    }
    if ((profile & WIT_PE_LIBRARY) && (profile & WIT_PE_RUNTIME_FULL)) {
        return WitPeUnsupportedImage;
    }
    WitU32 nt, optional, sections, alignment, maximum, debug_rva, debug_size;
    WitU16 characteristics;
    int entry_valid = 0;
    if (!file || !plan || size < 64) {
        return WitPeInvalidImage;
    }
    plan->Profile = profile;
    if (size > WIT_PE_MAX_FILE_SIZE) {
        return WitPeTooLarge;
    }
    if (u16(file) != 0x5A4D) {
        return WitPeInvalidImage;
    }
    nt = u32(file + 60);
    if (nt < 64 || !range(nt, 24, size) || u32(file + nt) != 0x4550) {
        return WitPeInvalidImage;
    }
    if (u16(file + nt + 4) != 0x8664) {
        return WitPeUnsupportedImage;
    }
    characteristics = u16(file + nt + 22);
    if (!(characteristics & 2)) {
        return WitPeInvalidImage;
    }
    if (((characteristics & 0x2000) != 0) != ((profile & WIT_PE_LIBRARY) != 0) ||
        u32(file + nt + 12) ||
        u32(file + nt + 16)) {
        return WitPeUnsupportedImage;
    }
    plan->SectionCount = u16(file + nt + 6);
    if (!plan->SectionCount) {
        return WitPeInvalidImage;
    }
    if (plan->SectionCount > WIT_PE_MAX_SECTIONS) {
        return WitPeTooLarge;
    }
    if (u16(file + nt + 20) != 240) {
        return WitPeUnsupportedImage;
    }
    optional = nt + 24;
    if (!range(optional, 240, size)) {
        return WitPeInvalidImage;
    }
    if (u16(file + optional) != 0x20B || u16(file + optional + 68) != 1) {
        return WitPeUnsupportedImage;
    }
    if (u32(file + optional + 32) != 4096 || u32(file + optional + 108) != 16 || (u16(file + optional + 70) & 0x4000)) {
        return WitPeUnsupportedImage;
    }
    if (u32(file + optional + 52) || u32(file + optional + 104)) {
        return WitPeInvalidImage;
    }
    alignment = u32(file + optional + 36);
    if (alignment < 512 || alignment > 4096 || (alignment & (alignment - 1))) {
        return WitPeUnsupportedImage;
    }
    plan->PreferredBase = u64(file + optional + 24);
    plan->ImageSize = u32(file + optional + 56);
    plan->HeadersSize = u32(file + optional + 60);
    plan->EntryRva = u32(file + optional + 16);
    if (!plan->ImageSize || (plan->ImageSize & 4095)) {
        return WitPeInvalidImage;
    }
    if (plan->ImageSize > ((profile & WIT_PE_RUNTIME_FULL) ? WIT_PE_FULL_IMAGE_SIZE : WIT_PE_MAX_IMAGE_SIZE)) {
        return WitPeTooLarge;
    }
    if (!plan->PreferredBase ||
        (plan->PreferredBase & 65535) ||
        plan->PreferredBase > 0x0000800000000000ULL - plan->ImageSize) {
        return WitPeInvalidImage;
    }
    if (!plan->HeadersSize ||
        plan->HeadersSize > 4096 ||
        (plan->HeadersSize & (alignment - 1)) ||
        !range(0, plan->HeadersSize, size)) {
        return WitPeInvalidImage;
    }
    sections = optional + 240;
    if (!range(sections, plan->SectionCount * 40, plan->HeadersSize)) {
        return WitPeInvalidImage;
    }

    plan->ImportRva = u32(file + optional + 120);
    plan->ImportSize = u32(file + optional + 124);
    plan->IatRva = u32(file + optional + 208);
    plan->IatSize = u32(file + optional + 212);
    plan->ExportRva = u32(file + optional + 112);
    plan->ExportSize = u32(file + optional + 116);
    plan->UnwindRva = u32(file + optional + 112 + 3 * 8);
    plan->UnwindSize = u32(file + optional + 116 + 3 * 8);
    plan->RelocRva = u32(file + optional + 112 + 5 * 8);
    plan->RelocSize = u32(file + optional + 116 + 5 * 8);
    plan->TlsRva = u32(file + optional + 112 + 9 * 8);
    plan->TlsSize = u32(file + optional + 116 + 9 * 8);
    debug_rva = u32(file + optional + 112 + 6 * 8);
    debug_size = u32(file + optional + 116 + 6 * 8);
    for (WitU32 i = 0; i < 16; ++i) {
        const WitU32 rva = u32(file + optional + 112 + i * 8);
        const WitU32 length = u32(file + optional + 116 + i * 8);
        if (i != 3 &&
            i != 5 &&
            i != 6 &&
            i != 9 &&
            !(i == 0 && (profile & WIT_PE_LIBRARY)) &&
            !((i == 1 || i == 12) && (profile & WIT_PE_LIBRARY_IMPORTS)) &&
            (rva || length)) {
            return WitPeUnsupportedImage;
        }
        if ((!rva) != (!length)) {
            return WitPeInvalidImage;
        }
    }
    if ((characteristics & 1) && plan->RelocSize) {
        return WitPeInvalidImage;
    }
    if ((profile & WIT_PE_LIBRARY) && !(profile & WIT_PE_LIBRARY_TLS) && plan->TlsSize) {
        return WitPeUnsupportedImage;
    }
    if ((profile & WIT_PE_LIBRARY) && !plan->EntryRva) {
        entry_valid = 1;
    }
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
        if (!s->VirtualSize) {
            s->VirtualSize = s->RawSize;
        }
        if (!(flags & 0x40000000U) || (flags & 0x1C000000U) || (flags & 0xA0000000U) == 0xA0000000U) {
            return WitPeUnsupportedImage;
        }
        if (u32(header + 24) || u32(header + 28) || u32(header + 32)) {
            return WitPeUnsupportedImage;
        }
        s->Flags = WIT_PE_READ | (flags & 0x80000000U ? WIT_PE_WRITE : 0) | (flags & 0x20000000U ? WIT_PE_EXECUTE : 0);
        extent = s->VirtualSize > s->RawSize ? s->VirtualSize : s->RawSize;
        if (!extent || s->Rva < 4096 || (s->Rva & 4095) || !range(s->Rva, extent, plan->ImageSize)) {
            return WitPeInvalidImage;
        }
        s->MapSize = (extent + 4095) & ~4095U;
        if (!range(s->Rva, s->MapSize, plan->ImageSize)) {
            return WitPeInvalidImage;
        }
        if (s->RawSize) {
            if (s->RawOffset < plan->HeadersSize ||
                (s->RawOffset & (alignment - 1)) ||
                (s->RawSize & (alignment - 1)) ||
                !range(s->RawOffset, s->RawSize, size)) {
                return WitPeInvalidImage;
            }
        } else if (s->RawOffset) {
            return WitPeInvalidImage;
        }
        for (WitU32 j = 0; j < i; ++j) {
            const WitPeSection *other = &plan->Sections[j];
            if (overlap(s->Rva, s->MapSize, other->Rva, other->MapSize) ||
                (s->RawSize && other->RawSize && overlap(s->RawOffset, s->RawSize, other->RawOffset, other->RawSize))) {
                return WitPeInvalidImage;
            }
        }
        if ((s->Flags & WIT_PE_EXECUTE) &&
            plan->EntryRva >= s->Rva &&
            plan->EntryRva - s->Rva < s->VirtualSize &&
            plan->EntryRva - s->Rva < s->RawSize) {
            entry_valid = 1;
        }
        if (s->Rva + s->MapSize > maximum) {
            maximum = s->Rva + s->MapSize;
        }
    }
    if (!entry_valid || maximum != plan->ImageSize) {
        return WitPeInvalidImage;
    }
    if (debug_size) {
        WitU32 ignored;
        /* Reproducible-link debug bytes are opaque data, never interpreted or invoked. */
        if (debug_size % 28 || !wit_pe_file_range(plan, debug_rva, debug_size, &ignored)) {
            return WitPeInvalidImage;
        }
    }
    {
        const WitPeStatus status =
            profile & WIT_PE_UNWIND_RUNTIME ? runtime_unwind_info(file, plan) : unwind_info(file, plan);
        if (status != WitPeOk) {
            return status;
        }
    }
    {
        const WitPeStatus status = tls_info(file, plan);
        if (status != WitPeOk) {
            return status;
        }
    }
    {
        const WitPeStatus status = wit_pe_exports_validate(file, plan);
        if (status != WitPeOk) {
            return status;
        }
    }
    return profile & WIT_PE_LIBRARY_IMPORTS ? import_relocations(file, size, plan) : relocations(file, plan, 0);
}

WitPeStatus wit_pe_validate(const WitU8 *file, WitU32 size, WitPeImage *plan)
{
    return wit_pe_validate_profile(file, size, plan, 0);
}
