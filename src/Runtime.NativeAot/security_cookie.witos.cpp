#include "native_security.h"
#include "diagnostics.h"
extern "C" {
WitU64 __security_cookie;
WitU64 __security_cookie_complement;
volatile WitU32 wit_native_security_state;
}
extern "C" void wit_native_security_initialize(WitU64 entropy)
{
    const WitU64 value=entropy&0x0000FFFFFFFFFFFFULL;
    if(!value||value==0x00002B992DDFA232ULL||!wit_native_claim_startup(&wit_native_security_state))
        wit_native_security_failure();
    __security_cookie=value;
    __security_cookie_complement=~value;
    wit_native_security_state=2;
}
extern "C" void wit_native_security_initialize_system(void)
{
    if(wit_native_security_state)wit_native_security_failure();
    for(unsigned i=0;i<4;++i){
        WitU64 entropy=0,copied=0;
        if(wit_native_call(WIT_CALL_RANDOM,(WitU64)&entropy,sizeof(entropy),0,&copied)!=WIT_STATUS_OK||copied!=sizeof(entropy))
            wit_native_security_failure();
        const auto cookie=entropy&0x0000FFFFFFFFFFFFULL;
        if(cookie&&cookie!=0x2B992DDFA232ULL){wit_native_security_initialize(entropy);*(volatile WitU64*)&entropy=0;return;}
        *(volatile WitU64*)&entropy=0;
    }
    wit_native_security_failure();
}
extern "C" WIT_NORETURN void wit_native_security_failure(void)
{
    wit_native_fail_fast(WIT_NATIVE_GS_FAILURE_EXIT);
}
