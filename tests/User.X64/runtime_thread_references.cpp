#include "witos/handles.h"
#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "protocol.h"
#include <errno.h>
extern "C" BOOL WINAPI wit_reference_direct_duplicate(HANDLE,HANDLE,HANDLE,LPHANDLE,DWORD,BOOL,DWORD);
extern "C" const void* const __imp_GetCurrentThread;
extern "C" DWORD WINAPI wit_reference_direct_id();
static volatile HANDLE worker_reference;
static volatile DWORD native_ids[3];
static bool duplicate(HANDLE source,HANDLE* result,DWORD access=0,DWORD options=DUPLICATE_SAME_ACCESS)
{ return DuplicateHandle(GetCurrentProcess(),source,GetCurrentProcess(),result,access,FALSE,options)!=0; }
static bool query(HANDLE handle,WitThreadReferenceInfo& info)
{WitU64 copied=0;return wit_native_call(WIT_CALL_THREAD_REFERENCE_QUERY,(WitU64)handle,(WitU64)&info,sizeof(info),&copied)==WIT_STATUS_OK&&copied==sizeof(info)&&info.Version==WIT_THREAD_REFERENCE_VERSION&&info.Size==sizeof(info);}
static WitU64 worker(WitU64 index)
{
    HANDLE handle=nullptr;
    if(!duplicate(GetCurrentThread(),&handle))return 2701;
    native_ids[index]=GetCurrentThreadId();
    if(!native_ids[index]||native_ids[index]!=wit_reference_direct_id())return 2722;
    worker_reference=handle;
    return 42+index;
}
static unsigned fill(HANDLE* handles)
{
    unsigned count=0;
    for(;count<WIT_HANDLE_CAPACITY;++count)if(!duplicate(GetCurrentThread(),&handles[count]))break;
    return count;
}
extern "C" WitU64 wit_test_references(const WitUserStartup* startup,WitU64 mode)
{
    auto report=(WitU64*)WIT_GC_INFO_REPORT;report[1]=2147483648ULL;
    const bool tls=mode==64;
    wit_native_security_initialize_system();wit_native_process_image_initialize(startup);if(tls)wit_native_tls_initialize(startup);
    SetLastError(0x57321468);if(tls)errno=127;
    if(GetCurrentProcess()!=(HANDLE)(intptr_t)-1||GetCurrentThread()!=(HANDLE)(intptr_t)-2)return 2702;
    HANDLE first=nullptr,second=nullptr,restricted=nullptr;
    if(!duplicate(GetCurrentThread(),&first)||!wit_reference_direct_duplicate(GetCurrentProcess(),first,GetCurrentProcess(),&second,0,FALSE,DUPLICATE_SAME_ACCESS)||first==second)return 2703;
    WitThreadReferenceInfo a,b;
    WitUserThreadInfo current;WitU64 copied=0;
    if(wit_native_call(WIT_CALL_THREAD_QUERY,(WitU64)&current,sizeof(current),WIT_THREAD_INFO_VERSION,&copied)!=WIT_STATUS_OK||
        !query(first,a)||!query(second,b)||a.ThreadId!=current.ThreadId||a.ThreadId!=b.ThreadId||a.State!=WIT_THREAD_REFERENCE_LIVE||
        a.StackLow!=current.StackLow||a.StackHigh!=current.StackHigh||first==(HANDLE)a.ThreadId||
        GetThreadPriority(first)!=THREAD_PRIORITY_NORMAL||GetThreadPriority(GetCurrentThread())!=THREAD_PRIORITY_NORMAL||GetLastError()!=0x57321468)return 2704;
    if(!duplicate(first,&restricted,SYNCHRONIZE,0)||GetThreadPriority(restricted)!=THREAD_PRIORITY_ERROR_RETURN||GetLastError()!=ERROR_ACCESS_DENIED)return 2705;
    HANDLE attempted=(HANDLE)0x1234;
    if(duplicate(restricted,&attempted,THREAD_QUERY_INFORMATION,0)||attempted!=(HANDLE)0x1234||GetLastError()!=ERROR_ACCESS_DENIED)return 2706;
    WitU64 arena=0;
    if(wit_native_call(WIT_CALL_MEMORY_RESERVE,8192,4096,0,&arena)!=WIT_STATUS_OK||
        wit_native_call(WIT_CALL_MEMORY_COMMIT,arena,4096,3,nullptr)!=WIT_STATUS_OK)return 2718;
    auto edge=(unsigned char*)(arena+4092);for(unsigned i=0;i<4;++i)edge[i]=0xa5;
    copied=99;
    if(wit_native_call(WIT_CALL_THREAD_REFERENCE_QUERY,(WitU64)first,(WitU64)edge,sizeof(a),&copied)!=WIT_STATUS_BAD_ADDRESS||copied)return 2719;
    for(unsigned i=0;i<4;++i)if(edge[i]!=0xa5)return 2720;
    if(wit_native_call(WIT_CALL_MEMORY_COMMIT,arena+4096,4096,3,nullptr)!=WIT_STATUS_OK||
        wit_native_call(WIT_CALL_THREAD_REFERENCE_QUERY,(WitU64)first,(WitU64)edge,sizeof(a),&copied)!=WIT_STATUS_OK||copied!=sizeof(a)||
        wit_native_call(WIT_CALL_MEMORY_RELEASE,arena,0,0,nullptr)!=WIT_STATUS_OK)return 2721;
    if(!current.NativeId||GetCurrentThreadId()!=current.NativeId||wit_reference_direct_id()!=current.NativeId||
        (WitU64)GetCurrentThreadId()==current.ThreadId)return 2723;
    report[2]=current.NativeId;
    const auto raw=(volatile WitU64*)current.RawTls;
    const auto savedIdentity=raw[1];raw[1]=0;
    const auto stillCurrent=GetCurrentThreadId();raw[1]=savedIdentity;
    if(stillCurrent!=current.NativeId)return 2724;
    copied=99;
    if(wit_native_call(WIT_CALL_THREAD_NATIVE_ID,1,0,0,&copied)!=WIT_STATUS_INVALID_ARGUMENT||copied)return 2725;
    if(!CloseHandle(first)||!query(second,b)||b.ThreadId!=a.ThreadId||query(first,b)||!CloseHandle(second)||!CloseHandle(restricted))return 2707;
    HANDLE handles[WIT_HANDLE_CAPACITY];const auto capacity=fill(handles);
    if(!capacity||capacity==WIT_HANDLE_CAPACITY||GetLastError()!=ERROR_NOT_ENOUGH_MEMORY)return 2708;
    for(unsigned i=0;i<capacity;++i)if(!CloseHandle(handles[i]))return 2709;
    const auto slot=__imp_GetCurrentThread;
    if(duplicate(GetCurrentThread(),(HANDLE*)&__imp_GetCurrentThread)||__imp_GetCurrentThread!=slot||GetLastError()!=ERROR_INVALID_ADDRESS)return 2710;
    if(fill(handles)!=capacity)return 2711;
    for(unsigned i=0;i<capacity;++i)if(!CloseHandle(handles[i]))return 2712;
    if(tls){
        WitU64 join,result;HANDLE previous=nullptr;WitU64 previousId=0;
        for(unsigned i=0;i<3;++i){
            worker_reference=nullptr;
            if(wit_native_thread_create(worker,i,&join)!=WIT_STATUS_OK||wit_native_call(WIT_CALL_THREAD_JOIN,join,0,0,&result)!=WIT_STATUS_OK||result!=42+i)return 2713;
            if(native_ids[i]<=current.NativeId||(i&&native_ids[i]<=native_ids[i-1]))return 2726;
            HANDLE exited=worker_reference;
            if(!query(exited,a)||a.State!=WIT_THREAD_REFERENCE_EXITED||a.ExitCode!=42+i||a.StackLow||a.StackHigh||a.ThreadId==previousId)return 2714;
            if(previous){if(!query(previous,b)||b.ThreadId!=previousId||b.State!=WIT_THREAD_REFERENCE_EXITED||!CloseHandle(previous))return 2715;}
            previous=exited;previousId=a.ThreadId;
        }
        if(!CloseHandle(previous))return 2716;
        wit_native_tls_leave();
    }
    SetLastError(0x57321468);
    return (!tls||errno==127)?WIT_TEST_EXIT_CODE:2717;
}

static volatile WitU32 creationEntered,creationCleaned,creationNativeId;
static thread_local WitU32 creationTls;
static void creation_cleanup(void*) { ++creationCleaned; }
static DWORD WINAPI created_worker(LPVOID argument)
{
    if(creationTls)return 4201;
    creationTls=731;
    creationNativeId=GetCurrentThreadId();
    if(!creationNativeId||wit_native_thread_on_cleanup(creation_cleanup,nullptr)!=WIT_STATUS_OK)return 4202;
    ++creationEntered;
    return 42+(DWORD)(uintptr_t)argument;
}
static bool creation_memory(WitUserMemoryInfo& info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY,(WitU64)&info,sizeof(info),WIT_MEMORY_INFO_VERSION,nullptr)==WIT_STATUS_OK;
}
static bool creation_reject(WitU64 request,WitU64 bytes,WitU64 reserved,WitU64 expected,WitU32& id)
{
    WitU64 result=99;
    return wit_native_call(WIT_CALL_THREAD_CREATE_REFERENCE,request,bytes,reserved,&result)==expected&&
        !result&&id==0xA5A5A5A5U;
}
static bool creation_bad_requests()
{
    WitU32 id=0xA5A5A5A5U;
    WitThreadCreateRequest request={WIT_THREAD_CREATE_REFERENCE_VERSION,sizeof(request),(WitU64)created_worker,0,0,(WitU64)&id,WIT_THREAD_START_SUSPENDED,0};
    WitUserMemoryInfo before,after;
    if(!creation_memory(before))return false;
    if(!creation_reject(0,sizeof(request),1,WIT_STATUS_INVALID_ARGUMENT,id)||
        !creation_reject(0,sizeof(request),0,WIT_STATUS_BAD_ADDRESS,id)||
        !creation_reject((WitU64)&request,sizeof(request)-1,0,WIT_STATUS_INVALID_ARGUMENT,id))return false;
    request.Version=2;
    if(!creation_reject((WitU64)&request,sizeof(request),0,WIT_STATUS_UNSUPPORTED,id))return false;
    request.Version=WIT_THREAD_CREATE_REFERENCE_VERSION;request.Size=0;
    if(!creation_reject((WitU64)&request,sizeof(request),0,WIT_STATUS_INVALID_ARGUMENT,id))return false;
    request.Size=sizeof(request);request.Reserved=1;
    if(!creation_reject((WitU64)&request,sizeof(request),0,WIT_STATUS_INVALID_ARGUMENT,id))return false;
    request.Reserved=0;request.Flags=2;
    if(!creation_reject((WitU64)&request,sizeof(request),0,WIT_STATUS_INVALID_ARGUMENT,id))return false;
    request.Flags=WIT_THREAD_START_SUSPENDED;request.StackBytes=65537;
    if(!creation_reject((WitU64)&request,sizeof(request),0,WIT_STATUS_UNSUPPORTED,id))return false;
    request.StackBytes=0;request.Entry=0;
    if(!creation_reject((WitU64)&request,sizeof(request),0,WIT_STATUS_BAD_ADDRESS,id))return false;
    return creation_memory(after)&&before.OwnedBytes==after.OwnedBytes&&before.PhysicalAvailableBytes==after.PhysicalAvailableBytes&&
        !creationEntered&&!creationCleaned;
}

extern "C" HANDLE WINAPI wit_creation_direct(LPSECURITY_ATTRIBUTES,SIZE_T,LPTHREAD_START_ROUTINE,LPVOID,DWORD,LPDWORD);
extern "C" WitU64 wit_test_thread_create(const WitUserStartup* startup,WitU64 mode)
{
    auto report=(WitU64*)WIT_GC_INFO_REPORT;report[0]=mode;report[1]=1;
    wit_native_security_initialize_system();wit_native_process_image_initialize(startup);wit_native_tls_initialize(startup);
    creationTls=911;creationEntered=creationCleaned=creationNativeId=0;
    if(!creation_bad_requests())return 4234;
    const DWORD caller=GetCurrentThreadId();
    for(unsigned round=0;round<4;++round){
        DWORD id=0;SetLastError(0x57321468);
        HANDLE thread=(round&1?wit_creation_direct:CreateThread)(nullptr,65536,created_worker,(void*)(uintptr_t)round,
            CREATE_SUSPENDED|STACK_SIZE_PARAM_IS_A_RESERVATION,&id);
        if(!thread||!id||id==caller||GetLastError()!=0x57321468)return 4210;
        WitThreadReferenceInfo info;
        if(!query(thread,info)||info.State!=WIT_THREAD_REFERENCE_SUSPENDED||info.SuspendCount!=1||
            info.StackHigh-info.StackLow!=65536||info.Rights!=WIT_THREAD_REFERENCE_ALL)return 4211;
        if(WaitForSingleObject(thread,0)!=WAIT_TIMEOUT||!SetThreadPriority(thread,THREAD_PRIORITY_NORMAL)||
            GetThreadPriority(thread)!=THREAD_PRIORITY_NORMAL||GetLastError()!=0x57321468)return 4212;
        (void)PalSwitchToThread();
        if(creationEntered!=round||creationCleaned!=round||creationTls!=911)return 4213;
        if(SetThreadPriority(thread,THREAD_PRIORITY_ABOVE_NORMAL)||GetLastError()!=ERROR_NOT_SUPPORTED)return 4214;
        HANDLE observer=nullptr;
        if(!duplicate(thread,&observer)||!CloseHandle(thread))return 4215;
        if(ResumeThread(observer)!=1||WaitForSingleObject(observer,10000)!=WAIT_OBJECT_0||
            creationEntered!=round+1||creationCleaned!=round+1||creationNativeId!=id||creationTls!=911)return 4216;
        if(!query(observer,info)||info.State!=WIT_THREAD_REFERENCE_EXITED||info.ExitCode!=42+round||
            info.StackLow||info.StackHigh||!CloseHandle(observer))return 4217;
        if(WaitForSingleObject(observer,0)!=WAIT_FAILED||GetLastError()!=ERROR_INVALID_HANDLE)return 4218;
    }
    // Exercise nonsuspended startup and optional DWORD output too.
    HANDLE immediate=CreateThread(nullptr,0,created_worker,nullptr,0,nullptr);
    if(!immediate||WaitForSingleObject(immediate,10000)!=WAIT_OBJECT_0||!CloseHandle(immediate)||
        creationEntered!=5||creationCleaned!=5)return 4219;
    DWORD sentinel=0xA5A5A5A5;
    if(CreateThread(nullptr,65537,created_worker,nullptr,CREATE_SUSPENDED,&sentinel)||
        GetLastError()!=ERROR_NOT_SUPPORTED||sentinel!=0xA5A5A5A5)return 4220;
    if(CreateThread(nullptr,0,created_worker,nullptr,0x8000,&sentinel)||
        GetLastError()!=ERROR_NOT_SUPPORTED||sentinel!=0xA5A5A5A5)return 4221;
    if(CreateThread(nullptr,0,nullptr,nullptr,0,&sentinel)||GetLastError()!=ERROR_INVALID_ADDRESS||sentinel!=0xA5A5A5A5)return 4222;
    WitU64 arena=0;
    if(wit_native_call(WIT_CALL_MEMORY_RESERVE,8192,4096,0,&arena)!=WIT_STATUS_OK||
        wit_native_call(WIT_CALL_MEMORY_COMMIT,arena,4096,3,nullptr)!=WIT_STATUS_OK)return 4223;
    auto edge=(unsigned char*)(arena+4093);for(unsigned i=0;i<3;++i)edge[i]=0xA5;
    WitUserMemoryInfo before,after;
    if(!creation_memory(before)||CreateThread(nullptr,0,created_worker,nullptr,CREATE_SUSPENDED,(DWORD*)edge)||
        GetLastError()!=ERROR_INVALID_ADDRESS||!creation_memory(after)||before.OwnedBytes!=after.OwnedBytes||
        before.PhysicalAvailableBytes!=after.PhysicalAvailableBytes)return 4224;
    for(unsigned i=0;i<3;++i)if(edge[i]!=0xA5)return 4225;
    if(wit_native_call(WIT_CALL_MEMORY_RELEASE,arena,0,0,nullptr)!=WIT_STATUS_OK)return 4226;
    // Leave one handle slot: reference grant succeeds, private identity grant fails.
    HANDLE held[WIT_HANDLE_CAPACITY];unsigned count=fill(held);
    if(!count||!CloseHandle(held[--count]))return 4227;
    if(!creation_memory(before))return 4228;
    for(unsigned attempt=0;attempt<6;++attempt){
        if(CreateThread(nullptr,0,created_worker,nullptr,CREATE_SUSPENDED,&sentinel)||
            GetLastError()!=ERROR_NOT_ENOUGH_MEMORY||sentinel!=0xA5A5A5A5)return 4229;
    }
    if(!creation_memory(after)||before.OwnedBytes!=after.OwnedBytes||before.PhysicalAvailableBytes!=after.PhysicalAvailableBytes)return 4230;
    while(count)if(!CloseHandle(held[--count]))return 4231;
    immediate=CreateThread(nullptr,0,created_worker,nullptr,0,&sentinel);
    if(!immediate||WaitForSingleObject(immediate,10000)!=WAIT_OBJECT_0||creationNativeId!=sentinel||
        !CloseHandle(immediate)||creationEntered!=6||creationCleaned!=6)return 4232;
    HANDLE event=CreateEventExW(nullptr,nullptr,CREATE_EVENT_MANUAL_RESET|CREATE_EVENT_INITIAL_SET,EVENT_MODIFY_STATE|SYNCHRONIZE);
    if(!event||WaitForSingleObject(event,0)!=WAIT_OBJECT_0||!ResetEvent(event)||
        WaitForSingleObject(event,0)!=WAIT_TIMEOUT||!SetEvent(event)||WaitForSingleObject(event,0)!=WAIT_OBJECT_0||!CloseHandle(event))return 4233;
    report[2]=creationEntered;report[3]=creationCleaned;report[4]=1;
    wit_native_tls_leave();
    return WIT_TEST_EXIT_CODE;
}
