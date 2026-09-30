#include "pal.witos.h"
#include "tls.h"
#include "native_security.h"
#include "protocol.h"
#include <string.h>
#include <errno.h>
#include "seh_validation.witos.h"
extern "C" void wit_seh_hardware_fault();
static unsigned used,finallyCount,handlerCount;
static int logItems[16];
static bool abnormal;
constexpr DWORD TestCode=0xE0435678;
static void mark(int value){if(used<16)logItems[used++]=value;else wit_native_fail_fast(4501);}
static LONG CALLBACK observe(EXCEPTION_POINTERS*){return EXCEPTION_CONTINUE_SEARCH;}
static LONG select(EXCEPTION_POINTERS* info,LONG action)
{
    mark(1);return info->ExceptionRecord->ExceptionCode==TestCode?action:EXCEPTION_CONTINUE_SEARCH;
}
static __declspec(noinline) int catchall()
{
    __try {RaiseException(TestCode,0,0,nullptr);return -1;}
    __except(EXCEPTION_EXECUTE_HANDLER){++handlerCount;return GetExceptionCode()==TestCode?42:-2;}
}
static __declspec(noinline) int nested()
{
    volatile int local=73;
    __try {
        __try {RaiseException(TestCode,0,0,nullptr);local=-100;}
        __finally {++finallyCount;abnormal=AbnormalTermination()!=FALSE;local+=7;mark(2);}
    } __except(select(GetExceptionInformation(),local==73?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH)) {
        ++handlerCount;mark(3);return local==80&&GetExceptionCode()==TestCode?42:-2;
    }
    return -1;
}
static __declspec(noinline) void inner()
{
    __try {RaiseException(TestCode,0,0,nullptr);}
    __finally {++finallyCount;abnormal=AbnormalTermination()!=FALSE;mark(2);}
}
static __declspec(noinline) int outer()
{
    volatile int local=0x1234;
    __try {inner();return -1;}
    __except(select(GetExceptionInformation(),EXCEPTION_EXECUTE_HANDLER)) {
        ++handlerCount;mark(3);return local==0x1234&&GetExceptionCode()==TestCode?42:-2;
    }
}
static __declspec(noinline) int continuation()
{
    __try {RaiseException(TestCode,0,0,nullptr);mark(4);}
    __except(select(GetExceptionInformation(),EXCEPTION_CONTINUE_EXECUTION)){return -1;}
    return 42;
}
static __declspec(noinline) int normal_finally()
{
    volatile int value=1;
    __try {++value;}
    __finally {abnormal=AbnormalTermination()!=FALSE;++finallyCount;value+=2;}
    return value==4?42:-1;
}
static __declspec(noinline) int hardware_catch()
{
    __try {wit_seh_hardware_fault();return -1;}
    __except(GetExceptionCode()==EXCEPTION_ILLEGAL_INSTRUCTION?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH){++handlerCount;return 42;}
}
static __declspec(noinline) int local_return()
{
    __try {return 42;}
    __finally {++finallyCount;abnormal=AbnormalTermination()!=FALSE;}
}
static __declspec(noinline) int local_leave()
{
    __try {__leave;}
    __finally {++finallyCount;abnormal=AbnormalTermination()!=FALSE;}
    return 42;
}
static __declspec(noinline) int local_goto()
{
    __try {goto target;}
    __finally {++finallyCount;abnormal=AbnormalTermination()!=FALSE;}
 target:return 42;
}
static __declspec(noinline) int local_nested_return()
{
    __try {
        __try {return 42;}
        __finally {++finallyCount;abnormal=AbnormalTermination()!=FALSE;mark(7);}
    } __finally {++finallyCount;abnormal=abnormal&&(AbnormalTermination()!=FALSE);mark(8);}
}
static unsigned innerCaught;
static LONG nested_filter(EXCEPTION_POINTERS* pointers)
{
    if(pointers->ExceptionRecord->ExceptionCode!=TestCode)return EXCEPTION_CONTINUE_SEARCH;
    mark(10);
    __try {RaiseException(TestCode+1,0,0,nullptr);}
    __except(GetExceptionCode()==TestCode+1?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH){++innerCaught;mark(11);}
    mark(12);return EXCEPTION_EXECUTE_HANDLER;
}
static __declspec(noinline) int nested_filter_case()
{
    __try {RaiseException(TestCode,0,0,nullptr);}
    __except(nested_filter(GetExceptionInformation())){++handlerCount;mark(13);return 42;}
    return -1;
}
static __declspec(noinline) int nested_finally_case()
{
    __try {
        __try {RaiseException(TestCode,0,0,nullptr);}
        __finally {
            ++finallyCount;mark(10);
            __try {RaiseException(TestCode+1,0,0,nullptr);}
            __except(GetExceptionCode()==TestCode+1?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH){++innerCaught;mark(11);}
            mark(12);
        }
    } __except(GetExceptionCode()==TestCode?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH){++handlerCount;mark(13);return 42;}
    return -1;
}
static LONG escaping_filter(EXCEPTION_POINTERS* pointers)
{
    if(pointers->ExceptionRecord->ExceptionCode!=TestCode)return EXCEPTION_CONTINUE_SEARCH;
    mark(20);RaiseException((TestCode+1),0,0,nullptr);mark(99);
    return EXCEPTION_EXECUTE_HANDLER;
}
static __declspec(noinline) int escaping_filter_case()
{
    __try {
        __try {
            __try {RaiseException(TestCode,0,0,nullptr);}
            __finally {++finallyCount;mark(22);}
        } __except(escaping_filter(GetExceptionInformation())) {mark(99);}
    } __except(GetExceptionCode()==(TestCode+1)?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH) {
        ++handlerCount;mark(23);return 42;
    }
    return -1;
}
static __declspec(noinline) int collided_finally_case()
{
    __try {
        __try {
            __try {RaiseException(TestCode,0,0,nullptr);}
            __finally {++finallyCount;mark(30);RaiseException((TestCode+1),0,0,nullptr);mark(99);}
        } __finally {++finallyCount;mark(31);}
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        ++handlerCount;mark(32);return GetExceptionCode()==(TestCode+1)?42:-2;
    }
    return -1;
}

static __declspec(noinline) int local_return_raises()
{
    __try {return -1;}
    __finally {++finallyCount;mark(40);RaiseException((TestCode+1),0,0,nullptr);mark(99);}
}
static __declspec(noinline) int interrupted_local_case()
{
    __try {local_return_raises();return -2;}
    __except(GetExceptionCode()==(TestCode+1)?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH) {
        ++handlerCount;mark(41);return 42;
    }
}


static __declspec(noinline) int repeated_collision_case()
{
    __try {
        __try {
            __try {
                __try {RaiseException(TestCode,0,0,nullptr);}
                __finally {++finallyCount;mark(50);RaiseException((TestCode+1),0,0,nullptr);mark(99);}
            } __finally {++finallyCount;mark(51);RaiseException((TestCode+2),0,0,nullptr);mark(99);}
        } __finally {++finallyCount;mark(52);}
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        ++handlerCount;mark(53);return GetExceptionCode()==(TestCode+2)?42:-2;
    }
    return -1;
}

static __declspec(noinline) int retained_collision_case()
{
    __try {
        __try {RaiseException(TestCode,0,0,nullptr);}
        __finally {
            ++finallyCount;mark(60);
            if(collided_finally_case()!=42)mark(99);
            mark(61);
        }
    } __except(GetExceptionCode()==TestCode?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH) {
        ++handlerCount;mark(62);return 42;
    }
    return -1;
}

static unsigned transferPhase;
static WitU64 transferTokens[6];
extern "C" WitU64 wit_exception_transfer_probe(WitThreadContext*,WitU64,WitU64);
extern "C" void wit_test_exception_transfer_prepare(WitThreadContext* captured)
{
    WitThreadContext context=*captured;
    const unsigned first=transferPhase?transferPhase+3:0,last=transferPhase?first+1:4;
    for(unsigned i=first;i<last;++i){
        if(wit_native_call(WIT_CALL_EXCEPTION_BEGIN,(WitU64)&context,sizeof(context),TestCode+i,&transferTokens[i])!=WIT_STATUS_OK)
            wit_native_fail_fast(4520);
        context.Flags|=WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE;
    }
    const WitU64 token=transferTokens[last-1];
    WitUserExceptionInfo before,after;
    if(wit_native_call(WIT_CALL_EXCEPTION_QUERY,token,(WitU64)&before,sizeof(before),nullptr)!=WIT_STATUS_OK)wit_native_fail_fast(4521);
    WitUserExceptionTransfer request={};request.Version=WIT_EXCEPTION_TRANSFER_VERSION;request.Size=sizeof(request);
    request.RetireThroughToken=transferPhase==0?transferTokens[2]:transferPhase==1?transferTokens[1]:transferTokens[0];
    request.Context=context;request.Context.Rax=42;request.Context.Rflags=0x247;
    if(!transferPhase){
        for(unsigned i=0;i<13;++i){
            auto invalid=request;WitU64 selected=token,size=sizeof(invalid),address=(WitU64)&invalid,expected=WIT_STATUS_INVALID_ARGUMENT;
            switch(i){
            case 0:selected=transferTokens[0];expected=WIT_STATUS_BAD_HANDLE;break;
            case 1:invalid.RetireThroughToken=0;expected=WIT_STATUS_BAD_HANDLE;break;
            case 2:invalid.RetireThroughToken=~0ULL;expected=WIT_STATUS_BAD_HANDLE;break;
            case 3:++invalid.Version;expected=WIT_STATUS_UNSUPPORTED;break;
            case 4:--invalid.Size;break;
            case 5:--size;break;
            case 6:address=context.StackLow-size/2;expected=WIT_STATUS_BAD_ADDRESS;break;
            case 7:++invalid.Context.ThreadId;break;
            case 8:invalid.Context.Cs=8;break;
            case 9:invalid.Context.Rflags|=0x3000;break;
            case 10:invalid.Context.Rsp=context.StackLow-1;expected=WIT_STATUS_BAD_ADDRESS;break;
            case 11:invalid.Context.Rip=context.StackLow;expected=WIT_STATUS_BAD_ADDRESS;break;
            case 12:invalid.Context.FxState[511]=1;break;
            }
            if(wit_native_call(WIT_CALL_EXCEPTION_UNWIND,selected,address,size,nullptr)!=expected||
               wit_native_call(WIT_CALL_EXCEPTION_QUERY,token,(WitU64)&after,sizeof(after),nullptr)!=WIT_STATUS_OK||memcmp(&before,&after,sizeof(before)))wit_native_fail_fast(4522+i);
        }
        WitStackLeaseInfo lease;
        if(wit_native_call(WIT_CALL_STACK_LEASE_ACQUIRE,WIT_THREAD_REFERENCE_CURRENT,(WitU64)&lease,sizeof(lease),nullptr)!=WIT_STATUS_OK||
           wit_native_call(WIT_CALL_EXCEPTION_UNWIND,token,(WitU64)&request,sizeof(request),nullptr)!=WIT_STATUS_BUSY||
           wit_native_call(WIT_CALL_EXCEPTION_QUERY,token,(WitU64)&after,sizeof(after),nullptr)!=WIT_STATUS_OK||memcmp(&before,&after,sizeof(before))||
           wit_native_call(WIT_CALL_STACK_LEASE_RELEASE,lease.Token,0,0,nullptr)!=WIT_STATUS_OK)wit_native_fail_fast(4536);
    }
    wit_native_call(WIT_CALL_EXCEPTION_UNWIND,token,(WitU64)&request,sizeof(request),nullptr);
    wit_native_fail_fast(4537);
}
static int transfer_case()
{
    for(transferPhase=0;transferPhase<3;++transferPhase){
        WitThreadContext captured,current;WitUserExceptionInfo pending;
        if(wit_exception_transfer_probe(&captured,WIT_THREAD_REFERENCE_CURRENT,sizeof(captured))!=42)return -1;
        if(wit_native_call(WIT_CALL_THREAD_CONTEXT_GET,WIT_THREAD_REFERENCE_CURRENT,(WitU64)&current,sizeof(current),nullptr)!=WIT_STATUS_OK||
           ((current.Flags&WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)!=0)!=(transferPhase<2))return -2;
        const WitU64 retained=transferPhase==0?transferTokens[1]:transferPhase==1?transferTokens[0]:0;
        for(unsigned i=0;i<(transferPhase?transferPhase+4:4);++i){
            const WitU64 expected=transferTokens[i]==retained?WIT_STATUS_OK:WIT_STATUS_BAD_HANDLE;
            if(wit_native_call(WIT_CALL_EXCEPTION_QUERY,transferTokens[i],(WitU64)&pending,sizeof(pending),nullptr)!=expected)return -3;
        }
    }
    // A fresh real compiler dispatch must work after the entire raw chain retires.
    return catchall();
}
extern "C" int wit_seh_gs_frame(unsigned);
extern "C" int wit_seh_gs_aligned_frame(unsigned);
extern "C" const WitU64 wit_seh_gs_aligned_cookie_delta;
extern "C" const WitU64 wit_seh_gs_cookie_delta;
extern "C" void wit_seh_gs_action(volatile unsigned char* buffer,unsigned mode)
{
    if(mode==157||mode==164)*(volatile WitU64*)(buffer+(mode>=163?wit_seh_gs_aligned_cookie_delta:wit_seh_gs_cookie_delta))^=1;
    if(mode!=159)RaiseException(TestCode,0,0,nullptr);
}
extern "C" LONG wit_seh_gs_select(EXCEPTION_POINTERS* pointers,volatile unsigned char* buffer,unsigned mode)
{
    ++((WitU64*)WIT_GC_INFO_REPORT)[7];
    if(mode==158||mode==165)*(volatile WitU64*)(buffer+(mode>=163?wit_seh_gs_aligned_cookie_delta:wit_seh_gs_cookie_delta))^=1;
    return pointers->ExceptionRecord->ExceptionCode!=TestCode?EXCEPTION_CONTINUE_SEARCH:mode==156?EXCEPTION_CONTINUE_EXECUTION:EXCEPTION_EXECUTE_HANDLER;
}
extern "C" void wit_seh_gs_finally(int value)
{auto report=(WitU64*)WIT_GC_INFO_REPORT;++report[8];report[10]=value!=0;}
extern "C" void wit_seh_gs_caught(){++((WitU64*)WIT_GC_INFO_REPORT)[9];}
extern "C" EXCEPTION_DISPOSITION __cdecl __GSHandlerCheck_SEH(EXCEPTION_RECORD*,void*,CONTEXT*,DISPATCHER_CONTEXT*);
static int gs_invalid_dispatch(WitU64 mode)
{
    const auto image=wit_native_process_image();
    const auto entries=(RUNTIME_FUNCTION*)(image->Base+image->UnwindRva);
    RUNTIME_FUNCTION* entry=nullptr;
    const WitU64 pc=(WitU64)&wit_seh_gs_frame-image->Base;
    for(WitU32 i=0;i<image->UnwindSize/sizeof(*entries);++i)if(pc>=entries[i].BeginAddress&&pc<entries[i].EndAddress){entry=&entries[i];break;}
    if(!entry)return -1;
    WitUnwindRecord record;
    if(wit_unwind_validate_function(image,(WitU32)((WitU64)entry-image->Base),&record)!=WitUnwindValid)return -2;
    EXCEPTION_RECORD exception={};CONTEXT context={};DISPATCHER_CONTEXT dispatcher={};
    dispatcher.ImageBase=image->Base;dispatcher.FunctionEntry=entry;dispatcher.EstablisherFrame=(WitU64)&context;
    dispatcher.LanguageHandler=(PEXCEPTION_ROUTINE)&__GSHandlerCheck_SEH;dispatcher.HandlerData=(void*)(image->Base+record.HandlerDataRva);
    RUNTIME_FUNCTION copy=*entry;
    if(mode==160)dispatcher.HandlerData=(WitU8*)dispatcher.HandlerData+4;
    if(mode==161)dispatcher.FunctionEntry=&copy;
    if(mode==162)dispatcher.LanguageHandler=nullptr;
    __GSHandlerCheck_SEH(&exception,&context,&context,&dispatcher);
    return -3;
}
extern "C" unsigned wit_gp_fault(unsigned);
extern "C" void wit_gp_resume();
extern "C" void wit_gp_unsupported();
static unsigned gpIndex;
static LONG CALLBACK gp_handler(EXCEPTION_POINTERS* pointers)
{
    auto report=(WitU64*)WIT_GC_INFO_REPORT;++report[11];
    const auto& record=*pointers->ExceptionRecord;const bool privileged=gpIndex==1||gpIndex==2;
    if(record.ExceptionCode!=(privileged?EXCEPTION_PRIV_INSTRUCTION:EXCEPTION_ACCESS_VIOLATION)||record.ExceptionFlags||
       record.NumberParameters!=(privileged?0U:2U)||(!privileged&&(record.ExceptionInformation[0]||record.ExceptionInformation[1]!=~(ULONG_PTR)0))||
       (WitU64)record.ExceptionAddress!=pointers->ContextRecord->Rip||!(pointers->ContextRecord->ContextFlags&CONTEXT_EXCEPTION_ACTIVE))wit_native_fail_fast(4560);
    pointers->ContextRecord->Rip=(WitU64)&wit_gp_resume;return EXCEPTION_CONTINUE_EXECUTION;
}
static int gp_case(WitU64 mode)
{
    const auto handler=AddVectoredExceptionHandler(1,gp_handler);if(!handler)return -1;
    if(mode==167){gpIndex=6;((WitU64*)WIT_GC_INFO_REPORT)[12]=(WitU64)&wit_gp_unsupported;wit_gp_fault(6);return -2;}
    SetLastError(0x72345678);errno=419;
    for(gpIndex=0;gpIndex<6;++gpIndex)if(wit_gp_fault(gpIndex)!=1)return -3;
    if(!RemoveVectoredExceptionHandler(handler)||GetLastError()!=0x72345678||errno!=419||((WitU64*)WIT_GC_INFO_REPORT)[11]!=6)return -4;
    __try {wit_gp_fault(3);return -5;}
    __except(GetExceptionCode()==EXCEPTION_ACCESS_VIOLATION?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH) {
        ++((WitU64*)WIT_GC_INFO_REPORT)[13];return 42;
    }
}
extern "C" WitU64 wit_test_seh(const WitUserStartup* startup,WitU64 mode)
{
    auto report=(WitU64*)WIT_GC_INFO_REPORT;report[0]=mode;report[1]=562949953421312ULL;report[2]=0;
    wit_native_security_initialize_system();wit_native_process_image_initialize(startup);wit_native_tls_initialize(startup);
    if(!AddVectoredExceptionHandler(1,observe))return 4502;
    used=finallyCount=handlerCount=innerCaught=0;abnormal=false;
    int result=0;
    if(mode==137)result=catchall();
    if(mode==138)result=nested();
    if(mode==139)result=outer();
    if(mode==140)result=continuation();
    if(mode==141)result=normal_finally();
    if(mode==142)result=hardware_catch();
    if(mode==143)result=local_return();
    if(mode==144)result=local_leave();
    if(mode==145)result=local_goto();
    if(mode==146)result=local_nested_return();
    if(mode==147)result=nested_filter_case();
    if(mode==148)result=nested_finally_case();
    if(mode==149)result=transfer_case();
    if(mode==150)result=escaping_filter_case();
    if(mode==151)result=collided_finally_case();
    if(mode==152)result=interrupted_local_case();
    if(mode==153)result=repeated_collision_case();
    if(mode==154)result=retained_collision_case();
    if(mode>=155&&mode<=159)result=wit_seh_gs_frame((unsigned)mode);
    if(mode>=160&&mode<=162)result=gs_invalid_dispatch(mode);
    if(mode>=163&&mode<=165)result=wit_seh_gs_aligned_frame((unsigned)mode);
    if(mode==166||mode==167)result=gp_case(mode);
    report[3]=used;report[4]=finallyCount;report[5]=handlerCount;report[6]=abnormal;
    if(result!=42)return 4503;
    if((mode==137||mode==142)&&handlerCount!=1)return 4504;
    if((mode==138||mode==139)&&(used!=3||logItems[0]!=1||logItems[1]!=2||logItems[2]!=3||finallyCount!=1||handlerCount!=1||!abnormal))return 4505;
    if(mode==140&&(used!=2||logItems[0]!=1||logItems[1]!=4||finallyCount||handlerCount))return 4506;
    if(mode==141&&(finallyCount!=1||abnormal))return 4507;
    if((mode==143||mode==145)&&(finallyCount!=1||!abnormal))return 4509;
    if(mode==144&&(finallyCount!=1||abnormal))return 4510;
    if(mode==146&&(finallyCount!=2||!abnormal||used!=2||logItems[0]!=7||logItems[1]!=8))return 4511;
    if((mode==147||mode==148)&&(innerCaught!=1||handlerCount!=1||used!=4||logItems[0]!=10||logItems[1]!=11||logItems[2]!=12||logItems[3]!=13||finallyCount!=(mode==148?1U:0U)))return 4512;
    if(mode==150&&(handlerCount!=1||finallyCount!=1||used!=3||logItems[0]!=20||logItems[1]!=22||logItems[2]!=23))return 4540;
    if(mode==151&&(handlerCount!=1||finallyCount!=2||used!=3||logItems[0]!=30||logItems[1]!=31||logItems[2]!=32))return 4541;
    if(mode==152&&(handlerCount!=1||finallyCount!=1||used!=2||logItems[0]!=40||logItems[1]!=41))return 4542;
    if(mode==153&&(handlerCount!=1||finallyCount!=3||used!=4||logItems[0]!=50||logItems[1]!=51||logItems[2]!=52||logItems[3]!=53))return 4544;
    if(mode==154&&(handlerCount!=2||finallyCount!=3||used!=6||logItems[0]!=60||logItems[1]!=30||logItems[2]!=31||logItems[3]!=32||logItems[4]!=61||logItems[5]!=62))return 4545;
    if(((mode>=155&&mode<=159)||(mode>=163&&mode<=165))&&(report[7]!=(mode==159?0U:1U)||report[8]!=1||report[9]!=((mode==155||mode==163)?1U:0U)||report[10]!=((mode==155||mode==163)?1U:0U)))return 4546;
    if(mode>=150&&catchall()!=42)return 4543;
    WitThreadContext context;
    if(wit_native_call(WIT_CALL_THREAD_CONTEXT_GET,WIT_THREAD_REFERENCE_CURRENT,(WitU64)&context,sizeof(context),nullptr)!=WIT_STATUS_OK||
        (context.Flags&WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE))return 4508;
    report[2]=1;return WIT_TEST_EXIT_CODE;
}
