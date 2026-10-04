#include "bootstrap.h"
#include "library_lifecycle.h"

/* Shared executor: no path/file/heap dependencies. Kernel publishes the whole
 * readonly plan; all native entrypoints execute here in user space. */
WitU64 wit_native_library_execute_lifecycle(WitU64 address)
{
    if (address) {
        WitCodeMemoryRequest check = {WIT_CODE_MEMORY_VERSION, sizeof(check), WIT_CODE_VALIDATE, 1, address, 0,
            sizeof(WitLibraryLifecycle), 0, 0, 0};
        if (wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&check, sizeof(check), 0, 0) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        check.Protection = 3;
        if (wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&check, sizeof(check), 0, 0) == WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        const WitLibraryLifecycle *plan = (const WitLibraryLifecycle *)address;
        if (plan->Version != WIT_LIBRARY_VERSION ||
            plan->Size != sizeof(*plan) ||
            plan->Attach > WIT_LIBRARY_THREAD_DETACH ||
            !plan->Count ||
            plan->Count > WIT_LIBRARY_CAPACITY ||
            !plan->Token) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        const WitU64 token = plan->Token;
        const WitU32 action = plan->Attach, attach = action == 1 ? 1U : 0U, count = plan->Count;
        void *reserved = action == WIT_LIBRARY_PROCESS_SHUTDOWN ? (void *)1 : 0;
        const WitU32 reason = action == WIT_LIBRARY_THREAD_ATTACH ? 2U
            : action == WIT_LIBRARY_THREAD_DETACH                 ? 3U
                                                                  : attach;
        for (WitU32 i = 0; i < count; ++i) {
            check.Address = plan->Entries[i].Entry;
            check.Bytes = 1;
            check.Protection = 5;
            if (!plan->Entries[i].Base ||
                wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&check, sizeof(check), 0, 0) != WIT_STATUS_OK) {
                wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
            }
        }
        typedef int (*Entry)(void *, WitU32, void *);
        WitU32 done = 0;
        int success = 1;
        for (; done < count;) {
            const WitLibraryLifecycleEntry *entry = &plan->Entries[done++];
            const int accepted = ((Entry)entry->Entry)((void *)entry->Base, reason, reserved);
            if (attach && !accepted) {
                success = 0;
                break;
            }
        }
        if (!success) {
            while (done) {
                const WitLibraryLifecycleEntry *entry = &plan->Entries[--done];
                (void)((Entry)entry->Entry)((void *)entry->Base, 0, 0);
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
        if (!success) {
            return WIT_STATUS_INITIALIZATION_FAILED;
        }
    }
    return WIT_STATUS_OK;
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
