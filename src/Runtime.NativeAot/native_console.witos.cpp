#include "pal.witos.h"
extern "C" HANDLE WINAPI wit_native_std_handle(DWORD which)
{
    if(which!=STD_OUTPUT_HANDLE&&which!=STD_ERROR_HANDLE){SetLastError(which==STD_INPUT_HANDLE?ERROR_NOT_SUPPORTED:ERROR_INVALID_PARAMETER);return INVALID_HANDLE_VALUE;}
    const auto handle=wit_native_process_console();
    if(!handle){SetLastError(ERROR_NOT_READY);return INVALID_HANDLE_VALUE;}
    return (HANDLE)handle;
}
extern "C" UINT WINAPI wit_native_console_codepage()
{
    // The serial console carries UTF-8 bytes; there is no mutable codepage service.
    return CP_UTF8;
}
extern "C" BOOL WINAPI wit_native_write_file(HANDLE handle,LPCVOID buffer,DWORD bytes,LPDWORD written,LPOVERLAPPED overlapped)
{
    if(overlapped){SetLastError(ERROR_NOT_SUPPORTED);return FALSE;}
    if(!written){SetLastError(ERROR_INVALID_PARAMETER);return FALSE;}
    WitConsoleWriteRequest request={WIT_CONSOLE_WRITE_VERSION,sizeof(request),(WitU64)handle,(WitU64)buffer,bytes,(WitU64)written};
    const auto status=wit_native_call(WIT_CALL_CONSOLE_WRITE,(WitU64)&request,sizeof(request),0,nullptr);
    if(status==WIT_STATUS_TOO_LARGE){SetLastError(ERROR_NOT_ENOUGH_QUOTA);return FALSE;}
    return (BOOL)wit_pal_result(status);
}
