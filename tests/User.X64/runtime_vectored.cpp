#include "pal.witos.h"
#include "tls.h"
#include "native_security.h"
#include "protocol.h"
#include <errno.h>
extern "C" WitU64 wit_test_exception_trigger(WitU64);
extern "C" WitU64 wit_unwind_protected_frame();
extern "C" WitU64 (*wit_test_gs_override)(CONTEXT *, volatile char *);
extern "C" void wit_exception_landing();
extern "C" void wit_exception_invalid();
extern "C" void wit_exception_general();
static unsigned count, kind;
static LONG order[16];
static WitU64 mode;
static PVOID selfHandle;

static LONG record(LONG id, EXCEPTION_POINTERS *info)
{
    if (count >= 16 || !info || !info->ContextRecord || !info->ExceptionRecord) {
        wit_native_fail_fast(4201);
    }
    order[count++] = id;
    if (kind == 4) {
        ((WitU64 *)WIT_GC_INFO_REPORT)[6] = count;
    }
    const DWORD codes[] = {EXCEPTION_ILLEGAL_INSTRUCTION, EXCEPTION_INT_DIVIDE_BY_ZERO, EXCEPTION_ACCESS_VIOLATION,
        EXCEPTION_BREAKPOINT, EXCEPTION_ACCESS_VIOLATION, EXCEPTION_ACCESS_VIOLATION};
    if (info->ExceptionRecord->ExceptionCode != codes[kind] ||
        !(info->ContextRecord->ContextFlags & CONTEXT_EXCEPTION_ACTIVE) ||
        info->ContextRecord->R12 != 0x1122334455667788ULL) {
        wit_native_fail_fast(4202);
    }
    if (kind == 2 &&
        (info->ExceptionRecord->NumberParameters != 2 ||
            info->ExceptionRecord->ExceptionInformation[0] != 0 ||
            info->ExceptionRecord->ExceptionInformation[1] != 0x400000000000ULL)) {
        wit_native_fail_fast(4203);
    }
    if (kind == 4 &&
        (info->ExceptionRecord->NumberParameters != 2 ||
            info->ExceptionRecord->ExceptionInformation[0] ||
            info->ExceptionRecord->ExceptionInformation[1] != ~(ULONG_PTR)0)) {
        wit_native_fail_fast(4228);
    }
    if (kind == 5 &&
        (info->ExceptionRecord->NumberParameters != 2 ||
            info->ExceptionRecord->ExceptionInformation[0] != 8 ||
            info->ExceptionRecord->ExceptionInformation[1])) {
        wit_native_fail_fast(4204);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

static LONG CALLBACK first(EXCEPTION_POINTERS *info)
{
    return record(1, info);
}

static LONG CALLBACK last(EXCEPTION_POINTERS *info)
{
    return record(2, info);
}

static LONG CALLBACK front(EXCEPTION_POINTERS *info)
{
    return record(3, info);
}

static LONG CALLBACK finish(EXCEPTION_POINTERS *info)
{
    record(4, info);
    auto &context = *info->ContextRecord;
    if (mode == 120) {
        return 1;
    }
    context.Rip = (DWORD64)&wit_exception_landing;
    context.Rax = 0xFEDCBA9876543210ULL;
    context.R12 = 0x778899AABBCCDD11ULL;
    context.EFlags = 0x247;
    context.FltSave.XmmRegisters[6].Low = context.R12;
    if (mode == 119) {
        context.SegCs = 8;
    }
    return EXCEPTION_CONTINUE_EXECUTION;
}

static LONG CALLBACK self_remove(EXCEPTION_POINTERS *info)
{
    record(5, info);
    if (!RemoveVectoredExceptionHandler(selfHandle)) {
        wit_native_fail_fast(4205);
    }
    selfHandle = nullptr;
    return EXCEPTION_CONTINUE_SEARCH;
}

static bool sequence(const LONG *expected, unsigned size)
{
    if (count != size) {
        return false;
    }
    for (unsigned i = 0; i < size; ++i) {
        if (order[i] != expected[i]) {
            return false;
        }
    }
    return true;
}

static WitU64 protected_fault(CONTEXT *context, volatile char *buffer)
{
    const auto image = wit_native_process_image();
    if (!image || buffer[0] != 7) {
        return 4221;
    }
    const WitU64 pc = context->Rip;
    PRUNTIME_FUNCTION entry = nullptr;
    auto entries = (PRUNTIME_FUNCTION)(image->Base + image->UnwindRva);
    for (unsigned i = 0; i < image->UnwindSize / 12; ++i) {
        if (pc - image->Base >= entries[i].BeginAddress && pc - image->Base < entries[i].EndAddress) {
            entry = &entries[i];
            break;
        }
    }
    if (!entry) {
        return 4222;
    }
    void *data = nullptr;
    DWORD64 frame = 0;
    if (!RtlVirtualUnwind(UNW_FLAG_UHANDLER, image->Base, pc, entry, context, &data, &frame, nullptr) ||
        !data ||
        !frame) {
        return 4223;
    }
    if (((const BYTE *)(image->Base + entry->UnwindData))[3] || (*(const int *)data & 4)) {
        return 4224;
    }
    auto cookie = (volatile WitU64 *)(frame + (WitU64)(long long)(*(const int *)data & ~7));
    if ((*cookie ^ frame) != __security_cookie) {
        return 4225;
    }
    if (mode == 124) {
        *cookie ^= 1;
    }
    ((WitU64 *)WIT_GC_INFO_REPORT)[4] = 1;
    wit_test_exception_trigger(0);
    return 4226;
}

extern "C" WitU64 wit_test_vectored(const WitUserStartup *startup, WitU64 selected)
{
    mode = selected;
    kind = 0;
    count = 0;
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[0] = mode;
    report[1] = 70368744177664ULL;
    report[2] = 0;
    wit_native_security_initialize_system();
    wit_native_process_image_initialize(startup);
    if (mode != 121) {
        wit_native_tls_initialize(startup);
    }
    if (mode == 121) {
        if (AddVectoredExceptionHandler(1, first) || GetLastError() != ERROR_INVALID_STATE) {
            return 4206;
        }
        report[2] = 1;
        return WIT_TEST_EXIT_CODE;
    }
    SetLastError(0xE2345678);
    errno = 307;
    if (mode == 123 || mode == 124) {
        if (!AddVectoredExceptionHandler(1, first)) {
            return 4227;
        }
        report[2] = 1;
        report[3] = (WitU64)&wit_exception_invalid;
        wit_test_gs_override = protected_fault;
        return wit_unwind_protected_frame();
    }
    if (mode >= 118) {
        if (mode == 122) {
            kind = 4;
        }
        if (!AddVectoredExceptionHandler(1, (mode == 118 || mode == 122) ? first : finish)) {
            return 4207;
        }
        report[2] = 1;
        report[3] = (WitU64)(mode == 122 ? &wit_exception_general : &wit_exception_invalid);
        wit_test_exception_trigger(mode == 122 ? 4 : 0);
        return 4208;
    }
    PVOID a = AddVectoredExceptionHandler(1, first), b = AddVectoredExceptionHandler(0, last),
          c = AddVectoredExceptionHandler(7, front), d = AddVectoredExceptionHandler(0, finish);
    if (!a || !b || !c || !d || a == b || b == c || c == d || GetLastError() != 0xE2345678) {
        return 4209;
    }
    const LONG all[] = {3, 1, 2, 4}, remaining[] = {1, 2, 4}, self[] = {5, 1, 2, 4};
    for (kind = 0; kind < 6; ++kind) {
        count = 0;
        if (!wit_test_exception_trigger(kind) || !sequence(all, 4)) {
            return 4210;
        }
    }
    kind = 0;
    if (!RemoveVectoredExceptionHandler(c) || RemoveVectoredExceptionHandler(c)) {
        return 4211;
    }
    count = 0;
    if (!wit_test_exception_trigger(0) || !sequence(remaining, 3)) {
        return 4212;
    }
    selfHandle = AddVectoredExceptionHandler(1, self_remove);
    if (!selfHandle) {
        return 4213;
    }
    count = 0;
    if (!wit_test_exception_trigger(0) || !sequence(self, 4) || selfHandle) {
        return 4214;
    }
    count = 0;
    if (!wit_test_exception_trigger(0) || !sequence(remaining, 3)) {
        return 4215;
    }
    PVOID extra[5];
    for (unsigned i = 0; i < 5; ++i) {
        extra[i] = AddVectoredExceptionHandler(0, last);
        if (!extra[i]) {
            return 4216;
        }
    }
    if (AddVectoredExceptionHandler(0, last) || GetLastError() != ERROR_NOT_ENOUGH_MEMORY) {
        return 4217;
    }
    for (auto entry : extra) {
        if (!RemoveVectoredExceptionHandler(entry)) {
            return 4218;
        }
    }
    if (AddVectoredExceptionHandler(0, nullptr) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        AddVectoredExceptionHandler(0, (PVECTORED_EXCEPTION_HANDLER)startup->ImageInfo) ||
        GetLastError() != ERROR_INVALID_PARAMETER) {
        return 4219;
    }
    SetLastError(0xE2345678);
    if (!RemoveVectoredExceptionHandler(a) ||
        !RemoveVectoredExceptionHandler(b) ||
        !RemoveVectoredExceptionHandler(d) ||
        GetLastError() != 0xE2345678 ||
        errno != 307) {
        return 4220;
    }
    report[2] = 1;
    return WIT_TEST_EXIT_CODE;
}
