#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "unwind_scope.witos.h"
#include "protocol.h"
#include <stddef.h>
#include <string.h>
#include <errno.h>
static_assert(sizeof(CONTEXT)==1232&&offsetof(CONTEXT,ContextFlags)==48&&offsetof(CONTEXT,Rip)==248&&offsetof(CONTEXT,Rsp)==152&&offsetof(CONTEXT,FltSave)==256);
static_assert(offsetof(CONTEXT,Rbx)==144&&offsetof(CONTEXT,Rbp)==160&&offsetof(CONTEXT,Rsi)==168&&offsetof(CONTEXT,Rdi)==176&&offsetof(CONTEXT,R12)==216&&offsetof(CONTEXT,R15)==240);
extern "C" {
void unwind_simple();void unwind_simple_push();void unwind_simple_body();void unwind_simple_epilog();void unwind_simple_pop();void unwind_simple_ret();
void unwind_frame_body();void unwind_frame_epilog();void unwind_large_body();void unwind_primary_body();void unwind_child();
void unwind_handled_body();EXCEPTION_ROUTINE unwind_handler;void unwind_v2_body();void unwind_v2_epilog();void unwind_v2_pop();void unwind_v2_ret();
WitU64 wit_unwind_protected_frame();WitU64 wit_test_unwind_slot();
PEXCEPTION_ROUTINE wit_test_unwind_direct(DWORD,DWORD64,DWORD64,PRUNTIME_FUNCTION,CONTEXT*,void**,DWORD64*,KNONVOLATILE_CONTEXT_POINTERS*);
PEXCEPTION_ROUTINE wit_test_unwind_import(DWORD,DWORD64,DWORD64,PRUNTIME_FUNCTION,CONTEXT*,void**,DWORD64*,KNONVOLATILE_CONTEXT_POINTERS*);
EXCEPTION_DISPOSITION __cdecl __GSHandlerCheck(EXCEPTION_RECORD*,void*,CONTEXT*,DISPATCHER_CONTEXT*);
}
extern "C" { WitU64 (*wit_test_gs_override)(CONTEXT*,volatile char*)=nullptr; }
static const WitUserImageInfo* image;
static WitU64 mode;
static unsigned cases;
static CONTEXT* volatile foreignContext;
static volatile HANDLE foreignReference;
static HANDLE foreignEvent;
static PRUNTIME_FUNCTION lookup(WitU64 pc)
{
    if(pc<image->Base||pc-image->Base>=image->ImageSize)return nullptr;
    auto table=(PRUNTIME_FUNCTION)(image->Base+image->UnwindRva);
    for(unsigned i=0;i<image->UnwindSize/12;++i)if(pc-image->Base>=table[i].BeginAddress&&pc-image->Base<table[i].EndAddress)return &table[i];
    return nullptr;
}
static bool check(unsigned transport,void(*pc)(),CONTEXT seed,WitU64 sp,WitU64 rbx,WitU64 rbp,WitU64 rsi,WitU64 rdi,DWORD handlerType=0,PEXCEPTION_ROUTINE handler=nullptr)
{
    const auto entry=lookup((WitU64)pc);if(!entry)return false;
    seed.Rip=(WitU64)pc;CONTEXT expected=seed;
    expected.Rsp=sp;expected.Rip=(WitU64)&unwind_simple_body;expected.Rbx=rbx;expected.Rbp=rbp;expected.Rsi=rsi;expected.Rdi=rdi;
    if(pc==unwind_frame_body||pc==unwind_frame_epilog)expected.Xmm6={0x1122334455667788ULL,0x1122334455667788LL};
    void* data=(void*)0x1234;DWORD64 frame=0;KNONVOLATILE_CONTEXT_POINTERS pointers={};
    const auto routine=(transport?wit_test_unwind_import:wit_test_unwind_direct)(handlerType,image->Base,seed.Rip,entry,&seed,&data,&frame,&pointers);
    ++cases;((WitU64*)WIT_GC_INFO_REPORT)[5]=cases;
    if(memcmp(&seed,&expected,sizeof(seed)))return false;
    if(routine!=handler||seed.Rsp!=sp||seed.Rip!=(WitU64)&unwind_simple_body||seed.Rbx!=rbx||seed.Rbp!=rbp||seed.Rsi!=rsi||seed.Rdi!=rdi)return false;
    if(pc==unwind_frame_body&&(WitU64)pointers.Xmm6!=sp-112)return false;
    if((pc==unwind_frame_body||pc==unwind_frame_epilog)&&(WitU64)pointers.Rbp!=sp-16)return false;
    if((pc==unwind_simple_body||pc==unwind_simple_epilog||pc==unwind_simple_push||pc==unwind_simple_pop||pc==unwind_primary_body||pc==unwind_child||pc==unwind_handled_body)&&
       (WitU64)pointers.Rbx!=sp-16)return false;
    return handler?data&&(WitU64)data>=image->Base: data==(void*)0x1234;
}
extern "C" WitU64 wit_unwind_check_gs(CONTEXT* context,volatile char* buffer)
{
    if(wit_test_gs_override)return wit_test_gs_override(context,buffer);
    if(mode==109){
        foreignContext=context;
        if(WaitForMultipleObjectsEx(1,&foreignEvent,FALSE,INFINITE,FALSE)!=WAIT_OBJECT_0)return 4021;
    }
    const WitU64 pc=context->Rip;auto entry=lookup(pc);if(!entry||buffer[0]!=7)return 4002;
    void* data=nullptr;DWORD64 frame=0;
    const auto handler=wit_test_unwind_import(UNW_FLAG_UHANDLER,image->Base,pc,entry,context,&data,&frame,nullptr);
    if((WitU64)handler!=(WitU64)&__GSHandlerCheck||!data||!frame)return 4003;
    const auto header=(const WitU8*)(image->Base+entry->UnwindData);
    const int encoded=*(const int*)data;
    // This fixed-array compiler fixture deliberately has no dynamic alignment or frame register.
    if(header[3]||(encoded&4))return 4004;
    const auto cookie=(volatile WitU64*)(frame+(WitU64)(long long)(encoded&~7));
    if((*cookie^frame)!=__security_cookie)return 4005;
    DISPATCHER_CONTEXT dispatch={};EXCEPTION_RECORD exception={};
    dispatch.ControlPc=pc;dispatch.ImageBase=image->Base;dispatch.FunctionEntry=entry;
    dispatch.EstablisherFrame=frame;dispatch.ContextRecord=context;dispatch.LanguageHandler=handler;dispatch.HandlerData=data;
    exception.ExceptionFlags=EXCEPTION_UNWINDING;
    if(mode==107){((WitU64*)WIT_GC_INFO_REPORT)[2]=0x1234;*cookie^=1;}
    if(handler(&exception,(void*)frame,context,&dispatch)!=ExceptionContinueSearch)return 4006;
    ++cases;return WIT_TEST_EXIT_CODE;
}
static WitU64 foreign_worker(WitU64)
{
    HANDLE reference=nullptr;
    if(!DuplicateHandle(GetCurrentProcess(),GetCurrentThread(),GetCurrentProcess(),&reference,0,FALSE,DUPLICATE_SAME_ACCESS))return 4022;
    foreignReference=reference;return wit_unwind_protected_frame();
}
static WitU64 foreign_test(WitU64* report)
{
    foreignContext=nullptr;foreignReference=nullptr;
    foreignEvent=CreateEventExW(nullptr,nullptr,0,SYNCHRONIZE|EVENT_MODIFY_STATE);if(!foreignEvent)return 4023;
    WitU64 thread=0,result=0,arena=0;
    if(wit_native_call(WIT_CALL_MEMORY_RESERVE,4096,4096,0,&arena)!=WIT_STATUS_OK||wit_native_call(WIT_CALL_MEMORY_COMMIT,arena,4096,3,nullptr)!=WIT_STATUS_OK||
       wit_native_thread_create(foreign_worker,0,&thread)!=WIT_STATUS_OK)return 4024;
    HANDLE target=nullptr;bool waiting=false;
    for(unsigned i=0;i<10000;++i){
        target=foreignReference;WitThreadReferenceInfo info;
        if(target&&foreignContext&&wit_native_call(WIT_CALL_THREAD_REFERENCE_QUERY,(WitU64)target,(WitU64)&info,sizeof(info),nullptr)==WIT_STATUS_OK&&info.State==WIT_THREAD_REFERENCE_WAITING){waiting=true;break;}
        wit_native_call(WIT_CALL_THREAD_YIELD,0,0,0,nullptr);
    }
    if(!waiting||SuspendThread(target)!=0)return 4025;
    {
        WitNativeUnwindScope scope((WitU64)target);if(scope.Status()!=WIT_STATUS_OK)return 4026;
        auto direct=(CONTEXT*)arena;auto indirect=(CONTEXT*)(arena+sizeof(CONTEXT));
        memcpy(direct,(const void*)foreignContext,sizeof(*direct));*indirect=*direct;
        const WitU64 pc=direct->Rip,sp=direct->Rsp;auto entry=lookup(pc);if(!entry)return 4027;
        void* firstData=(void*)0x1234;void* secondData=(void*)0x1234;DWORD64 firstFrame=0,secondFrame=0;
        KNONVOLATILE_CONTEXT_POINTERS first={},second={};
        if(wit_test_unwind_direct(0,image->Base,pc,entry,direct,&firstData,&firstFrame,&first)||
           wit_test_unwind_import(0,image->Base,pc,entry,indirect,&secondData,&secondFrame,&second)||
           firstData!=(void*)0x1234||secondData!=(void*)0x1234||firstFrame!=secondFrame||memcmp(direct,indirect,sizeof(*direct))||memcmp(&first,&second,sizeof(first)))return 4028;
        WitStackLeaseInfo lease;
        if(WitNativeUnwindScope::Current(sp,&lease)!=WIT_STATUS_OK||direct->Rsp<=sp||direct->Rsp>lease.StackHigh||
           !wit_native_image_range(image,direct->Rip,1,WIT_IMAGE_INFO_EXECUTE,WIT_IMAGE_INFO_WRITE,1))return 4029;
        const auto location=(const volatile WitU64*)(direct->Rsp-8);
        wit_native_call(WIT_CALL_THREAD_YIELD,0,0,0,nullptr);
        if(*location!=direct->Rip||wit_native_call(WIT_CALL_THREAD_RESUME,(WitU64)target,0,0,nullptr)!=WIT_STATUS_BUSY)return 4030;
    }
    if(!SetEvent(foreignEvent)||ResumeThread(target)!=1||wit_native_call(WIT_CALL_THREAD_JOIN,thread,0,0,&result)!=WIT_STATUS_OK||result!=WIT_TEST_EXIT_CODE||
       !CloseHandle(target)||!CloseHandle(foreignEvent)||wit_native_call(WIT_CALL_MEMORY_RELEASE,arena,0,0,nullptr)!=WIT_STATUS_OK||cases!=1)return 4031;
    report[2]=2;report[3]=1;return WIT_TEST_EXIT_CODE;
}
extern "C" WitU64 wit_test_unwind(const WitUserStartup* startup,WitU64 selected)
{
    mode=selected;auto report=(WitU64*)WIT_GC_INFO_REPORT;report[0]=mode;report[1]=17592186044416ULL;
    wit_native_security_initialize_system();wit_native_process_image_initialize(startup);wit_native_tls_initialize(startup);
    image=wit_native_process_image();cases=0;SetLastError(0xC2345678);errno=283;
    report[4]=wit_test_unwind_slot();
    if(mode==109)return foreign_test(report);
    if(mode==107)return wit_unwind_protected_frame();
    if(mode==108){
        WitNativeUnwindScope scope(WIT_THREAD_REFERENCE_CURRENT);if(scope.Status()!=WIT_STATUS_OK)return 4007;
        WitStackLeaseInfo current;if(WitNativeUnwindScope::Current((WitU64)&current,&current)!=WIT_STATUS_OK)return 4008;
        CONTEXT c={};c.Rsp=current.StackHigh-4;c.Rip=(WitU64)&unwind_simple_body;
        void* data=nullptr;DWORD64 frame=0;report[2]=0x1234;
        wit_test_unwind_direct(0,image->Base,c.Rip,lookup(c.Rip),&c,&data,&frame,nullptr);return 4009;
    }
    alignas(16) unsigned char stack[4608];memset(stack,0x5A,sizeof(stack));const WitU64 body=(WitU64)(stack+128);
    const WitU64 saved=0x1122334455667788ULL,frameSaved=0xABCDEF1234567890ULL;
    for(unsigned transport=0;transport<2;++transport){
        WitNativeUnwindScope scope(WIT_THREAD_REFERENCE_CURRENT);if(scope.Status()!=WIT_STATUS_OK)return 4032;
        if(!transport&&scope.Close()!=WIT_STATUS_OK)return 4033; // Exercise real current-stack fallback without an ambient scope.
        *(WitU64*)(body+32)=saved;*(WitU64*)(body+40)=(WitU64)&unwind_simple_body;
        CONTEXT c={};c.ContextFlags=CONTEXT_FULL;c.Rsp=body;
        if(!check(transport,unwind_simple_body,c,body+48,saved,0,0,0)||!check(transport,unwind_simple_epilog,c,body+48,saved,0,0,0))return 4010;
        c.Rsp=body+32;if(!check(transport,unwind_simple_push,c,body+48,saved,0,0,0)||!check(transport,unwind_simple_pop,c,body+48,saved,0,0,0))return 4011;
        c.Rsp=body+40;c.Rbx=saved;if(!check(transport,unwind_simple,c,body+48,saved,0,0,0)||!check(transport,unwind_simple_ret,c,body+48,saved,0,0,0))return 4012;
        c={};c.Rsp=body;c.Rbp=body+64;*(WitU64*)(body+128)=frameSaved;*(WitU64*)(body+136)=(WitU64)&unwind_simple_body;
        *(M128A*)(body+32)={saved,(long long)saved};
        if(!check(transport,unwind_frame_body,c,body+144,0,frameSaved,0,0))return 4013;
        c.Xmm6=*(M128A*)(body+32);if(!check(transport,unwind_frame_epilog,c,body+144,0,frameSaved,0,0))return 4014;
        c={};c.Rsp=body;*(WitU64*)(body+4096)=(WitU64)&unwind_simple_body;
        if(!check(transport,unwind_large_body,c,body+4104,0,0,0,0))return 4015;
        *(WitU64*)(body+32)=saved;*(WitU64*)(body+40)=(WitU64)&unwind_simple_body;
        if(!check(transport,unwind_primary_body,c,body+48,saved,0,0,0)||!check(transport,unwind_child,c,body+48,saved,0,0,0)||
           !check(transport,unwind_handled_body,c,body+48,saved,0,0,0)||!check(transport,unwind_handled_body,c,body+48,saved,0,0,0,UNW_FLAG_EHANDLER,&unwind_handler))return 4016;
        *(WitU64*)body=saved;*(WitU64*)(body+8)=frameSaved;*(WitU64*)(body+16)=(WitU64)&unwind_simple_body;
        if(!check(transport,unwind_v2_body,c,body+24,0,0,saved,frameSaved)||!check(transport,unwind_v2_epilog,c,body+24,0,0,saved,frameSaved))return 4017;
        c.Rsi=saved;c.Rsp=body+8;if(!check(transport,unwind_v2_pop,c,body+24,0,0,saved,frameSaved))return 4018;
        c.Rdi=frameSaved;c.Rsp=body+16;if(!check(transport,unwind_v2_ret,c,body+24,0,0,saved,frameSaved))return 4019;
    }
    if(wit_unwind_protected_frame()!=WIT_TEST_EXIT_CODE||GetLastError()!=0xC2345678||errno!=283)return 4020;
    report[2]=cases;report[3]=1;return WIT_TEST_EXIT_CODE;
}
