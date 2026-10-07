#include <stdint.h>
#include "minipal.h"
#include "function_tables_guest.witos.h"
#include "unwind_checked.witos.h"
#include "unwind_scope.witos.h"
extern "C" {
#include "bootstrap.h"
#include "../User/protocol.h"
void wit_dynamic_frame_begin();
void wit_dynamic_frame_end();
void wit_dynamic_nested_begin();
void wit_dynamic_nested_inner();
void wit_dynamic_nested_fault();
void wit_dynamic_nested_landing();
void wit_dynamic_nested_end();
void wit_dynamic_fault_begin();
void wit_dynamic_fault_end();
void wit_dynamic_fault_site();
void wit_dynamic_fault_resume();
PEXCEPTION_ROUTINE __cdecl wit_coreclr_virtual_unwind(
    DWORD, DWORD64, DWORD64, PRUNTIME_FUNCTION, CONTEXT *, void **, DWORD64 *, KNONVOLATILE_CONTEXT_POINTERS *);
}
static bool checkedFrame;
static unsigned directUnwind;
static CONTEXT directOutput;
static RUNTIME_FUNCTION functionEntry;
static DWORD64 codeBase;
static unsigned callbackCalls;
static unsigned failureMode;

static PRUNTIME_FUNCTION CALLBACK callback(DWORD64, void *context)
{
    ++callbackCalls;
    return context == &functionEntry ? &functionEntry : nullptr;
}

static void inspect_frame(DWORD64 sp, DWORD64 pc, DWORD64 savedRbx, DWORD64 returnAddress)
{
    DWORD64 base = 0;
    auto entry = RtlLookupFunctionEntry(pc, &base, nullptr);
    if (entry != &functionEntry || base != codeBase) {
        return;
    }
    CONTEXT context = {};
    context.ContextFlags = CONTEXT_FULL;
    context.Rsp = sp;
    context.Rip = pc;
    void *data = (void *)0x1234;
    DWORD64 frame = 0;
    KNONVOLATILE_CONTEXT_POINTERS pointers = {};
    auto forged = *entry;
    if (failureMode == 2) {
        entry = &forged;
    }
    auto handler = RtlVirtualUnwind(0, base, pc, entry, &context, &data, &frame, &pointers);
    checkedFrame = !handler &&
        data == (void *)0x1234 &&
        context.Rsp == sp + 48 &&
        context.Rip == returnAddress &&
        context.Rbx == savedRbx &&
        pointers.Rbx == (PDWORD64)(sp + 32);
    if (directUnwind == 1) {
        RtlUnwind((void *)sp, (void *)pc, nullptr, (void *)731);
    }
    if (directUnwind == 2) {
        RtlUnwindEx((void *)sp, (void *)pc, nullptr, (void *)731, &directOutput, nullptr);
    }
}

static volatile WitU64 foreignReady, foreignRelease;
static WitU64 foreignReference, foreignArguments[4];

static void foreign_capture(DWORD64 sp, DWORD64 pc, DWORD64 saved, DWORD64 returned)
{
    foreignArguments[0] = sp;
    foreignArguments[1] = pc;
    foreignArguments[2] = saved;
    foreignArguments[3] = returned;
    if (wit_native_call(WIT_CALL_THREAD_REFERENCE_DUPLICATE, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&foreignReference, 0,
            nullptr) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    foreignReady = 1;
    while (!foreignRelease) {
        (void)wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
    }
}

static WIT_NORETURN void foreign_worker(WitU64 entry)
{
    const auto result = ((int (*)(void (*)(DWORD64, DWORD64, DWORD64, DWORD64)))entry)(foreign_capture);
    (void)wit_native_call(WIT_CALL_THREAD_EXIT, (WitU64)result, 0, 0, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static bool foreign_walk(unsigned char *rx)
{
    foreignReady = foreignRelease = 0;
    foreignReference = 0;
    WitU64 thread = 0, previous = 99, result = 0;
    if (wit_native_call(WIT_CALL_THREAD_CREATE_SIMPLE, (WitU64)foreign_worker, (WitU64)rx, 0, &thread) !=
        WIT_STATUS_OK) {
        return false;
    }
    while (!foreignReady) {
        (void)wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
    }
    if (wit_native_call(WIT_CALL_THREAD_SUSPEND, foreignReference, 0, 0, &previous) != WIT_STATUS_OK || previous) {
        return false;
    }
    checkedFrame = false;
    {
        WitNativeUnwindScope scope(foreignReference);
        if (scope.Status() != WIT_STATUS_OK) {
            return false;
        }
        inspect_frame(foreignArguments[0], foreignArguments[1], foreignArguments[2], foreignArguments[3]);
        if (!checkedFrame ||
            wit_native_call(WIT_CALL_THREAD_RESUME, foreignReference, 0, 0, &previous) != WIT_STATUS_BUSY) {
            return false;
        }
    }
    if (wit_native_call(WIT_CALL_THREAD_RESUME, foreignReference, 0, 0, &previous) != WIT_STATUS_OK || previous != 1) {
        return false;
    }
    foreignRelease = 1;
    return wit_native_call(WIT_CALL_THREAD_JOIN, thread, 0, 0, &result) == WIT_STATUS_OK &&
        result == 42 &&
        wit_native_call(WIT_CALL_CLOSE, foreignReference, 0, 0, nullptr) == WIT_STATUS_OK;
}

static DWORD64 faultBase, faultPc, resumePc;
static unsigned handledFaults, cleanupFaults, dispatchMode;
static CONTEXT unwindOutput;

static LONG CALLBACK search_only(EXCEPTION_POINTERS *)
{
    return EXCEPTION_CONTINUE_SEARCH;
}

static EXCEPTION_DISPOSITION __cdecl fault_handler(
    EXCEPTION_RECORD *exception, void *frame, CONTEXT *context, DISPATCHER_CONTEXT *dispatcher)
{
    if (exception->ExceptionCode != EXCEPTION_ACCESS_VIOLATION ||
        exception->ExceptionInformation[0] != 0 ||
        exception->ExceptionInformation[1] ||
        (DWORD64)exception->ExceptionAddress != faultPc ||
        dispatcher->ImageBase != faultBase ||
        dispatcher->FunctionEntry != &functionEntry ||
        (DWORD64)dispatcher->LanguageHandler != faultBase + 384) {
        return ExceptionContinueSearch;
    }
    if (exception->ExceptionFlags & EXCEPTION_UNWINDING) {
        if (!(exception->ExceptionFlags & EXCEPTION_TARGET_UNWIND) || dispatcher->TargetIp != resumePc) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        ++cleanupFaults;
        return ExceptionContinueSearch;
    }
    ++handledFaults;
    if (dispatchMode == 1) {
        RtlUnwind(frame, (void *)resumePc, exception, (void *)731);
    }
    if (dispatchMode == 2) {
        RtlUnwindEx(frame, (void *)resumePc, exception, (void *)731, &unwindOutput, nullptr);
    }
    context->Rip = resumePc;
    context->Rax = 731;
    return ExceptionContinueExecution;
}

static bool exception_dispatch_probe()
{
    void *mapper = nullptr;
    size_t maximum = 0;
    if (!VMToOSInterface::CreateDoubleMemoryMapper(&mapper, &maximum)) {
        return false;
    }
    auto rx = (unsigned char *)VMToOSInterface::ReserveDoubleMappedMemory(mapper, 0, 65536, nullptr, nullptr);
    auto rw = (unsigned char *)VMToOSInterface::GetRWMapping(mapper, rx, 0, 65536);
    if (!rx || !rw || VMToOSInterface::CommitDoubleMappedMemory(rx, 4096, true) != rx) {
        return false;
    }
    const auto size = (size_t)((uintptr_t)wit_dynamic_fault_end - (uintptr_t)wit_dynamic_fault_begin);
    for (size_t i = 0; i < size; ++i) {
        rw[i] = ((const unsigned char *)wit_dynamic_fault_begin)[i];
    }
    const unsigned char info[] = {25, 5, 2, 0, 5, 0x32, 1, 0x30, 0x80, 1, 0, 0}; // EH handler RVA 384.
    for (unsigned i = 0; i < sizeof(info); ++i) {
        rw[256 + i] = info[i];
    }
    rw[384] = 0x48;
    rw[385] = 0xb8;
    *(WitU64 *)(rw + 386) = (WitU64)fault_handler;
    rw[394] = 0xff;
    rw[395] = 0xe0;
    faultBase = (DWORD64)rx;
    faultPc = faultBase + (uintptr_t)wit_dynamic_fault_site - (uintptr_t)wit_dynamic_fault_begin;
    resumePc = faultBase + (uintptr_t)wit_dynamic_fault_resume - (uintptr_t)wit_dynamic_fault_begin;
    functionEntry = {0, (DWORD)size, 256};
    handledFaults = 0;
    auto veh = AddVectoredExceptionHandler(0, search_only);
    if (!veh) {
        return false;
    }
    if (!RtlInstallFunctionTableCallback(
            faultBase | 3, faultBase, 65536, callback, &functionEntry, L"boot:/mscordaccore.dll")) {
        return false;
    }
    WitCodeMemoryRequest publish = {
        WIT_CODE_MEMORY_VERSION, sizeof(publish), WIT_CODE_PUBLISH, 0, faultBase, 0, 4096, 0, 0, 0};
    if (wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&publish, sizeof(publish), 0, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    for (dispatchMode = 0; dispatchMode < 3; ++dispatchMode) {
        handledFaults = cleanupFaults = 0;
        for (size_t i = 0; i < sizeof(unwindOutput); ++i) {
            ((unsigned char *)&unwindOutput)[i] = 0xA5;
        }
        if (((int (*)())rx)() != 742 || handledFaults != 1 || cleanupFaults != (dispatchMode ? 1U : 0U)) {
            return false;
        }
        if (dispatchMode == 2 && (unwindOutput.Rip != resumePc || unwindOutput.Rax != 731)) {
            return false;
        }
        WitThreadContext metadata;
        if (wit_native_call(WIT_CALL_THREAD_CONTEXT_METADATA, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&metadata,
                sizeof(metadata), nullptr) != WIT_STATUS_OK ||
            (metadata.Flags & WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)) {
            return false;
        }
    }
    if (!RtlDeleteFunctionTable((PRUNTIME_FUNCTION)(faultBase | 3)) || !RemoveVectoredExceptionHandler(veh)) {
        return false;
    }
    if (!VMToOSInterface::ReleaseRWMapping(rw, 65536) ||
        !VMToOSInterface::ReleaseDoubleMappedMemory(mapper, rx, 0, 65536)) {
        return false;
    }
    VMToOSInterface::DestroyDoubleMemoryMapper(mapper);
    return true;
}

static RUNTIME_FUNCTION nestedEntries[2];
static uint64_t nestedBase, nestedFault, nestedLanding, nestedTrace;
static bool redirectedCollision;

static PRUNTIME_FUNCTION CALLBACK nested_lookup(DWORD64 pc, void *)
{
    for (auto &entry : nestedEntries) {
        if (pc >= nestedBase + entry.BeginAddress && pc < nestedBase + entry.EndAddress) {
            return &entry;
        }
    }
    return nullptr;
}

static EXCEPTION_DISPOSITION __cdecl nested_handler(
    EXCEPTION_RECORD *exception, void *frame, CONTEXT *, DISPATCHER_CONTEXT *dispatcher)
{
    if (exception->ExceptionCode != EXCEPTION_ACCESS_VIOLATION ||
        (DWORD64)exception->ExceptionAddress != nestedFault ||
        dispatcher->ImageBase != nestedBase) {
        return ExceptionContinueSearch;
    }
    const bool inner = dispatcher->FunctionEntry == &nestedEntries[1];
    if (redirectedCollision && inner) {
        // Like CoreCLR FixupDispatcherContext: replace the dispatcher tuple,
        // including a personality that differs from the selected metadata.
        nestedTrace = nestedTrace * 10 + ((exception->ExceptionFlags & EXCEPTION_UNWINDING) ? 3 : 1);
        ((volatile WitU64 *)WIT_GC_INFO_REPORT)[4] = nestedTrace;
        dispatcher->ControlPc = dispatcher->ContextRecord->Rip;
        dispatcher->FunctionEntry = RtlLookupFunctionEntry(dispatcher->ControlPc, &dispatcher->ImageBase, nullptr);
        CONTEXT caller = *dispatcher->ContextRecord;
        void *data = nullptr;
        RtlVirtualUnwind(0, dispatcher->ImageBase, dispatcher->ControlPc, dispatcher->FunctionEntry, &caller, &data,
            &dispatcher->EstablisherFrame, nullptr);
        dispatcher->LanguageHandler = (PEXCEPTION_ROUTINE)(nestedBase + 400);
        dispatcher->HandlerData = (void *)0x1234;
        dispatcher->ScopeIndex = 7;
        if (failureMode == 4) {
            dispatcher->ContextRecord = nullptr;
        }
        if (failureMode == 5) {
            dispatcher->FunctionEntry = &functionEntry;
        }
        if (failureMode == 6) {
            dispatcher->LanguageHandler = (PEXCEPTION_ROUTINE)&nestedTrace;
        }
        if (failureMode == 7) {
            dispatcher->EstablisherFrame += 8;
        }
        if (failureMode == 8) {
            WitUserThreadInfo owner = {};
            if (wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&owner, sizeof(owner), WIT_THREAD_INFO_VERSION,
                    nullptr) != WIT_STATUS_OK) {
                wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
            }
            dispatcher->ContextRecord = (CONTEXT *)(owner.StackHigh - 8);
        }
        ((volatile WitU64 *)WIT_GC_INFO_REPORT)[5] = failureMode;
        return ExceptionCollidedUnwind;
    }
    if (redirectedCollision &&
        !inner &&
        ((uint64_t)dispatcher->LanguageHandler != nestedBase + 400 ||
            dispatcher->HandlerData != (void *)0x1234 ||
            dispatcher->ScopeIndex != 7)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    if (exception->ExceptionFlags & EXCEPTION_UNWINDING) {
        nestedTrace = nestedTrace * 10 + (inner ? 3 : 4);
        if ((exception->ExceptionFlags & EXCEPTION_TARGET_UNWIND) != (inner ? 0U : (DWORD)EXCEPTION_TARGET_UNWIND)) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        return ExceptionContinueSearch;
    }
    nestedTrace = nestedTrace * 10 + (inner ? 1 : 2);
    if (!inner) {
        RtlUnwindEx(frame, (void *)nestedLanding, exception, (void *)731, &unwindOutput, nullptr);
    }
    return ExceptionContinueSearch;
}

static bool nested_target_probe(bool redirect = false)
{
    void *mapper = nullptr;
    size_t maximum = 0;
    if (!VMToOSInterface::CreateDoubleMemoryMapper(&mapper, &maximum)) {
        return false;
    }
    auto rx = (unsigned char *)VMToOSInterface::ReserveDoubleMappedMemory(mapper, 0, 65536, nullptr, nullptr);
    auto rw = (unsigned char *)VMToOSInterface::GetRWMapping(mapper, rx, 0, 65536);
    if (!rx || !rw || VMToOSInterface::CommitDoubleMappedMemory(rx, 4096, true) != rx) {
        return false;
    }
    const auto inner = (uint32_t)((uintptr_t)wit_dynamic_nested_inner - (uintptr_t)wit_dynamic_nested_begin);
    const auto bytes = (uint32_t)((uintptr_t)wit_dynamic_nested_end - (uintptr_t)wit_dynamic_nested_begin);
    if (bytes > 200) {
        return false;
    }
    for (unsigned i = 0; i < bytes; ++i) {
        rw[i] = ((unsigned char *)wit_dynamic_nested_begin)[i];
    }
    const unsigned char info[] = {25, 5, 2, 0, 5, 0x32, 1, 0x30, 0x80, 1, 0, 0};
    for (unsigned i = 0; i < sizeof(info); ++i) {
        rw[256 + i] = info[i];
        rw[272 + i] = info[i];
    }
    rw[384] = 0x48;
    rw[385] = 0xb8;
    *(WitU64 *)(rw + 386) = (WitU64)nested_handler;
    rw[394] = 0xff;
    rw[395] = 0xe0;
    for (unsigned i = 0; i < 12; ++i) {
        rw[400 + i] = rw[384 + i];
    }
    redirectedCollision = redirect;
    nestedBase = (uint64_t)rx;
    nestedFault = nestedBase + (uintptr_t)wit_dynamic_nested_fault - (uintptr_t)wit_dynamic_nested_begin;
    nestedLanding = nestedBase + (uintptr_t)wit_dynamic_nested_landing - (uintptr_t)wit_dynamic_nested_begin;
    nestedEntries[0] = {0, inner, 256};
    nestedEntries[1] = {inner, bytes, 272};
    nestedTrace = 0;
    auto veh = AddVectoredExceptionHandler(0, search_only);
    if (!veh || !RtlInstallFunctionTableCallback(nestedBase | 3, nestedBase, 65536, nested_lookup, nullptr, nullptr)) {
        return false;
    }
    WitCodeMemoryRequest publish = {
        WIT_CODE_MEMORY_VERSION, sizeof(publish), WIT_CODE_PUBLISH, 0, nestedBase, 0, 4096, 0, 0, 0};
    if (wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&publish, sizeof(publish), 0, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    const int value = ((int (*)())rx)();
    ((volatile WitU64 *)WIT_GC_INFO_REPORT)[4] = nestedTrace;
    if (value != 742 || nestedTrace != 1234) {
        return false;
    }
    if (!RtlDeleteFunctionTable((PRUNTIME_FUNCTION)(nestedBase | 3)) ||
        !RemoveVectoredExceptionHandler(veh) ||
        !VMToOSInterface::ReleaseRWMapping(rw, 65536) ||
        !VMToOSInterface::ReleaseDoubleMappedMemory(mapper, rx, 0, 65536)) {
        return false;
    }
    VMToOSInterface::DestroyDoubleMemoryMapper(mapper);
    return true;
}

extern "C" bool wit_dynamic_unwind_probe(unsigned mode)
{
    failureMode = mode;
    if (mode >= 4) {
        *(volatile WitU64 *)WIT_GC_INFO_REPORT = mode;
        return nested_target_probe(true);
    }
    void *mapper = nullptr;
    size_t maximum = 0;
    if (!VMToOSInterface::CreateDoubleMemoryMapper(&mapper, &maximum)) {
        return false;
    }
    auto rx = (unsigned char *)VMToOSInterface::ReserveDoubleMappedMemory(mapper, 0, 65536, nullptr, nullptr);
    auto rw = (unsigned char *)VMToOSInterface::GetRWMapping(mapper, rx, 0, 65536);
    if (!rx || !rw || VMToOSInterface::CommitDoubleMappedMemory(rx, 4096, true) != rx) {
        return false;
    }
    const auto bytes = (size_t)((uintptr_t)wit_dynamic_frame_end - (uintptr_t)wit_dynamic_frame_begin);
    if (bytes > 128) {
        return false;
    }
    for (size_t i = 0; i < bytes; ++i) {
        rw[i] = ((const unsigned char *)wit_dynamic_frame_begin)[i];
    }
    const unsigned char info[] = {1, 5, 2, 0, 5, 0x32, 1, 0x30};
    for (unsigned i = 0; i < sizeof(info); ++i) {
        rw[256 + i] = info[i];
    }
    codeBase = (DWORD64)rx;
    functionEntry = {0, (DWORD)bytes, 256};
    WitCodeMemoryRequest publish = {
        WIT_CODE_MEMORY_VERSION, sizeof(publish), WIT_CODE_PUBLISH, 0, codeBase, 0, 4096, 0, 0, 0};
    if (wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&publish, sizeof(publish), 0, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    if (mode == 3) {
        rw[256] = 7; // A valid entry must not bypass metadata validation.
    }
    *(volatile WitU64 *)WIT_GC_INFO_REPORT = mode;
    for (unsigned kind = 0; kind < 2; ++kind) {
        checkedFrame = false;
        callbackCalls = 0;
        const auto key = kind ? (PRUNTIME_FUNCTION)(codeBase | 3) : &functionEntry;
        if (kind ? !RtlInstallFunctionTableCallback(
                       (DWORD64)key, codeBase, 65536, callback, &functionEntry, L"boot:/mscordaccore.dll")
                 : !RtlAddFunctionTable(&functionEntry, 1, codeBase)) {
            return false;
        }
        if (((int (*)(void (*)(DWORD64, DWORD64, DWORD64, DWORD64)))rx)(inspect_frame) != 42 ||
            !checkedFrame ||
            (kind && !callbackCalls)) {
            return false;
        }
        if (!mode && kind == 0) {
            for (directUnwind = 1; directUnwind <= 2; ++directUnwind) {
                checkedFrame = false;
                if (((int (*)(void (*)(DWORD64, DWORD64, DWORD64, DWORD64)))rx)(inspect_frame) != 42 || !checkedFrame) {
                    return false;
                }
                if (directUnwind == 2 &&
                    (directOutput.Rax != 731 || directOutput.Rip < codeBase || directOutput.Rip >= codeBase + bytes)) {
                    return false;
                }
                WitThreadContext metadata;
                if (wit_native_call(WIT_CALL_THREAD_CONTEXT_METADATA, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&metadata,
                        sizeof(metadata), nullptr) != WIT_STATUS_OK ||
                    (metadata.Flags & WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)) {
                    return false;
                }
            }
            directUnwind = 0;
        }
        if (!mode && kind == 1) {
            if (!foreign_walk(rx)) {
                return false;
            }
            if (!VMToOSInterface::ReleaseRWMapping(rw, 65536)) {
                return false;
            }
            rw = nullptr;
            if (VMToOSInterface::ReleaseDoubleMappedMemory(mapper, rx, 0, 65536)) {
                return false;
            }
        }
        if (!RtlDeleteFunctionTable(key) || RtlDeleteFunctionTable(key)) {
            return false;
        }
        DWORD64 untouched = 0x1234;
        if (RtlLookupFunctionEntry(codeBase + 5, &untouched, nullptr) || untouched != 0x1234) {
            return false;
        }
    }
    if ((rw && !VMToOSInterface::ReleaseRWMapping(rw, 65536)) ||
        !VMToOSInterface::ReleaseDoubleMappedMemory(mapper, rx, 0, 65536)) {
        return false;
    }
    if (RtlAddFunctionTable(&functionEntry, 1, codeBase) ||
        RtlInstallFunctionTableCallback(codeBase | 3, codeBase, 65536, callback, &functionEntry, nullptr)) {
        return false;
    }
    VMToOSInterface::DestroyDoubleMemoryMapper(mapper);
    return !mode && exception_dispatch_probe() && nested_target_probe() && nested_target_probe(true);
}
