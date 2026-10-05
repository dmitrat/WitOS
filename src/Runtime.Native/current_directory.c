#include "path.h"

/* The current directory is the process's (P6.4.j3a): the kernel keeps it, so every module of a component resolves
 * against the same directory and sees a change any of them makes. Resolution stays here; the kernel takes only the
 * canonical result and checks that it names a directory of the package. */
static WitU64 directory_call(
    WitU32 operation, const char *path, WitU32 bytes, char *buffer, WitU32 capacity, WitU64 *size)
{
    WitProcessStateRequest request = {0};
    request.Version = WIT_PROCESS_STATE_VERSION;
    request.Size = sizeof(request);
    request.Operation = operation;
    request.Name = (WitU64)path;
    request.NameUnits = bytes;
    request.Buffer = (WitU64)buffer;
    request.BufferBytes = capacity;
    return wit_native_call(WIT_CALL_PROCESS_STATE, (WitU64)&request, sizeof(request), 0, size);
}

WitU64 wit_native_cwd_get(WitNativePath *output)
{
    if (!output) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitNativePath path;
    WitU64 bytes = 0;
    const WitU64 status = directory_call(WIT_PROCESS_CWD_GET, 0, 0, path.Text, WIT_PATH_BUFFER - 1, &bytes);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!bytes || bytes > WIT_PATH_BUFFER - 1 || path.Text[0] != '/') {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT); // the kernel's directory always fits and is rooted
    }
    path.Bytes = (WitU32)bytes;
    path.Text[bytes] = 0;
    path.RequireDirectory = 1;
    *output = path;
    return WIT_STATUS_OK;
}

WitU64 wit_native_path_resolve(const char *input, WitU32 bytes, WitNativePath *output)
{
    WitNativePath snapshot;
    const WitU64 status = wit_native_cwd_get(&snapshot);
    return status != WIT_STATUS_OK ? status : wit_path_resolve(snapshot.Text, snapshot.Bytes, input, bytes, output);
}

WitU64 wit_native_path_full(const char *input, WitU32 bytes, WitNativePath *output)
{
    if (!output) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitNativePath path;
    WitU64 status = wit_native_path_resolve(input, bytes, &path);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    WitStorageInfo info;
    status = wit_native_storage_stat(path.Text + 1, path.Bytes - 1, &info);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (path.RequireDirectory && info.Kind != WIT_STORAGE_DIRECTORY) {
        return WIT_STATUS_WRONG_TYPE;
    }
    *output = path;
    return WIT_STATUS_OK;
}

WitU64 wit_native_cwd_set(const char *input, WitU32 bytes)
{
    WitNativePath path;
    const WitU64 status = wit_native_path_resolve(input, bytes, &path);
    return status != WIT_STATUS_OK ? status : directory_call(WIT_PROCESS_CWD_SET, path.Text, path.Bytes, 0, 0, 0);
}
