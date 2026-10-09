#include "witos/files.h"

void wit_files_initialize(WitFileTable *files)
{
    for (WitU32 i = 0; i < WIT_PROCESS_HANDLE_CAPACITY; ++i) {
        const WitFile empty = {0};
        files->Entries[i] = empty;
    }
}

WitU64 wit_file_open(WitFileTable *files, WitHandleTable *handles, const WitPackage *package, const WitU8 *name,
    WitU32 bytes, WitU64 *result)
{
    *result = 0;
    WitPackageFile file;
    const WitPackageStatus status = wit_package_find(package, name, bytes, &file);
    if (status == WitPackageMissing) {
        WitPackageNode node;
        if (wit_package_stat(package, name, bytes, &node) == WitPackageOk && node.Kind == WIT_PACKAGE_DIRECTORY) {
            return WIT_STATUS_WRONG_TYPE;
        }
        return WIT_STATUS_NOT_FOUND;
    }
    if (status != WitPackageOk) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 handle = wit_handle_grant(handles, WIT_HANDLE_FILE, WIT_RIGHT_READ);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    const WitFile opened = {handle, file.Offset, file.Length, 0};
    files->Entries[(WitU32)(handle & 0xFFFF) - 1] = opened;
    *result = handle;
    return WIT_STATUS_OK;
}

WitU64 wit_file_get(WitFileTable *files, WitHandleTable *handles, WitU64 token, WitFile **output)
{
    const WitU64 status = wit_handle_check(handles, token, WIT_HANDLE_FILE, WIT_RIGHT_READ);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    WitFile *file = &files->Entries[(WitU32)(token & 0xFFFF) - 1];
    if (file->Token != token) {
        return WIT_STATUS_BAD_HANDLE;
    }
    *output = file;
    return WIT_STATUS_OK;
}

WitU64 wit_file_close(WitFileTable *files, WitHandleTable *handles, WitU64 token)
{
    WitFile *file;
    const WitU64 status = wit_file_get(files, handles, token, &file);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const WitU64 closed = wit_handle_close(handles, token);
    if (closed == WIT_STATUS_OK) {
        const WitFile empty = {0};
        *file = empty;
    }
    return closed;
}

WitU64 wit_file_seek(WitFile *file, WitU64 offset, WitU32 origin, WitU64 *result)
{
    WitU64 base;
    if (origin == WIT_FILE_SEEK_BEGIN) {
        base = 0;
    } else if (origin == WIT_FILE_SEEK_CURRENT) {
        base = file->Position;
    } else if (origin == WIT_FILE_SEEK_END) {
        base = file->Length;
    } else {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (offset >> 63) {
        const WitU64 magnitude = (~offset) + 1;
        if (magnitude > base) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        base -= magnitude;
    } else {
        if (offset > 0x7FFFFFFFFFFFFFFFULL - base) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        base += offset;
    }
    file->Position = base;
    *result = base;
    return WIT_STATUS_OK;
}
