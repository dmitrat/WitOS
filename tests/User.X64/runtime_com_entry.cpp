#include "tls.h"
#include "../User/protocol.h"
extern "C" WitU64 wit_test_suspension(const WitUserStartup *, WitU64);
extern "C" WitU64 wit_test_context_capture(const WitUserStartup *, WitU64);
extern "C" WitU64 wit_test_context_storage(const WitUserStartup *, WitU64);
extern "C" WitU64 wit_test_gc_policy(const WitUserStartup *, WitU64);
extern "C" WitU64 wit_test_com_lifecycle(const WitUserStartup *);

extern "C" WitU64 wit_native_main(const WitUserStartup *startup)
{
    const auto mode = ((const WitUserTestConfig *)startup)->Mode;
    if (mode >= 91) {
        return wit_test_suspension(startup, mode);
    }
    if (mode >= 89) {
        return wit_test_context_capture(startup, mode);
    }
    if (mode >= 86) {
        return wit_test_context_storage(startup, mode);
    }
    return mode >= 82 ? wit_test_gc_policy(startup, mode) : wit_test_com_lifecycle(startup);
}
