// Real C++ path ownership and exception propagation remain required by these PAL methods.
#pragma warning(push)
#pragma warning(disable : 4100)
#include "pal.h"
#pragma warning(pop)
extern "C" {
#include "library.h"
}
#include "host_path_codec.witos.h"

namespace {
struct LibraryReference {
    pal::dll_t Value;

    ~LibraryReference()
    {
        if (Value) {
            pal::unload_library(Value);
        }
    }
};
}

bool pal::get_module_path(dll_t library, string_t *output)
{
    if (!output) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }
    const DWORD previous = GetLastError();
    WitLibraryPath origin;
    const auto status = wit_native_library_path(reinterpret_cast<WitU64>(library), &origin);
    if (status != WIT_STATUS_OK) {
        SetLastError(WitHostPath::error(status));
        return false;
    }
    if (origin.Version != WIT_LIBRARY_VERSION ||
        origin.Size != sizeof(origin) ||
        origin.Reserved ||
        !origin.NameBytes ||
        origin.NameBytes > WIT_LIBRARY_PATH_BYTES) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    WitNativePath path = {};
    path.Bytes = origin.NameBytes + 1;
    path.Text[0] = '/';
    for (WitU32 i = 0; i < origin.NameBytes; ++i) {
        path.Text[i + 1] = (char)origin.Name[i];
    }
    pal::char_t wide[WIT_PATH_BUFFER];
    const auto count = WitHostPath::wide(path, wide);
    string_t value(wide, count);
    output->swap(value);
    SetLastError(previous);
    return true;
}

bool pal::get_loaded_library(const char_t *name, const char *symbol, dll_t *output, string_t *path)
{
    if (!name || !symbol || !output || !path) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }
    size_t count = 0;
    while (count <= WIT_PATH_INPUT_MAX && name[count]) {
        ++count;
    }
    if (!count) {
        SetLastError(ERROR_INVALID_NAME);
        return false;
    }
    if (count > WIT_PATH_INPUT_MAX) {
        SetLastError(ERROR_FILENAME_EXCED_RANGE);
        return false;
    }
    const DWORD previous = GetLastError();
    const string_t requested(name, count);
    char input[WIT_PATH_INPUT_MAX];
    WitU32 bytes = 0;
    if (!WitHostPath::utf8(requested, input, bytes)) {
        return false;
    }
    bool basename = true;
    for (WitU32 i = 0; i < bytes; ++i) {
        if (input[i] == '/' || input[i] == '\\') {
            basename = false;
        }
    }
    WitU64 handle = 0;
    const auto status = wit_native_library_find(input, bytes, basename, &handle);
    if (status != WIT_STATUS_OK) {
        SetLastError(WitHostPath::error(status));
        return false;
    }
    LibraryReference reference{reinterpret_cast<dll_t>(handle)};
    if (!pal::get_symbol(reference.Value, symbol)) {
        return false;
    }
    // If string construction throws, the acquired reference is released while
    // the caller's module/path outputs remain unchanged.
    if (!pal::get_module_path(reference.Value, path)) {
        return false;
    }
    *output = reference.Value;
    reference.Value = nullptr;
    SetLastError(previous);
    return true;
}
