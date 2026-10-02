// Windows reference for the actual dispatcher tuple used by CoreCLR's
// FixupDispatcherContext. Native fixture only; no managed-runtime claim.
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <cstdint>
extern "C" {
void wit_dynamic_nested_begin();void wit_dynamic_nested_inner();
void wit_dynamic_nested_fault();void wit_dynamic_nested_landing();void wit_dynamic_nested_end();
}
static RUNTIME_FUNCTION entries[2];
static DWORD64 imageBase,faultPc,landing,trace;
static bool redirect;
static CONTEXT redirected,output;
static PRUNTIME_FUNCTION CALLBACK lookup(DWORD64 pc,void*)
{
    for(auto& entry:entries)if(pc>=imageBase+entry.BeginAddress&&pc<imageBase+entry.EndAddress)return &entry;
    return nullptr;
}
static EXCEPTION_DISPOSITION __cdecl handler(EXCEPTION_RECORD* exception,void* frame,CONTEXT* fault,DISPATCHER_CONTEXT* dispatcher)
{
    if(exception->ExceptionCode!=EXCEPTION_ACCESS_VIOLATION||(DWORD64)exception->ExceptionAddress!=faultPc||dispatcher->ImageBase!=imageBase)
        return ExceptionContinueSearch;
    const bool inner=dispatcher->FunctionEntry==&entries[1];
    const bool unwinding=(exception->ExceptionFlags&EXCEPTION_UNWINDING)!=0;
    trace=trace*10+(unwinding?(inner?3:4):(inner?1:2));
    if(redirect&&inner){
        // Restore a real known context, then publish the complete new tuple.
        redirected=*fault;void* data=nullptr;DWORD64 ignored=0;
        RtlVirtualUnwind(0,imageBase,faultPc,&entries[1],&redirected,&data,&ignored,nullptr);
        dispatcher->ContextRecord=&redirected;dispatcher->ControlPc=redirected.Rip;
        dispatcher->FunctionEntry=RtlLookupFunctionEntry(redirected.Rip,&dispatcher->ImageBase,nullptr);
        CONTEXT temp=redirected;
        RtlVirtualUnwind(0,dispatcher->ImageBase,dispatcher->ControlPc,dispatcher->FunctionEntry,&temp,&data,&dispatcher->EstablisherFrame,nullptr);
        dispatcher->LanguageHandler=(PEXCEPTION_ROUTINE)(imageBase+400);
        dispatcher->HandlerData=(void*)0x1234;dispatcher->ScopeIndex=7;
        return ExceptionCollidedUnwind;
    }
    if(redirect&&((DWORD64)dispatcher->LanguageHandler!=imageBase+400||dispatcher->HandlerData!=(void*)0x1234||dispatcher->ScopeIndex!=7))ExitProcess(11);
    if(unwinding){
        if((exception->ExceptionFlags&EXCEPTION_TARGET_UNWIND)!=(inner?0U:(DWORD)EXCEPTION_TARGET_UNWIND))ExitProcess(12);
        return ExceptionContinueSearch;
    }
    if(!inner)RtlUnwindEx(frame,(void*)landing,exception,(void*)731,&output,nullptr);
    return ExceptionContinueSearch;
}
int main()
{
    auto code=(unsigned char*)VirtualAlloc(nullptr,65536,MEM_RESERVE|MEM_COMMIT,PAGE_EXECUTE_READWRITE);
    if(!code)return 1;
    const auto inner=(DWORD)((uintptr_t)wit_dynamic_nested_inner-(uintptr_t)wit_dynamic_nested_begin);
    const auto bytes=(DWORD)((uintptr_t)wit_dynamic_nested_end-(uintptr_t)wit_dynamic_nested_begin);
    if(bytes>200)return 2;
    memcpy(code,(const void*)wit_dynamic_nested_begin,bytes);
    const unsigned char info[]={25,5,2,0,5,0x32,1,0x30,0x80,1,0,0};
    memcpy(code+256,info,sizeof(info));memcpy(code+272,info,sizeof(info));
    code[384]=0x48;code[385]=0xb8;*(uint64_t*)(code+386)=(uint64_t)handler;code[394]=0xff;code[395]=0xe0;
    memcpy(code+400,code+384,12);
    imageBase=(DWORD64)code;faultPc=imageBase+(uintptr_t)wit_dynamic_nested_fault-(uintptr_t)wit_dynamic_nested_begin;
    landing=imageBase+(uintptr_t)wit_dynamic_nested_landing-(uintptr_t)wit_dynamic_nested_begin;
    entries[0]={0,inner,256};entries[1]={inner,bytes,272};
    if(!RtlInstallFunctionTableCallback(imageBase|3,imageBase,65536,lookup,nullptr,nullptr))return 3;
    if(!FlushInstructionCache(GetCurrentProcess(),code,4096))return 4;
    for(unsigned mode=0;mode<2;++mode){
        redirect=mode!=0;trace=0;
        const int value=((int(*)())code)();
        if(value!=742||trace!=1234){printf("FAIL: target mode=%u value=%d trace=%llu\n",mode,value,(unsigned long long)trace);return 5;}
    }
    if(!RtlDeleteFunctionTable((PRUNTIME_FUNCTION)(imageBase|3))||!VirtualFree(code,0,MEM_RELEASE))return 6;
    puts("PASS: Windows target unwind and CoreCLR collided dispatcher tuple (2 cases; HOSTED only)");return 0;
}
