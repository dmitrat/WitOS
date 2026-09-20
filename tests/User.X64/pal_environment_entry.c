#include "tls.h"
WitU64 wit_environment_configure(const WitUserStartup *startup);
WitU64 wit_environment_program(void);
WitU64 wit_environment_finish(WitU64 result);
WitU64 wit_native_main(const WitUserStartup *startup)
{
    WitU64 result = wit_environment_configure(startup);
    if (result) return result;
    wit_native_tls_initialize(startup);
    result = wit_environment_program();
    wit_native_tls_leave();
    return wit_environment_finish(result);
}
