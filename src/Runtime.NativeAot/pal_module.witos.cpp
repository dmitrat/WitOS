#include "pal.witos.h"

HANDLE PalGetModuleHandleFromPointer(void *pointer)
{
    const auto image = wit_native_process_image();
    if (!image) {
        SetLastError(ERROR_NOT_READY);
        return nullptr;
    }
    const auto base = wit_native_image_from_address(image, (uintptr_t)pointer);
    if (!base) {
        SetLastError(ERROR_INVALID_ADDRESS);
        return nullptr;
    }
    return (HANDLE)(uintptr_t)base;
}

void PalGetModuleBounds(HANDLE module, uint8_t **low, uint8_t **high)
{
    const auto image = wit_native_process_image();
    DWORD error = ERROR_SUCCESS;
    if (!low || !high || low == high) {
        error = ERROR_INVALID_PARAMETER;
    } else if (!image) {
        error = ERROR_NOT_READY;
    } else if ((uintptr_t)module != image->Base) {
        error = ERROR_INVALID_HANDLE;
    }
    // This upstream interface cannot return failure. Never fabricate bounds or
    // partially overwrite outputs for an unsupported handle or initialization state.
    if (error) {
        SetLastError(error);
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    *low = (uint8_t *)(uintptr_t)image->Base;
    *high = (uint8_t *)(uintptr_t)(image->Base + image->ImageSize - 1); // Inclusive, as in pinned PalCommon.cpp.
}
