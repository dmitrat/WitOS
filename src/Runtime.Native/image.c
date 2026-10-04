#include "bootstrap.h"

/* One immutable kernel handoff per component. Publish before any user TLS
 * constructor or worker. This is independent of compiler TLS and the PE bytes. */
static const WitUserImageInfo *process_image;
static volatile WitU32 image_state;
static WitU64 process_console;

int wit_native_image_range(
    const WitUserImageInfo *image, WitU64 address, WitU64 size, WitU32 required, WitU32 forbidden, int initialized)
{
    WitU64 rva;
    if (!image ||
        image->RangeCount > WIT_IMAGE_INFO_MAX_RANGES ||
        address < image->Base ||
        !size ||
        address - image->Base >= image->ImageSize ||
        size > image->ImageSize - (address - image->Base)) {
        return 0;
    }
    rva = address - image->Base;
    for (WitU32 i = 0; i < image->RangeCount; ++i) {
        const WitUserImageRange *range = &image->Ranges[i];
        const WitU32 extent = initialized ? range->InitializedSize : range->Size;
        if ((range->Flags & required) == required &&
            !(range->Flags & forbidden) &&
            rva >= range->Rva &&
            rva - range->Rva < extent &&
            size <= extent - (rva - range->Rva)) {
            return 1;
        }
    }
    return 0;
}

int wit_native_image_valid(const WitUserImageInfo *image)
{
    if (!image ||
        image->Version != WIT_IMAGE_INFO_VERSION ||
        image->Size != sizeof(*image) ||
        image->ResourceReserved ||
        !wit_image_resource_valid(image->ResourceName, image->ResourceNameLength) ||
        image->Reserved ||
        !image->ImageSize ||
        !image->RangeCount ||
        image->RangeCount > WIT_IMAGE_INFO_MAX_RANGES ||
        image->Base > ~0ULL - image->ImageSize ||
        !image->HeadersSize ||
        image->HeadersSize > image->ImageSize) {
        return 0;
    }
    for (WitU32 i = 0; i < image->RangeCount; ++i) {
        const WitUserImageRange *range = &image->Ranges[i];
        if (!range->Size ||
            range->Rva < image->HeadersSize ||
            range->Rva >= image->ImageSize ||
            range->Size > image->ImageSize - range->Rva ||
            range->InitializedSize > range->Size ||
            !(range->Flags & WIT_IMAGE_INFO_READ) ||
            (range->Flags & ~7U) ||
            (range->Flags & 6U) == 6U) {
            return 0;
        }
        for (WitU32 j = 0; j < i; ++j) {
            const WitUserImageRange *other = &image->Ranges[j];
            if ((WitU64)range->Rva < (WitU64)other->Rva + other->Size &&
                (WitU64)other->Rva < (WitU64)range->Rva + range->Size) {
                return 0;
            }
        }
    }
    if ((!image->UnwindRva) != (!image->UnwindSize) || image->UnwindSize % 12) {
        return 0;
    }
    if (image->UnwindSize &&
        !wit_native_image_range(image, image->Base + image->UnwindRva, image->UnwindSize, WIT_IMAGE_INFO_READ,
            WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE, 1)) {
        return 0;
    }
    return wit_native_image_range(image, image->Entry, 1, WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE, 1);
}

WitU64 wit_native_image_from_address(const WitUserImageInfo *image, WitU64 address)
{
    if (!image) {
        return 0;
    }
    if (address >= image->Base && address - image->Base < image->HeadersSize) {
        return image->Base;
    }
    return wit_native_image_range(image, address, 1, WIT_IMAGE_INFO_READ, 0, 0) ? image->Base : 0;
}

void wit_native_process_image_initialize(const WitUserStartup *startup)
{
    if (!wit_native_claim_startup(&image_state) ||
        !startup ||
        startup->Version != WIT_ABI_VERSION ||
        startup->Size != sizeof(*startup) ||
        !startup->ImageInfo ||
        !wit_native_image_valid((const WitUserImageInfo *)startup->ImageInfo)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    process_image = (const WitUserImageInfo *)startup->ImageInfo;
    process_console = startup->ConsoleHandle;
    image_state = 2;
}

const WitUserImageInfo *wit_native_process_image(void)
{
    return image_state == 2 ? process_image : 0;
}

WitU64 wit_native_process_console(void)
{
    return image_state == 2 ? process_console : 0;
}
