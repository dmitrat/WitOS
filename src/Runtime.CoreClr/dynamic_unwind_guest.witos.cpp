#include "function_tables_guest.witos.h"
#include "unwind_checked.witos.h"
#include "unwind_scope.witos.h"
extern "C" {
#include "bootstrap.h"
#include "image.h"
PEXCEPTION_ROUTINE __cdecl wit_native_rtl_virtual_unwind(
    DWORD, DWORD64, DWORD64, PRUNTIME_FUNCTION, CONTEXT *, void **, DWORD64 *, KNONVOLATILE_CONTEXT_POINTERS *);
}

namespace {
const void *read(void *, DWORD64 address, DWORD bytes, bool code)
{
    return wit_coreclr_unwind_read(address, bytes, code);
}

PRUNTIME_FUNCTION lookup(void *value, DWORD64 pc)
{
    return wit_coreclr_leased_function(*(WitFunctionLease *)value, pc);
}

PEXCEPTION_ROUTINE perform(DWORD type, DWORD64 base, DWORD64 pc, PRUNTIME_FUNCTION entry, CONTEXT *context, void **data,
    DWORD64 *frame, KNONVOLATILE_CONTEXT_POINTERS *pointers)
{
    WitStackLeaseInfo stack;
    if (!context || WitNativeUnwindScope::Current(context->Rsp, &stack) != WIT_STATUS_OK) {
        wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    }
    WitFunctionLease registration = {};
    if (!wit_coreclr_acquire_function(pc, &registration)) {
        wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    }
    const WitUnwindStackRange bounds = {stack.StackLow, stack.StackHigh};
    const WitDynamicUnwindSource source = {registration.Base, registration.Length, &registration, read, lookup};
    PEXCEPTION_ROUTINE handler = nullptr;
    const auto status = registration.Base == base && registration.Entry == (WitRuntimeFunction *)entry
        ? wit_checked_virtual_unwind_dynamic(
              &source, &bounds, type, pc, entry, context, data, frame, pointers, &handler)
        : E_INVALIDARG;
    wit_coreclr_release_function(&registration);
    if (FAILED(status)) {
        wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    }
    return handler;
}
}

extern "C" PEXCEPTION_ROUTINE __cdecl wit_coreclr_virtual_unwind(DWORD type, DWORD64 base, DWORD64 pc,
    PRUNTIME_FUNCTION entry, CONTEXT *context, void **data, DWORD64 *frame, KNONVOLATILE_CONTEXT_POINTERS *pointers)
{
    const auto image = wit_native_process_image();
    if (image &&
        base == image->Base &&
        wit_native_image_range(image, pc, 1, WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE, 1)) {
        return wit_native_rtl_virtual_unwind(type, base, pc, entry, context, data, frame, pointers);
    }
    if (!context) {
        wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    }
    WitStackLeaseInfo stack;
    const auto status = WitNativeUnwindScope::Current(context->Rsp, &stack);
    if (status == WIT_STATUS_OK) {
        return perform(type, base, pc, entry, context, data, frame, pointers);
    }
    if (status != WIT_STATUS_CLOSED) {
        wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    }
    WitNativeUnwindScope current(WIT_THREAD_REFERENCE_CURRENT);
    if (current.Status() != WIT_STATUS_OK) {
        wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    }
    return perform(type, base, pc, entry, context, data, frame, pointers);
}
