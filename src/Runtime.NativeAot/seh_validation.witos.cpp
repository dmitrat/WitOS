#include "seh_validation.witos.h"

namespace {
WitU32 read32(const WitU8 *p)
{
    return p[0] | ((WitU32)p[1] << 8) | ((WitU32)p[2] << 16) | ((WitU32)p[3] << 24);
}

bool range(const WitUserImageInfo *image, WitU32 rva, WitU32 size, bool code)
{
    if (!size || rva >= image->ImageSize || size > image->ImageSize - rva) {
        return false;
    }
    for (WitU32 i = 0; i < image->RangeCount; ++i) {
        const auto &r = image->Ranges[i];
        if ((r.Flags & WIT_IMAGE_INFO_READ) &&
            !(r.Flags & WIT_IMAGE_INFO_WRITE) &&
            ((r.Flags & WIT_IMAGE_INFO_EXECUTE) != 0) == code &&
            rva >= r.Rva &&
            rva - r.Rva < r.InitializedSize &&
            size <= r.InitializedSize - (rva - r.Rva)) {
            return true;
        }
    }
    return false;
}
}

bool wit_seh_scope(const WitSehTable *table, WitU32 index, WitSehScope *output)
{
    if (!table || !output || !table->Entries || table->Count > WIT_SEH_SCOPE_CAPACITY || index >= table->Count) {
        return false;
    }
    const auto p = table->Entries + index * 16;
    const WitSehScope result = {read32(p), read32(p + 4), read32(p + 8), read32(p + 12)};
    *output = result;
    return true;
}

WitSehValidation wit_seh_validate(
    const WitUserImageInfo *image, WitU32 functionRva, WitU64 address, WitSehTable *output)
{
    WitUnwindRecord unwind;
    if (!image ||
        !output ||
        !image->UnwindSize ||
        image->UnwindSize % 12 ||
        functionRva < image->UnwindRva ||
        functionRva - image->UnwindRva >= image->UnwindSize ||
        (functionRva - image->UnwindRva) % 12) {
        return WitSehBadFormat;
    }
    if (wit_unwind_validate_function(image, functionRva, &unwind) != WitUnwindValid || !unwind.HandlerRva) {
        return WitSehBadFormat;
    }
    if (address < image->Base ||
        address - image->Base != unwind.HandlerDataRva ||
        (address & 3) ||
        !range(image, unwind.HandlerDataRva, 4, false)) {
        return WitSehBadRange;
    }
    const auto bytes = (const WitU8 *)address;
    const WitU32 count = read32(bytes);
    if (count > WIT_SEH_SCOPE_CAPACITY) {
        return WitSehQuota;
    }
    if (!range(image, unwind.HandlerDataRva, 4 + count * 16, false)) {
        return WitSehBadRange;
    }
    const WitSehTable table = {bytes + 4, count};
    for (WitU32 i = 0; i < count; ++i) {
        WitSehScope scope;
        if (!wit_seh_scope(&table, i, &scope)) {
            return WitSehBadFormat;
        }
        if (scope.Begin >= scope.End || !range(image, scope.Begin, scope.End - scope.Begin, true)) {
            return WitSehBadRange;
        }
        if (scope.Target) {
            if (!range(image, scope.Target, 1, true) || (scope.Handler != 1 && !range(image, scope.Handler, 1, true))) {
                return WitSehBadRange;
            }
        } else if (!range(image, scope.Handler, 1, true)) {
            return WitSehBadRange;
        }
    }
    *output = table;
    return WitSehValid;
}

WitSehValidation wit_seh_gs_validate(
    const WitUserImageInfo *image, WitU32 functionRva, WitU64 address, WitSehGsData *output)
{
    if (!output) {
        return WitSehBadFormat;
    }
    WitSehTable table;
    const auto status = wit_seh_validate(image, functionRva, address, &table);
    if (status != WitSehValid) {
        return status;
    }
    const WitU64 rva = address - image->Base + 4 + WitU64(table.Count) * 16;
    if (rva >= image->ImageSize || !range(image, (WitU32)rva, 4, false)) {
        return WitSehBadRange;
    }
    const auto data = (const WitU8 *)(image->Base + rva);
    const WitU32 encoded = read32(data);
    if (encoded & 4) {
        if (!range(image, (WitU32)rva, 12, false)) {
            return WitSehBadRange;
        }
        const WitU32 alignment = read32(data + 8);
        if (!alignment || (alignment & (alignment - 1))) {
            return WitSehBadFormat;
        }
    }
    *output = {image->Base + rva, encoded & 3U};
    return WitSehValid;
}
