#include "pal.witos.h"
#include "native_process.h"
#include "protocol.h"
#include <stdlib.h>
#include <errno.h>
extern "C" __declspec(noreturn) void __cdecl __report_rangecheckfailure(void);
extern "C" int __cdecl __tlregdtor(void(__cdecl *)(void));

static void require(bool value)
{
    if (!value) {
        wit_native_fail_fast(1901);
    }
}

static void cleanup()
{
    ((volatile WitU64 *)WIT_GC_INFO_REPORT)[2] += 1;
}

static void notification(void *)
{
    cleanup();
}

extern "C" WitU64 wit_test_fatal(const WitUserStartup *startup, WitU64 mode)
{
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[1] = 8388608;
    report[2] = 0;
    if (mode != 33 && mode != 38) {
        wit_native_process_image_initialize(startup);
    }
    if (mode == 29 || mode == 31 || mode == 32) {
        wit_native_tls_initialize(startup);
        require(__tlregdtor(cleanup) == 0 &&
            atexit(cleanup) == 0 &&
            wit_native_thread_on_exit(notification, nullptr) == WIT_STATUS_OK);
        errno = 73;
    }
    if (mode == 34 || mode == 37) {
        require(wit_native_call(WIT_CALL_CLOSE, startup->ConsoleHandle, 0, 0, nullptr) == WIT_STATUS_OK);
    }
    if (mode == 31 || mode == 33) {
        (void)_purecall();
        return 1902;
    }
    if (mode == 32 || mode == 34) {
        __report_rangecheckfailure();
    }
    if (mode == 35) {
        PalPrintFatalError(nullptr);
        return 1903;
    }
    char oversized[WIT_ABI_MAX_WRITE + 1];
    for (unsigned i = 0; i < sizeof(oversized); ++i) {
        oversized[i] = 'x';
    }
    if (mode == 36) {
        PalPrintFatalError(oversized);
        return 1904;
    }
    if (mode == 37 || mode == 38) {
        PalPrintFatalError("must not print\n");
        return 1905;
    }
    SetLastError(0x75432109);
    PalPrintFatalError("");
    PalPrintFatalError("[NATIVE-DIAGNOSTIC] bounded output\n");
    oversized[WIT_ABI_MAX_WRITE] = 0;
    oversized[WIT_ABI_MAX_WRITE - 1] = '\n';
    PalPrintFatalError(oversized);
    WitU64 arena = 0;
    require(wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) == WIT_STATUS_OK &&
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) == WIT_STATUS_OK);
    char *edge = (char *)(arena + 4093);
    edge[0] = 'E';
    edge[1] = '\n';
    edge[2] = 0;
    require(wit_native_call(WIT_CALL_MEMORY_PROTECT, arena, 4096, WIT_MEMORY_READ, nullptr) == WIT_STATUS_OK);
    PalPrintFatalError(edge); // NUL at page end, next page is uncommitted.
    require(wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) == WIT_STATUS_OK);
    require(GetLastError() == 0x75432109);
    if (mode == 29) {
        require(errno == 73 && report[2] == 0);
        wit_native_process_shutdown();
        require(report[2] == 3); // Positive control for callbacks bypassed by fatal modes.
    }
    return WIT_TEST_EXIT_CODE;
}
