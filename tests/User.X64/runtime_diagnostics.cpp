#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "protocol.h"
#include <new>
#include <errno.h>
extern "C" DWORD WINAPI wit_test_direct_FormatMessageW(DWORD,LPCVOID,DWORD,DWORD,LPWSTR,DWORD,va_list*);
extern "C" HLOCAL WINAPI wit_test_direct_LocalFree(HLOCAL);
extern "C" HANDLE WINAPI wit_test_direct_RegisterEventSourceW(LPCWSTR,LPCWSTR);
extern "C" BOOL WINAPI wit_test_direct_DeregisterEventSource(HANDLE);
extern "C" BOOL WINAPI wit_test_direct_ReportEventW(HANDLE,WORD,WORD,DWORD,PSID,WORD,DWORD,LPCWSTR*,LPVOID);
extern "C" BOOL WINAPI wit_test_direct_IsDebuggerPresent();
extern "C" const void* const __imp_FormatMessageW;
extern "C" const void* const __imp_LocalFree;
static constexpr DWORD flags=FORMAT_MESSAGE_FROM_SYSTEM|FORMAT_MESSAGE_IGNORE_INSERTS|FORMAT_MESSAGE_ARGUMENT_ARRAY;
static bool same(const wchar_t* a,const wchar_t* b)
{
    for(unsigned i=0;i<256;++i){if(a[i]!=b[i])return false;if(!a[i])return true;}return false;
}
static bool optional_services(HANDLE validConsole)
{
    const DWORD previous=GetLastError();
    if(IsDebuggerPresent()||wit_test_direct_IsDebuggerPresent()||GetLastError()!=previous)return false;
    if(RegisterEventSourceW(nullptr,L".NET Runtime")||GetLastError()!=ERROR_NOT_SUPPORTED||
        wit_test_direct_RegisterEventSourceW(L"remote",L".NET Runtime")||GetLastError()!=ERROR_NOT_SUPPORTED)return false;
    if(DeregisterEventSource(validConsole)||GetLastError()!=ERROR_INVALID_HANDLE||
        wit_test_direct_DeregisterEventSource(nullptr)||GetLastError()!=ERROR_INVALID_HANDLE||
        ReportEventW(validConsole,EVENTLOG_ERROR_TYPE,0,1000,nullptr,1,1,(LPCWSTR*)~0ULL,(void*)~0ULL)||GetLastError()!=ERROR_INVALID_HANDLE||
        wit_test_direct_ReportEventW(nullptr,0,0,0,nullptr,0,0,nullptr,nullptr)||GetLastError()!=ERROR_INVALID_HANDLE)return false;
    SetLastError(previous);return true;
}
static WitU64 worker(WitU64 index)
{
    SetLastError((DWORD)(6200+index));errno=(int)(6300+index);
    wchar_t* text=nullptr;
    if(!FormatMessageW(flags|FORMAT_MESSAGE_ALLOCATE_BUFFER,nullptr,ERROR_BUSY,0,(LPWSTR)&text,0,nullptr)||!text||
        !optional_services((HANDLE)wit_native_process_console())||GetLastError()!=6200+index||errno!=6300+index)return 3201;
    return (WitU64)text; // Component-owned allocation survives the allocating thread.
}
extern "C" WitU64 wit_test_diagnostics(const WitUserStartup* startup,WitU64 mode)
{
    auto report=(WitU64*)WIT_GC_INFO_REPORT;report[1]=68719476736ULL;
    wit_native_security_initialize_system();
    wchar_t text[256],copy[256];
    if(!FormatMessageW(flags,nullptr,ERROR_NOT_READY,0,text,256,nullptr)||!optional_services((HANDLE)startup->ConsoleHandle))return 3202;
    wit_native_process_image_initialize(startup);const bool tls=mode==76;if(tls)wit_native_tls_initialize(startup);
    SetLastError(0x34567812);if(tls)errno=197;
    const DWORD codes[]={ERROR_SIGNAL_REFUSED,ERROR_SUCCESS,ERROR_ACCESS_DENIED,ERROR_INVALID_HANDLE,ERROR_NOT_ENOUGH_MEMORY,ERROR_INVALID_ADDRESS,ERROR_NOT_SUPPORTED,
        ERROR_INVALID_PARAMETER,ERROR_INVALID_FLAGS,ERROR_BUSY,ERROR_POSSIBLE_DEADLOCK,ERROR_TIMEOUT,ERROR_GEN_FAILURE,ERROR_ALREADY_INITIALIZED,
        ERROR_INVALID_STATE,ERROR_NOT_READY,ERROR_BUFFER_OVERFLOW,ERROR_INSUFFICIENT_BUFFER,ERROR_NOT_ENOUGH_QUOTA,ERROR_ENVVAR_NOT_FOUND,
        ERROR_NO_UNICODE_TRANSLATION,ERROR_PATH_NOT_FOUND,ERROR_MOD_NOT_FOUND,ERROR_PROC_NOT_FOUND,ERROR_MR_MID_NOT_FOUND,ERROR_RESOURCE_LANG_NOT_FOUND,ERROR_RESOURCE_TYPE_NOT_FOUND};
    for(const auto code:codes){
        const DWORD n=FormatMessageW(flags,nullptr,code,0,text,256,(va_list*)~0ULL);
        if(!n||n>=256||text[n]||wit_test_direct_FormatMessageW(flags|255,nullptr,code,0x409,copy,256,nullptr)!=n||!same(text,copy)||GetLastError()!=0x34567812)return 3203;
    }
    const DWORD n=FormatMessageW(flags,nullptr,ERROR_INVALID_HANDLE,0,text,256,nullptr);
    if(!same(text,L"The resource handle is invalid or has been closed."))return 3204;
    for(DWORD cap=0;cap<=n+1;++cap){
        for(auto& c:copy)c=0x5A5A;
        const DWORD result=FormatMessageW(flags,nullptr,ERROR_INVALID_HANDLE,0,copy,cap,nullptr);
        if(cap<=n){if(result||GetLastError()!=ERROR_INSUFFICIENT_BUFFER)return 3205;for(auto c:copy)if(c!=0x5A5A)return 3206;}
        else if(result!=n||!same(text,copy)||copy[n+1]!=0x5A5A)return 3207;
    }
    auto image=wit_native_process_image();
    if(FormatMessageW(flags|FORMAT_MESSAGE_FROM_HMODULE,(void*)image->Base,ERROR_INVALID_HANDLE,0,copy,256,nullptr)!=n||!same(text,copy))return 3208;
    for(auto& c:copy)c=0x5A5A;
    if(FormatMessageW(flags,nullptr,0xF0012345,0,copy,256,nullptr)||GetLastError()!=ERROR_MR_MID_NOT_FOUND||
        FormatMessageW(flags,nullptr,ERROR_BUSY,0x411,copy,256,nullptr)||GetLastError()!=ERROR_RESOURCE_LANG_NOT_FOUND||
        FormatMessageW(flags|0x80000000,nullptr,ERROR_BUSY,0,copy,256,nullptr)||GetLastError()!=ERROR_INVALID_FLAGS||
        FormatMessageW(FORMAT_MESSAGE_FROM_STRING,L"ignored",0,0,copy,256,nullptr)||GetLastError()!=ERROR_NOT_SUPPORTED||
        FormatMessageW(FORMAT_MESSAGE_FROM_HMODULE,(void*)image->Base,ERROR_BUSY,0,copy,256,nullptr)||GetLastError()!=ERROR_RESOURCE_TYPE_NOT_FOUND||
        FormatMessageW(flags|FORMAT_MESSAGE_FROM_HMODULE,(void*)(image->Base+1),ERROR_BUSY,0,copy,256,nullptr)||GetLastError()!=ERROR_MOD_NOT_FOUND)return 3209;
    for(auto c:copy)if(c!=0x5A5A)return 3210;
    WitU64 arena=0;
    if(wit_native_call(WIT_CALL_MEMORY_RESERVE,8192,4096,0,&arena)!=WIT_STATUS_OK||wit_native_call(WIT_CALL_MEMORY_COMMIT,arena,4096,3,nullptr)!=WIT_STATUS_OK)return 3211;
    auto edge=(wchar_t*)(arena+4096)-(n+1);
    if(FormatMessageW(flags,nullptr,ERROR_INVALID_HANDLE,0,edge,n+1,nullptr)!=n||!same(edge,text)||wit_native_call(WIT_CALL_MEMORY_RELEASE,arena,0,0,nullptr)!=WIT_STATUS_OK)return 3212;
    wchar_t *a=nullptr,*b=nullptr;char* cpp=new(std::nothrow) char[16];if(!cpp)return 3213;cpp[0]='x';
    if(FormatMessageW(flags|FORMAT_MESSAGE_ALLOCATE_BUFFER,nullptr,ERROR_INVALID_HANDLE,0,(LPWSTR)&a,4096,nullptr)!=n||!a||!same(a,text)||
        wit_test_direct_FormatMessageW(flags|FORMAT_MESSAGE_ALLOCATE_BUFFER,nullptr,ERROR_BUSY,0,(LPWSTR)&b,0,nullptr)==0||!b)return 3214;
    a[4095]=0x1234; // nSize is a minimum allocation size in UTF-16 units.
    SetLastError(0x45678923);
    if(LocalFree(nullptr)||GetLastError()!=0x45678923||LocalFree(cpp)!=cpp||GetLastError()!=ERROR_INVALID_HANDLE||cpp[0]!='x'||
        LocalFree(a+1)!=a+1||GetLastError()!=ERROR_INVALID_HANDLE||a[4095]!=0x1234)return 3215;
    SetLastError(0x45678923);if(wit_test_direct_LocalFree(b)||GetLastError()!=0x45678923||cpp[0]!='x'||!same(a,text))return 3216;
    if(LocalFree(b)!=b||GetLastError()!=ERROR_INVALID_HANDLE||LocalFree((HLOCAL)image->Base)!=(HLOCAL)image->Base||GetLastError()!=ERROR_INVALID_HANDLE)return 3217;
    if(LocalFree(a))return 3218;delete[] cpp;
    wchar_t* untouched=(wchar_t*)0x1234;
    if(FormatMessageW(flags|FORMAT_MESSAGE_ALLOCATE_BUFFER,nullptr,ERROR_BUSY,0,(LPWSTR)&untouched,0xFFFFFFFF,nullptr)||GetLastError()!=ERROR_NOT_ENOUGH_MEMORY||untouched!=(wchar_t*)0x1234)return 3219;
    char* held[256];unsigned count=0;
    while(count<256&&(held[count]=new(std::nothrow) char[1])!=nullptr)++count;
    if(!count||count==256||FormatMessageW(flags|FORMAT_MESSAGE_ALLOCATE_BUFFER,nullptr,ERROR_BUSY,0,(LPWSTR)&untouched,0,nullptr)||GetLastError()!=ERROR_NOT_ENOUGH_MEMORY||untouched!=(wchar_t*)0x1234)return 3220;
    while(count)delete[] held[--count];
    SetLastError(0x34567812);
    if(!optional_services((HANDLE)startup->ConsoleHandle)||!wit_native_image_range(image,(WitU64)&__imp_FormatMessageW,8,WIT_IMAGE_INFO_READ,WIT_IMAGE_INFO_WRITE|WIT_IMAGE_INFO_EXECUTE,1)||
        !wit_native_image_range(image,(WitU64)&__imp_LocalFree,8,WIT_IMAGE_INFO_READ,WIT_IMAGE_INFO_WRITE|WIT_IMAGE_INFO_EXECUTE,1))return 3221;
    if(tls){WitU64 handles[3],value;
        for(WitU64 i=0;i<3;++i)if(wit_native_thread_create(worker,i,&handles[i])!=WIT_STATUS_OK)return 3222;
        for(unsigned i=0;i<3;++i){
            if(wit_native_call(WIT_CALL_THREAD_JOIN,handles[i],0,0,&value)!=WIT_STATUS_OK||value<0x10000)return 3223;
            if(!same((const wchar_t*)value,L"The resource is currently in use.")||LocalFree((HLOCAL)value))return 3224;
        }
        wit_native_tls_leave();
    }
    if(GetLastError()!=0x34567812||(tls&&errno!=197))return 3225;
    report[2]=27;return WIT_TEST_EXIT_CODE;
}
