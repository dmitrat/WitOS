#include "bootstrap.h"

WitU64 wit_native_module_from_address(const WitNativeModule *module, WitU64 address)
{
    return module ? wit_native_image_from_address(module->Image, address) : 0;
}

WitU64 wit_native_bootstrap(WitNativeModule *module, const WitUserStartup *startup,
    const WitNativeInitializer *initializers, WitU32 count, WitNativeMain main, WitU64 *exit_code)
{
    WitU32 completed = 0;
    WitU64 status = WIT_NATIVE_OK;
    *exit_code = 0;
    if (!wit_native_claim_startup(&module->State)) {
        return WIT_NATIVE_ALREADY_STARTED;
    }
    module->Initialized = 0;
    module->FailureCode = 0;
    module->Startup = 0;
    module->Image = 0;
    /* The startup pointer and descriptor come from the kernel's immutable handoff.
     * Callback tables must themselves be immutable initialized image data. */
    if (!startup ||
        startup->Version != WIT_ABI_VERSION ||
        startup->Size != sizeof(*startup) ||
        !startup->ImageInfo ||
        !wit_native_image_valid((const WitUserImageInfo *)startup->ImageInfo) ||
        count > WIT_NATIVE_MAX_INITIALIZERS) {
        goto invalid;
    }
    module->Startup = startup;
    module->Image = (const WitUserImageInfo *)startup->ImageInfo;
    if (!wit_native_image_range(module->Image, (WitU64)main, 1, WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE, 1) ||
        (count &&
            !wit_native_image_range(module->Image, (WitU64)initializers, count * sizeof(*initializers),
                WIT_IMAGE_INFO_READ, WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE, 1))) {
        goto invalid;
    }
    for (WitU32 i = 0; i < count; ++i) {
        if (!wit_native_image_range(module->Image, (WitU64)initializers[i].Initialize, 1, WIT_IMAGE_INFO_EXECUTE,
                WIT_IMAGE_INFO_WRITE, 1) ||
            (initializers[i].Cleanup &&
                !wit_native_image_range(module->Image, (WitU64)initializers[i].Cleanup, 1, WIT_IMAGE_INFO_EXECUTE,
                    WIT_IMAGE_INFO_WRITE, 1))) {
            goto invalid;
        }
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
        if (entry->Cleanup) {
            entry->Cleanup(module);
        }
        module->Initialized = completed;
    }
    module->State = status == WIT_NATIVE_OK ? WIT_NATIVE_STOPPED : WIT_NATIVE_FAILED;
    return status;
invalid:
    module->State = WIT_NATIVE_FAILED;
    return WIT_NATIVE_INVALID_BOOTSTRAP;
}
