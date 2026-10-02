#include <atomic>
#include <intrin.h>
#include "function_tables_guest.witos.h"
#include "unwind_scope.witos.h"
extern "C" {
#include "library.h"
#include "protocol.h"
}

namespace {
std::atomic<unsigned> ready{0}, released{0};
WitU64 reference, stack, control, callerStack, callerControl;

__declspec(noinline) int capture(void *callerSlot, void *returned)
{
    auto ownSlot = (void **)_AddressOfReturnAddress();
    stack = (WitU64)(ownSlot + 1);
    control = (WitU64)*ownSlot;
    callerStack = (WitU64)callerSlot + 8;
    callerControl = (WitU64)returned;
    if (wit_native_call(WIT_CALL_THREAD_REFERENCE_DUPLICATE, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&reference, 0,
            nullptr) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    ready.store(1, std::memory_order_release);
    while (!released.load(std::memory_order_acquire)) {
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
    return 37;
}

WIT_NORETURN void worker(WitU64 pc)
{
    const int result = ((int (*)(int (*)(void *, void *), int))pc)(capture, 5);
    (void)wit_native_call(WIT_CALL_THREAD_EXIT, (WitU64)result, 0, 0, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
}

extern "C" WitU64 wit_foreign_module_unwind_probe(WitU64 root, WitU64 pc, WitU64 expectedBase)
{
    WitFunctionLease module = {};
    if (!wit_coreclr_acquire_function(pc, &module) || !module.ModuleReader || module.Base != expectedBase) {
        return 3120;
    }
    auto forged = module;
    ++forged.Base;
    if (wit_coreclr_leased_function(forged, pc)) {
        return 3121;
    }
    ready.store(0, std::memory_order_relaxed);
    released.store(0, std::memory_order_relaxed);
    reference = 0;
    WitU64 thread = 0, previous = 99, result = 0;
    if (wit_native_call(WIT_CALL_THREAD_CREATE, (WitU64)worker, pc, 0, &thread) != WIT_STATUS_OK) {
        return 3122;
    }
    while (!ready.load(std::memory_order_acquire)) {
        (void)wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
    }
    if (wit_native_call(WIT_CALL_THREAD_SUSPEND, reference, 0, 0, &previous) != WIT_STATUS_OK || previous) {
        return 3123;
    }
    // Drop the external reference while the independent reader owns the image.
    // Its final release is delayed until the foreign DLL frame has returned.
    if (wit_native_library_unload(root) != WIT_STATUS_OK || wit_native_library_unload(root) != WIT_STATUS_DENIED) {
        return 3124;
    }
    {
        WitNativeUnwindScope scope(reference);
        if (scope.Status() != WIT_STATUS_OK) {
            return 3125;
        }
        DWORD64 base = 0;
        auto entry = RtlLookupFunctionEntry(control, &base, nullptr);
        if (!entry || base != expectedBase || wit_coreclr_leased_function(module, control) != entry) {
            return 3126;
        }
        CONTEXT context = {};
        context.ContextFlags = CONTEXT_FULL;
        context.Rip = control;
        context.Rsp = stack;
        void *data = (void *)0x1234;
        DWORD64 frame = 0;
        if (RtlVirtualUnwind(0, base, control, entry, &context, &data, &frame, nullptr) ||
            data != (void *)0x1234 ||
            context.Rip != callerControl ||
            context.Rsp != callerStack) {
            return 3127;
        }
        if (wit_native_call(WIT_CALL_THREAD_RESUME, reference, 0, 0, &previous) != WIT_STATUS_BUSY) {
            return 3128;
        }
    }
    if (wit_native_call(WIT_CALL_THREAD_RESUME, reference, 0, 0, &previous) != WIT_STATUS_OK || previous != 1) {
        return 3129;
    }
    released.store(1, std::memory_order_release);
    if (wit_native_call(WIT_CALL_THREAD_JOIN, thread, 0, 0, &result) != WIT_STATUS_OK ||
        result != 42 ||
        wit_native_call(WIT_CALL_CLOSE, reference, 0, 0, nullptr) != WIT_STATUS_OK) {
        return 3130;
    }
    if (!wit_coreclr_leased_function(module, pc)) {
        return 3131;
    }
    wit_coreclr_release_function(&module);
    WitLibraryInfo info;
    if (wit_native_library_info(root, &info) != WIT_STATUS_BAD_HANDLE) {
        return 3132;
    }
    return 42;
}
