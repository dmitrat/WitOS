#include "user.h"
#include "witos/package.h"
#include "witos/platform.h"
#include "witos/storage.h"

/* The environment and the current directory of a component (P6.4.j3a), kept in the process as kernel32 keeps them for
 * a Windows process, so every module of the component sees one state. The environment is a block of "Name=Value\0"
 * records in the order they were set; names compare with ASCII case folding and hold no '=' or NUL. The directory is
 * canonical UTF-8 from '/'. Calls run with interrupts disabled, so each operation is atomic for the component's
 * threads, and a failure changes nothing. */

static WitU16 fold(WitU16 c)
{
    return c >= 'a' && c <= 'z' ? (WitU16)(c - ('a' - 'A')) : c;
}

static int name_valid(const WitU16 *name, WitU32 units)
{
    if (!units || units > WIT_PROCESS_NAME_UNITS) {
        return 0;
    }
    for (WitU32 i = 0; i < units; ++i) {
        if (!name[i] || name[i] == '=') {
            return 0;
        }
    }
    return 1;
}

/* The offset of the variable's record, or the environment's units when there is none; length counts its terminator. */
static WitU32 find(const WitUserProcess *process, const WitU16 *name, WitU32 units, WitU32 *length)
{
    WitU32 at = 0;
    while (at < process->EnvironmentUnits) {
        WitU32 end = at;
        while (process->Environment[end]) {
            ++end;
        }
        /* A record's name holds no '=', so a match through the separator is the whole name. */
        int match = end - at > units && process->Environment[at + units] == '=';
        for (WitU32 i = 0; match && i < units; ++i) {
            match = fold(process->Environment[at + i]) == fold(name[i]);
        }
        if (match) {
            *length = end - at + 1;
            return at;
        }
        at = end + 1;
    }
    return process->EnvironmentUnits;
}

/* Sets or removes a variable whose name is validated; the value comes from the kernel or, already validated as
 * readable and free of NUL, from the component. A value of neither removes the variable. */
static WitU64 put(WitUserProcess *process, const WitU16 *name, WitU32 units, const WitU16 *kernelValue,
    WitU64 userValue, WitU32 valueUnits)
{
    WitU32 length = 0;
    const WitU32 at = find(process, name, units, &length);
    const int found = at < process->EnvironmentUnits;
    const int remove = !kernelValue && !userValue;
    if (remove && !found) {
        return WIT_STATUS_NOT_FOUND;
    }
    const WitU64 need = remove ? 0 : (WitU64)units + 1 + valueUnits + 1;
    const WitU64 available = WIT_ENVIRONMENT_UNITS - process->EnvironmentUnits + (found ? length : 0);
    if (need > available || (!found && process->EnvironmentVariables == WIT_ENVIRONMENT_VARIABLES)) {
        return WIT_STATUS_NO_MEMORY;
    }
    if (found) {
        for (WitU32 i = at; i + length < process->EnvironmentUnits; ++i) {
            process->Environment[i] = process->Environment[i + length];
        }
        process->EnvironmentUnits -= length;
        --process->EnvironmentVariables;
    }
    if (remove) {
        return WIT_STATUS_OK;
    }
    WitU16 *record = process->Environment + process->EnvironmentUnits;
    for (WitU32 i = 0; i < units; ++i) {
        record[i] = name[i];
    }
    record[units] = '=';
    if (kernelValue) {
        for (WitU32 i = 0; i < valueUnits; ++i) {
            record[units + 1 + i] = kernelValue[i];
        }
    } else if (!wit_user_copy_from(&process->Space, userValue, (WitU8 *)(record + units + 1), valueUnits * 2)) {
        /* It was read whole with interrupts disabled throughout; it cannot have changed. */
        wit_panic("Environment value changed while it was set");
    }
    record[units + 1 + valueUnits] = 0;
    process->EnvironmentUnits += (WitU32)need;
    ++process->EnvironmentVariables;
    return WIT_STATUS_OK;
}

void wit_user_process_state_reset(WitUserProcess *process)
{
    process->EnvironmentVariables = 0;
    process->EnvironmentUnits = 0;
    process->Directory[0] = '/';
    process->DirectoryBytes = 1;
}

WitU64 wit_user_environment_set(
    WitUserProcess *process, const WitU16 *name, WitU32 nameUnits, const WitU16 *value, WitU32 valueUnits)
{
    if (!name_valid(name, nameUnits)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    for (WitU32 i = 0; value && i < valueUnits; ++i) {
        if (!value[i]) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
    }
    return put(process, name, nameUnits, value, 0, value ? valueUnits : 0);
}

/* Whether a user value is readable whole and holds no NUL, read in bounded pieces. */
static WitU64 check_value(const WitUserProcess *process, WitU64 address, WitU64 units)
{
    if (units > WIT_ENVIRONMENT_UNITS) {
        return WIT_STATUS_NO_MEMORY;
    }
    if (!wit_user_buffer_readable(&process->Space, address, (WitU32)units * 2)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    WitU16 piece[256];
    for (WitU64 done = 0; done < units;) {
        const WitU32 count = (WitU32)(units - done < 256 ? units - done : 256);
        if (!wit_user_copy_from(&process->Space, address + done * 2, (WitU8 *)piece, count * 2)) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        for (WitU32 i = 0; i < count; ++i) {
            if (!piece[i]) {
                return WIT_STATUS_INVALID_ARGUMENT;
            }
        }
        done += count;
    }
    return WIT_STATUS_OK;
}

/* Copies output only when the buffer holds it whole; the result is its size either way. */
static WitU64 answer(WitUserProcess *process, const WitProcessStateRequest *request, const void *data, WitU32 bytes,
    WitU64 size, WitU64 *result)
{
    if (request->BufferBytes >= bytes &&
        (!wit_user_buffer_writable(&process->Space, request->Buffer, bytes) ||
            !wit_user_copy_to(&process->Space, request->Buffer, (const WitU8 *)data, bytes))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    *result = size;
    return WIT_STATUS_OK;
}

static WitU64 set_directory(WitUserProcess *process, const WitProcessStateRequest *request)
{
    WitU8 path[WIT_PROCESS_PATH_BYTES];
    if (!request->NameUnits) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request->NameUnits > sizeof(path)) {
        return WIT_STATUS_TOO_LARGE;
    }
    if (!wit_user_copy_from(&process->Space, request->Name, path, (WitU32)request->NameUnits)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (path[0] != '/') {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitPackage *package = wit_storage_package();
    if (!package) {
        return WIT_STATUS_UNSUPPORTED;
    }
    /* The package validates the canonical name: no empty, '.' or '..' component, no '\', no trailing separator; the
     * empty name after '/' is the root. */
    WitPackageNode node;
    const WitPackageStatus status = wit_package_stat(package, path + 1, (WitU32)request->NameUnits - 1, &node);
    if (status == WitPackageMissing) {
        return WIT_STATUS_NOT_FOUND;
    }
    if (status != WitPackageOk) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (node.Kind != WIT_PACKAGE_DIRECTORY) {
        return WIT_STATUS_WRONG_TYPE;
    }
    for (WitU32 i = 0; i < request->NameUnits; ++i) {
        process->Directory[i] = path[i];
    }
    process->DirectoryBytes = (WitU32)request->NameUnits;
    return WIT_STATUS_OK;
}

WitU64 wit_user_process_state(WitUserProcess *process, WitU64 address, WitU64 size, WitU64 reserved, WitU64 *result)
{
    WitProcessStateRequest request;
    *result = 0;
    if (reserved || size != sizeof(request)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&process->Space, address, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_PROCESS_STATE_VERSION ||
        request.Size != sizeof(request) ||
        request.Operation > WIT_PROCESS_CWD_SET) {
        return WIT_STATUS_UNSUPPORTED;
    }
    const WitU32 operation = request.Operation;
    const int named = operation == WIT_PROCESS_ENV_GET || operation == WIT_PROCESS_ENV_SET;
    const int valued = operation == WIT_PROCESS_ENV_SET;
    const int buffered =
        operation == WIT_PROCESS_ENV_GET || operation == WIT_PROCESS_ENV_BLOCK || operation == WIT_PROCESS_CWD_GET;
    if (request.Reserved ||
        (!named && operation != WIT_PROCESS_CWD_SET && (request.Name || request.NameUnits)) ||
        (!valued && (request.Value || request.ValueUnits)) ||
        (!buffered && (request.Buffer || request.BufferBytes)) ||
        request.BufferBytes > 0xFFFFFFFFULL) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (operation == WIT_PROCESS_CWD_SET) {
        return set_directory(process, &request);
    }
    if (operation == WIT_PROCESS_CWD_GET) {
        return answer(process, &request, process->Directory, process->DirectoryBytes, process->DirectoryBytes, result);
    }
    if (operation == WIT_PROCESS_ENV_BLOCK) {
        /* The records and the final terminator, for which the block keeps one unit beyond the quota. */
        process->Environment[process->EnvironmentUnits] = 0;
        return answer(process, &request, process->Environment, (process->EnvironmentUnits + 1) * 2,
            process->EnvironmentUnits + 1, result);
    }
    WitU16 name[WIT_PROCESS_NAME_UNITS];
    if (!request.NameUnits || request.NameUnits > WIT_PROCESS_NAME_UNITS) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&process->Space, request.Name, (WitU8 *)name, (WitU32)request.NameUnits * 2)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (!name_valid(name, (WitU32)request.NameUnits)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (operation == WIT_PROCESS_ENV_SET) {
        if (!request.Value) {
            return request.ValueUnits ? WIT_STATUS_INVALID_ARGUMENT
                                      : put(process, name, (WitU32)request.NameUnits, 0, 0, 0);
        }
        const WitU64 status = check_value(process, request.Value, request.ValueUnits);
        return status != WIT_STATUS_OK
            ? status
            : put(process, name, (WitU32)request.NameUnits, 0, request.Value, (WitU32)request.ValueUnits);
    }
    WitU32 length = 0;
    const WitU32 at = find(process, name, (WitU32)request.NameUnits, &length);
    if (at == process->EnvironmentUnits) {
        return WIT_STATUS_NOT_FOUND;
    }
    const WitU32 units = length - (WitU32)request.NameUnits - 2; /* without the name, '=' and the terminator */
    return answer(process, &request, process->Environment + at + request.NameUnits + 1, units * 2, units, result);
}
