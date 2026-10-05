#include "library.h"

static WitU64 call(WitLibraryRequest *request, WitU64 *output)
{
    request->Version = WIT_LIBRARY_VERSION;
    request->Size = sizeof(*request);
    WitU64 result = 0;
    const WitU64 status = wit_native_call(WIT_CALL_LIBRARY, (WitU64)request, sizeof(*request), 0, &result);
    if (status == WIT_STATUS_OK && output) {
        *output = result;
    }
    return status;
}

static WitU64 lifecycle_call(WitLibraryRequest *request, WitU64 *output)
{
    WitU64 address = 0, result = 0;
    request->Flags = WIT_LIBRARY_USER_LIFECYCLE;
    request->Buffer = (WitU64)&address;
    request->BufferBytes = sizeof(address);
    WitU64 status = call(request, &result);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    status = wit_native_library_execute_lifecycle(address);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (output) {
        *output = result;
    }
    return WIT_STATUS_OK;
}

WitU64 wit_native_library_load(const char *name, WitU32 bytes, WitU64 *output)
{
    if (!output) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitNativePath path;
    const WitU64 status = wit_native_path_full(name, bytes, &path);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    WitLibraryRequest request = {0};
    request.Operation = WIT_LIBRARY_LOAD;
    request.Name = (WitU64)(path.Text + 1);
    request.NameBytes = path.Bytes - 1;
    return lifecycle_call(&request, output);
}

WitU64 wit_native_library_symbol(WitU64 handle, const char *name, WitU32 bytes, WitU32 ordinal, WitU64 *output)
{
    if (!output) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitLibraryRequest request = {0};
    request.Operation = WIT_LIBRARY_SYMBOL;
    request.Handle = handle;
    request.Name = (WitU64)name;
    request.NameBytes = bytes;
    request.Ordinal = ordinal;
    request.Flags = name ? 0 : WIT_LIBRARY_BY_ORDINAL;
    return call(&request, output);
}

WitU64 wit_native_library_info(WitU64 handle, WitLibraryInfo *output)
{
    WitLibraryRequest request = {0};
    request.Operation = WIT_LIBRARY_QUERY;
    request.Handle = handle;
    request.Buffer = (WitU64)output;
    request.BufferBytes = sizeof(*output);
    return call(&request, 0);
}

WitU64 wit_native_library_unload(WitU64 handle)
{
    WitLibraryRequest request = {0};
    request.Operation = WIT_LIBRARY_UNLOAD;
    request.Handle = handle;
    return lifecycle_call(&request, 0);
}

WitU64 wit_native_library_find(const char *name, WitU32 bytes, int basename, WitU64 *output)
{
    if (!output || !name || !bytes) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitNativePath path;
    if (!basename) {
        const WitU64 status = wit_native_path_resolve(name, bytes, &path);
        if (status != WIT_STATUS_OK) {
            return status;
        }
        if (path.RequireDirectory) {
            return WIT_STATUS_WRONG_TYPE;
        }
        name = path.Text + 1;
        bytes = path.Bytes - 1;
    }
    WitLibraryRequest request = {0};
    request.Operation = WIT_LIBRARY_FIND;
    request.Flags = basename ? WIT_LIBRARY_BY_BASENAME : 0;
    request.Name = (WitU64)name;
    request.NameBytes = bytes;
    return call(&request, output);
}

WitU64 wit_native_library_path(WitU64 handle, WitLibraryPath *output)
{
    WitLibraryRequest request = {0};
    request.Operation = WIT_LIBRARY_PATH;
    request.Handle = handle;
    request.Buffer = (WitU64)output;
    request.BufferBytes = sizeof(*output);
    return call(&request, 0);
}

WitU64 wit_native_module_path(WitU64 address, WitU32 flags, WitLibraryPath *output)
{
    WitLibraryRequest request = {0};
    request.Operation = WIT_LIBRARY_MODULE_PATH;
    request.Flags = flags;
    request.Ordinal = address;
    request.Buffer = (WitU64)output;
    request.BufferBytes = sizeof(*output);
    return call(&request, 0);
}

WitU64 wit_native_library_acquire_reader(WitU64 pc, WitLibraryInfo *info, WitU64 *output)
{
    if (!output) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitLibraryRequest request = {0};
    request.Operation = WIT_LIBRARY_ACQUIRE_READER;
    request.Ordinal = pc;
    request.Buffer = (WitU64)info;
    request.BufferBytes = sizeof(*info);
    return call(&request, output);
}

WitU64 wit_native_library_query_reader(WitU64 reader, WitLibraryInfo *info)
{
    WitLibraryRequest request = {0};
    request.Operation = WIT_LIBRARY_QUERY_READER;
    request.Handle = reader;
    request.Buffer = (WitU64)info;
    request.BufferBytes = sizeof(*info);
    return call(&request, 0);
}

WitU64 wit_native_library_release_reader(WitU64 reader)
{
    WitLibraryRequest request = {0};
    request.Operation = WIT_LIBRARY_RELEASE_READER;
    request.Handle = reader;
    return lifecycle_call(&request, 0);
}
