#include "tls.h"
#include "protocol.h"
extern "C" WitU64 wit_test_thread_create(const WitUserStartup *, WitU64);
extern "C" WitU64 wit_test_diagnostics(const WitUserStartup *, WitU64);
extern "C" WitU64 wit_test_thread_names(const WitUserStartup *, WitU64);
extern "C" WitU64 wit_test_module_names(const WitUserStartup *, WitU64);
extern "C" WitU64 wit_test_console(const WitUserStartup *, WitU64);
extern "C" WitU64 wit_test_object_wait(const WitUserStartup *, WitU64);
extern "C" WitU64 wit_test_references(const WitUserStartup *, WitU64);

extern "C" WitU64 wit_native_main(const WitUserStartup *startup)
{
    const auto mode = ((const WitUserTestConfig *)startup)->Mode;
    ((WitU64 *)WIT_GC_INFO_REPORT)[0] = mode;
    if (mode == 168) {
        return wit_test_thread_create(startup, mode);
    }
    if (mode >= 76) {
        return wit_test_diagnostics(startup, mode);
    }
    if (mode >= 74) {
        return wit_test_thread_names(startup, mode);
    }
    if (mode >= 71) {
        return wit_test_module_names(startup, mode);
    }
    if (mode >= 68) {
        return wit_test_console(startup, mode);
    }
    return mode >= 66 ? wit_test_object_wait(startup, mode) : wit_test_references(startup, mode);
}
