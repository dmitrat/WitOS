#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "../User/protocol.h"
extern "C" bool wit_gs_check_abi(uintptr_t);
extern "C" EXCEPTION_DISPOSITION __cdecl __GSHandlerCheck(EXCEPTION_RECORD *, void *, CONTEXT *, DISPATCHER_CONTEXT *);
extern "C" const RUNTIME_FUNCTION wit_gs_plain_function, wit_gs_aligned_function, wit_gs_bad_version_function,
    wit_gs_bad_alignment_function;
extern "C" const uint32_t wit_gs_plain_data, wit_gs_aligned_data, wit_gs_bad_version_data, wit_gs_bad_alignment_data;
constexpr WitU64 Seed = 0xFEED123456789ABCULL;

static bool handler(bool aligned, WitU64 mode)
{
    __declspec(align(16)) WitU64 slots[12] = {};
    DISPATCHER_CONTEXT dispatcher = {};
    dispatcher.ImageBase = wit_native_process_image()->Base;
    dispatcher.FunctionEntry = (RUNTIME_FUNCTION *)(aligned ? &wit_gs_aligned_function : &wit_gs_plain_function);
    dispatcher.HandlerData = (void *)(aligned ? &wit_gs_aligned_data : &wit_gs_plain_data);
    const auto frame = (uintptr_t)slots;
    slots[aligned ? 3 : 1] = __security_cookie ^ (frame + (aligned ? 32 : 0));
    RUNTIME_FUNCTION writable = *dispatcher.FunctionEntry;
    if (mode == 49) {
        dispatcher.FunctionEntry = &writable;
    }
    if (mode == 50) {
        slots[aligned ? 3 : 1] ^= 1;
    }
    if (mode == 51) {
        dispatcher.FunctionEntry = (RUNTIME_FUNCTION *)&wit_gs_bad_version_function;
        dispatcher.HandlerData = (void *)&wit_gs_bad_version_data;
    }
    if (mode == 52) {
        dispatcher.FunctionEntry = (RUNTIME_FUNCTION *)&wit_gs_bad_alignment_function;
        dispatcher.HandlerData = (void *)&wit_gs_bad_alignment_data;
    }
    return __GSHandlerCheck(nullptr, (void *)frame, nullptr, &dispatcher) == ExceptionContinueSearch;
}

static WitU64 worker(WitU64 index)
{
    SetLastError(DWORD(1800 + index));
    for (unsigned i = 0; i < 3; ++i) {
        if (!wit_gs_check_abi(__security_cookie) ||
            !handler((i & 1) != 0, 0) ||
            wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK ||
            GetLastError() != 1800 + index) {
            return 2301;
        }
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" WitU64 wit_test_security(const WitUserStartup *startup, WitU64 mode)
{
    ((WitU64 *)WIT_GC_INFO_REPORT)[1] = 134217728;
    if (mode >= 55) {
        wit_native_security_initialize(mode == 55 ? 0 : mode == 56 ? 0x2B992DDFA232ULL : 0xFFFF000000000000ULL);
        return 2312;
    }
    if (mode == 46) {
        (void)wit_gs_check_abi(0);
        return 2302;
    }
    wit_native_security_initialize(Seed); // Deterministic mechanism test, never production entropy.
    if (__security_cookie != (Seed & 0x0000FFFFFFFFFFFFULL) || __security_cookie_complement != ~__security_cookie) {
        return 2303;
    }
    if (mode == 47) {
        (void)wit_gs_check_abi(__security_cookie ^ 1);
        return 2304;
    }
    if (mode == 48) {
        __security_cookie |= 1ULL << 63;
        (void)wit_gs_check_abi(__security_cookie);
        return 2305;
    }
    if (mode == 53) {
        wit_native_security_initialize(Seed);
        return 2306;
    }
    SetLastError(0x62813549);
    if (!wit_gs_check_abi(__security_cookie) || GetLastError() != 0x62813549) {
        return 2307;
    }
    wit_native_process_image_initialize(startup);
    if (mode >= 49 && mode <= 52) {
        (void)handler(false, mode);
        return 2308;
    }
    if (!handler(false, 0) || !handler(true, 0) || GetLastError() != 0x62813549) {
        return 2309;
    }
    if (mode == 45) {
        wit_native_tls_initialize(startup);
        WitU64 handles[3], result;
        for (WitU64 i = 0; i < 3; ++i) {
            if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) {
                return 2310;
            }
        }
        for (unsigned i = 0; i < 3; ++i) {
            if (wit_native_thread_join(handles[i], &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE) {
                return 2311;
            }
        }
        wit_native_tls_leave();
    }
    return WIT_TEST_EXIT_CODE;
}
