#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "protocol.h"
#include <new>
#include <errno.h>
static volatile WitU64 callbacks;
static volatile WitU32 callbackGate;
static bool query(WitThreadNameInfo& info)
{
    WitU64 id=0;
    return wit_native_call(WIT_CALL_THREAD_NAME_QUERY,(WitU64)&info,sizeof(info),WIT_THREAD_NAME_VERSION,nullptr)==WIT_STATUS_OK&&
        wit_native_call(WIT_CALL_THREAD_CURRENT,0,0,0,&id)==WIT_STATUS_OK&&info.Version==WIT_THREAD_NAME_VERSION&&info.Size==sizeof(info)&&
        info.ThreadId==id&&!info.Reserved&&info.Length<WIT_THREAD_NAME_CAPACITY;
}
static bool equals(const wchar_t* expected)
{
    WitThreadNameInfo info;
    if(!query(info))return false;
    WitU32 i=0;
    while(expected[i]){if(i>=info.Length||info.Name[i]!=expected[i])return false;++i;}
    if(i!=info.Length)return false;
    for(;i<WIT_THREAD_NAME_CAPACITY;++i)if(info.Name[i])return false;
    return true;
}
static void exited(void*)
{
    if(!equals(L"worker"))wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    while(!wit_native_try_lock(&callbackGate))wit_native_call(WIT_CALL_THREAD_YIELD,0,0,0,nullptr);
    ++callbacks;wit_native_unlock(&callbackGate);
}
static WitU64 worker(WitU64 index)
{
    SetLastError((DWORD)(5200+index));errno=(int)(5300+index);
    if(!equals(L"")||!PalSetCurrentThreadName("worker")||!equals(L"worker")||
        wit_native_thread_on_exit(exited,nullptr)!=WIT_STATUS_OK||GetLastError()!=5200+index||errno!=5300+index)return 3101;
    return WIT_TEST_EXIT_CODE;
}
extern "C" WitU64 wit_test_thread_names(const WitUserStartup* startup,WitU64 mode)
{
    auto report=(WitU64*)WIT_GC_INFO_REPORT;report[1]=34359738368ULL;
    wit_native_security_initialize_system();
    if(!equals(L"")||!PalSetCurrentThreadName("early")||!equals(L"early"))return 3102;
    wit_native_process_image_initialize(startup);
    const bool tls=mode==74;if(tls)wit_native_tls_initialize(startup);
    SetLastError(0x23456781);if(tls)errno=187;
    if(!PalSetCurrentThreadName("\xE2\x82\xAC\xF0\x9F\x98\x80")||!equals(L"\x20AC\xD83D\xDE00")||GetLastError()!=0x23456781||
        !PalSetCurrentThreadName("\xE0\x80x")||!equals(L"\xFFFDx"))return 3103;
    const wchar_t unpaired[]={0xD800,0};
    if(!PalSetCurrentThreadNameW(unpaired)||!equals(unpaired)||!PalSetCurrentThreadNameW(L"main"))return 3104;
    WitUserThreadInfo thread;
    if(wit_native_call(WIT_CALL_THREAD_QUERY,(WitU64)&thread,sizeof(thread),WIT_THREAD_INFO_VERSION,nullptr)!=WIT_STATUS_OK)return 3105;
    auto raw=(WitU64*)thread.RawTls;const WitU64 savedId=raw[1];raw[1]=~savedId;
    const bool identity=PalSetCurrentThreadNameW(L"main")&&equals(L"main");raw[1]=savedId;if(!identity)return 3106;
    WitU64 arena=0;
    if(wit_native_call(WIT_CALL_MEMORY_RESERVE,8192,4096,0,&arena)!=WIT_STATUS_OK||wit_native_call(WIT_CALL_MEMORY_COMMIT,arena,4096,3,nullptr)!=WIT_STATUS_OK)return 3107;
    auto edge=(wchar_t*)(arena+4096)-WIT_THREAD_NAME_CAPACITY;
    for(WitU32 i=0;i<WIT_THREAD_NAME_CAPACITY-1;++i)edge[i]=L'x';edge[WIT_THREAD_NAME_CAPACITY-1]=0;
    if(!PalSetCurrentThreadNameW(edge)||!equals(edge))return 3108;
    edge[WIT_THREAD_NAME_CAPACITY-1]=L'x';
    if(PalSetCurrentThreadNameW(edge)||GetLastError()!=ERROR_BUFFER_OVERFLOW)return 3109;
    WitThreadNameInfo info;if(!query(info)||info.Length!=WIT_THREAD_NAME_CAPACITY-1)return 3110;
    if(!PalSetCurrentThreadNameW(L"main"))return 3111;
    const WitU16 embedded[]={L'x',0};
    if(wit_native_call(WIT_CALL_THREAD_NAME_SET,arena+4094,3,0,nullptr)!=WIT_STATUS_BAD_ADDRESS||
        wit_native_call(WIT_CALL_THREAD_NAME_SET,~0ULL,1,1,nullptr)!=WIT_STATUS_INVALID_ARGUMENT||
        wit_native_call(WIT_CALL_THREAD_NAME_SET,~0ULL,~0ULL,0,nullptr)!=WIT_STATUS_TOO_LARGE||
        wit_native_call(WIT_CALL_THREAD_NAME_SET,(WitU64)embedded,2,0,nullptr)!=WIT_STATUS_INVALID_ARGUMENT||!equals(L"main"))return 3112;
    auto tail=(WitU8*)(arena+4092);for(unsigned i=0;i<4;++i)tail[i]=0x5A;
    if(wit_native_call(WIT_CALL_THREAD_NAME_QUERY,(WitU64)tail,sizeof(info),WIT_THREAD_NAME_VERSION,nullptr)!=WIT_STATUS_BAD_ADDRESS)return 3113;
    for(unsigned i=0;i<4;++i)if(tail[i]!=0x5A)return 3114;
    if(wit_native_call(WIT_CALL_THREAD_NAME_QUERY,startup->ImageInfo,sizeof(info),WIT_THREAD_NAME_VERSION,nullptr)!=WIT_STATUS_BAD_ADDRESS||
        wit_native_call(WIT_CALL_THREAD_NAME_QUERY,(WitU64)tail,sizeof(info)-1,WIT_THREAD_NAME_VERSION,nullptr)!=WIT_STATUS_INVALID_ARGUMENT||
        wit_native_call(WIT_CALL_THREAD_NAME_QUERY,(WitU64)tail,sizeof(info),0,nullptr)!=WIT_STATUS_UNSUPPORTED)return 3115;
    auto exact=(WitThreadNameInfo*)(arena+4096-sizeof(info));
    if(wit_native_call(WIT_CALL_THREAD_NAME_QUERY,(WitU64)exact,sizeof(info),WIT_THREAD_NAME_VERSION,nullptr)!=WIT_STATUS_OK||exact->Length!=4||
        wit_native_call(WIT_CALL_MEMORY_RELEASE,arena,0,0,nullptr)!=WIT_STATUS_OK)return 3116;
    if(PalSetCurrentThreadName(nullptr)||GetLastError()!=ERROR_INVALID_PARAMETER||PalSetCurrentThreadNameW(nullptr)||GetLastError()!=ERROR_INVALID_PARAMETER||!equals(L"main"))return 3117;
    char tooLong[3*(WIT_THREAD_NAME_CAPACITY-1)+2];for(unsigned i=0;i<sizeof(tooLong)-1;++i)tooLong[i]='x';tooLong[sizeof(tooLong)-1]=0;
    if(PalSetCurrentThreadName(tooLong)||GetLastError()!=ERROR_BUFFER_OVERFLOW||!equals(L"main"))return 3118;
    // Exhaust the real native allocation registry; failed UTF conversion must not rename.
    char* held[256];unsigned used=0;
    while(used<256&&(held[used]=new(std::nothrow) char[1])!=nullptr)++used;
    if(!used||used==256||PalSetCurrentThreadName("lost")||GetLastError()!=ERROR_NOT_ENOUGH_MEMORY||!equals(L"main"))return 3119;
    while(used)delete[] held[--used];
    if(wit_native_call(WIT_CALL_THREAD_NAME_SET,~0ULL,0,0,nullptr)!=WIT_STATUS_OK||!equals(L"")||!PalSetCurrentThreadName("main"))return 3120;
    SetLastError(0x23456781);
    if(tls){WitU64 handles[3],result;
        for(WitU64 i=0;i<3;++i)if(wit_native_thread_create(worker,i,&handles[i])!=WIT_STATUS_OK)return 3121;
        for(unsigned i=0;i<3;++i)if(wit_native_call(WIT_CALL_THREAD_JOIN,handles[i],0,0,&result)!=WIT_STATUS_OK||result!=WIT_TEST_EXIT_CODE)return 3122;
        if(wit_native_thread_create(worker,3,&handles[0])!=WIT_STATUS_OK||wit_native_call(WIT_CALL_THREAD_JOIN,handles[0],0,0,&result)!=WIT_STATUS_OK||result!=WIT_TEST_EXIT_CODE||callbacks!=4)return 3123;
        wit_native_tls_leave();
    }
    if(!equals(L"main")||GetLastError()!=0x23456781||(tls&&errno!=187))return 3124;
    report[2]=callbacks;return WIT_TEST_EXIT_CODE;
}
