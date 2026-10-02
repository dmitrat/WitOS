// Actual hash-verified corehost PAL signatures; reference-counted WitOS modules.
#pragma warning(push)
#pragma warning(disable : 4100)
#include "pal.h"
#pragma warning(pop)
extern "C" {
#include "library.h"
#include "witos/pe.h"
}
#include "host_path_codec.witos.h"

bool pal::load_library(const string_t *path, dll_t *library)
{
    if (!path || !library) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }
    const DWORD previous = GetLastError();
    // A failed load publishes no module, as in the upstream Unix PAL.
    *library = nullptr;
    char input[WIT_PATH_INPUT_MAX];
    WitU32 bytes = 0;
    if (!WitHostPath::utf8(*path, input, bytes)) {
        return false;
    }
    WitU64 handle = 0;
    const auto status = wit_native_library_load(input, bytes, &handle);
    if (status != WIT_STATUS_OK) {
        SetLastError(WitHostPath::error(status));
        return false;
    }
    *library = reinterpret_cast<dll_t>(handle);
    SetLastError(previous);
    return true;
}

pal::proc_t pal::get_symbol(dll_t library, const char *name)
{
    if (!name) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return nullptr;
    }
    WitU32 bytes = 0;
    while (bytes <= WIT_PE_EXPORT_NAME_MAX && name[bytes]) {
        ++bytes;
    }
    if (!bytes || bytes > WIT_PE_EXPORT_NAME_MAX) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return nullptr;
    }
    const DWORD previous = GetLastError();
    WitU64 address = 0;
    const auto status = wit_native_library_symbol(reinterpret_cast<WitU64>(library), name, bytes, 0, &address);
    if (status != WIT_STATUS_OK) {
        SetLastError(status == WIT_STATUS_NOT_FOUND ? ERROR_PROC_NOT_FOUND : WitHostPath::error(status));
        return nullptr;
    }
    SetLastError(previous);
    return reinterpret_cast<proc_t>(address);
}

void pal::unload_library(dll_t library)
{
    const DWORD previous = GetLastError();
    const auto status = wit_native_library_unload(reinterpret_cast<WitU64>(library));
    // The void PAL contract cannot report failed ownership release to its caller.
    if (status != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    SetLastError(previous);
}
