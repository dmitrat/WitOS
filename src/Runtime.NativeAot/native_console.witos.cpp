#include "pal.witos.h"

extern "C" HANDLE WINAPI wit_native_std_handle(DWORD which)
{
    if (which != STD_OUTPUT_HANDLE && which != STD_ERROR_HANDLE) {
        SetLastError(which == STD_INPUT_HANDLE ? ERROR_NOT_SUPPORTED : ERROR_INVALID_PARAMETER);
        return INVALID_HANDLE_VALUE;
    }
    const auto handle = wit_native_process_console();
    if (!handle) {
        SetLastError(ERROR_NOT_READY);
        return INVALID_HANDLE_VALUE;
    }
    return (HANDLE)handle;
}

extern "C" UINT WINAPI wit_native_console_codepage()
{
    // The serial console carries UTF-8 bytes; there is no mutable codepage service.
    return CP_UTF8;
}

extern "C" BOOL WINAPI wit_native_write_file(
    HANDLE handle, LPCVOID buffer, DWORD bytes, LPDWORD written, LPOVERLAPPED overlapped)
{
    if (overlapped) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return FALSE;
    }
    if (!written) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    // The count's destination is validated as a whole before anything changes, as the kernel's former CONSOLE_WRITE
    // validated its output pointer: a readonly or partly unmapped destination fails and no byte is written.
    WitCodeMemoryRequest probe = {WIT_CODE_MEMORY_VERSION, sizeof(probe), WIT_CODE_VALIDATE,
        WIT_MEMORY_READ | WIT_MEMORY_WRITE, (WitU64)written, 0, sizeof(*written), 0, 0, 0};
    if (wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&probe, sizeof(probe), 0, nullptr) != WIT_STATUS_OK) {
        SetLastError(ERROR_INVALID_ADDRESS);
        return FALSE;
    }
    *written = 0;
    WitU64 count = 0;
    const auto status = wit_native_call(WIT_CALL_DEBUG_WRITE, (WitU64)handle, (WitU64)buffer, bytes, &count);
    if (status == WIT_STATUS_OK) {
        *written = (DWORD)count;
    }
    if (status == WIT_STATUS_TOO_LARGE) {
        SetLastError(ERROR_NOT_ENOUGH_QUOTA);
        return FALSE;
    }
    return (BOOL)wit_pal_result(status);
}
