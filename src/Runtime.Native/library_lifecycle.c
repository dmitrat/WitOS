#include "bootstrap.h"
#include "library_lifecycle.h"

/* Shared executor: no path/file/heap dependencies. Kernel publishes the whole readonly plan; all native entry points
 * and PE TLS callbacks execute here in user space. It stays one function: small fixtures that link it must remain
 * within the plain image profile's unwind-entry limit. */
WitU64 wit_native_library_execute_lifecycle(WitU64 address)
{
    if (!address) {
        return WIT_STATUS_OK;
    }
    typedef int (*Entry)(void *, WitU32, void *);
    typedef void (*Callback)(void *, WitU32, void *);
    const WitLibraryLifecycle *plan = (const WitLibraryLifecycle *)address;
    /* The plan and every callback list are readonly, never writable; every function they name is executable. */
    WitCodeMemoryRequest check = {
        WIT_CODE_MEMORY_VERSION, sizeof(check), WIT_CODE_VALIDATE, 1, address, 0, sizeof(*plan), 0, 0, 0};
    int valid = wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&check, sizeof(check), 0, 0) == WIT_STATUS_OK;
    check.Protection = 3;
    valid = valid &&
        wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&check, sizeof(check), 0, 0) != WIT_STATUS_OK &&
        plan->Version == WIT_LIBRARY_VERSION &&
        plan->Size == sizeof(*plan) &&
        plan->Attach <= WIT_LIBRARY_THREAD_DETACH &&
        plan->Count &&
        plan->Count <= WIT_LIBRARY_CAPACITY &&
        plan->Token;
    for (WitU32 i = 0; valid && i < plan->Count; ++i) {
        const WitLibraryLifecycleEntry *entry = &plan->Entries[i];
        const WitU64 *list = (const WitU64 *)entry->Callbacks;
        valid = entry->Base &&
            (entry->Entry || entry->CallbackCount) &&
            entry->CallbackCount <= WIT_PE_TLS_CALLBACK_CAPACITY;
        if (valid && entry->CallbackCount) {
            check.Address = entry->Callbacks;
            check.Bytes = 8ULL * (entry->CallbackCount + 1);
            check.Protection = 1;
            valid = wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&check, sizeof(check), 0, 0) == WIT_STATUS_OK;
            check.Protection = 3;
            valid = valid &&
                wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&check, sizeof(check), 0, 0) != WIT_STATUS_OK &&
                !list[entry->CallbackCount];
        }
        for (WitU32 k = 0; valid && k <= entry->CallbackCount; ++k) {
            /* The callbacks, then the entry point, if any. */
            check.Address = k < entry->CallbackCount ? list[k] : entry->Entry;
            check.Bytes = 1;
            check.Protection = 5;
            valid = (!check.Address && k == entry->CallbackCount) ||
                wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&check, sizeof(check), 0, 0) == WIT_STATUS_OK;
        }
    }
    if (!valid) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    const WitU64 token = plan->Token;
    const WitU32 action = plan->Attach, attach = action == 1 ? 1U : 0U, count = plan->Count;
    void *reserved = action == WIT_LIBRARY_PROCESS_SHUTDOWN ? (void *)1 : 0;
    WitU32 reason = action == WIT_LIBRARY_THREAD_ATTACH ? 2U : action == WIT_LIBRARY_THREAD_DETACH ? 3U : attach;
    /* As on Windows, a library's TLS callbacks run before its entry point for every reason; a callback-only library
     * accepts. A failed attach detaches the libraries already called, newest first, in the same order. */
    WitU32 done = 0;
    int success = 1, forward = 1;
    while (forward ? done < count : done > 0) {
        const WitLibraryLifecycleEntry *entry = &plan->Entries[forward ? done++ : --done];
        for (WitU32 k = 0; k < entry->CallbackCount; ++k) {
            ((Callback)((const WitU64 *)entry->Callbacks)[k])((void *)entry->Base, reason, reserved);
        }
        const int accepted = entry->Entry ? ((Entry)entry->Entry)((void *)entry->Base, reason, reserved) : 1;
        if (forward && attach && !accepted) {
            success = forward = 0;
            reason = 0;
            reserved = 0;
        }
    }
    WitLibraryRequest finish = {0};
    finish.Operation = WIT_LIBRARY_FINISH_LIFECYCLE;
    finish.Handle = token;
    finish.Ordinal = success ? 1 : 0;
    finish.Version = WIT_LIBRARY_VERSION;
    finish.Size = sizeof(finish);
    if (wit_native_call(WIT_CALL_LIBRARY, (WitU64)&finish, sizeof(finish), 0, 0) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return success ? WIT_STATUS_OK : WIT_STATUS_INITIALIZATION_FAILED;
}

/* The three notifications share one request/dispatch frame. Keep the shutdown
 * BUSY result visible; only thread notifications wait for a foreign owner. */
static WitU64 lifecycle_notification(WitU32 operation)
{
    for (;;) {
        WitU64 address = 0;
        WitLibraryRequest request = {WIT_LIBRARY_VERSION, sizeof(request), operation, WIT_LIBRARY_USER_LIFECYCLE, 0, 0,
            0, 0, (WitU64)&address, 8};
        const WitU64 status = wit_native_call(WIT_CALL_LIBRARY, (WitU64)&request, sizeof(request), 0, 0);
        if (status != WIT_STATUS_BUSY || operation == WIT_LIBRARY_SHUTDOWN) {
            return status == WIT_STATUS_OK ? wit_native_library_execute_lifecycle(address) : status;
        }
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, 0) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
}

WitU64 wit_native_library_shutdown(void)
{
    return lifecycle_notification(WIT_LIBRARY_SHUTDOWN);
}

WitU64 wit_native_library_thread_enter(void)
{
    return lifecycle_notification(WIT_LIBRARY_THREAD_ENTER);
}

WitU64 wit_native_library_thread_leave(void)
{
    return lifecycle_notification(WIT_LIBRARY_THREAD_LEAVE);
}
