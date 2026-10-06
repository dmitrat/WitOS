#include "tls.h"
#include "pal_environment.witos.h"
extern "C" {
#include "image.h"
#include "native_security.h"
#include "../User/protocol.h"
}

/* The host runtime fixture (P6.4.i): the WitOS C++ runtime, the UCRT subset and the separately compiled sources of
 * the pinned microsoft/STL in one image, which the kernel loads with the full runtime profile, as it will load the
 * host. Its startup does what a module's startup does, in order: the GS cookies, the image and compiler TLS before
 * any thread starts, and the static initializers, among them the STL's locks and locale objects. Each mode runs one
 * group of scenarios; all but the native heap's against the trace Windows prints. */
extern "C" int wit_cxx_run_initializers(void);
extern "C" WitU64 wit_cxx_exceptions_probe();
extern "C" WitU64 wit_crt_scenarios_probe();
extern "C" WitU64 wit_stl_scenarios_probe();
extern "C" WitU64 wit_stl_no_utc_probe();
extern "C" WitU64 wit_heap_scenarios_probe();
extern "C" WitU64 wit_host_pal_probe();
extern "C" WitU64 wit_cxx_library_probe();
extern "C" WitU64 wit_host_libraries_probe();
extern "C" WitU64 wit_host_muxer_probe();

namespace {
// The defaults the image seeds into the environment of the host PAL scenarios (mode 26), readonly image data; the
// kernel test sets WITOS_SEEDED as the creator, which wins.
const WitPalEnvironmentEntry ENVIRONMENT[] = {{L"CORE_SERVICING", L"/", 14, 1}, {L"WITOS_SEEDED", L"table", 12, 5}};
} // namespace

extern "C" WitU64 wit_native_main(const WitUserStartup *startup)
{
    if (!startup || startup->Version != WIT_ABI_VERSION || startup->Size != sizeof(*startup)) {
        return 2500;
    }
    const WitU64 mode = ((const WitUserTestConfig *)startup)->Mode;
    wit_native_security_initialize_system();
    if (mode == 26) {
        // The process's image and environment, published before compiler TLS as the host's startup will.
        wit_native_process_image_initialize(startup);
        if (!wit_pal_environment_initialize(ENVIRONMENT, 2)) {
            return 2503;
        }
    }
    wit_native_tls_initialize(startup);
    if (wit_cxx_run_initializers()) {
        return 2502;
    }
    WitU64 code = 2501;
    switch (mode) {
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
    case 25:
        code = wit_heap_scenarios_probe();
        break;
    case 26:
        code = wit_host_pal_probe();
        break;
    case 27:
        code = wit_cxx_library_probe();
        break;
    case 28:
        code = wit_host_libraries_probe();
        break;
    case 29:
        code = wit_host_muxer_probe();
        break;
    }
    wit_native_tls_leave();
    return code;
}
