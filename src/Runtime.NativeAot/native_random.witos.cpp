#include "pal.witos.h"
#include <bcrypt.h>
extern "C" NTSTATUS WINAPI wit_native_bcrypt_random(BCRYPT_ALG_HANDLE algorithm,PUCHAR output,ULONG bytes,ULONG flags)
{
    if(algorithm)return (NTSTATUS)0xC0000008UL;
    if(flags!=BCRYPT_USE_SYSTEM_PREFERRED_RNG)return (NTSTATUS)0xC00000BBUL;
    WitU64 copied=0;
    const auto status=wit_native_call(WIT_CALL_RANDOM,(uintptr_t)output,bytes,0,&copied);
    if(status==WIT_STATUS_OK){if(copied!=bytes)wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);return 0;}
    if(copied)wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    if(status==WIT_STATUS_BAD_ADDRESS)return (NTSTATUS)0xC0000005UL;
    if(status==WIT_STATUS_TOO_LARGE)return (NTSTATUS)0xC0000044UL;
    return (NTSTATUS)0xC000000DUL;
}
