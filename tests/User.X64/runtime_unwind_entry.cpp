#include "tls.h"
#include "protocol.h"
extern "C" WitU64 wit_test_unwind(const WitUserStartup*,WitU64);
extern "C" WitU64 wit_test_exception(const WitUserStartup*,WitU64);
extern "C" WitU64 wit_test_vectored(const WitUserStartup*,WitU64);
extern "C" WitU64 wit_test_raise(const WitUserStartup*,WitU64);
extern "C" WitU64 wit_test_failfast(const WitUserStartup*,WitU64);
extern "C" WitU64 wit_test_seh(const WitUserStartup*,WitU64);
extern "C" WitU64 wit_native_main(const WitUserStartup* startup)
{const auto mode=((const WitUserTestConfig*)startup)->Mode;return mode>=137?wit_test_seh(startup,mode):mode>=129?wit_test_failfast(startup,mode):mode>=125?wit_test_raise(startup,mode):mode>=117?wit_test_vectored(startup,mode):mode>=110?wit_test_exception(startup,mode):wit_test_unwind(startup,mode);}
