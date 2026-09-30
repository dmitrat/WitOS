#include <windows.h>
#include <stdio.h>
#include "exception_classification.h"
extern "C" unsigned wit_gp_fault(unsigned);
extern "C" void wit_gp_resume();
extern "C" void wit_gp_end();
static EXCEPTION_RECORD observed;
static unsigned calls;
static bool matchingAddress;
static LONG CALLBACK capture(EXCEPTION_POINTERS* pointers)
{
    const auto pc=pointers->ContextRecord->Rip;
    if(pc<(DWORD64)&wit_gp_fault||pc>=(DWORD64)&wit_gp_end)return EXCEPTION_CONTINUE_SEARCH;
    observed=*pointers->ExceptionRecord;++calls;
    matchingAddress=(DWORD64)observed.ExceptionAddress==pc;
    pointers->ContextRecord->Rip=(DWORD64)&wit_gp_resume;
    return EXCEPTION_CONTINUE_EXECUTION;
}
int main()
{
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    const auto handler=AddVectoredExceptionHandler(1,capture);if(!handler)return 1;
    for(unsigned mode=0;mode<6;++mode){
        calls=0;observed={};matchingAddress=false;
        if(wit_gp_fault(mode)!=1||calls!=1||!matchingAddress||observed.NumberParameters>2)return 2;
        const bool privileged=mode==1||mode==2;
        if(observed.ExceptionFlags||observed.ExceptionCode!=(privileged?EXCEPTION_PRIV_INSTRUCTION:EXCEPTION_ACCESS_VIOLATION)||
           observed.NumberParameters!=(privileged?0U:2U)||(!privileged&&(observed.ExceptionInformation[0]||observed.ExceptionInformation[1]!=~(ULONG_PTR)0)))return 4;
        const auto actual=wit_x64_classify_gp((const WitU8*)observed.ExceptionAddress,15,mode==0?0xFFF8:0);
        if(actual!=(privileged?WIT_GP_PRIVILEGED:WIT_GP_ACCESS_UNKNOWN))return 5;
        printf("GP mode=%u code=%08lx flags=%08lx count=%lu p0=%016llx p1=%016llx\n",mode,observed.ExceptionCode,observed.ExceptionFlags,observed.NumberParameters,
            (unsigned long long)observed.ExceptionInformation[0],(unsigned long long)observed.ExceptionInformation[1]);
    }
    if(!RemoveVectoredExceptionHandler(handler))return 3;
    auto memory=(BYTE*)VirtualAlloc(nullptr,8192,MEM_RESERVE,PAGE_NOACCESS);
    if(!memory||!VirtualAlloc(memory,4096,MEM_COMMIT,PAGE_READWRITE))return 6;
    auto last=memory+4095;*last=0x0F;
    if(wit_x64_classify_gp(last,1,0)!=WIT_GP_UNSUPPORTED)return 7;
    *last=0x48;if(wit_x64_classify_gp(last,1,0)!=WIT_GP_UNSUPPORTED)return 8;
    *last=0xFA;if(wit_x64_classify_gp(last,1,0)!=WIT_GP_PRIVILEGED||wit_x64_classify_gp(last,1,1)!=WIT_GP_UNSUPPORTED)return 9;
    const WitU8 unsupported[]={0x0F,0xAE,0x00},regOnly[]={0x48,0x8B,0xC0};
    if(wit_x64_classify_gp(unsupported,3,0)!=WIT_GP_UNSUPPORTED||wit_x64_classify_gp(regOnly,3,0)!=WIT_GP_UNSUPPORTED||
       wit_x64_classify_gp(last,0,0)!=WIT_GP_UNSUPPORTED||wit_x64_classify_gp(last,16,0)!=WIT_GP_UNSUPPORTED)return 10;
    if(!VirtualFree(memory,0,MEM_RELEASE))return 11;
    puts("PASS: 6 Windows x64 GP translations and bounded decoder (HOSTED only)");return 0;
}
