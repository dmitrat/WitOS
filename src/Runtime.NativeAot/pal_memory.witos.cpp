#include "pal.witos.h"

static bool protection(uint32_t requested, WitU64 *value)
{
    switch (requested) {
    case PAGE_NOACCESS:
        *value = WIT_MEMORY_NONE;
        return true;
    case PAGE_READONLY:
        *value = WIT_MEMORY_READ;
        return true;
    case PAGE_READWRITE:
        *value = WIT_MEMORY_READ | WIT_MEMORY_WRITE;
        return true;
    default:
        return false; // Executable, copy-on-write, guard and cache modes are not ported.
    }
}

void *PalVirtualAlloc(uintptr_t size, uint32_t protect)
{
    WitU64 mode, address = 0;
    if (!size || size > UINTPTR_MAX - 4095) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return nullptr;
    }
    if (!protection(protect, &mode)) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return nullptr;
    }
    const WitU64 rounded = (size + 4095) & ~(WitU64)4095;
    if (!wit_pal_result(wit_native_call(WIT_CALL_MEMORY_RESERVE, rounded, 65536, 0, &address))) {
        return nullptr;
    }
    if (!wit_pal_result(wit_native_call(WIT_CALL_MEMORY_COMMIT, address, rounded, mode, nullptr))) {
        if (wit_native_call(WIT_CALL_MEMORY_RELEASE, address, 0, 0, nullptr) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        return nullptr;
    }
    return (void *)(uintptr_t)address;
}

void PalVirtualFree(void *address, uintptr_t size)
{
    (void)size; // As in the Windows PAL: release the exact reservation, not a subrange.
    if (!wit_pal_result(wit_native_call(WIT_CALL_MEMORY_RELEASE, (uintptr_t)address, 0, 0, nullptr))) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
}

UInt32_BOOL PalVirtualProtect(void *address, uintptr_t size, uint32_t protect)
{
    const uintptr_t first = (uintptr_t)address;
    WitU64 mode;
    if (!first || !size || first > UINTPTR_MAX - size || first + size > UINTPTR_MAX - 4095) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return UInt32_FALSE;
    }
    if (!protection(protect, &mode)) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return UInt32_FALSE;
    }
    const WitU64 low = first & ~(WitU64)4095;
    const WitU64 end = (first + size + 4095) & ~(WitU64)4095;
    // Match the upstream byte-range contract, but remain inside one committed
    // dynamic reservation. Fixed image/stack protection is deliberately excluded.
    return wit_pal_result(wit_native_call(WIT_CALL_MEMORY_PROTECT, low, end - low, mode, nullptr));
}
