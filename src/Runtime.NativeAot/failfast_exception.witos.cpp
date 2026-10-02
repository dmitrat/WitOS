#include "pal.witos.h"
#include <string.h>

namespace {
void hex(char *&out, WitU64 value, unsigned digits)
{
    const char alphabet[] = "0123456789ABCDEF";
    for (unsigned i = digits; i; --i) {
        *out++ = alphabet[(value >> ((i - 1) * 4)) & 15];
    }
}

void word(char *&out, const char *value)
{
    while (*value) {
        *out++ = *value++;
    }
}
}

extern "C" void __cdecl wit_native_raise_fail_fast(
    const EXCEPTION_RECORD *record, const CONTEXT *context, DWORD flags, WitU64 caller)
{
    WitUserFatalInfo info = {};
    info.Version = WIT_FATAL_INFO_VERSION;
    info.Size = sizeof(info);
    info.Code = 0xC0000602U;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&info.Context,
            sizeof(info.Context), nullptr) != WIT_STATUS_OK) {
        wit_native_fail_fast(info.Code);
    }
    if (record) {
        info.Code = record->ExceptionCode;
        info.ExceptionFlags = record->ExceptionFlags;
        info.Address = (WitU64)record->ExceptionAddress;
        info.ParameterCount = record->NumberParameters;
        if (info.ParameterCount > WIT_FATAL_PARAMETER_CAPACITY) {
            info.ParameterCount = WIT_FATAL_PARAMETER_CAPACITY;
        }
        for (WitU32 i = 0; i < info.ParameterCount; ++i) {
            info.Parameters[i] = record->ExceptionInformation[i];
        }
    }
    if (!record || (flags & FAIL_FAST_GENERATE_EXCEPTION_ADDRESS)) {
        info.Address = caller;
    }
    (void)wit_native_call(WIT_CALL_FATAL_REPORT, (WitU64)&info, sizeof(info), WIT_FATAL_INFO_VERSION, nullptr);
    if (context) {
        // Diagnostic values, never an execution request. Do not impose restore
        // restrictions on a supplied failing context or mutate the caller's copy.
        info.NativeContextFlags = context->ContextFlags;
        auto &w = info.Context;
        const auto &c = *context;
        w.Rip = c.Rip;
        w.Rsp = c.Rsp;
        w.Rflags = c.EFlags;
        w.Cs = c.SegCs;
        w.Ss = c.SegSs;
        w.Rax = c.Rax;
        w.Rbx = c.Rbx;
        w.Rcx = c.Rcx;
        w.Rdx = c.Rdx;
        w.Rbp = c.Rbp;
        w.Rsi = c.Rsi;
        w.Rdi = c.Rdi;
        w.R8 = c.R8;
        w.R9 = c.R9;
        w.R10 = c.R10;
        w.R11 = c.R11;
        w.R12 = c.R12;
        w.R13 = c.R13;
        w.R14 = c.R14;
        w.R15 = c.R15;
        memcpy(w.FxState, &c.FltSave, sizeof(w.FxState));
    }
    // Commit the cause before optional output. Any later user fault or output
    // failure is forced through the armed kernel fatal path without handlers.
    (void)wit_native_call(WIT_CALL_FATAL_REPORT, (WitU64)&info, sizeof(info), WIT_FATAL_INFO_VERSION, nullptr);
    char text[128];
    char *out = text;
    word(out, "[NATIVE-FAIL-FAST] code=0x");
    hex(out, info.Code, 8);
    word(out, " address=0x");
    hex(out, info.Address, 16);
    word(out, " rip=0x");
    hex(out, info.Context.Rip, 16);
    *out++ = '\n';
    const WitU64 console = wit_native_process_console();
    if (console) {
        (void)wit_native_call(WIT_CALL_WRITE, console, (WitU64)text, (WitU64)(out - text), nullptr);
    }
    wit_native_fail_fast(info.Code);
}
