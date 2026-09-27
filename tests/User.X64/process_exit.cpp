#include "native_process.h"
#include "error.h"
#include "protocol.h"
#include <stdlib.h>

static WitU64 mode, sequence, called;
static volatile WitU64 released, attempted;
static __declspec(thread) bool worker_thread;
static __declspec(thread) bool tls_destroyed;
static WitU64* report() { return (WitU64*)WIT_GC_INFO_REPORT; }
static void require(bool value) { if (!value) wit_native_fail_fast(0xFFFF1010ULL); }
static void tick() { require(wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) == WIT_STATUS_OK); }
static void count_call() { report()[3] = ++called; }
static void extra() { sequence = sequence * 10 + 4; }
static void from_tls() { sequence = sequence * 10 + 3; }
struct Cleanup {
    ~Cleanup() noexcept {
        tls_destroyed = true; if (!worker_thread) report()[5] = 1;
        if (!worker_thread && (mode == 0 || mode == 1)) require(atexit(from_tls) == 0);
    }
};
static thread_local Cleanup cleanup;
static void first()
{
    require(tls_destroyed);
    sequence = sequence * 10 + 1;
    require(sequence == 3241);
    report()[2] = sequence;
}
static void second()
{
    require(tls_destroyed);
    sequence = sequence * 10 + 2;
    require(atexit(extra) == 0);
}
static void recursive() { report()[1] = 1; wit_native_process_shutdown(); }
static void forever() { ++report()[1]; require(atexit(forever) == 0); }
static void fault() { report()[1] = 1; *(volatile WitU64*)0 = 1; }
static WitU64 worker(WitU64)
{
    worker_thread = true;
    wit_native_error_set(0x12345678);
    if (mode == 3) {
        for (WitU32 i = 0; i < 8; ++i) { require(atexit(count_call) == 0); tick(); }
    } else {
        while (!released) tick();
        if (mode == 9) { report()[1] = 1; wit_native_process_shutdown(); }
        require(atexit(count_call) != 0);
        attempted = 1;
    }
    require(wit_native_error_get() == 0x12345678);
    return WIT_TEST_EXIT_CODE;
}
static void handoff()
{
    released = 1;
    while (!attempted) tick(); // The registry gate must be released here.
    require(called == 0);
    report()[2] = 1;
}
extern "C" WitU64 wit_native_main(const WitUserStartup* startup)
{
    mode = ((const WitUserTestConfig*)startup)->Mode;
    report()[0] = mode;
    require(atexit(count_call) != 0); // No published image yet.
    wit_native_process_image_initialize(startup);
    if (mode <= 1) require(atexit(first) == 0); // Before compiler TLS constructors.
    wit_native_tls_initialize(startup);
    wit_native_error_set(0xABCDEF12);
    require(atexit(nullptr) != 0 && atexit((void(__cdecl*)())startup) != 0 &&
        atexit((void(__cdecl*)())&sequence) != 0);
    WitU64 handles[3] = {}, result;
    if (mode <= 1) {
        require(atexit(second) == 0);
        if (mode == 1) wit_native_process_exit(WIT_TEST_EXIT_CODE);
    } else if (mode == 2) {
        for (WitU32 i = 0; i < WIT_NATIVE_EXIT_MAX_CALLBACKS; ++i) require(atexit(count_call) == 0);
        require(atexit(count_call) != 0);
    } else if (mode == 3) {
        for (WitU32 i = 0; i < 3; ++i) require(wit_native_thread_create(worker, 0, &handles[i]) == WIT_STATUS_OK);
        for (WitU32 i = 0; i < 3; ++i) require(wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) == WIT_STATUS_OK && result == WIT_TEST_EXIT_CODE);
        require(called == 0); // Individual thread exit must not drain process callbacks.
    } else if (mode == 4) require(atexit(recursive) == 0);
    else if (mode == 5) require(atexit(forever) == 0);
    else if (mode == 6) require(atexit(fault) == 0);
    else if (mode == 7) {
        require(atexit(count_call) == 0);
        report()[1] = 1;
        (void)wit_native_call(WIT_CALL_EXIT, WIT_TEST_EXIT_CODE, 0, 0, nullptr);
        require(false);
    } else {
        require(wit_native_thread_create(worker, 0, &handles[0]) == WIT_STATUS_OK);
        require(atexit(handoff) == 0);
    }
    wit_native_process_shutdown();
    wit_native_process_shutdown(); // Completed cleanup is idempotent for its owner.
    require(tls_destroyed && atexit(count_call) != 0 && wit_native_error_get() == 0xABCDEF12);
    if (mode == 2 || mode == 3) require(called == (mode == 2 ? 32U : 24U));
    if (mode == 8) require(wit_native_call(WIT_CALL_THREAD_JOIN, handles[0], 0, 0, &result) == WIT_STATUS_OK && result == WIT_TEST_EXIT_CODE);
    report()[3] = called;
    report()[4] = 1;
    return WIT_TEST_EXIT_CODE;
}
