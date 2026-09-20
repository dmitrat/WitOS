#include "tls.h"
void wit_module_configure(const WitUserStartup *startup);
WitU64 wit_module_program(void);
WitU64 wit_module_finish(WitU64 result);
WitU64 wit_native_main(const WitUserStartup *startup)
{
    WitU64 result;
    wit_module_configure(startup);
    wit_native_tls_initialize(startup);
    result = wit_module_program();
    wit_native_tls_leave();
    return wit_module_finish(result);
}
