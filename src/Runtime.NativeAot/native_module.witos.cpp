#include "pal.witos.h"

namespace {
const WitUserImageInfo *module_info(HANDLE module)
{
    const auto image = wit_native_process_image();
    if (!image) {
        SetLastError(ERROR_NOT_READY);
        return nullptr;
    }
    if (module && (uintptr_t)module != image->Base) {
        SetLastError(ERROR_MOD_NOT_FOUND);
        return nullptr;
    }
    return image;
}

wchar_t fold(wchar_t c)
{
    if (c >= L'A' && c <= L'Z') {
        return c + (L'a' - L'A');
    }
    return c == L'\\' ? L'/' : c;
}
}

int32_t PalGetModuleFileName(const TCHAR **output, HANDLE module)
{
    if (!output) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return 0;
    }
    const auto image = module_info(module);
    *output = nullptr;
    if (!image) {
        return 0;
    }
    if (!image->ResourceNameLength) {
        SetLastError(ERROR_PATH_NOT_FOUND);
        return 0;
    }
    *output = (const wchar_t *)image->ResourceName;
    return (int32_t)image->ResourceNameLength;
}

extern "C" DWORD WINAPI wit_native_module_filename(HMODULE module, LPWSTR output, DWORD capacity)
{
    const auto image = module_info(module);
    if (!image) {
        return 0;
    }
    if (!image->ResourceNameLength) {
        SetLastError(ERROR_PATH_NOT_FOUND);
        return 0;
    }
    if (!capacity) {
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return 0;
    }
    if (!output) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return 0;
    }
    const DWORD length = image->ResourceNameLength;
    const DWORD copied = length < capacity ? length : capacity - 1;
    for (DWORD i = 0; i < copied; ++i) {
        output[i] = (wchar_t)image->ResourceName[i];
    }
    output[copied] = 0;
    if (length >= capacity) {
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return capacity;
    }
    return length;
}

extern "C" HMODULE WINAPI wit_native_module_handle(LPCWSTR name)
{
    const auto image = module_info(nullptr);
    if (!image) {
        return nullptr;
    }
    if (!name) {
        return (HMODULE)(uintptr_t)image->Base;
    }
    DWORD length = 0;
    bool qualified = false;
    while (length < WIT_IMAGE_RESOURCE_CAPACITY && name[length]) {
        if (name[length] == L'/' || name[length] == L'\\' || name[length] == L':') {
            qualified = true;
        }
        ++length;
    }
    const DWORD start = qualified ? 0 : 6; // Valid named images use the boot:/ namespace.
    if (length && image->ResourceNameLength > start && length == image->ResourceNameLength - start) {
        DWORD i = 0;
        for (; i < length; ++i) {
            if (fold(name[i]) != fold((wchar_t)image->ResourceName[start + i])) {
                break;
            }
        }
        if (i == length) {
            return (HMODULE)(uintptr_t)image->Base;
        }
    }
    SetLastError(ERROR_MOD_NOT_FOUND);
    return nullptr;
}

extern "C" FARPROC WINAPI wit_native_module_proc(HMODULE module, LPCSTR name)
{
    if (!module_info(module)) {
        return nullptr;
    }
    if (!name) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return nullptr;
    }
    // The current kernel PE profile rejects export directories before allocation.
    // The only published image consequently has no export by name or ordinal.
    // Do not fabricate ntdll or addresses for optional startup DLL probes.
    SetLastError(ERROR_PROC_NOT_FOUND);
    return nullptr;
}
