#include "unwind_checked.witos.h"
#include "unwind_scope.witos.h"
extern "C" {
#include "bootstrap.h"
#include "image.h"
}
[[noreturn]] void wit_unwind_access_failure(WitUnwindFailureReason)
{
    // Never recursively invoke managed/native exception unwinding here.
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
namespace {
// Fresh compiler TLS for every thread/component; no shared gate can be held
// across a GC suspension. Publication/RO mappings precede all runtime callers.
static __declspec(thread) WitValidatedUnwindImage validated;
PEXCEPTION_ROUTINE perform(DWORD type,DWORD64 imageBase,DWORD64 pc,PRUNTIME_FUNCTION entry,CONTEXT* context,
    void** data,DWORD64* frame,KNONVOLATILE_CONTEXT_POINTERS* pointers)
{
    const WitUserImageInfo* image=wit_native_process_image();
    WitStackLeaseInfo lease;
    if(!image||image->Base!=imageBase||!context||
        WitNativeUnwindScope::Current(context->Rsp,&lease)!=WIT_STATUS_OK)wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    const WitUnwindStackRange stack={lease.StackLow,lease.StackHigh};
    PEXCEPTION_ROUTINE handler=nullptr;
    if(FAILED(validated.Initialize(image)))wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    if(FAILED(wit_checked_virtual_unwind_prevalidated(validated,&stack,type,pc,entry,context,data,frame,pointers,&handler)))
        wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    return handler;
}
}
extern "C" PEXCEPTION_ROUTINE __cdecl wit_native_rtl_virtual_unwind(DWORD type,DWORD64 imageBase,DWORD64 pc,PRUNTIME_FUNCTION entry,
    CONTEXT* context,void** data,DWORD64* frame,KNONVOLATILE_CONTEXT_POINTERS* pointers)
{
    if(!context)wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    WitStackLeaseInfo lease;
    const WitU64 status=WitNativeUnwindScope::Current(context->Rsp,&lease);
    if(status==WIT_STATUS_OK)return perform(type,imageBase,pc,entry,context,data,frame,pointers);
    if(status!=WIT_STATUS_CLOSED)wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    // An executing caller intrinsically owns its current stack lifetime after
    // return. Foreign roots require an enclosing scope; never infer them from RSP.
    WitNativeUnwindScope current(WIT_THREAD_REFERENCE_CURRENT);
    if(current.Status()!=WIT_STATUS_OK)wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    return perform(type,imageBase,pc,entry,context,data,frame,pointers);
}
