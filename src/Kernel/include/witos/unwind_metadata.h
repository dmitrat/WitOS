#ifndef WITOS_UNWIND_METADATA_H
#define WITOS_UNWIND_METADATA_H
#include "types.h"

typedef enum WitUnwindValidation {
    WitUnwindValid,
    WitUnwindBadRange,
    WitUnwindBadFormat,
    WitUnwindUnsupported,
    WitUnwindCycle,
    WitUnwindQuota
} WitUnwindValidation;

typedef struct WitUnwindRecord {
    WitU32 InfoRva, InfoBytes, HandlerRva, HandlerDataRva, Depth, Version;
} WitUnwindRecord;

/* Immutable file/mapped view. Read must return a contiguous initialized readonly
 * span, executable iff code!=0. Visit records provisional metadata ranges;
 * its caller must discard all records when validation fails. No allocation. */
typedef struct WitUnwindMetadataView {
    void *Context;
    const WitU8 *(*Read)(void *, WitU32, WitU32, int);
    int (*Visit)(void *, WitU32, WitU32);
    WitU32 ImageSize, UnwindRva, UnwindSize, MaxEntries;
} WitUnwindMetadataView;

static inline int wit_unwind_meta_view_valid(const WitUnwindMetadataView *v)
{
    return v &&
        v->Read &&
        v->ImageSize &&
        v->MaxEntries &&
        v->UnwindRva < v->ImageSize &&
        v->UnwindSize <= v->ImageSize - v->UnwindRva;
}

static inline int wit_unwind_meta_data(const WitUnwindMetadataView *v, WitU32 rva, WitU32 size)
{
    return size && rva < v->ImageSize && size <= v->ImageSize - rva && v->Read(v->Context, rva, size, 0) != 0;
}

static inline int wit_unwind_meta_code(const WitUnwindMetadataView *v, WitU32 rva, WitU32 size)
{
    return size && rva < v->ImageSize && size <= v->ImageSize - rva && v->Read(v->Context, rva, size, 1) != 0;
}

static inline const WitU8 *wit_unwind_meta_at(const WitUnwindMetadataView *v, WitU32 rva)
{
    return v->Read(v->Context, rva, 1, 0);
}

static inline WitU16 wit_unwind_meta_u16(const WitU8 *p)
{
    return (WitU16)(p[0] | ((WitU16)p[1] << 8));
}

static inline WitU32 wit_unwind_meta_u32(const WitU8 *p)
{
    return p[0] | ((WitU32)p[1] << 8) | ((WitU32)p[2] << 16) | ((WitU32)p[3] << 24);
}

static inline int wit_unwind_meta_nonvolatile(WitU32 reg)
{
    return reg == 3 || reg == 5 || reg == 6 || reg == 7 || (reg >= 12 && reg <= 15);
}

/* first points to a stable, already range-validated 12-byte function entry.
 * Dynamic JIT tables can live outside the code heap; chain entries remain RVAs. */
static inline WitUnwindValidation wit_unwind_metadata_record(
    const WitUnwindMetadataView *image, const WitU8 *first, WitUnwindRecord *result)
{
    if (!wit_unwind_meta_view_valid(image) || !first || !result) {
        return WitUnwindBadFormat;
    }
    WitU32 entryRva = 0;
    WitUnwindRecord record = {0};
    WitU32 seen[33] = {0}, seenCount = 0, frameSignature = ~0U;
    for (;;) {
        if (seenCount && !wit_unwind_meta_data(image, entryRva, 12)) {
            return WitUnwindBadRange;
        }
        const WitU8 *entry = seenCount ? wit_unwind_meta_at(image, entryRva) : first;
        const WitU32 begin = wit_unwind_meta_u32(entry), end = wit_unwind_meta_u32(entry + 4),
                     info = wit_unwind_meta_u32(entry + 8);
        if (begin >= end ||
            !wit_unwind_meta_code(image, begin, end - begin) ||
            (info & 3) ||
            !wit_unwind_meta_data(image, info, 4)) {
            return WitUnwindBadRange;
        }
        for (WitU32 i = 0; i < seenCount; ++i) {
            if (seen[i] == info) {
                return WitUnwindCycle;
            }
        }
        if (seenCount == 33) {
            return WitUnwindQuota;
        }
        seen[seenCount++] = info;
        const WitU8 *bytes = wit_unwind_meta_at(image, info);
        const WitU32 version = bytes[0] & 7, flags = bytes[0] >> 3, prolog = bytes[1], count = bytes[2],
                     frame = bytes[3] & 15;
        if (version != 1 && version != 2) {
            return WitUnwindUnsupported;
        }
        if ((flags & ~7U) ||
            ((flags & 4) && (flags & 3)) ||
            prolog > end - begin ||
            (!frame && bytes[3]) ||
            (frame && !wit_unwind_meta_nonvolatile(frame))) {
            return WitUnwindBadFormat;
        }
        if (frameSignature == ~0U) {
            frameSignature = bytes[3];
        } else if (frameSignature != bytes[3]) {
            return WitUnwindBadFormat;
        }
        const WitU32 aligned = (4 + count * 2 + 3) & ~3U;
        const WitU32 extent = aligned + ((flags & 4) ? 12 : (flags & 3) ? 4 : 0);
        if (!wit_unwind_meta_data(image, info, extent)) {
            return WitUnwindBadRange;
        }
        if (info < image->UnwindRva + image->UnwindSize && image->UnwindRva < info + extent) {
            return WitUnwindBadFormat;
        }
        WitU32 slot = 0;
        if (version == 2 && count && (bytes[5] & 15) == 6) {
            const WitU32 epilogSize = bytes[4];
            if (!epilogSize || (bytes[5] >> 4) > 1 || epilogSize > end - begin - prolog) {
                return WitUnwindBadFormat;
            }
            slot = 1;
            int hasOffset = (bytes[5] >> 4) != 0, padded = 0;
            while (slot < count && (bytes[4 + slot * 2 + 1] & 15) == 6) {
                const WitU8 *ep = bytes + 4 + slot * 2;
                const WitU32 offset = ep[0] | ((WitU32)(ep[1] >> 4) << 8);
                if (!offset) {
                    if (padded) {
                        return WitUnwindBadFormat;
                    }
                    padded = 1;
                } else {
                    if (padded || offset < epilogSize || offset > end - begin - prolog) {
                        return WitUnwindBadFormat;
                    }
                    hasOffset = 1;
                }
                ++slot;
            }
            if (!hasOffset || (slot & 1)) {
                return WitUnwindBadFormat;
            }
        }
        WitU32 previous = prolog, frameSets = 0;
        int machine = 0;
        while (slot < count) {
            const WitU8 *op = bytes + 4 + slot * 2;
            const WitU32 operation = op[1] & 15, argument = op[1] >> 4;
            if (op[0] > previous) {
                return WitUnwindBadFormat;
            }
            previous = op[0];
            WitU32 extra = 0;
            switch (operation) {
            case 0:
                break; // Runtime helper frames also describe saved volatile GPRs.
            case 1:
                if (argument > 1) {
                    return WitUnwindBadFormat;
                }
                extra = argument ? 2 : 1;
                break;
            case 2:
                break;
            case 3:
                if (!frame || ++frameSets > 1) {
                    return WitUnwindBadFormat;
                }
                break;
            case 4:
            case 5:
                extra = operation == 4 ? 1 : 2;
                break; // Four-bit register index bounds the 16-register context.
            case 8:
            case 9:
                extra = operation == 8 ? 1 : 2;
                break; // Real runtime stubs save XMM0-5 as well as XMM6-15.
            case 10:
                if (argument > 1 || machine || slot + 1 != count || (flags & 4)) {
                    return WitUnwindBadFormat;
                }
                machine = 1;
                break;
            default:
                return WitUnwindUnsupported;
            }
            if (extra >= count - slot) {
                return WitUnwindBadFormat;
            }
            if (operation == 1 &&
                ((!argument && !wit_unwind_meta_u16(op + 2)) ||
                    (argument && (!wit_unwind_meta_u32(op + 2) || (wit_unwind_meta_u32(op + 2) & 7))))) {
                return WitUnwindBadFormat;
            }
            if ((operation == 5 && (wit_unwind_meta_u32(op + 2) & 7)) ||
                (operation == 9 && (wit_unwind_meta_u32(op + 2) & 15))) {
                return WitUnwindBadFormat;
            }
            slot += extra + 1;
        }
        if (seenCount == 1) {
            record.InfoRva = info;
            record.InfoBytes = extent;
            record.Version = version;
        }
        if (flags & 3) {
            const WitU32 handler = wit_unwind_meta_u32(bytes + aligned);
            if (!wit_unwind_meta_code(image, handler, 1)) {
                return WitUnwindBadRange;
            }
            record.HandlerRva = handler;
            record.HandlerDataRva = info + extent;
        }
        if (image->Visit && !image->Visit(image->Context, info, extent)) {
            return WitUnwindQuota;
        }
        if (!(flags & 4)) {
            if (frame && !frameSets) {
                return WitUnwindBadFormat;
            }
            break;
        }
        entryRva = info + aligned;
    }
    record.Depth = seenCount;
    *result = record;
    return WitUnwindValid;
}

static inline WitUnwindValidation wit_unwind_metadata_function(
    const WitUnwindMetadataView *image, WitU32 entryRva, WitUnwindRecord *result)
{
    if (!wit_unwind_meta_view_valid(image) || !result) {
        return WitUnwindBadFormat;
    }
    if (!wit_unwind_meta_data(image, entryRva, 12)) {
        return WitUnwindBadRange;
    }
    return wit_unwind_metadata_record(image, wit_unwind_meta_at(image, entryRva), result);
}

static inline WitUnwindValidation wit_unwind_metadata_image(const WitUnwindMetadataView *image)
{
    if (!wit_unwind_meta_view_valid(image) ||
        !image->UnwindSize ||
        image->UnwindSize % 12 ||
        (image->UnwindRva & 3) ||
        image->UnwindRva >= image->ImageSize ||
        image->UnwindSize > image->ImageSize - image->UnwindRva) {
        return WitUnwindBadFormat;
    }
    if (image->UnwindSize / 12 > image->MaxEntries) {
        return WitUnwindQuota;
    }
    if (!wit_unwind_meta_data(image, image->UnwindRva, image->UnwindSize)) {
        return WitUnwindBadRange;
    }
    WitU32 previous = 0;
    for (WitU32 offset = 0; offset < image->UnwindSize; offset += 12) {
        const WitU8 *entry = wit_unwind_meta_at(image, image->UnwindRva + offset);
        if (wit_unwind_meta_u32(entry) < previous) {
            return WitUnwindBadFormat;
        }
        previous = wit_unwind_meta_u32(entry + 4);
        WitUnwindRecord record;
        const WitUnwindValidation status = wit_unwind_metadata_function(image, image->UnwindRva + offset, &record);
        if (status != WitUnwindValid) {
            return status;
        }
    }
    return WitUnwindValid;
}

#endif
