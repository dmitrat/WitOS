#include "tls.h"
extern "C" {
#include "../User/protocol.h"
}

/* The host runtime fixture (P6.4.i): the WitOS C++ runtime, the UCRT subset and the separately compiled sources of
 * the pinned microsoft/STL in one image, which the kernel loads with the full runtime profile, as it will load the
 * host. Its startup publishes the image and compiler TLS before any thread starts, as a module's startup does. Each
 * mode runs one group of scenarios against the trace Windows prints. */
extern "C" WitU64 wit_cxx_exceptions_probe();
extern "C" WitU64 wit_crt_scenarios_probe();
extern "C" WitU64 wit_stl_scenarios_probe();
extern "C" WitU64 wit_stl_no_utc_probe();

extern "C" WitU64 wit_native_main(const WitUserStartup *startup)
{
    if (!startup || startup->Version != WIT_ABI_VERSION || startup->Size != sizeof(*startup)) {
        return 2500;
    }
    wit_native_tls_initialize(startup);
    WitU64 code = 2501;
    switch (((const WitUserTestConfig *)startup)->Mode) {
    case 21:
        code = wit_cxx_exceptions_probe();
        break;
    case 22:
        code = wit_crt_scenarios_probe();
        break;
    case 23:
        code = wit_stl_scenarios_probe();
        break;
    case 24:
        code = wit_stl_no_utc_probe();
        break;
    }
    wit_native_tls_leave();
    return code;
}
