#include "pal.witos.h"
#include "native_encoding.witos.h"
#include <new>

// Preserve the upstream UTF-8 replacement policy, but bound the input and reject
// an oversized diagnostic name before any kernel mutation. No silent truncation.
bool PalSetCurrentThreadNameW(const WCHAR *name)
{
    if (!name) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }
    WitU32 length = 0;
    while (length < WIT_THREAD_NAME_CAPACITY && name[length]) {
        ++length;
    }
    if (length == WIT_THREAD_NAME_CAPACITY) {
        SetLastError(ERROR_BUFFER_OVERFLOW);
        return false;
    }
    return wit_pal_result(wit_native_call(WIT_CALL_THREAD_NAME_SET, (WitU64)name, length, 0, nullptr)) != 0;
}

bool PalSetCurrentThreadName(const char *name)
{
    if (!name) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }
    constexpr WitU32 maxBytes = 3 * (WIT_THREAD_NAME_CAPACITY - 1);
    WitU32 length = 0;
    while (length <= maxBytes && name[length]) {
        ++length;
    }
    if (length > maxBytes) {
        SetLastError(ERROR_BUFFER_OVERFLOW);
        return false;
    }
    if (!length) {
        return PalSetCurrentThreadNameW(L"");
    }
    const int units = wit_native_multibyte_to_wide(CP_UTF8, 0, name, (int)length, nullptr, 0);
    if (!units) {
        return false;
    }
    if (units >= WIT_THREAD_NAME_CAPACITY) {
        SetLastError(ERROR_BUFFER_OVERFLOW);
        return false;
    }
    auto wide = new (std::nothrow) wchar_t[(size_t)units + 1];
    if (!wide) {
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return false;
    }
    const int converted = wit_native_multibyte_to_wide(CP_UTF8, 0, name, (int)length, wide, units);
    bool success = false;
    if (converted == units) {
        wide[units] = 0;
        success = PalSetCurrentThreadNameW(wide);
    }
    const DWORD error = GetLastError();
    delete[] wide;
    SetLastError(error);
    return success;
}
