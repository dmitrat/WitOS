#include "pal.witos.h"
#include "NativeContext.h"
#include "context_conversion.witos.h"
using namespace WitContext;
bool PalGetCompleteThreadContext(HANDLE handle,NATIVE_CONTEXT* output)
{
    if(!flags(output?&output->ctx:nullptr))return false;
    WitCpuContextInfo info;if(!profile(info))return error(ERROR_NOT_SUPPORTED);
    WitThreadContext w;
    const auto status=wit_native_call(WIT_CALL_THREAD_CONTEXT_GET,(WitU64)handle,(WitU64)&w,sizeof(w),nullptr);
    if(!wit_pal_result(status))return false;
    if(!(w.Flags&WIT_THREAD_CONTEXT_SUSPENDED))return error(ERROR_BUSY);
    CONTEXT result;encode(w,result);
    output->ctx=result;return true;
}
bool PalSetThreadContext(HANDLE handle,NATIVE_CONTEXT* input)
{
    if(!flags(input?&input->ctx:nullptr))return false;
    WitCpuContextInfo info;if(!profile(info))return error(ERROR_NOT_SUPPORTED);
    WitThreadContext w;
    const auto status=wit_native_call(WIT_CALL_THREAD_CONTEXT_METADATA,(WitU64)handle,(WitU64)&w,sizeof(w),nullptr);
    if(!wit_pal_result(status))return false;
    if(!(w.Flags&WIT_THREAD_CONTEXT_SUSPENDED)||(w.Flags&WIT_THREAD_CONTEXT_SERVICE_ACTIVE)||w.State!=WIT_THREAD_CONTEXT_READY)return error(ERROR_BUSY);
    if(!decode(input->ctx,w,info))return false;
    return wit_pal_result(wit_native_call(WIT_CALL_THREAD_CONTEXT_SET,(WitU64)handle,(WitU64)&w,sizeof(w),nullptr))!=0;
}
void PalRestoreContext(NATIVE_CONTEXT* input)
{
    if(!flags(input?&input->ctx:nullptr))wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    WitCpuContextInfo info;WitThreadContext w;
    if(!profile(info)){SetLastError(ERROR_NOT_SUPPORTED);wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);}
    const auto metadata=wit_native_call(WIT_CALL_THREAD_CONTEXT_METADATA,WIT_THREAD_REFERENCE_CURRENT,(WitU64)&w,sizeof(w),nullptr);
    if(metadata!=WIT_STATUS_OK){wit_pal_set_status(metadata);wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);}
    if(!decode(input->ctx,w,info))wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    const auto status=wit_native_call(WIT_CALL_THREAD_CONTEXT_RESTORE,(WitU64)&w,sizeof(w),WIT_THREAD_CONTEXT_VERSION,nullptr);
    if(status==WIT_STATUS_OK)SetLastError(ERROR_INVALID_STATE);else wit_pal_set_status(status);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT); // Successful restore cannot return.
}
uintptr_t GetSSP(CONTEXT* context)
{
    WitCpuContextInfo info;
    if(!context||((context->ContextFlags&CONTEXT_XSTATE)==CONTEXT_XSTATE)||!profile(info))wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return 0; // No CET record exists in the checked legacy context profile.
}
void SetSSP(CONTEXT* context,uintptr_t ssp)
{
    WitCpuContextInfo info;
    if(!context||ssp||((context->ContextFlags&CONTEXT_XSTATE)==CONTEXT_XSTATE)||!profile(info))wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    // Setting an absent, disabled shadow-stack pointer to zero changes no state.
}
