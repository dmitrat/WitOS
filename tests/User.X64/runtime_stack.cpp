#include "tls.h"
#include "error.h"
#include "protocol.h"
extern "C" WitU64 wit_stack_check_registers(WitU64 size);
extern "C" void wit_stack_probe_to(void* target);
extern "C" void wit_stack_probe_huge();
static __declspec(noinline) WitU64 large_frame(WitU64 seed)
{
    // /Gs4096 plus a real 8 KiB volatile frame must emit __chkstk.
    volatile unsigned char data[8192];
    for (unsigned i = 0; i < sizeof(data); ++i) data[i] = (unsigned char)(i + seed);
    if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) return 1780;
    for (unsigned i = 0; i < sizeof(data); ++i) if (data[i] != (unsigned char)(i + seed)) return 1781;
    return WIT_TEST_EXIT_CODE;
}
extern "C" bool wit_test_stack_early()
{
    wit_native_error_set(0x71829304);
    const WitU64 sizes[] = {0, 1, 4095, 4096, 4097, 8192, 8193};
    for (auto size : sizes) if (wit_stack_check_registers(size) != 1) return false;
    return large_frame(73) == WIT_TEST_EXIT_CODE && wit_native_error_get() == 0x71829304;
}
static WitU64 worker(WitU64 seed)
{
    if (wit_stack_check_registers(4097) != 1) return 1782;
    return large_frame(seed);
}
extern "C" bool wit_test_stack_threads()
{
    for (unsigned round = 0; round < 2; ++round) {
        WitU64 handles[3], result;
        for (WitU64 i = 0; i < 3; ++i) if (wit_native_thread_create(worker, i + 17, &handles[i]) != WIT_STATUS_OK) return false;
        for (unsigned i = 0; i < 3; ++i)
            if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE) return false;
    }
    return wit_native_error_get() == 0x71829304;
}
extern "C" void wit_test_stack_overflow(WitU64 mode)
{
    if (mode == 21) wit_stack_probe_to((void*)WIT_GC_INFO_REPORT);
    else wit_stack_probe_huge();
}
