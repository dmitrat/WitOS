#include "pal.witos.h"
#include "NativeContext.h"
#include <new>
static_assert(sizeof(NATIVE_CONTEXT)==sizeof(CONTEXT)&&alignof(NATIVE_CONTEXT)==16,"Pinned AMD64 native context layout");
static_assert(offsetof(NATIVE_CONTEXT,ctx)==0,"Native context prefix");
namespace {
bool cpu_state(WitCpuContextInfo& info)
{
    return wit_native_call(WIT_CALL_CPU_CONTEXT_QUERY,(WitU64)&info,sizeof(info),WIT_CPU_CONTEXT_VERSION,nullptr)==WIT_STATUS_OK&&
        info.Version==WIT_CPU_CONTEXT_VERSION&&info.Size==sizeof(info)&&info.DebugPolicy==WIT_CPU_DEBUG_DISABLED&&info.MxcsrMask&&info.EnabledState==WIT_CPU_CONTEXT_LEGACY&&
        info.LegacySaveBytes==512&&(info.CodeSelector&3)==3&&(info.StackSelector&3)==3;
}
WitCpuContextInfo required_state()
{
    WitCpuContextInfo info;
    if(!cpu_state(info))wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return info;
}
}
UInt32_BOOL PalAreShadowStacksEnabled()
{
    return (required_state().EnabledState&WIT_CPU_CONTEXT_CET)!=0;
}
HijackFunc* PalGetHijackTarget(HijackFunc* defaultTarget)
{
    (void)required_state();
    // Exact upstream non-CET fallback. This does not perform a hijack.
    return defaultTarget;
}
void PopulateControlSegmentRegisters(CONTEXT* context)
{
    if(!context)wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    const auto info=required_state();
    context->SegCs=info.CodeSelector;context->SegSs=info.StackSelector;
}
NATIVE_CONTEXT* PalAllocateCompleteOSContext(uint8_t** contextBuffer)
{
    if(!contextBuffer){SetLastError(ERROR_INVALID_PARAMETER);return nullptr;}
    *contextBuffer=nullptr;
    WitCpuContextInfo info;
    if(!cpu_state(info)){SetLastError(ERROR_NOT_SUPPORTED);return nullptr;}
    // Allocation/initialization only. Register capture and debug-register state
    // must be supplied by the real get-context implementation, still unresolved.
    constexpr size_t alignment=alignof(NATIVE_CONTEXT);
    auto buffer=new(std::nothrow) uint8_t[sizeof(NATIVE_CONTEXT)+alignment-1];
    if(!buffer){SetLastError(ERROR_NOT_ENOUGH_MEMORY);return nullptr;}
    const uintptr_t aligned=((uintptr_t)buffer+alignment-1)&~(uintptr_t)(alignment-1);
    auto context=(NATIVE_CONTEXT*)aligned;
    for(size_t i=0;i<sizeof(*context);++i)((uint8_t*)context)[i]=0;
    context->ctx.ContextFlags=CONTEXT_FULL|CONTEXT_DEBUG_REGISTERS;
    *contextBuffer=buffer;
    return context;
}
