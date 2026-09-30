#ifndef WITOS_CONTEXT_CONVERSION_H
#define WITOS_CONTEXT_CONVERSION_H
#include "pal.witos.h"
#include <string.h>
namespace WitContext {
constexpr DWORD complete=CONTEXT_FULL|CONTEXT_DEBUG_REGISTERS;
constexpr DWORD reporting=CONTEXT_EXCEPTION_REQUEST|CONTEXT_EXCEPTION_REPORTING|CONTEXT_SERVICE_ACTIVE|CONTEXT_EXCEPTION_ACTIVE;
inline bool profile(WitCpuContextInfo& info)
{
    return wit_native_call(WIT_CALL_CPU_CONTEXT_QUERY,(WitU64)&info,sizeof(info),WIT_CPU_CONTEXT_VERSION,nullptr)==WIT_STATUS_OK&&
        info.Version==WIT_CPU_CONTEXT_VERSION&&info.Size==sizeof(info)&&info.EnabledState==WIT_CPU_CONTEXT_LEGACY&&
        info.DebugPolicy==WIT_CPU_DEBUG_DISABLED&&info.LegacySaveBytes==512&&info.MxcsrMask;
}
inline bool error(DWORD code) {SetLastError(code);return false;}
inline bool flags(const CONTEXT* context)
{
    if(!context||((uintptr_t)context&15))return error(ERROR_INVALID_PARAMETER);
    if((context->ContextFlags&complete)!=complete||(context->ContextFlags&~(complete|reporting)))return error(ERROR_NOT_SUPPORTED);
    return true;
}
inline bool decode(const CONTEXT& c,WitThreadContext& w,const WitCpuContextInfo& info,bool exception=false)
{
    if((c.ContextFlags&CONTEXT_SERVICE_ACTIVE)||(!exception&&(c.ContextFlags&CONTEXT_EXCEPTION_ACTIVE)))return error(ERROR_BUSY);
    if(c.Dr0||c.Dr1||c.Dr2||c.Dr3||c.Dr6||c.Dr7||c.DebugControl||c.LastBranchToRip||c.LastBranchFromRip||c.LastExceptionToRip||c.LastExceptionFromRip||c.VectorControl)
        return error(ERROR_NOT_SUPPORTED);
    for(const auto& v:c.VectorRegister)if(v.Low||v.High)return error(ERROR_NOT_SUPPORTED);
    if(c.MxCsr!=c.FltSave.MxCsr||c.FltSave.MxCsr_Mask!=info.MxcsrMask)return error(ERROR_INVALID_PARAMETER);
    w.Rip=c.Rip;w.Rsp=c.Rsp;w.Rflags=c.EFlags;w.Cs=c.SegCs;w.Ss=c.SegSs;
    w.Rax=c.Rax;w.Rbx=c.Rbx;w.Rcx=c.Rcx;w.Rdx=c.Rdx;w.Rbp=c.Rbp;w.Rsi=c.Rsi;w.Rdi=c.Rdi;
    w.R8=c.R8;w.R9=c.R9;w.R10=c.R10;w.R11=c.R11;w.R12=c.R12;w.R13=c.R13;w.R14=c.R14;w.R15=c.R15;
    static_assert(sizeof(c.FltSave)==sizeof(w.FxState));
    memcpy(w.FxState,&c.FltSave,sizeof(w.FxState));
    return true;
}
inline void encode(const WitThreadContext& w,CONTEXT& result)
{
    result={};
    result.ContextFlags=complete|CONTEXT_EXCEPTION_REPORTING|((w.Flags&WIT_THREAD_CONTEXT_SERVICE_ACTIVE)?CONTEXT_SERVICE_ACTIVE:0)|((w.Flags&WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)?CONTEXT_EXCEPTION_ACTIVE:0);
    result.Rip=w.Rip;result.Rsp=w.Rsp;result.EFlags=(DWORD)w.Rflags;result.SegCs=(WORD)w.Cs;result.SegSs=(WORD)w.Ss;
    result.Rax=w.Rax;result.Rbx=w.Rbx;result.Rcx=w.Rcx;result.Rdx=w.Rdx;result.Rbp=w.Rbp;result.Rsi=w.Rsi;result.Rdi=w.Rdi;
    result.R8=w.R8;result.R9=w.R9;result.R10=w.R10;result.R11=w.R11;result.R12=w.R12;result.R13=w.R13;result.R14=w.R14;result.R15=w.R15;
    memcpy(&result.FltSave,w.FxState,sizeof(w.FxState));result.MxCsr=result.FltSave.MxCsr;
}
}
#endif
