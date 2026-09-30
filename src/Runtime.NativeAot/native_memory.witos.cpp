#include "pal.witos.h"
namespace {
bool protection(DWORD flags,WitU64& mode)
{
    switch(flags){case PAGE_NOACCESS:mode=WIT_MEMORY_NONE;return true;
    case PAGE_READONLY:mode=WIT_MEMORY_READ;return true;
    case PAGE_READWRITE:mode=WIT_MEMORY_READ|WIT_MEMORY_WRITE;return true;
    default:SetLastError(ERROR_NOT_SUPPORTED);return false;}
}
bool range(uintptr_t address,SIZE_T size,WitU64& low,WitU64& length)
{
    if(!size||address>UINTPTR_MAX-size||address+size>UINTPTR_MAX-4095){SetLastError(ERROR_INVALID_PARAMETER);return false;}
    low=address&~WitU64(4095);
    length=((address+size+4095)&~WitU64(4095))-low;
    return true;
}
}
extern "C" LPVOID WINAPI wit_native_virtual_alloc(LPVOID requested,SIZE_T size,DWORD kind,DWORD protect)
{
    WitU64 mode,low,length;
    if(!range((uintptr_t)requested,size,low,length))return nullptr;
    if(!protection(protect,mode))return nullptr;
    if(kind==MEM_RESET){
        if(!requested){SetLastError(ERROR_INVALID_PARAMETER);return nullptr;}
        return wit_pal_result(wit_native_call(WIT_CALL_MEMORY_RESET,low,length,0,nullptr))?(void*)low:nullptr;
    }
    if(!kind){SetLastError(ERROR_INVALID_PARAMETER);return nullptr;}
    if(kind&~(MEM_RESERVE|MEM_COMMIT)){SetLastError(ERROR_NOT_SUPPORTED);return nullptr;}
    // Fixed-address reservation is not supplied by the current kernel API.
    // A non-null commit is supported only inside an already owned reservation.
    if(requested&&(kind&MEM_RESERVE)){SetLastError(ERROR_NOT_SUPPORTED);return nullptr;}
    const bool reserve=!requested;
    if(reserve){
        if(!wit_pal_result(wit_native_call(WIT_CALL_MEMORY_RESERVE,length,65536,0,&low)))return nullptr;
    }
    if(kind&MEM_COMMIT){
        if(!wit_pal_result(wit_native_call(WIT_CALL_MEMORY_COMMIT,low,length,mode,nullptr))){
            // Raw rollback preserves the original native last-error value.
            if(reserve&&wit_native_call(WIT_CALL_MEMORY_RELEASE,low,0,0,nullptr)!=WIT_STATUS_OK)
                wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
            return nullptr;
        }
    }
    return (LPVOID)low;
}
extern "C" BOOL WINAPI wit_native_virtual_free(LPVOID address,SIZE_T size,DWORD kind)
{
    if(!address){SetLastError(ERROR_INVALID_PARAMETER);return FALSE;}
    if(kind==MEM_RELEASE){
        if(size){SetLastError(ERROR_INVALID_PARAMETER);return FALSE;}
        return (BOOL)wit_pal_result(wit_native_call(WIT_CALL_MEMORY_RELEASE,(uintptr_t)address,0,0,nullptr));
    }
    if(kind==MEM_DECOMMIT){
        // Decommit of an explicit byte interval; zero-size whole-reservation
        // discovery is not part of this bounded prototype interface.
        if(!size){SetLastError(ERROR_NOT_SUPPORTED);return FALSE;}
        WitU64 low,length;
        if(!range((uintptr_t)address,size,low,length))return FALSE;
        return (BOOL)wit_pal_result(wit_native_call(WIT_CALL_MEMORY_DECOMMIT,low,length,0,nullptr));
    }
    SetLastError(ERROR_NOT_SUPPORTED);return FALSE;
}
