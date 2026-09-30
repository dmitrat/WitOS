#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "protocol.h"
#include <errno.h>
extern "C" HMODULE WINAPI wit_module_direct_handle(LPCWSTR);
extern "C" DWORD WINAPI wit_module_direct_filename(HMODULE,LPWSTR,DWORD);
extern "C" FARPROC WINAPI wit_module_direct_proc(HMODULE,LPCSTR);
extern "C" const void* const __imp_GetModuleHandleW;
extern "C" const void* const __imp_GetModuleFileNameW;
extern "C" const void* const __imp_GetProcAddress;
static const wchar_t expected[]=L"boot:/RuntimeThreadFixture.pe";
static bool verify(bool anonymous)
{
    const auto image=wit_native_process_image();
    const HMODULE self=(HMODULE)(uintptr_t)image->Base;
    const DWORD saved=GetLastError();
    wchar_t buffer[WIT_IMAGE_RESOURCE_CAPACITY+1];
    for(auto& c:buffer)c=0x5A5A;
    const wchar_t* borrowed=(const wchar_t*)1;
    if(GetModuleHandleW(nullptr)!=self||wit_module_direct_handle(nullptr)!=self)return false;
    if(anonymous){
        if(PalGetModuleFileName(&borrowed,nullptr)||borrowed||GetLastError()!=ERROR_PATH_NOT_FOUND||
            GetModuleFileNameW(self,buffer,WIT_IMAGE_RESOURCE_CAPACITY)||GetLastError()!=ERROR_PATH_NOT_FOUND||buffer[0]!=0x5A5A||
            GetModuleHandleW(expected)||GetLastError()!=ERROR_MOD_NOT_FOUND)return false;
    }else{
        const DWORD length=(DWORD)(sizeof(expected)/sizeof(wchar_t)-1);
        if(PalGetModuleFileName(&borrowed,nullptr)!=(int32_t)length||borrowed!=(const wchar_t*)image->ResourceName||
            GetModuleFileNameW(nullptr,buffer,WIT_IMAGE_RESOURCE_CAPACITY)!=length||
            GetModuleHandleW(L"RuntimeThreadFixture.pe")!=self||wit_module_direct_handle(L"BOOT:\\RUNTIMETHREADFIXTURE.PE")!=self||GetLastError()!=saved)return false;
        for(DWORD i=0;i<=length;++i)if(buffer[i]!=expected[i]||borrowed[i]!=expected[i])return false;
        // Every capacity boundary, including NUL-only and exact character count.
        for(DWORD cap=0;cap<=length+2;++cap){
            for(auto& c:buffer)c=0x5A5A;SetLastError(saved);
            const DWORD got=wit_module_direct_filename(self,buffer,cap);
            if(!cap){if(got||GetLastError()!=ERROR_INSUFFICIENT_BUFFER) return false;}
            else {
                const DWORD copied=length<cap?length:cap-1;
                if(got!=(length<cap?length:cap)||GetLastError()!=(length<cap?saved:ERROR_INSUFFICIENT_BUFFER)||buffer[copied])return false;
                for(DWORD i=0;i<copied;++i)if(buffer[i]!=expected[i])return false;
            }
            for(DWORD i=cap;i<WIT_IMAGE_RESOURCE_CAPACITY+1;++i)if(buffer[i]!=0x5A5A)return false;
        }
        WitU64 arena=0;
        if(wit_native_call(WIT_CALL_MEMORY_RESERVE,8192,4096,0,&arena)!=WIT_STATUS_OK||wit_native_call(WIT_CALL_MEMORY_COMMIT,arena,4096,3,nullptr)!=WIT_STATUS_OK)return false;
        auto edge=(wchar_t*)(arena+4096)-length-1;
        if(GetModuleFileNameW(self,edge,length+1)!=length||GetModuleHandleW(edge)!=self)return false;
        for(DWORD i=0;i<=length;++i)if(edge[i]!=expected[i])return false;
        edge=(wchar_t*)(arena+4096)-WIT_IMAGE_RESOURCE_CAPACITY;
        for(DWORD i=0;i<WIT_IMAGE_RESOURCE_CAPACITY;++i)edge[i]=L'x';
        if(GetModuleHandleW(edge)||GetLastError()!=ERROR_MOD_NOT_FOUND||wit_native_call(WIT_CALL_MEMORY_RELEASE,arena,0,0,nullptr)!=WIT_STATUS_OK)return false;
    }
    const auto wrong=(HMODULE)(uintptr_t)(image->Base+1);
    buffer[0]=0x5A5A;
    if(GetModuleFileNameW(wrong,buffer,4)||GetLastError()!=ERROR_MOD_NOT_FOUND||buffer[0]!=0x5A5A||
        PalGetModuleFileName(&borrowed,wrong)||borrowed||GetLastError()!=ERROR_MOD_NOT_FOUND||
        PalGetModuleFileName(nullptr,self)||GetLastError()!=ERROR_INVALID_PARAMETER)return false;
    if(GetModuleHandleW(L"ntdll.dll")||GetLastError()!=ERROR_MOD_NOT_FOUND||
        GetProcAddress(nullptr,"RtlDllShutdownInProgress")||GetLastError()!=ERROR_PROC_NOT_FOUND||
        wit_module_direct_proc(self,"MissingExport")||GetLastError()!=ERROR_PROC_NOT_FOUND||
        GetProcAddress(self,(const char*)1)||GetLastError()!=ERROR_PROC_NOT_FOUND||
        GetProcAddress(wrong,"MissingExport")||GetLastError()!=ERROR_MOD_NOT_FOUND)return false;
    const void* slots[]={&__imp_GetModuleHandleW,&__imp_GetModuleFileNameW,&__imp_GetProcAddress};
    for(const auto slot:slots)if(!wit_native_image_range(image,(WitU64)slot,8,WIT_IMAGE_INFO_READ,WIT_IMAGE_INFO_WRITE|WIT_IMAGE_INFO_EXECUTE,1))return false;
    WitUserImageInfo changed=*image;
    changed.ResourceNameLength=WIT_IMAGE_RESOURCE_CAPACITY;if(wit_native_image_valid(&changed))return false;
    changed=*image;changed.ResourceReserved=1;if(wit_native_image_valid(&changed))return false;
    changed=*image;changed.ResourceName[changed.ResourceNameLength]=L'x';if(wit_native_image_valid(&changed))return false;
    if(!anonymous){changed=*image;changed.ResourceName[0]=L'z';if(wit_native_image_valid(&changed))return false;}
    SetLastError(saved);return true;
}
static WitU64 worker(WitU64 index)
{
    SetLastError((DWORD)(4200+index));errno=(int)(4300+index);
    if(!verify(false)||GetLastError()!=4200+index||errno!=4300+index)return 3001;
    return WIT_TEST_EXIT_CODE;
}
extern "C" WitU64 wit_test_module_names(const WitUserStartup* startup,WitU64 mode)
{
    auto report=(WitU64*)WIT_GC_INFO_REPORT;report[1]=17179869184ULL;
    wit_native_security_initialize_system();
    wchar_t untouched=0x5A5A;const wchar_t* borrowed=(const wchar_t*)1;
    if(GetModuleHandleW(nullptr)||GetLastError()!=ERROR_NOT_READY||GetModuleFileNameW(nullptr,&untouched,1)||untouched!=0x5A5A||
        PalGetModuleFileName(&borrowed,nullptr)||borrowed||GetLastError()!=ERROR_NOT_READY)return 3002;
    wit_native_process_image_initialize(startup);
    const bool tls=mode==71;if(tls)wit_native_tls_initialize(startup);
    SetLastError(0x14253647);if(tls)errno=177;
    if(!verify(mode==73))return 3003;
    if(tls){
        for(WitU64 i=0;i<3;++i){WitU64 handle=0,result=0;
            if(wit_native_thread_create(worker,i,&handle)!=WIT_STATUS_OK||wit_native_call(WIT_CALL_THREAD_JOIN,handle,0,0,&result)!=WIT_STATUS_OK||result!=WIT_TEST_EXIT_CODE)return 3004;
        }
        wit_native_tls_leave();
        if(!verify(false)||errno!=177)return 3005;
    }
    if(GetLastError()!=0x14253647)return 3006;
    report[2]=0x4D4F44554C45ULL;
    return WIT_TEST_EXIT_CODE;
}
