#include "pal.witos.h"
#include "tls.h"
#include "native_security.h"
#include "protocol.h"
#include <errno.h>
extern "C" void wit_test_noncontinuable_frame();
extern "C" void wit_raise_direct(DWORD, DWORD, DWORD, const ULONG_PTR *);
extern "C" WitU64 wit_test_exception_trigger(WitU64);
extern "C" void wit_exception_landing();
static WitU64 mode;
static unsigned outer, inner, hardware, noncontinuable;
constexpr DWORD Outer = 0xE0421234, Inner = 0xE0421235;

static LONG CALLBACK handler(EXCEPTION_POINTERS *pointers)
{
    if (!pointers || !pointers->ExceptionRecord || !pointers->ContextRecord) {
        wit_native_fail_fast(4301);
    }
    auto &record = *pointers->ExceptionRecord;
    auto &context = *pointers->ContextRecord;
    if (!(context.ContextFlags & CONTEXT_EXCEPTION_ACTIVE)) {
        wit_native_fail_fast(4302);
    }
    if (record.ExceptionCode == EXCEPTION_NONCONTINUABLE_EXCEPTION) {
        if (!(record.ExceptionFlags & EXCEPTION_NONCONTINUABLE) ||
            !record.ExceptionRecord ||
            record.ExceptionRecord->ExceptionCode != Outer) {
            wit_native_fail_fast(4303);
        }
        ++noncontinuable;
        ((WitU64 *)WIT_GC_INFO_REPORT)[4] = noncontinuable;
        return EXCEPTION_CONTINUE_SEARCH;
    }
    if (record.ExceptionCode == EXCEPTION_ILLEGAL_INSTRUCTION) {
        ++hardware;
        RaiseException(Inner, 0, 0, nullptr);
        WitThreadContext current;
        if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&current,
                sizeof(current), nullptr) != WIT_STATUS_OK ||
            !(current.Flags & WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)) {
            wit_native_fail_fast(4304);
        }
        context.Rip = (DWORD64)&wit_exception_landing;
        context.Rax = 0xFEDCBA9876543210ULL;
        context.R12 = 0x778899AABBCCDD11ULL;
        context.EFlags = 0x247;
        context.FltSave.XmmRegisters[6].Low = context.R12;
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    if (record.ExceptionCode == Inner) {
        ++inner;
        if (record.NumberParameters) {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    if (record.ExceptionCode != Outer) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    ++outer;
    if (mode == 128) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    if (mode >= 126) {
        record.ExceptionFlags = 0;
        return EXCEPTION_CONTINUE_EXECUTION;
    } // Cannot erase original noncontinuable intent.
    if (record.NumberParameters) {
        if (record.NumberParameters != 15) {
            wit_native_fail_fast(4305);
        }
        for (unsigned i = 0; i < 15; ++i) {
            if (record.ExceptionInformation[i] != 0xA000 + i) {
                wit_native_fail_fast(4306);
            }
        }
    }
    RaiseException(Inner, 0, 0, nullptr);
    WitThreadContext current;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&current, sizeof(current),
            nullptr) != WIT_STATUS_OK ||
        !(current.Flags & WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)) {
        wit_native_fail_fast(4307);
    }
    return EXCEPTION_CONTINUE_EXECUTION;
}

extern "C" EXCEPTION_DISPOSITION wit_test_noncont_language_handler(EXCEPTION_RECORD *record, void *, CONTEXT *, void *)
{
    if (record->ExceptionCode == Outer) {
        ++((WitU64 *)WIT_GC_INFO_REPORT)[5];
        return ExceptionContinueExecution;
    }
    return ExceptionContinueSearch;
}

extern "C" WitU64 wit_test_raise(const WitUserStartup *startup, WitU64 selected)
{
    mode = selected;
    outer = inner = hardware = noncontinuable = 0;
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[0] = mode;
    report[1] = 140737488355328ULL;
    report[2] = 0;
    wit_native_security_initialize_system();
    wit_native_process_image_initialize(startup);
    wit_native_tls_initialize(startup);
    if (!AddVectoredExceptionHandler(1, handler)) {
        return 4308;
    }
    SetLastError(0xF2345678);
    errno = 317;
    ULONG_PTR args[15];
    for (unsigned i = 0; i < 15; ++i) {
        args[i] = 0xA000 + i;
    }
    if (mode == 126) {
        report[2] = 1;
        RaiseException(Outer, EXCEPTION_NONCONTINUABLE, 0, nullptr);
        return noncontinuable ? 4309 : WIT_TEST_EXIT_CODE;
    }
    if (mode == 128) {
        report[2] = 1;
        wit_test_noncontinuable_frame();
        return 4309;
    }
    if (mode == 127) {
        report[2] = 1;
        RaiseException(Outer, 0, 16, args);
        return 4310;
    }
    WitThreadContext original, invalid, after;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&original, sizeof(original),
            nullptr) != WIT_STATUS_OK) {
        return 4313;
    }
    for (unsigned i = 0; i < 4; ++i) {
        invalid = original;
        WitU64 expected = WIT_STATUS_INVALID_ARGUMENT;
        switch (i) {
        case 0:
            invalid.Cs = 8;
            break;
        case 1:
            invalid.Rflags |= 0x3000;
            break;
        case 2:
            ++invalid.ThreadId;
            break;
        case 3:
            invalid.Rip = startup->ImageInfo;
            expected = WIT_STATUS_BAD_ADDRESS;
            break;
        }
        WitU64 token = 99;
        if (wit_native_call(WIT_CALL_EXCEPTION_BEGIN, (WitU64)&invalid, sizeof(invalid), Outer, &token) != expected ||
            token ||
            wit_native_call(WIT_CALL_THREAD_CONTEXT_METADATA, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&after,
                sizeof(after), nullptr) != WIT_STATUS_OK ||
            (after.Flags & WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)) {
            return 4314;
        }
    }
    WitU64 token = 99;
    if (wit_native_call(WIT_CALL_EXCEPTION_BEGIN, (WitU64)&original, sizeof(original) - 1, Outer, &token) !=
            WIT_STATUS_INVALID_ARGUMENT ||
        token) {
        return 4315;
    }
    wit_raise_direct(Outer, 0, 15, args);
    RaiseException(Outer, 0, 0, nullptr);
    RaiseException(Outer, 0, 0xFFFFFFFF, nullptr);
    if (!wit_test_exception_trigger(0) ||
        outer != 3 ||
        inner != 4 ||
        hardware != 1 ||
        GetLastError() != 0xF2345678 ||
        errno != 317) {
        return 4311;
    }
    WitThreadContext context;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&context, sizeof(context),
            nullptr) != WIT_STATUS_OK ||
        (context.Flags & WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)) {
        return 4312;
    }
    report[2] = outer;
    report[3] = inner;
    return WIT_TEST_EXIT_CODE;
}
