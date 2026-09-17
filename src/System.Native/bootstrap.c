#include "bootstrap.h"

int wit_native_image_range(const WitUserImageInfo *image, WitU64 address, WitU64 size,
    WitU32 required, WitU32 forbidden, int initialized)
{
    WitU64 rva;
    if (!image || image->RangeCount > WIT_IMAGE_INFO_MAX_RANGES || address < image->Base || !size || address - image->Base >= image->ImageSize ||
        size > image->ImageSize - (address - image->Base)) return 0;
    rva = address - image->Base;
    for (WitU32 i = 0; i < image->RangeCount; ++i) {
        const WitUserImageRange *range = &image->Ranges[i];
        const WitU32 extent = initialized ? range->InitializedSize : range->Size;
        if ((range->Flags & required) == required && !(range->Flags & forbidden) &&
            rva >= range->Rva && rva - range->Rva < extent && size <= extent - (rva - range->Rva))
            return 1;
    }
    return 0;
}

static int image_valid(const WitUserImageInfo *image)
{
    if (!image || image->Version != WIT_IMAGE_INFO_VERSION || image->Size != sizeof(*image) ||
        image->Reserved || !image->ImageSize || !image->RangeCount ||
        image->RangeCount > WIT_IMAGE_INFO_MAX_RANGES ||
        image->Base > ~0ULL - image->ImageSize || !image->HeadersSize ||
        image->HeadersSize > image->ImageSize) return 0;
    for (WitU32 i = 0; i < image->RangeCount; ++i) {
        const WitUserImageRange *range = &image->Ranges[i];
        if (!range->Size || range->Rva < image->HeadersSize || range->Rva >= image->ImageSize ||
            range->Size > image->ImageSize - range->Rva || range->InitializedSize > range->Size ||
            !(range->Flags & WIT_IMAGE_INFO_READ) || (range->Flags & ~7U) ||
            (range->Flags & 6U) == 6U) return 0;
        for (WitU32 j = 0; j < i; ++j) {
            const WitUserImageRange *other = &image->Ranges[j];
            if ((WitU64)range->Rva < (WitU64)other->Rva + other->Size &&
                (WitU64)other->Rva < (WitU64)range->Rva + range->Size) return 0;
        }
    }
    if ((!image->UnwindRva) != (!image->UnwindSize) || image->UnwindSize % 12) return 0;
    if (image->UnwindSize && !wit_native_image_range(image, image->Base + image->UnwindRva,
        image->UnwindSize, WIT_IMAGE_INFO_READ, WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE, 1)) return 0;
    return wit_native_image_range(image, image->Entry, 1, WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE, 1);
}

WitU64 wit_native_module_from_address(const WitNativeModule *module, WitU64 address)
{
    const WitUserImageInfo *image = module->Image;
    if (!image) return 0;
    if (address >= image->Base && address - image->Base < image->HeadersSize) return image->Base;
    return wit_native_image_range(image, address, 1, WIT_IMAGE_INFO_READ, 0, 0) ? image->Base : 0;
}

WitU64 wit_native_bootstrap(WitNativeModule *module, const WitUserStartup *startup,
    const WitNativeInitializer *initializers, WitU32 count, WitNativeMain main, WitU64 *exit_code)
{
    WitU32 completed = 0;
    WitU64 status = WIT_NATIVE_OK;
    *exit_code = 0;
    if (!wit_native_claim_startup(&module->State)) return WIT_NATIVE_ALREADY_STARTED;
    module->Initialized = 0;
    module->FailureCode = 0;
    module->Startup = 0;
    module->Image = 0;
    /* The startup pointer and descriptor come from the kernel's immutable handoff.
     * Callback tables must themselves be immutable initialized image data. */
    if (!startup || startup->Version != WIT_ABI_VERSION || startup->Size != sizeof(*startup) ||
        !startup->ImageInfo || !image_valid((const WitUserImageInfo *)startup->ImageInfo) ||
        count > WIT_NATIVE_MAX_INITIALIZERS) goto invalid;
    module->Startup = startup;
    module->Image = (const WitUserImageInfo *)startup->ImageInfo;
    if (!wit_native_image_range(module->Image, (WitU64)main, 1, WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE, 1) ||
        (count && !wit_native_image_range(module->Image, (WitU64)initializers, count * sizeof(*initializers),
            WIT_IMAGE_INFO_READ, WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE, 1))) goto invalid;
    for (WitU32 i = 0; i < count; ++i) {
        if (!wit_native_image_range(module->Image, (WitU64)initializers[i].Initialize, 1,
                WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE, 1) ||
            (initializers[i].Cleanup && !wit_native_image_range(module->Image, (WitU64)initializers[i].Cleanup, 1,
                WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE, 1))) goto invalid;
    }
    for (; completed < count; ++completed) {
        const WitU64 error = initializers[completed].Initialize(module);
        if (error) {
            module->FailureCode = error;
            status = WIT_NATIVE_INITIALIZER_FAILED;
            break;
        }
        module->Initialized = completed + 1;
    }
    if (status == WIT_NATIVE_OK) {
        module->State = WIT_NATIVE_READY;
        *exit_code = main(module);
    }
    module->State = WIT_NATIVE_FINALIZING;
    while (completed) {
        const WitNativeInitializer *entry = &initializers[--completed];
        if (entry->Cleanup) entry->Cleanup(module);
        module->Initialized = completed;
    }
    module->State = status == WIT_NATIVE_OK ? WIT_NATIVE_STOPPED : WIT_NATIVE_FAILED;
    return status;
invalid:
    module->State = WIT_NATIVE_FAILED;
    return WIT_NATIVE_INVALID_BOOTSTRAP;
}
