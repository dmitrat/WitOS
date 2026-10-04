#include "unwind_validation.witos.h"

namespace {
bool descriptor(const WitUserImageInfo *image)
{
    if (!image ||
        !image->Base ||
        image->Version != WIT_IMAGE_INFO_VERSION ||
        image->Size != sizeof(*image) ||
        !image->ImageSize ||
        !image->RangeCount ||
        image->RangeCount > WIT_IMAGE_INFO_MAX_RANGES ||
        image->Base > ~0ULL - image->ImageSize ||
        image->UnwindRva >= image->ImageSize ||
        image->UnwindSize > image->ImageSize - image->UnwindRva) {
        return false;
    }
    for (WitU32 i = 0; i < image->RangeCount; ++i) {
        const auto &r = image->Ranges[i];
        if (!r.Size ||
            r.Rva >= image->ImageSize ||
            r.Size > image->ImageSize - r.Rva ||
            r.InitializedSize > r.Size ||
            !(r.Flags & WIT_IMAGE_INFO_READ) ||
            (r.Flags & ~7U) ||
            (r.Flags & 6U) == 6U) {
            return false;
        }
        for (WitU32 j = 0; j < i; ++j) {
            if (r.Rva < image->Ranges[j].Rva + image->Ranges[j].Size && image->Ranges[j].Rva < r.Rva + r.Size) {
                return false;
            }
        }
    }
    return true;
}

bool range(const WitUserImageInfo *image, WitU32 rva, WitU32 size, WitU32 required, WitU32 forbidden)
{
    if (!image ||
        image->RangeCount > WIT_IMAGE_INFO_MAX_RANGES ||
        !size ||
        rva >= image->ImageSize ||
        size > image->ImageSize - rva ||
        image->Base > ~0ULL - image->ImageSize) {
        return false;
    }
    for (WitU32 i = 0; i < image->RangeCount; ++i) {
        const auto &part = image->Ranges[i];
        if (part.Rva >= image->ImageSize ||
            part.InitializedSize > part.Size ||
            part.Size > image->ImageSize - part.Rva) {
            return false;
        }
        if ((part.Flags & required) == required &&
            !(part.Flags & forbidden) &&
            rva >= part.Rva &&
            rva - part.Rva < part.InitializedSize &&
            size <= part.InitializedSize - (rva - part.Rva)) {
            return true;
        }
    }
    return false;
}

bool data(const WitUserImageInfo *image, WitU32 rva, WitU32 size)
{
    return range(image, rva, size, WIT_IMAGE_INFO_READ, WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE);
}

bool code(const WitUserImageInfo *image, WitU32 rva, WitU32 size)
{
    return range(image, rva, size, WIT_IMAGE_INFO_READ | WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE);
}

const WitU8 *at(const WitUserImageInfo *image, WitU32 rva)
{
    return (const WitU8 *)(image->Base + rva);
}
}

namespace {
const WitU8 *metadata_read(void *context, WitU32 rva, WitU32 size, int executable)
{
    const auto image = (const WitUserImageInfo *)context;
    return (executable ? code(image, rva, size) : data(image, rva, size)) ? at(image, rva) : nullptr;
}

WitUnwindMetadataView metadata_view(const WitUserImageInfo *image)
{
    return {(void *)image, metadata_read, nullptr, image->ImageSize, image->UnwindRva, image->UnwindSize, 4096};
}
}

WitUnwindValidation wit_unwind_validate_function(const WitUserImageInfo *image, WitU32 entry, WitUnwindRecord *result)
{
    if (!descriptor(image)) {
        return WitUnwindBadFormat;
    }
    const auto view = metadata_view(image);
    return wit_unwind_metadata_function(&view, entry, result);
}

WitUnwindValidation wit_unwind_validate_image(const WitUserImageInfo *image)
{
    if (!descriptor(image)) {
        return WitUnwindBadFormat;
    }
    const auto view = metadata_view(image);
    return wit_unwind_metadata_image(&view);
}

bool wit_unwind_read_stack(const WitUnwindStackRange *stack, WitU64 address, void *output, WitU32 size)
{
    if (!stack ||
        !output ||
        (size != 8 && size != 16) ||
        stack->Low >= stack->High ||
        address < stack->Low ||
        address >= stack->High ||
        size > stack->High - address) {
        return false;
    }
    const auto input = (const WitU8 *)address;
    auto destination = (WitU8 *)output;
    for (WitU32 i = 0; i < size; ++i) {
        destination[i] = input[i];
    }
    return true;
}

bool wit_unwind_read_code(const WitUserImageInfo *image, WitU64 address, void *output, WitU32 size)
{
    if (!descriptor(image) ||
        !output ||
        !size ||
        size > 32 ||
        address < image->Base ||
        address - image->Base >= image->ImageSize ||
        !code(image, (WitU32)(address - image->Base), size)) {
        return false;
    }
    const auto input = (const WitU8 *)address;
    auto destination = (WitU8 *)output;
    for (WitU32 i = 0; i < size; ++i) {
        destination[i] = input[i];
    }
    return true;
}

const WitU8 *wit_unwind_info_address(const WitUserImageInfo *image, WitU64 address)
{
    if (!descriptor(image) || address < image->Base || address - image->Base >= image->ImageSize) {
        return nullptr;
    }
    const WitU32 rva = (WitU32)(address - image->Base);
    if ((rva & 3) || !data(image, rva, 4)) {
        return nullptr;
    }
    const auto header = at(image, rva);
    const WitU32 flags = header[0] >> 3;
    const WitU32 size = ((4 + (WitU32)header[2] * 2 + 3) & ~3U) + ((flags & 4) ? 12 : (flags & 3) ? 4 : 0);
    return data(image, rva, size) ? header : nullptr;
}
