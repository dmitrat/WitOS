#include "tls.h"
#include "protocol.h"
extern "C" WitU64 wit_test_services(const WitUserStartup*,WitU64);
extern "C" WitU64 wit_test_memory(const WitUserStartup*,WitU64);
extern "C" WitU64 wit_test_random(const WitUserStartup*,WitU64);
extern "C" WitU64 wit_test_security(const WitUserStartup*,WitU64);
extern "C" bool wit_test_format(bool);
extern "C" bool wit_test_math(bool);
extern "C" bool wit_test_affinity(bool);
extern "C" WitU64 wit_test_fatal(const WitUserStartup*, WitU64);
extern "C" bool wit_test_native_clock_early();
extern "C" bool wit_test_native_clock_threads();
extern "C" bool wit_test_cpu_early();
extern "C" bool wit_test_cpu_threads();
extern "C" void wit_cpu_avx_probe();
extern "C" WitU64 wit_native_main(const WitUserStartup* startup)
{
    auto report = (WitU64*)WIT_GC_INFO_REPORT;
    const WitU64 mode = ((const WitUserTestConfig*)startup)->Mode;
    report[0] = mode; report[1] = 2097152;
    if (mode == 62 || mode == 63) return wit_test_services(startup,mode);
    if (mode == 60 || mode == 61) return wit_test_memory(startup,mode);
    if (mode == 58 || mode == 59) return wit_test_random(startup,mode);
    if (mode >= 45) return wit_test_security(startup,mode);
    if (mode == 43 || mode == 44) {
        report[1] = 67108864;
        wit_native_process_image_initialize(startup);
        if (mode == 43) wit_native_tls_initialize(startup);
        if (!wit_test_format(mode == 43)) return 2202;
        if (mode == 43) wit_native_tls_leave();
        return WIT_TEST_EXIT_CODE;
    }
    if (mode == 41 || mode == 42) {
        report[1] = 33554432;
        wit_native_process_image_initialize(startup);
        if (mode == 41) wit_native_tls_initialize(startup);
        if (!wit_test_math(mode == 41)) return 2102;
        if (mode == 41) wit_native_tls_leave();
        return WIT_TEST_EXIT_CODE;
    }
    if (mode == 39 || mode == 40) {
        report[1] = 16777216;
        wit_native_process_image_initialize(startup);
        if (mode == 39) wit_native_tls_initialize(startup);
        if (!wit_test_affinity(mode == 39)) return 2002;
        if (mode == 39) wit_native_tls_leave();
        return WIT_TEST_EXIT_CODE;
    }
    if (mode >= 29) return wit_test_fatal(startup, mode);
    if (mode == 27 || mode == 28) {
        report[1] = 4194304;
        if (!wit_test_native_clock_early()) return 1802;
        if (mode == 27) {
            wit_native_process_image_initialize(startup);
            wit_native_tls_initialize(startup);
            if (!wit_test_native_clock_threads()) return 1803;
            wit_native_tls_leave();
        }
        return WIT_TEST_EXIT_CODE;
    }
    if (mode == 26) { wit_cpu_avx_probe(); return 1792; }
    if (!wit_test_cpu_early()) return 1793;
    if (mode == 24) {
        wit_native_process_image_initialize(startup);
        wit_native_tls_initialize(startup);
        if (!wit_test_cpu_threads()) return 1794;
        wit_native_tls_leave();
    }
    return WIT_TEST_EXIT_CODE;
}
