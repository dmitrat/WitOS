#include "tls.h"
void wit_dynamic_configure(const WitUserStartup *startup);
WitU64 wit_dynamic_program(const WitUserStartup *startup);
WitU64 wit_dynamic_finish(WitU64 code);
WitU64 wit_native_main(const WitUserStartup *startup)
{
    WitU64 code;
    wit_dynamic_configure(startup);
    wit_native_tls_initialize(startup);
    code = wit_dynamic_program(startup);
    wit_native_tls_leave();
    wit_native_tls_leave(); // Explicitly idempotent after successful detach.
    return wit_dynamic_finish(code);
}

WitU64 wit_dynamic_lazy_body(WitU64 index);
void wit_dynamic_lazy_entry(WitU64 index)
{
    wit_native_thread_exit(wit_dynamic_lazy_body(index));
}
