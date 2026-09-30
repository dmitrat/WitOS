#include "pal.witos.h"
static_assert(WIT_THREAD_SUSPEND_MAX==MAXIMUM_SUSPEND_COUNT,"Suspend count contract");
static DWORD change(HANDLE thread,bool resume)
{
    WitU64 previous=0;
    const auto status=wit_native_call(resume?WIT_CALL_THREAD_RESUME:WIT_CALL_THREAD_SUSPEND,(WitU64)thread,0,0,&previous);
    if(status==WIT_STATUS_TOO_LARGE){SetLastError(ERROR_SIGNAL_REFUSED);return (DWORD)-1;}
    if(!wit_pal_result(status))return (DWORD)-1;
    if(previous>WIT_THREAD_SUSPEND_MAX)wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return (DWORD)previous;
}
extern "C" DWORD WINAPI wit_native_suspend_thread(HANDLE thread) { return change(thread,false); }
extern "C" DWORD WINAPI wit_native_resume_thread(HANDLE thread) { return change(thread,true); }
