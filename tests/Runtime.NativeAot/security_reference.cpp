#include <windows.h>
#include <bcrypt.h>
#include "native_security.h"
extern "C" unsigned wit_gs_reference_protected(unsigned);
extern "C" int wit_native_claim_startup(volatile WitU32* state)
{
    return InterlockedCompareExchange((volatile LONG*)state,1,0)==0;
}
extern "C" WIT_NORETURN void wit_native_fail_fast(WitU64 code) { ExitProcess((UINT)code); }
extern "C" void wit_gs_reference_corrupt(volatile unsigned char* buffer,unsigned tamper)
{
    // The tool validates the compiler listing: the generated offset points exactly to the GS
    // slot, before this negative child process is ever executed.
    if(tamper)buffer[WITOS_GS_COOKIE_OFFSET]^=1;
}
// A real Windows entropy transport for this HOSTED mechanism reference.
// Any unexpected call fails the reference instead of supplying a dummy API.
extern "C" WitU64 wit_native_call(WitU64 call,WitU64 output,WitU64 bytes,WitU64 flags,WitU64* copied)
{
    if(call!=WIT_CALL_RANDOM||!output||bytes!=8||flags||!copied)ExitProcess(211);
    *copied=0;
    if(BCryptGenRandom(nullptr,(PUCHAR)output,(ULONG)bytes,BCRYPT_USE_SYSTEM_PREFERRED_RNG))ExitProcess(212);
    *copied=bytes;return WIT_STATUS_OK;
}
extern "C" void wit_gs_reference_start()
{
    wit_native_security_initialize_system();
    char mode[4];mode[0]=0;
    GetEnvironmentVariableA("WITOS_GS_REFERENCE_CASE",mode,sizeof(mode));
    const auto result=wit_gs_reference_protected(mode[0]=='1');
    ExitProcess(result==0?42:203);
}
