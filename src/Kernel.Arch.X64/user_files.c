#include "user.h"
#include "witos/storage.h"

/* Nonblocking file calls run with IF clear. The package lives in immutable,
 * supervisor-only kernel storage for the entire boot; user handles own no pages. */
WitU64 wit_user_file_call(WitUserProcess *process, WitU64 address, WitU64 size, WitU64 reserved, WitU64 *result)
{
    WitFileRequest request;
    *result = 0;
    if (reserved || size != sizeof(request)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&process->Space, address, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_FILE_IO_VERSION || request.Size != sizeof(request)) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Reserved0 || request.Reserved1) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request.Operation > WIT_FILE_SEEK) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Operation != WIT_FILE_SEEK && request.Flags) {
        return WIT_STATUS_UNSUPPORTED;
    }
    const WitPackage *package = wit_storage_package();
    if (!package) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Operation == WIT_FILE_OPEN) {
        WitU8 name[WIT_PACKAGE_MAX_NAME];
        if (request.Handle || request.Offset || !request.Bytes) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        if (request.Bytes > sizeof(name)) {
            return WIT_STATUS_TOO_LARGE;
        }
        if (!wit_user_copy_from(&process->Space, request.Address, name, (WitU32)request.Bytes)) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        return wit_file_open(&process->Files, &process->Handles, package, name, (WitU32)request.Bytes, result);
    }
    WitFile *file;
    WitU64 status = wit_file_get(&process->Files, &process->Handles, request.Handle, &file);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (request.Operation == WIT_FILE_LENGTH) {
        if (request.Address || request.Bytes || request.Offset) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        *result = file->Length;
        return WIT_STATUS_OK;
    }
    if (request.Operation == WIT_FILE_SEEK) {
        if (request.Address || request.Bytes) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        return wit_file_seek(file, request.Offset, request.Flags, result);
    }
    if (request.Bytes > WIT_FILE_MAX_READ) {
        return WIT_STATUS_TOO_LARGE;
    }
    if (request.Offset > 0x7FFFFFFFFFFFFFFFULL || (request.Operation == WIT_FILE_READ && request.Offset)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_buffer_writable(&process->Space, request.Address, (WitU32)request.Bytes)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    const WitU64 offset = request.Operation == WIT_FILE_READ ? file->Position : request.Offset;
    const WitU64 available = offset < file->Length ? file->Length - offset : 0;
    const WitU32 bytes = (WitU32)(request.Bytes < available ? request.Bytes : available);
    /* Bounds below are kernel-owned, yet retain fail-closed validation before
     * pointer arithmetic. The source pointer is never exposed to user space. */
    if (file->Offset > package->Size || file->Length > package->Size - file->Offset) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    for (WitU32 done = 0; done < bytes;) {
        const WitU64 to = request.Address + done;
        WitU32 chunk = 4096 - (WitU32)(to & 4095);
        if (chunk > bytes - done) {
            chunk = bytes - done;
        }
        WitU8 *physical = (WitU8 *)wit_user_space_physical(&process->Space, to, 1, 0);
        for (WitU32 i = 0; i < chunk; ++i) {
            physical[i] = package->Data[file->Offset + offset + done + i];
        }
        done += chunk;
    }
    if (request.Operation == WIT_FILE_READ) {
        file->Position += bytes;
    }
    *result = bytes;
    return WIT_STATUS_OK;
}

WitU64 wit_user_storage_query(WitUserProcess *process, WitU64 address, WitU64 size, WitU64 reserved, WitU64 *result)
{
    WitStorageQuery request;
    WitU8 name[WIT_STORAGE_NAME_BYTES];
    *result = 0;
    WIT_STATIC_ASSERT(WIT_STORAGE_NAME_BYTES == WIT_PACKAGE_MAX_NAME, "Storage name quotas");
    WIT_STATIC_ASSERT(
        WIT_STORAGE_FILE == WIT_PACKAGE_FILE && WIT_STORAGE_DIRECTORY == WIT_PACKAGE_DIRECTORY, "Storage node kinds");
    if (reserved || size != sizeof(request)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&process->Space, address, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_STORAGE_QUERY_VERSION ||
        request.Size != sizeof(request) ||
        request.Operation > WIT_STORAGE_LIST) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Reserved ||
        request.Reserved2 ||
        request.BufferBytes != sizeof(WitStorageInfo) ||
        request.Cursor > 0xFFFFFFFFULL ||
        (request.Operation == WIT_STORAGE_STAT && request.Cursor)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request.PathBytes > sizeof(name)) {
        return WIT_STATUS_TOO_LARGE;
    }
    if (!wit_user_copy_from(&process->Space, request.Path, name, (WitU32)request.PathBytes) ||
        !wit_user_buffer_writable(&process->Space, request.Buffer, sizeof(WitStorageInfo))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    const WitPackage *package = wit_storage_package();
    if (!package) {
        return WIT_STATUS_UNSUPPORTED;
    }
    WitPackageNode node;
    WitU32 next = 0;
    const WitPackageStatus status = request.Operation == WIT_STORAGE_STAT
        ? wit_package_stat(package, name, (WitU32)request.PathBytes, &node)
        : wit_package_list(package, name, (WitU32)request.PathBytes, (WitU32)request.Cursor, &node, &next);
    if (status == WitPackageEnd) {
        return WIT_STATUS_OK;
    }
    if (status == WitPackageMissing) {
        return WIT_STATUS_NOT_FOUND;
    }
    if (status == WitPackageNotDirectory) {
        return WIT_STATUS_WRONG_TYPE;
    }
    if (status != WitPackageOk) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitStorageInfo info;
    for (WitU32 i = 0; i < sizeof(info); ++i) {
        ((WitU8 *)&info)[i] = 0;
    }
    info.Version = WIT_STORAGE_QUERY_VERSION;
    info.Size = sizeof(info);
    info.Kind = node.Kind;
    info.NameBytes = node.NameLength;
    info.Length = node.Length;
    info.NextCursor = next;
    for (WitU32 i = 0; i < node.NameLength; ++i) {
        info.Name[i] = node.Name[i];
    }
    if (!wit_user_copy_to(&process->Space, request.Buffer, (const WitU8 *)&info, sizeof(info))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    *result = sizeof(info);
    return WIT_STATUS_OK;
}
