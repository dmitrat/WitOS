#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <initializer_list>
#include <stdlib.h>
#include "seh_scope.witos.h"
static int trace[32],used;
static unsigned filters,finalizers,handlers;
static bool abnormal;
static void mark(int value){if(used<32)trace[used++]=value;}
static LONG filter(EXCEPTION_POINTERS* p,LONG action)
{
    ++filters;mark(1);
    return p->ExceptionRecord->ExceptionCode==0xE0431234?action:EXCEPTION_CONTINUE_SEARCH;
}
extern "C" __declspec(noinline) int seh_nested()
{
    __try {
        __try {RaiseException(0xE0431234,0,0,nullptr);return -1;}
        __finally {++finalizers;abnormal=AbnormalTermination()!=FALSE;mark(2);}
    } __except(filter(GetExceptionInformation(),EXCEPTION_EXECUTE_HANDLER)) {++handlers;mark(3);}
    return 42;
}
extern "C" __declspec(noinline) int seh_continue()
{
    __try {RaiseException(0xE0431234,0,0,nullptr);mark(4);}
    __except(filter(GetExceptionInformation(),EXCEPTION_CONTINUE_EXECUTION)){return -1;}
    return 42;
}
extern "C" __declspec(noinline) int seh_catchall()
{
    __try {RaiseException(0xE0431234,0,0,nullptr);}
    __except(EXCEPTION_EXECUTE_HANDLER){mark(5);return 42;}
    return -1;
}
static bool describe(WitUserImageInfo& image)
{
    auto base=(BYTE*)GetModuleHandleW(nullptr);auto nt=(IMAGE_NT_HEADERS64*)(base+((IMAGE_DOS_HEADER*)base)->e_lfanew);
    if(nt->FileHeader.NumberOfSections>WIT_IMAGE_INFO_MAX_RANGES)return false;
    image={};image.Version=WIT_IMAGE_INFO_VERSION;image.Size=sizeof(image);image.Base=(WitU64)base;image.ImageSize=nt->OptionalHeader.SizeOfImage;
    image.RangeCount=nt->FileHeader.NumberOfSections;auto sections=IMAGE_FIRST_SECTION(nt);
    for(unsigned i=0;i<image.RangeCount;++i){const auto& s=sections[i];image.Ranges[i]={s.VirtualAddress,s.Misc.VirtualSize,s.SizeOfRawData<s.Misc.VirtualSize?s.SizeOfRawData:s.Misc.VirtualSize,
        WIT_IMAGE_INFO_READ|((s.Characteristics&IMAGE_SCN_MEM_WRITE)?WIT_IMAGE_INFO_WRITE:0)|((s.Characteristics&IMAGE_SCN_MEM_EXECUTE)?WIT_IMAGE_INFO_EXECUTE:0)};}
    image.UnwindRva=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION].VirtualAddress;image.UnwindSize=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION].Size;
    return true;
}
static bool inspect(WitUserImageInfo& image,void* function,bool needFilter,bool needFinally,bool needConstant)
{
    DWORD64 base=0;auto entry=RtlLookupFunctionEntry((DWORD64)function,&base,nullptr);if(!entry||base!=image.Base)return false;
    WitUnwindRecord unwind;WitSehTable table;
    const auto entryRva=(WitU32)((WitU64)entry-image.Base);
    if(wit_unwind_validate_function(&image,entryRva,&unwind)!=WitUnwindValid||
       wit_seh_validate(&image,entryRva,image.Base+unwind.HandlerDataRva,&table)!=WitSehValid)return false;
    bool filterFound=false,finallyFound=false,constantFound=false;
    for(unsigned i=0;i<table.Count;++i){WitSehScope s;if(!wit_seh_scope(&table,i,&s))return false;
        if(!s.Target)finallyFound=true;else if(s.Handler==1)constantFound=true;else filterFound=true;
        printf("scope %u: begin=%x end=%x handler=%x target=%x\n",i,s.Begin,s.End,s.Handler,s.Target);}
    return (!needFilter||filterFound)&&(!needFinally||finallyFound)&&(!needConstant||constantFound);
}
static bool livePassed;
static unsigned manualFinally;
static __declspec(noinline) bool inspect_live(volatile int* local)
{
    CONTEXT context={};RtlCaptureContext(&context);DWORD64 base=0,frame=0;void* data=nullptr;
    auto entry=RtlLookupFunctionEntry(context.Rip,&base,nullptr);if(!entry)return false;
    RtlVirtualUnwind(0,base,context.Rip,entry,&context,&data,&frame,nullptr);
    const DWORD64 pc=context.Rip;entry=RtlLookupFunctionEntry(pc,&base,nullptr);if(!entry)return false;
    CONTEXT parent=context;
    auto language=RtlVirtualUnwind(UNW_FLAG_EHANDLER|UNW_FLAG_UHANDLER,base,pc,entry,&context,&data,&frame,nullptr);
    if(!language||!data)return false;
    WitUserImageInfo image;WitSehTable table;
    if(!describe(image)||wit_seh_validate(&image,(WitU32)((WitU64)entry-base),(WitU64)data,&table)!=WitSehValid)return false;
    EXCEPTION_RECORD exception={};exception.ExceptionCode=0xE0431234;
    ULONG_PTR low=0,high=0;GetCurrentThreadStackLimits(&low,&high);WitUnwindStackRange bounds={low,high};
    WitSehDecision decision={0xA5A5A5A5,0xA5A5A5A5};
    if(wit_seh_search(&image,(WitU32)((WitU64)entry-base),(WitU64)data,pc,frame,&bounds,&exception,&parent,&decision)!=WitSehTarget||*local!=0x77)return false;
    const WitSehDecision saved=decision;
    *local=0x78;
    if(wit_seh_search(&image,(WitU32)((WitU64)entry-base),(WitU64)data,pc,frame,&bounds,&exception,&parent,&decision)!=WitSehSearch||memcmp(&saved,&decision,sizeof(saved)))return false;
    *local=0x76;
    if(wit_seh_search(&image,(WitU32)((WitU64)entry-base),(WitU64)data,pc,frame,&bounds,&exception,&parent,&decision)!=WitSehContinue||memcmp(&saved,&decision,sizeof(saved)))return false;
    *local=0x77;
    DWORD cursor=0;
    // A target remaining inside the protected region must not terminate it.
    if(!wit_seh_terminate(&image,(WitU32)((WitU64)entry-base),(WitU64)data,pc,frame,&bounds,true,pc,&cursor)||cursor||manualFinally||*local!=0x77)return false;
    // A target handler outside the inner try terminates its finally exactly once.
    if(!wit_seh_terminate(&image,(WitU32)((WitU64)entry-base),(WitU64)data,pc,frame,&bounds,true,base+decision.Target,&cursor)||!cursor||manualFinally!=1||*local!=0x81)return false;
    if(!wit_seh_terminate(&image,(WitU32)((WitU64)entry-base),(WitU64)data,pc,frame,&bounds,true,base+decision.Target,&cursor)||manualFinally!=1)return false;
    const DWORD expectedCursor=cursor;
    *local=0x77;manualFinally=0;
    DISPATCHER_CONTEXT dispatcher={};dispatcher.ControlPc=pc;dispatcher.ImageBase=base;dispatcher.FunctionEntry=entry;
    dispatcher.EstablisherFrame=frame;dispatcher.ContextRecord=&parent;dispatcher.LanguageHandler=language;dispatcher.HandlerData=data;dispatcher.TargetIp=pc;
    exception.ExceptionFlags=EXCEPTION_UNWINDING|EXCEPTION_TARGET_UNWIND;
    if(language(&exception,(void*)frame,&parent,&dispatcher)!=ExceptionContinueSearch||dispatcher.ScopeIndex||manualFinally||*local!=0x77)return false;
    dispatcher.TargetIp=base+decision.Target;
    if(language(&exception,(void*)frame,&parent,&dispatcher)!=ExceptionContinueSearch||dispatcher.ScopeIndex!=expectedCursor||manualFinally!=1||*local!=0x81)return false;
    return true;
}
extern "C" __declspec(noinline) int seh_live_scope()
{
    volatile int local=0x77;
    __try {
        __try {livePassed=inspect_live(&local);}
        __finally {if(AbnormalTermination())++manualFinally;local+=10;}
    } __except(local==0x76?EXCEPTION_CONTINUE_EXECUTION:local==0x77?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH) {return -1;}
    return local;
}
static bool malformed()
{
    auto memory=(BYTE*)VirtualAlloc(nullptr,8192,MEM_RESERVE,PAGE_NOACCESS);
    if(!memory||!VirtualAlloc(memory,4096,MEM_COMMIT,PAGE_READWRITE))return false;
    WitUserImageInfo image={};image.Version=WIT_IMAGE_INFO_VERSION;image.Size=sizeof(image);image.Base=(WitU64)memory;image.ImageSize=4096;
    image.RangeCount=3;image.Ranges[0]={64,192,192,WIT_IMAGE_INFO_READ|WIT_IMAGE_INFO_EXECUTE};
    image.Ranges[1]={512,3072,3072,WIT_IMAGE_INFO_READ};image.Ranges[2]={3584,12,12,WIT_IMAGE_INFO_READ};image.UnwindRva=3584;image.UnwindSize=12;
    auto set=[&](unsigned offset,DWORD value){memcpy(memory+offset,&value,4);};
    auto reset=[&](){memset(memory,0,4096);set(3584,64);set(3588,128);set(3592,512);memory[512]=9;set(516,80);set(520,1);set(524,64);set(528,128);set(532,96);set(536,112);};
    unsigned checks=0;
    auto check=[&](WitSehValidation expected,WitU64 address=0){WitSehTable result;memset(&result,0xA5,sizeof(result));const auto before=result;
        auto got=wit_seh_validate(&image,3584,address?address:image.Base+520,&result);++checks;
        if(got!=expected||(got!=WitSehValid&&memcmp(&before,&result,sizeof(result)))){printf("scope validation case=%u expected=%u actual=%u\n",checks,expected,got);return false;}return true;};
    reset();if(!check(WitSehValid))return false;
    set(532,1);if(!check(WitSehValid))return false;
    set(532,96);set(536,0);if(!check(WitSehValid))return false;
    set(532,1);if(!check(WitSehBadRange))return false;
    reset();if(!check(WitSehBadRange,image.Base+524)||!check(WitSehBadRange,image.Base+521))return false;
    reset();set(520,129);if(!check(WitSehQuota))return false;
    reset();set(528,64);if(!check(WitSehBadRange))return false;
    reset();set(536,512);if(!check(WitSehBadRange))return false;
    reset();set(532,512);if(!check(WitSehBadRange))return false;
    reset();image.Ranges[1].InitializedSize=24;if(!check(WitSehBadRange))return false;image.Ranges[1].InitializedSize=3072;
    reset();image.Ranges[1].Flags|=WIT_IMAGE_INFO_WRITE;if(!check(WitSehBadFormat))return false;image.Ranges[1].Flags=WIT_IMAGE_INFO_READ;
    reset();set(520,0);if(!check(WitSehValid))return false;
    reset();set(520,2);set(532,1);set(540,64);set(544,128);set(548,512);set(552,112);
    CONTEXT context={};EXCEPTION_RECORD exception={};WitSehDecision decision={0xA5A5A5A5,0xA5A5A5A5};const auto decisionBefore=decision;
    WitUnwindStackRange stack={(WitU64)&context,(WitU64)&context+sizeof(context)};
    if(wit_seh_search(&image,3584,image.Base+520,image.Base+64,stack.Low,&stack,&exception,&context,&decision)!=WitSehInvalid||memcmp(&decision,&decisionBefore,sizeof(decision)))return false;++checks;
    DWORD cursor=0;
    if(wit_seh_terminate(&image,3584,image.Base+520,image.Base+64,stack.Low,&stack,false,0,&cursor)||cursor)return false;++checks;
    reset();WitSehTable rejected;memset(&rejected,0xA5,sizeof(rejected));const auto rejectedBefore=rejected;
    if(wit_seh_validate(&image,3585,image.Base+520,&rejected)!=WitSehBadFormat||memcmp(&rejected,&rejectedBefore,sizeof(rejected)))return false;++checks;
    memcpy(memory+1024,memory+3584,12);
    if(wit_seh_validate(&image,1024,image.Base+520,&rejected)!=WitSehBadFormat||memcmp(&rejected,&rejectedBefore,sizeof(rejected)))return false;++checks;
    reset();set(3592,4072);image.Ranges[1].Size=image.Ranges[1].InitializedSize=8;
    image.RangeCount=4;image.Ranges[3]={4072,24,24,WIT_IMAGE_INFO_READ};memory[4072]=9;set(4076,80);set(4080,1);
    // The first record would straddle an inaccessible guard page.
    if(!check(WitSehBadRange,image.Base+4080))return false;
    if(!VirtualFree(memory,0,MEM_RELEASE))return false;
    printf("PASS: %u C-specific scope-table boundary cases\n",checks);return true;
}
static int localCount;
static bool localAbnormal;
extern "C" __declspec(noinline) int seh_local_return()
{
    __try {return 42;}
    __finally {++localCount;localAbnormal=AbnormalTermination()!=FALSE;}
}
extern "C" __declspec(noinline) int seh_local_leave()
{
    __try {__leave;}
    __finally {++localCount;localAbnormal=AbnormalTermination()!=FALSE;}
    return 42;
}
extern "C" __declspec(noinline) int seh_local_goto()
{
    __try {goto target;}
    __finally {++localCount;localAbnormal=AbnormalTermination()!=FALSE;}
 target:return 42;
}
static bool local_tests()
{
    localCount=0;localAbnormal=false;
    if(seh_local_return()!=42||localCount!=1||!localAbnormal)return false;printf("local return abnormal=%u\n",localAbnormal?1:0);
    localCount=0;localAbnormal=true;
    if(seh_local_leave()!=42||localCount!=1||localAbnormal)return false;printf("local leave abnormal=%u\n",localAbnormal?1:0);
    localCount=0;localAbnormal=false;
    if(seh_local_goto()!=42||localCount!=1||!localAbnormal)return false;printf("local goto abnormal=%u\n",localAbnormal?1:0);
    puts("PASS: compiler local return/leave/goto unwind (HOSTED only)");return true;
}
static bool gs_boundaries()
{
    auto memory=(BYTE*)VirtualAlloc(nullptr,8192,MEM_RESERVE,PAGE_NOACCESS);
    if(!memory||!VirtualAlloc(memory,4096,MEM_COMMIT,PAGE_READWRITE))return false;
    WitUserImageInfo image={};image.Version=WIT_IMAGE_INFO_VERSION;image.Size=sizeof(image);image.Base=(WitU64)memory;image.ImageSize=4096;
    image.RangeCount=3;image.Ranges[0]={64,192,192,WIT_IMAGE_INFO_READ|WIT_IMAGE_INFO_EXECUTE};
    image.Ranges[1]={512,3072,3072,WIT_IMAGE_INFO_READ};image.Ranges[2]={3584,12,12,WIT_IMAGE_INFO_READ};image.UnwindRva=3584;image.UnwindSize=12;
    auto set=[&](unsigned at,unsigned value){memcpy(memory+at,&value,4);};
    set(3584,64);set(3588,128);set(3592,512);memory[512]=9;set(516,80);set(520,0);set(524,0xA1);
    unsigned checks=0;
    auto check=[&](WitSehValidation expected,WitU64 address){
        WitSehGsData output;memset(&output,0xA5,sizeof(output));const auto before=output;
        const auto actual=wit_seh_gs_validate(&image,3584,address,&output);++checks;
        return actual==expected&&(actual==WitSehValid?(output.Address==address+4&&output.Flags==(*(DWORD*)(address+4)&3U)):memcmp(&before,&output,sizeof(output))==0);
    };
    const WitU64 table=image.Base+520;
    if(!check(WitSehValid,table))return false;
    set(524,0xA0);if(!check(WitSehValid,table))return false;
    image.Ranges[1].InitializedSize=12;if(!check(WitSehBadRange,table))return false;
    image.Ranges[1].InitializedSize=15;if(!check(WitSehBadRange,table))return false;
    set(524,0xA5);set(528,0);set(532,16);
    image.Ranges[1].InitializedSize=16;if(!check(WitSehBadRange,table))return false;
    image.Ranges[1].InitializedSize=23;if(!check(WitSehBadRange,table))return false;
    image.Ranges[1].InitializedSize=24;set(532,0);if(!check(WitSehBadFormat,table))return false;
    set(532,3);if(!check(WitSehBadFormat,table))return false;
    set(532,16);if(!check(WitSehValid,table))return false;
    set(520,129);if(!check(WitSehQuota,table))return false;
    // The aligned tail would cross into an actually inaccessible page.
    set(3592,4080);image.RangeCount=4;image.Ranges[3]={4080,16,16,WIT_IMAGE_INFO_READ};
    memory[4080]=9;set(4084,80);set(4088,0);set(4092,0xA5);
    if(!check(WitSehBadRange,image.Base+4088))return false;
    if(!VirtualFree(memory,0,MEM_RELEASE))return false;
    printf("PASS: %u combined GS/SEH payload boundaries and transactional outputs\n",checks);return true;
}
static unsigned innerCaught;
static LONG nested_filter(EXCEPTION_POINTERS* pointers)
{
    if(pointers->ExceptionRecord->ExceptionCode!=0xE0431234U)return EXCEPTION_CONTINUE_SEARCH;
    mark(10);
    __try {RaiseException(0xE0431234U+1,0,0,nullptr);}
    __except(GetExceptionCode()==0xE0431234U+1?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH){++innerCaught;mark(11);}
    mark(12);return EXCEPTION_EXECUTE_HANDLER;
}
static __declspec(noinline) int nested_filter_case()
{
    __try {RaiseException(0xE0431234U,0,0,nullptr);}
    __except(nested_filter(GetExceptionInformation())){++handlers;mark(13);return 42;}
    return -1;
}
static __declspec(noinline) int nested_finally_case()
{
    __try {
        __try {RaiseException(0xE0431234U,0,0,nullptr);}
        __finally {
            ++finalizers;mark(10);
            __try {RaiseException(0xE0431234U+1,0,0,nullptr);}
            __except(GetExceptionCode()==0xE0431234U+1?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH){++innerCaught;mark(11);}
            mark(12);
        }
    } __except(GetExceptionCode()==0xE0431234U?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH){++handlers;mark(13);return 42;}
    return -1;
}

static LONG escaping_filter(EXCEPTION_POINTERS* pointers)
{
    if(pointers->ExceptionRecord->ExceptionCode!=0xE0431234U)return EXCEPTION_CONTINUE_SEARCH;
    mark(20);RaiseException(0xE0431235U,0,0,nullptr);mark(99);
    return EXCEPTION_EXECUTE_HANDLER;
}
static __declspec(noinline) int escaping_filter_case()
{
    __try {
        __try {
            __try {RaiseException(0xE0431234U,0,0,nullptr);}
            __finally {++finalizers;mark(22);}
        } __except(escaping_filter(GetExceptionInformation())) {mark(99);}
    } __except(GetExceptionCode()==0xE0431235U?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH) {
        ++handlers;mark(23);return 42;
    }
    return -1;
}
static __declspec(noinline) int collided_finally_case()
{
    __try {
        __try {
            __try {RaiseException(0xE0431234U,0,0,nullptr);}
            __finally {++finalizers;mark(30);RaiseException(0xE0431235U,0,0,nullptr);mark(99);}
        } __finally {++finalizers;mark(31);}
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        ++handlers;mark(32);return GetExceptionCode()==0xE0431235U?42:-2;
    }
    return -1;
}

static __declspec(noinline) int local_return_raises()
{
    __try {return -1;}
    __finally {++finalizers;mark(40);RaiseException(0xE0431235U,0,0,nullptr);mark(99);}
}
static __declspec(noinline) int interrupted_local_case()
{
    __try {local_return_raises();return -2;}
    __except(GetExceptionCode()==0xE0431235U?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH) {
        ++handlers;mark(41);return 42;
    }
}

static __declspec(noinline) int repeated_collision_case()
{
    __try {
        __try {
            __try {
                __try {RaiseException(0xE0431234U,0,0,nullptr);}
                __finally {++finalizers;mark(50);RaiseException(0xE0431235U,0,0,nullptr);mark(99);}
            } __finally {++finalizers;mark(51);RaiseException(0xE0431236U,0,0,nullptr);mark(99);}
        } __finally {++finalizers;mark(52);}
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        ++handlers;mark(53);return GetExceptionCode()==0xE0431236U?42:-2;
    }
    return -1;
}
static __declspec(noinline) int retained_collision_case()
{
    __try {
        __try {RaiseException(0xE0431234U,0,0,nullptr);}
        __finally {
            ++finalizers;mark(60);
            if(collided_finally_case()!=42)mark(99);
            mark(61);
        }
    } __except(GetExceptionCode()==0xE0431234U?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH) {
        ++handlers;mark(62);return 42;
    }
    return -1;
}
static bool escaping_tests()
{
    used=handlers=finalizers=0;
    const int filterResult=escaping_filter_case();
    printf("escaping filter: result=%d finalizers=%u handlers=%u trace=",filterResult,finalizers,handlers);
    for(int i=0;i<used;++i)printf("%d,",trace[i]);puts("");
    if(filterResult!=42||handlers!=1||finalizers!=1||used!=3||trace[0]!=20||trace[1]!=22||trace[2]!=23)return false;
    used=handlers=finalizers=0;
    const int finallyResult=collided_finally_case();
    printf("collided finally: result=%d finalizers=%u handlers=%u trace=",finallyResult,finalizers,handlers);
    for(int i=0;i<used;++i)printf("%d,",trace[i]);puts("");
    if(finallyResult!=42||handlers!=1||finalizers!=2||used!=3||trace[0]!=30||trace[1]!=31||trace[2]!=32)return false;
    used=handlers=finalizers=0;
    const int localResult=interrupted_local_case();
    printf("interrupted local unwind: result=%d finalizers=%u handlers=%u trace=",localResult,finalizers,handlers);
    for(int i=0;i<used;++i)printf("%d,",trace[i]);puts("");
    if(localResult!=42||handlers!=1||finalizers!=1||used!=2||trace[0]!=40||trace[1]!=41)return false;
    used=handlers=finalizers=0;
    const int repeatedResult=repeated_collision_case();
    printf("repeated collision: result=%d finalizers=%u handlers=%u trace=",repeatedResult,finalizers,handlers);
    for(int i=0;i<used;++i)printf("%d,",trace[i]);puts("");
    if(repeatedResult!=42||handlers!=1||finalizers!=3||used!=4||trace[0]!=50||trace[1]!=51||trace[2]!=52||trace[3]!=53)return false;
    used=handlers=finalizers=0;
    const int retainedResult=retained_collision_case();
    printf("retained outer unwind: result=%d finalizers=%u handlers=%u trace=",retainedResult,finalizers,handlers);
    for(int i=0;i<used;++i)printf("%d,",trace[i]);puts("");
    if(retainedResult!=42||handlers!=2||finalizers!=3||used!=6||trace[0]!=60||trace[1]!=30||trace[2]!=31||trace[3]!=32||trace[4]!=61||trace[5]!=62)return false;
    puts("PASS: escaping filter and collided finally exact-once cleanup (HOSTED only)");return true;
}

extern "C" __declspec(noinline) int seh_gs_scope()
{
    volatile unsigned char buffer[128];
    for(unsigned i=0;i<128;++i)buffer[i]=(unsigned char)i;
    buffer[0]=17;buffer[127]=25;
    __try {RaiseException(0xE0431234U,0,0,nullptr);}
    __except(GetExceptionCode()==0xE0431234U?EXCEPTION_EXECUTE_HANDLER:EXCEPTION_CONTINUE_SEARCH) {
        ++handlers;return buffer[0]+buffer[127];
    }
    return -1;
}
extern "C" int wit_seh_gs_frame(unsigned);
extern "C" int wit_seh_gs_aligned_frame(unsigned);
extern "C" const unsigned long long wit_seh_gs_aligned_cookie_delta;
extern "C" const unsigned long long wit_seh_gs_cookie_delta;
static unsigned gsFilters,gsFinalizers,gsHandlers;
static bool gsAbnormal;
extern "C" void wit_seh_gs_action(volatile unsigned char* buffer,unsigned mode)
{
    puts("GS-ACTION");
    if(mode==157||mode==164)*(volatile unsigned long long*)(buffer+(mode>=163?wit_seh_gs_aligned_cookie_delta:wit_seh_gs_cookie_delta))^=1;
    if(mode!=159)RaiseException(0xE0435678U,0,0,nullptr);
}
extern "C" LONG wit_seh_gs_select(EXCEPTION_POINTERS* pointers,volatile unsigned char* buffer,unsigned mode)
{
    ++gsFilters;puts("GS-FILTER");
    if(mode==158||mode==165)*(volatile unsigned long long*)(buffer+(mode>=163?wit_seh_gs_aligned_cookie_delta:wit_seh_gs_cookie_delta))^=1;
    return pointers->ExceptionRecord->ExceptionCode!=0xE0435678U?EXCEPTION_CONTINUE_SEARCH:mode==156?EXCEPTION_CONTINUE_EXECUTION:EXCEPTION_EXECUTE_HANDLER;
}
extern "C" void wit_seh_gs_finally(int value){++gsFinalizers;gsAbnormal=value!=0;puts("GS-FINALLY");}
extern "C" void wit_seh_gs_caught(){++gsHandlers;puts("GS-CATCH");}
static bool shared_gs_tests()
{
    for(unsigned mode: {155U,156U,159U,163U}){
        gsFilters=gsFinalizers=gsHandlers=0;gsAbnormal=false;
        if((mode>=163?wit_seh_gs_aligned_frame(mode):wit_seh_gs_frame(mode))!=42||gsFilters!=(mode==159?0U:1U)||gsFinalizers!=1||gsHandlers!=((mode==155||mode==163)?1U:0U)||gsAbnormal!=(mode==155||mode==163))return false;
    }
    puts("PASS: shared GS/SEH catch, continuation and normal finally");return true;
}
int main(int argc,char** argv)
{
    setvbuf(stdout,nullptr,_IONBF,0);
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    if(argc==2){const auto mode=(unsigned)atoi(argv[1]);return mode>=163?wit_seh_gs_aligned_frame(mode):wit_seh_gs_frame(mode); }
    WitUserImageInfo image;
    if(!malformed()||seh_live_scope()!=0x8B||!livePassed||!describe(image)||!inspect(image,(void*)&seh_nested,true,true,false)||!inspect(image,(void*)&seh_continue,true,false,false)||!inspect(image,(void*)&seh_catchall,false,false,true))return 1;
    if(seh_nested()!=42||filters!=1||finalizers!=1||handlers!=1||!abnormal||used!=3||trace[0]!=1||trace[1]!=2||trace[2]!=3)return 2;
    if(seh_continue()!=42||filters!=2||trace[used-1]!=4)return 3;
    if(seh_catchall()!=42||trace[used-1]!=5)return 4;
    if(!local_tests())return 5;
    used=innerCaught=handlers=finalizers=0;
    if(nested_filter_case()!=42||innerCaught!=1||handlers!=1||used!=4||trace[0]!=10||trace[1]!=11||trace[2]!=12||trace[3]!=13)return 6;
    used=innerCaught=handlers=finalizers=0;
    if(nested_finally_case()!=42||innerCaught!=1||handlers!=1||finalizers!=1||used!=4||trace[0]!=10||trace[1]!=11||trace[2]!=12||trace[3]!=13)return 7;
    puts("PASS: nested exceptions handled within compiler filter/finally callbacks (HOSTED only)");
    if(!escaping_tests())return 8;
    handlers=0;
    if(seh_gs_scope()!=42||handlers!=1)return 9;
    puts("PASS: actual compiler GS/SEH protected catch (HOSTED only)");
    if(!shared_gs_tests())return 10;
    if(!gs_boundaries())return 11;
    puts("PASS: actual filter/finally funclet calls on live establisher frame");
    puts("PASS: real compiler C-specific tables, filter/abnormal-finally/handler order and continuation (HOSTED only)");return 0;
}
