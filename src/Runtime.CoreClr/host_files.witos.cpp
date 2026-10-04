// Actual corehost PAL signatures. The build uses a hash-verified pal.h with
// only the WITOS_HOST_FILES munmap declaration correction. Other PAL/native
// dependencies stay real and unresolved until implemented.
#pragma warning(push)
#pragma warning(disable : 4100) // Upstream inline mkdir intentionally ignores mode on Windows.
#include "pal.h"
#pragma warning(pop)
extern "C" {
#include "file_view.h"
}
#include "host_path_codec.witos.h"

namespace {
using WitHostPath::error;

void *map(const pal::string_t &path, size_t *length, WitU32 mode)
{
    const DWORD previous = GetLastError();
    WitNativePath resolved;
    if (!WitHostPath::resolve(path, resolved, true)) {
        return nullptr;
    }
    WitU64 handle = 0;
    WitU64 status = wit_native_file_open(resolved.Text + 1, resolved.Bytes - 1, &handle);
    if (status != WIT_STATUS_OK) {
        SetLastError(error(status));
        return nullptr;
    }
    WitU64 fileSize = 0;
    status = wit_native_file_length(handle, &fileSize);
    if (status == WIT_STATUS_OK && length) {
        *length = (size_t)fileSize;
    }
    WitNativeFileView view = {0};
    if (status == WIT_STATUS_OK) {
        status = wit_native_file_view(handle, mode, &view);
    }
    if (wit_native_file_close(handle) != WIT_STATUS_OK) {
        if (status == WIT_STATUS_OK) {
            (void)wit_native_file_unview(&view);
        }
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    if (status != WIT_STATUS_OK) {
        SetLastError(!fileSize && status == WIT_STATUS_INVALID_ARGUMENT ? ERROR_FILE_INVALID : error(status));
        return nullptr;
    }
    SetLastError(previous);
    return view.Address;
}
}

const void *pal::mmap_read(const pal::string_t &path, size_t *length)
{
    return map(path, length, WIT_FILE_VIEW_READONLY);
}

void *pal::mmap_copy_on_write(const pal::string_t &path, size_t *length)
{
    return map(path, length, WIT_FILE_VIEW_PRIVATE);
}

bool pal::munmap(void *address, size_t length)
{
    const DWORD previous = GetLastError();
    const auto status = wit_native_unmap_file(address, length);
    if (status != WIT_STATUS_OK) {
        SetLastError(error(status));
        return false;
    }
    SetLastError(previous);
    return true;
}

bool pal::file_exists(const pal::string_t &path)
{
    const DWORD previous = GetLastError();
    WitNativePath resolved;
    if (!WitHostPath::resolve(path, resolved, true)) {
        return false;
    }
    SetLastError(previous);
    return true;
}
