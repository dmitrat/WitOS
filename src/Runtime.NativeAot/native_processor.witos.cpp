#include "pal.witos.h"
static_assert(sizeof(PROCESSOR_NUMBER)==4);
extern "C" void WINAPI wit_native_processor_number(PPROCESSOR_NUMBER output)
{
    WitU64 copied=0;
    if(wit_native_call(WIT_CALL_PROCESSOR_QUERY,(uintptr_t)output,sizeof(*output),0,&copied)!=WIT_STATUS_OK||copied!=sizeof(*output))
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
