#include "tls.h"
void wit_background_configure(const WitUserStartup *startup);
WitU64 wit_background_program(const WitUserStartup *startup);
WitU64 wit_background_finish(WitU64 result);
WitU64 wit_native_main(const WitUserStartup *startup)
{
    WitU64 result;
    wit_background_configure(startup);
    wit_native_tls_initialize(startup);
    result = wit_background_program(startup);
    wit_native_tls_leave();
    return wit_background_finish(result);
}
