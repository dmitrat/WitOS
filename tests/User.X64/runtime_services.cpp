#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "protocol.h"
#include <minipal/time.h>
#include <errno.h>
extern "C" BOOL WINAPI wit_services_direct_close(HANDLE);
extern "C" void WINAPI wit_services_direct_sleep(DWORD);
static bool calls()
{
    const DWORD saved=GetLastError();
    HANDLE event=PalCreateEventW(nullptr,false,true,nullptr);
    if(!event||!CloseHandle(event)||GetLastError()!=saved)return false;
    HANDLE next=PalCreateEventW(nullptr,false,true,nullptr);
    if(!next||next==event||wit_services_direct_close(event)||GetLastError()!=ERROR_INVALID_HANDLE||
        PalWaitForSingleObjectEx(next,0,false)!=WAIT_OBJECT_0)return false;
    SetLastError(saved);
    if(!wit_services_direct_close(next)||GetLastError()!=saved)return false;
    Sleep(0);
    const auto start=minipal_hires_ticks();
    wit_services_direct_sleep(1);
    const auto end=minipal_hires_ticks();
    return end-start>=minipal_hires_tick_frequency()/1000&&GetLastError()==saved;
}
static WitU64 worker(WitU64 index)
{
    SetLastError(DWORD(2900+index));errno=int(3000+index);
    HANDLE event=PalCreateEventW(nullptr,false,true,nullptr);
    if(!event)return 2601;
    Sleep(1);
    if(PalWaitForSingleObjectEx(event,0,false)!=WAIT_OBJECT_0||!CloseHandle(event)||
        GetLastError()!=2900+index||errno!=3000+index)return 2602;
    return WIT_TEST_EXIT_CODE;
}
extern "C" WitU64 wit_test_services(const WitUserStartup* startup,WitU64 mode)
{
    ((WitU64*)WIT_GC_INFO_REPORT)[1]=1073741824;
    wit_native_security_initialize_system();wit_native_process_image_initialize(startup);
    const bool tls=mode==62;if(tls)wit_native_tls_initialize(startup);
    SetLastError(0x72183465);if(tls)errno=113;
    if(!calls())return 2603;
    if(tls){WitU64 handles[3],result;
        for(WitU64 i=0;i<3;++i)if(wit_native_thread_create(worker,i,&handles[i])!=WIT_STATUS_OK)return 2604;
        for(unsigned i=0;i<3;++i)if(wit_native_call(WIT_CALL_THREAD_JOIN,handles[i],0,0,&result)!=WIT_STATUS_OK||result!=WIT_TEST_EXIT_CODE)return 2605;
        wit_native_tls_leave();
    }
    return GetLastError()==0x72183465&&(!tls||errno==113)?WIT_TEST_EXIT_CODE:2606;
}
