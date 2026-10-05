extern "C" {
#include "bootstrap.h"
#include "../User/protocol.h"
}

/* The host runtime fixture (P6.4.i): the WitOS C++ runtime, the UCRT subset and the separately compiled sources of
 * the pinned microsoft/STL in one image, which the kernel loads with the full runtime profile, as it will load the
 * host. Each mode runs one group of scenarios against the trace Windows prints. */
extern "C" WitU64 wit_cxx_exceptions_probe();
extern "C" WitU64 wit_crt_scenarios_probe();
extern "C" WitU64 wit_stl_scenarios_probe();

extern "C" WitU64 wit_native_main(const WitUserStartup *startup)
{
    if (!startup || startup->Version != WIT_ABI_VERSION || startup->Size != sizeof(*startup)) {
        return 2500;
    }
    wit_native_process_image_initialize(startup);
    switch (((const WitUserTestConfig *)startup)->Mode) {
    case 21:
        return wit_cxx_exceptions_probe();
    case 22:
        return wit_crt_scenarios_probe();
    case 23:
        return wit_stl_scenarios_probe();
    default:
        return 2501;
    }
}
