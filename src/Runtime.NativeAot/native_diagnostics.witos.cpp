#include "pal.witos.h"
#include "native_heap.witos.h"

namespace {
struct Message {
    DWORD Code;
    const wchar_t *Text;
};

// WitOS messages describe actual native errors; these are not copied Windows
// message resources. Unknown codes retain the upstream managed hex fallback.
const Message messages[] = {{ERROR_SUCCESS, L"The operation completed successfully."},
    {ERROR_ACCESS_DENIED, L"Access to the requested resource was denied."},
    {ERROR_INVALID_HANDLE, L"The resource handle is invalid or has been closed."},
    {ERROR_NOT_ENOUGH_MEMORY, L"There is not enough native memory for this operation."},
    {ERROR_INVALID_ADDRESS, L"The memory range is not accessible with the requested permissions."},
    {ERROR_NOT_SUPPORTED, L"This operation is not supported by the current WitOS profile."},
    {ERROR_INVALID_PARAMETER, L"An argument is invalid for this operation."},
    {ERROR_INVALID_FLAGS, L"The requested flags are invalid for this operation."},
    {ERROR_SIGNAL_REFUSED, L"The thread suspend count has reached its limit."},
    {ERROR_BUSY, L"The resource is currently in use."},
    {ERROR_POSSIBLE_DEADLOCK, L"The requested wait would create a deadlock."},
    {ERROR_TIMEOUT, L"The operation timed out."}, {ERROR_GEN_FAILURE, L"The native operation failed."},
    {ERROR_ALREADY_INITIALIZED, L"The native subsystem has already been initialized."},
    {ERROR_INVALID_STATE, L"The operation is not valid in the current state."},
    {ERROR_NOT_READY, L"The required native subsystem is not ready."},
    {ERROR_BUFFER_OVERFLOW, L"The input exceeds the supported size limit."},
    {ERROR_INSUFFICIENT_BUFFER, L"The destination buffer is too small."},
    {ERROR_NOT_ENOUGH_QUOTA, L"The operation exceeds the available resource quota."},
    {ERROR_ENVVAR_NOT_FOUND, L"The requested environment variable does not exist."},
    {ERROR_NO_UNICODE_TRANSLATION, L"The input contains an invalid Unicode sequence."},
    {ERROR_PATH_NOT_FOUND, L"The image has no published resource path."},
    {ERROR_MOD_NOT_FOUND, L"The requested module is not loaded."},
    {ERROR_PROC_NOT_FOUND, L"The requested procedure is not exported by the module."},
    {ERROR_MR_MID_NOT_FOUND, L"No diagnostic message is defined for this error code."},
    {ERROR_RESOURCE_TYPE_NOT_FOUND, L"The module has no message resource table."},
    {ERROR_RESOURCE_LANG_NOT_FOUND, L"The requested diagnostic language is unavailable."}};

DWORD fail(DWORD error)
{
    SetLastError(error);
    return 0;
}
}

extern "C" DWORD WINAPI wit_native_format_message(
    DWORD flags, LPCVOID source, DWORD id, DWORD language, LPWSTR buffer, DWORD size, va_list *)
{
    constexpr DWORD supported = FORMAT_MESSAGE_FROM_SYSTEM |
        FORMAT_MESSAGE_IGNORE_INSERTS |
        FORMAT_MESSAGE_ARGUMENT_ARRAY |
        FORMAT_MESSAGE_ALLOCATE_BUFFER;
    constexpr DWORD optional = FORMAT_MESSAGE_FROM_STRING | FORMAT_MESSAGE_FROM_HMODULE | FORMAT_MESSAGE_MAX_WIDTH_MASK;
    if (flags & ~(supported | optional)) {
        return fail(ERROR_INVALID_FLAGS);
    }
    if ((flags & FORMAT_MESSAGE_FROM_STRING) ||
        ((flags & FORMAT_MESSAGE_MAX_WIDTH_MASK) != 0 && (flags & FORMAT_MESSAGE_MAX_WIDTH_MASK) != 255)) {
        return fail(ERROR_NOT_SUPPORTED);
    }
    if (!buffer) {
        return fail(ERROR_INVALID_PARAMETER);
    }
    if (flags & FORMAT_MESSAGE_FROM_HMODULE) {
        const auto image = wit_native_process_image();
        if (!image) {
            return fail(ERROR_NOT_READY);
        }
        if (source && (uintptr_t)source != image->Base) {
            return fail(ERROR_MOD_NOT_FOUND);
        }
        // The selected kernel PE profile rejects resource directories. When
        // FROM_SYSTEM is also requested, use the actual system catalogue.
        if (!(flags & FORMAT_MESSAGE_FROM_SYSTEM)) {
            return fail(ERROR_RESOURCE_TYPE_NOT_FOUND);
        }
    }
    if (!(flags & FORMAT_MESSAGE_FROM_SYSTEM)) {
        return fail(ERROR_INVALID_PARAMETER);
    }
    if (language && language != 0x409) {
        return fail(ERROR_RESOURCE_LANG_NOT_FOUND);
    }
    const wchar_t *message = nullptr;
    for (const auto &entry : messages) {
        if (entry.Code == id) {
            message = entry.Text;
            break;
        }
    }
    if (!message) {
        return fail(ERROR_MR_MID_NOT_FOUND);
    }
    DWORD length = 0;
    while (message[length]) {
        ++length;
    }
    wchar_t *output = buffer;
    if (flags & FORMAT_MESSAGE_ALLOCATE_BUFFER) {
        const size_t units = size > length + 1 ? size : length + 1;
        output = (wchar_t *)wit_native_local_allocate(units * sizeof(wchar_t));
        if (!output) {
            return fail(ERROR_NOT_ENOUGH_MEMORY);
        }
    } else if (size <= length) {
        return fail(ERROR_INSUFFICIENT_BUFFER);
    }
    for (DWORD i = 0; i <= length; ++i) {
        output[i] = message[i];
    }
    // Publish allocation only after the message and terminator exist. Caller
    // owns this native buffer/pointer slot, just as for the native CRT routines.
    if (flags & FORMAT_MESSAGE_ALLOCATE_BUFFER) {
        *(wchar_t **)buffer = output;
    }
    return length;
}

extern "C" HLOCAL WINAPI wit_native_local_free(HLOCAL memory)
{
    if (wit_native_local_release(memory)) {
        return nullptr;
    }
    SetLastError(ERROR_INVALID_HANDLE);
    return memory;
}

extern "C" HANDLE WINAPI wit_native_event_source(LPCWSTR, LPCWSTR)
{
    // No Windows Event Log service/provider exists in this profile. CoreLib's
    // EventReporter explicitly returns when registration fails. No fake handle.
    SetLastError(ERROR_NOT_SUPPORTED);
    return nullptr;
}

extern "C" BOOL WINAPI wit_native_event_source_close(HANDLE)
{
    SetLastError(ERROR_INVALID_HANDLE);
    return FALSE;
}

extern "C" BOOL WINAPI wit_native_report_event(HANDLE, WORD, WORD, DWORD, PSID, WORD, DWORD, LPCWSTR *, LPVOID)
{
    // No live event-source capability can have been issued. Do not read payload.
    SetLastError(ERROR_INVALID_HANDLE);
    return FALSE;
}

extern "C" BOOL WINAPI wit_native_debugger_present()
{
    // The current kernel has no guest process debugger attachment facility.
    // Host-side QEMU debugging is not a guest process debugger attachment.
    return FALSE;
}
