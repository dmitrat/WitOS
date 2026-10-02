#include "pal.witos.h"
#include "tls.h"
#include "native_security.h"
#include "protocol.h"
#include <stdlib.h>
extern "C" void wit_failfast_direct(EXCEPTION_RECORD *, CONTEXT *, DWORD);
extern "C" WitU64 wit_test_exception_trigger(WitU64);
extern "C" int __cdecl __tlregdtor(void(__cdecl *)(void));
static WitU64 mode;

static void cleanup()
{
    ++((volatile WitU64 *)WIT_GC_INFO_REPORT)[2];
}

static void notify(void *)
{
    cleanup();
}

static LONG CALLBACK handler(EXCEPTION_POINTERS *)
{
    if (mode == 136) {
        ((volatile WitU64 *)WIT_GC_INFO_REPORT)[3] = 1;
        EXCEPTION_RECORD record = {};
        record.ExceptionCode = 0xE0426789;
        record.ExceptionAddress = (void *)0x12345678;
        RaiseFailFastException(&record, nullptr, 0);
    }
    cleanup();
    return EXCEPTION_CONTINUE_SEARCH;
}

extern "C" WitU64 wit_test_failfast(const WitUserStartup *startup, WitU64 selected)
{
    mode = selected;
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[0] = mode;
    report[1] = 281474976710656ULL;
    report[2] = 0;
    report[3] = 0;
    wit_native_security_initialize_system();
    wit_native_process_image_initialize(startup);
    if (mode != 132) {
        wit_native_tls_initialize(startup);
        if (!AddVectoredExceptionHandler(1, handler) ||
            __tlregdtor(cleanup) ||
            atexit(cleanup) ||
            wit_native_thread_on_exit(notify, nullptr) != WIT_STATUS_OK) {
            return 4401;
        }
    }
    if (mode == 136) {
        wit_test_exception_trigger(0);
        return 4402;
    }
    EXCEPTION_RECORD record = {};
    CONTEXT context = {};
    record.ExceptionCode = 0xE0426789;
    record.ExceptionFlags = EXCEPTION_NONCONTINUABLE;
    record.ExceptionAddress = (void *)0x12345678;
    record.NumberParameters = 1;
    record.ExceptionInformation[0] = 0xABCDEF;
    context.ContextFlags = CONTEXT_FULL;
    context.Rip = 0x23456789;
    context.Rsp = 0x34567890;
    context.R12 = 0x1122334455667788ULL;
    report[4] = (WitU64)&record;
    report[5] = (WitU64)&context;
    if (mode == 133 && wit_native_call(WIT_CALL_CLOSE, startup->ConsoleHandle, 0, 0, nullptr) != WIT_STATUS_OK) {
        return 4403;
    }
    if (mode == 129 || mode == 132) {
        RaiseFailFastException(nullptr, nullptr, mode == 132 ? FAIL_FAST_GENERATE_EXCEPTION_ADDRESS : 0);
    }
    if (mode == 130) {
        wit_failfast_direct(&record, &context, 0);
    }
    if (mode == 131) {
        RaiseFailFastException(&record, nullptr, FAIL_FAST_GENERATE_EXCEPTION_ADDRESS);
    }
    if (mode == 133) {
        RaiseFailFastException(&record, &context, 0);
    }
    if (mode == 134) {
        RaiseFailFastException((EXCEPTION_RECORD *)0x400000000000ULL, nullptr, 0);
    }
    if (mode == 135) {
        RaiseFailFastException(&record, (CONTEXT *)0x400000000000ULL, 0);
    }
    return 4404;
}
