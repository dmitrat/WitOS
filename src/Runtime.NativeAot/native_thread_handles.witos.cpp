#include "pal.witos.h"

extern "C" DWORD WINAPI wit_native_thread_id()
{
    WitU64 identity = 0;
    if (wit_native_call(WIT_CALL_THREAD_NATIVE_ID, 0, 0, 0, &identity) != WIT_STATUS_OK ||
        !identity ||
        identity > UINT32_MAX) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return (DWORD)identity;
}

extern "C" HANDLE WINAPI wit_native_current_process()
{
    return (HANDLE)(intptr_t)-1;
}

extern "C" HANDLE WINAPI wit_native_current_thread()
{
    return (HANDLE)(intptr_t)-2;
}

extern "C" BOOL WINAPI wit_native_duplicate_handle(HANDLE sourceProcess, HANDLE source, HANDLE targetProcess,
    LPHANDLE output, DWORD access, BOOL inherit, DWORD options)
{
    if (sourceProcess != (HANDLE)(intptr_t)-1 || targetProcess != (HANDLE)(intptr_t)-1) {
        SetLastError(ERROR_INVALID_HANDLE);
        return FALSE;
    }
    if (inherit || (options & ~DUPLICATE_SAME_ACCESS)) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return FALSE;
    }
    if (!output) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    WitU64 rights = 0;
    if (!(options & DUPLICATE_SAME_ACCESS)) {
        const DWORD supported = SYNCHRONIZE |
            THREAD_QUERY_INFORMATION |
            THREAD_QUERY_LIMITED_INFORMATION |
            THREAD_GET_CONTEXT |
            THREAD_SET_CONTEXT |
            THREAD_SUSPEND_RESUME;
        if (access & ~supported) {
            SetLastError(ERROR_NOT_SUPPORTED);
            return FALSE;
        }
        if (access & (THREAD_QUERY_INFORMATION | THREAD_QUERY_LIMITED_INFORMATION)) {
            rights |= WIT_THREAD_REFERENCE_QUERY;
        }
        if (access & SYNCHRONIZE) {
            rights |= WIT_THREAD_REFERENCE_WAIT;
        }
        if (access & THREAD_GET_CONTEXT) {
            rights |= WIT_THREAD_REFERENCE_GET_CONTEXT;
        }
        if (access & THREAD_SUSPEND_RESUME) {
            rights |= WIT_THREAD_REFERENCE_SUSPEND_RESUME;
        }
        if (access & THREAD_SET_CONTEXT) {
            rights |= WIT_THREAD_REFERENCE_SET_CONTEXT;
        }
        if (!rights) {
            SetLastError(ERROR_NOT_SUPPORTED);
            return FALSE;
        }
    }
    WitU64 copied = 0;
    const auto status =
        wit_native_call(WIT_CALL_THREAD_REFERENCE_DUPLICATE, (uintptr_t)source, (uintptr_t)output, rights, &copied);
    if (status == WIT_STATUS_OK && copied != sizeof(HANDLE)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return (BOOL)wit_pal_result(status);
}

extern "C" int WINAPI wit_native_thread_priority(HANDLE handle)
{
    WitThreadReferenceInfo info;
    WitU64 copied = 0;
    if (handle == (HANDLE)(intptr_t)-2) {
        WitUserThreadInfo current;
        if (wit_native_call(WIT_CALL_THREAD_QUERY, (uintptr_t)&current, sizeof(current), WIT_THREAD_INFO_VERSION,
                &copied) != WIT_STATUS_OK ||
            copied != sizeof(current) ||
            !current.ThreadId) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        return THREAD_PRIORITY_NORMAL;
    }
    const auto status =
        wit_native_call(WIT_CALL_THREAD_REFERENCE_QUERY, (uintptr_t)handle, (uintptr_t)&info, sizeof(info), &copied);
    if (!wit_pal_result(status)) {
        return THREAD_PRIORITY_ERROR_RETURN;
    }
    if (copied != sizeof(info) || info.Version != WIT_THREAD_REFERENCE_VERSION || info.Size != sizeof(info)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    // Every thread in the supported scheduler has the normal fixed priority.
    return THREAD_PRIORITY_NORMAL;
}
