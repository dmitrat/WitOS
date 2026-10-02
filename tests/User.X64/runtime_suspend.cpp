#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "protocol.h"
#include <errno.h>
extern "C" DWORD WINAPI wit_test_suspend(HANDLE);
extern "C" DWORD WINAPI wit_test_resume(HANDLE);
static HANDLE eventHandle;
static volatile HANDLE reference;
static volatile WitU64 completed;

static bool duplicate(HANDLE source, HANDLE *output, DWORD access = 0, DWORD options = DUPLICATE_SAME_ACCESS)
{
    return DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), output, access, FALSE, options) != 0;
}

static bool info(HANDLE handle, WitThreadReferenceInfo &value)
{
    return wit_native_call(WIT_CALL_THREAD_REFERENCE_QUERY, (WitU64)handle, (WitU64)&value, sizeof(value), nullptr) ==
        WIT_STATUS_OK;
}

static void yield()
{
    wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
}

static bool parked(HANDLE *result)
{
    for (unsigned i = 0; i < 10000; ++i) {
        WitThreadReferenceInfo value;
        const HANDLE found = reference;
        if (found && info(found, value) && value.State == WIT_THREAD_REFERENCE_WAITING) {
            *result = found;
            return true;
        }
        yield();
    }
    return false;
}

static WitU64 waiter(WitU64 closed)
{
    HANDLE handle = nullptr;
    if (!duplicate(GetCurrentThread(), &handle)) {
        return 3701;
    }
    reference = handle;
    const DWORD result = WaitForMultipleObjectsEx(1, &eventHandle, FALSE, INFINITE, FALSE);
    if (result != (closed ? WAIT_FAILED : WAIT_OBJECT_0) || (closed && GetLastError() != ERROR_INVALID_HANDLE)) {
        return 3702;
    }
    completed = 1;
    return WIT_TEST_EXIT_CODE;
}

static WitU64 resumer(WitU64 target)
{
    for (unsigned i = 0; i < 10000; ++i) {
        WitThreadReferenceInfo value;
        if (info((HANDLE)target, value) && value.SuspendCount == 1) {
            return ResumeThread((HANDLE)target) == 1 ? WIT_TEST_EXIT_CODE : 3703;
        }
        yield();
    }
    return 3704;
}

extern "C" WitU64 wit_test_suspension(const WitUserStartup *startup, WitU64 mode)
{
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[0] = mode;
    report[1] = 2199023255552ULL;
    wit_native_security_initialize_system();
    wit_native_process_image_initialize(startup);
    if (mode == 93) {
        report[2] = 1;
        SuspendThread(GetCurrentThread());
        report[2] = 2;
        return 3705;
    }
    const bool tls = mode == 91;
    if (tls) {
        wit_native_tls_initialize(startup);
    }
    SetLastError(0x92345678);
    if (tls) {
        errno = 247;
    }
    if (ResumeThread(GetCurrentThread()) || GetLastError() != 0x92345678) {
        return 3706;
    }
    HANDLE restricted = nullptr;
    if (!duplicate(GetCurrentThread(), &restricted, THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, 0) ||
        SuspendThread(restricted) != (DWORD)-1 ||
        GetLastError() != ERROR_ACCESS_DENIED ||
        ResumeThread(restricted) != (DWORD)-1 ||
        GetLastError() != ERROR_ACCESS_DENIED ||
        !CloseHandle(restricted)) {
        return 3707;
    }
    WitU64 result = 99;
    if (wit_native_call(WIT_CALL_THREAD_SUSPEND, WIT_THREAD_REFERENCE_CURRENT, 1, 0, &result) !=
            WIT_STATUS_INVALID_ARGUMENT ||
        result) {
        return 3708;
    }
    if (tls) {
        for (unsigned closed = 0; closed < 2; ++closed) {
            eventHandle = CreateEventExW(nullptr, nullptr, 0, SYNCHRONIZE | EVENT_MODIFY_STATE);
            if (!eventHandle) {
                return 3709;
            }
            reference = nullptr;
            completed = 0;
            WitU64 join = 0;
            HANDLE target = nullptr, controller = nullptr;
            if (wit_native_thread_create(waiter, closed, &join) != WIT_STATUS_OK ||
                !parked(&target) ||
                !duplicate(target, &controller,
                    THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION, 0)) {
                return 3710;
            }
            if (wit_test_suspend(controller) != 0 || SuspendThread(controller) != 1) {
                return 3711;
            }
            WitThreadReferenceInfo value;
            WitThreadContext before, after;
            if (!info(controller, value) ||
                value.Version != WIT_THREAD_REFERENCE_VERSION ||
                value.SuspendCount != 2 ||
                value.State != WIT_THREAD_REFERENCE_SUSPENDED ||
                value.Reserved ||
                wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, (WitU64)controller, (WitU64)&before, sizeof(before),
                    nullptr) != WIT_STATUS_OK ||
                before.SuspendCount != 2 ||
                (before.Flags & (WIT_THREAD_CONTEXT_SUSPENDED | WIT_THREAD_CONTEXT_SERVICE_ACTIVE)) !=
                    (WIT_THREAD_CONTEXT_SUSPENDED | WIT_THREAD_CONTEXT_SERVICE_ACTIVE)) {
                return 3712;
            }
            if (wit_native_call(WIT_CALL_THREAD_CONTEXT_SET, (WitU64)controller, (WitU64)&before, sizeof(before),
                    nullptr) != WIT_STATUS_BUSY) {
                return 3730;
            }
            if (!closed) {
                for (DWORD i = 2; i < WIT_THREAD_SUSPEND_MAX; ++i) {
                    if (SuspendThread(controller) != i) {
                        return 3713;
                    }
                }
                if (SuspendThread(controller) != (DWORD)-1 || GetLastError() != ERROR_SIGNAL_REFUSED) {
                    return 3714;
                }
                for (DWORD i = WIT_THREAD_SUSPEND_MAX; i > 2; --i) {
                    if (ResumeThread(controller) != i) {
                        return 3715;
                    }
                }
            }
            if (!CloseHandle(target)) {
                return 3716; // Closing a reference must not resume the target.
            }
            HANDLE replacement = nullptr;
            if (closed) {
                if (!CloseHandle(eventHandle)) {
                    return 3717;
                }
                replacement =
                    CreateEventExW(nullptr, nullptr, CREATE_EVENT_INITIAL_SET, SYNCHRONIZE | EVENT_MODIFY_STATE);
                if (!replacement) {
                    return 3718;
                }
            } else if (!SetEvent(eventHandle)) {
                return 3719;
            }
            for (unsigned i = 0; i < 8; ++i) {
                yield();
            }
            if (completed ||
                !info(controller, value) ||
                value.SuspendCount != 2 ||
                wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, (WitU64)controller, (WitU64)&after, sizeof(after),
                    nullptr) != WIT_STATUS_OK ||
                after.State != WIT_THREAD_CONTEXT_READY ||
                after.SuspendCount != 2 ||
                (after.Flags & WIT_THREAD_CONTEXT_SERVICE_ACTIVE) ||
                !(after.Flags & WIT_THREAD_CONTEXT_SUSPENDED) ||
                after.Rip != before.Rip ||
                after.Rsp != before.Rsp) {
                return 3720;
            }
            if (wit_test_resume(controller) != 2) {
                return 3721;
            }
            for (unsigned i = 0; i < 8; ++i) {
                yield();
            }
            if (completed || !info(controller, value) || value.SuspendCount != 1 || ResumeThread(controller) != 1) {
                return 3722;
            }
            if (wit_native_call(WIT_CALL_THREAD_JOIN, join, 0, 0, &result) != WIT_STATUS_OK ||
                result != WIT_TEST_EXIT_CODE ||
                completed != 1) {
                return 3723;
            }
            if (SuspendThread(controller) != (DWORD)-1 ||
                GetLastError() != ERROR_INVALID_HANDLE ||
                !CloseHandle(controller) ||
                ResumeThread(controller) != (DWORD)-1 ||
                GetLastError() != ERROR_INVALID_HANDLE) {
                return 3724;
            }
            if (closed) {
                if (WaitForMultipleObjectsEx(1, &replacement, FALSE, 0, FALSE) != WAIT_OBJECT_0 ||
                    !CloseHandle(replacement)) {
                    return 3725;
                }
            } else if (!CloseHandle(eventHandle)) {
                return 3726;
            }
        }
        HANDLE self = nullptr;
        WitU64 join = 0;
        if (!duplicate(GetCurrentThread(), &self) ||
            wit_native_thread_create(resumer, (WitU64)self, &join) != WIT_STATUS_OK) {
            return 3727;
        }
        if (SuspendThread(GetCurrentThread()) != 0 ||
            ResumeThread(self) != 0 ||
            wit_native_call(WIT_CALL_THREAD_JOIN, join, 0, 0, &result) != WIT_STATUS_OK ||
            result != WIT_TEST_EXIT_CODE ||
            !CloseHandle(self)) {
            return 3728;
        }
        wit_native_tls_leave();
    }
    SetLastError(0x92345678);
    if (wit_test_resume(GetCurrentThread()) || GetLastError() != 0x92345678 || (tls && errno != 247)) {
        return 3729;
    }
    return WIT_TEST_EXIT_CODE;
}
