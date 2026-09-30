#include "tls.h"
#include "protocol.h"
extern "C" WitU64 wit_test_context_mutation(const WitUserStartup*,WitU64);
extern "C" WitU64 wit_test_stack_lease(const WitUserStartup*,WitU64);
extern "C" WitU64 wit_native_main(const WitUserStartup* startup) {const auto mode=((const WitUserTestConfig*)startup)->Mode;return mode>=100?wit_test_stack_lease(startup,mode):wit_test_context_mutation(startup,mode);}
