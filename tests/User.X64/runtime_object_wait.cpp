#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "../User/protocol.h"
#include <errno.h>
extern "C" DWORD WINAPI wit_wait_direct(DWORD, const HANDLE *, BOOL, DWORD, BOOL);
extern "C" DWORD WINAPI wit_apc_direct(PAPCFUNC, HANDLE, ULONG_PTR);
extern "C" HANDLE WINAPI wit_event_direct(LPSECURITY_ATTRIBUTES, LPCWSTR, DWORD, DWORD);
extern "C" BOOL WINAPI wit_set_direct(HANDLE);
extern "C" const void *const __imp_WaitForMultipleObjectsEx;
static volatile bool requeue;
static HANDLE events[2];
static volatile HANDLE target;
static volatile WitU64 callbacks, sequence, callbackThread, phase, proceed;
static volatile DWORD expectedThread;
static HANDLE mainReference;
static volatile bool closeReference;

// A queued user APC is an activation (RFC 0011 section 7.5): the kernel runs this callback on the target thread
// through the process's fault callback at the target's next return to user mode, before the target's own code
// resumes, and a wait the target was parked in reports the interruption. A callback may queue another activation,
// which follows after this one continues.
static void CALLBACK callback(ULONG_PTR value)
{
    ++callbacks;
    sequence = sequence * 10 + value;
    callbackThread = GetCurrentThreadId();
    if (requeue && value == 1 && !QueueUserAPC(callback, GetCurrentThread(), 5)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
}

static HANDLE event(DWORD flags = 0, DWORD access = MAXIMUM_ALLOWED | SYNCHRONIZE | EVENT_MODIFY_STATE)
{
    return CreateEventExW(nullptr, nullptr, flags, access);
}

static bool duplicate(HANDLE source, HANDLE *output, DWORD access = 0, DWORD options = DUPLICATE_SAME_ACCESS)
{
    return DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), output, access, FALSE, options) != 0;
}

static bool snapshot(HANDLE handle, WitUserThreadInfo &info)
{
    return wit_native_thread_query((WitU64)handle, &info) == WIT_STATUS_OK;
}

static bool parked(HANDLE handle)
{
    for (;;) {
        WitUserThreadInfo info;
        if (!snapshot(handle, info) || info.State == WIT_THREAD_STATE_EXITED) {
            return false;
        }
        if (info.State == WIT_THREAD_STATE_WAITING) {
            return true;
        }
        Sleep(0);
    }
}

static bool reached(volatile WitU64 &flag)
{
    while (!flag) {
        Sleep(0);
    }
    return true;
}

static WitU64 reference_target(WitU64)
{
    HANDLE reference = nullptr;
    if (!duplicate(GetCurrentThread(), &reference)) {
        return 2840;
    }
    target = reference;
    return WaitForMultipleObjectsEx(1, events, FALSE, INFINITE, FALSE) == WAIT_OBJECT_0 ? WIT_TEST_EXIT_CODE : 2841;
}

static WitU64 reference_controller(WitU64)
{
    if (!parked(mainReference)) {
        return 2842;
    }
    if (closeReference && !CloseHandle(target)) {
        return 2843;
    }
    return SetEvent(events[0]) ? WIT_TEST_EXIT_CODE : 2844;
}

static WitU64 worker(WitU64 mode)
{
    HANDLE reference = nullptr;
    if (!duplicate(GetCurrentThread(), &reference)) {
        return 2801;
    }
    expectedThread = GetCurrentThreadId();
    target = reference;
    const bool all = mode == 2;
    const bool alertable = mode == 0 || mode == 4;
    const DWORD result = WaitForMultipleObjectsEx(all ? 2 : 1, events, all, mode == 4 ? 1 : INFINITE, alertable);
    if (mode == 0) {
        // Activated while parked in an alertable wait: the callback ran, the wait reports it.
        if (result != WAIT_IO_COMPLETION || callbacks != 1) {
            return 2802;
        }
    } else if (mode == 3) {
        if (result != WAIT_FAILED || GetLastError() != ERROR_INVALID_HANDLE) {
            return 2803;
        }
        phase = 1;
        reached(proceed);
        // The activation queued after the close is delivered at this thread's next return to user mode; the poll
        // itself has nothing to report.
        if (WaitForMultipleObjectsEx(1, &events[1], FALSE, 0, TRUE) != WAIT_TIMEOUT || callbacks != 1) {
            return 2805;
        }
    } else {
        // Mode 1: the activation interrupted the non-alertable wait, the callback ran and the wait restarted until
        // the event was set. Mode 2: the activation queued after the wait-all completed ran before this code resumed.
        // Mode 4: the alertable wait timed out before any activation.
        if (result != (mode == 4 ? WAIT_TIMEOUT : WAIT_OBJECT_0) || callbacks != (mode == 4 ? 0U : 1U)) {
            return 2804;
        }
        phase = 1;
        if (mode == 4) {
            reached(proceed);
        }
        if (WaitForMultipleObjectsEx(1, &events[1], FALSE, INFINITE, TRUE) != WAIT_IO_COMPLETION) {
            return 2807;
        }
    }
    if (callbackThread != expectedThread) {
        return 2806;
    }
    return WIT_TEST_EXIT_CODE;
}

// Suspended with four activations pending, then resumed: the four run in order before the wait reports them.
static WitU64 queued_worker(WitU64)
{
    HANDLE reference = nullptr;
    if (!duplicate(GetCurrentThread(), &reference)) {
        return 2860;
    }
    expectedThread = GetCurrentThreadId();
    target = reference;
    if (WaitForMultipleObjectsEx(1, &events[1], FALSE, INFINITE, TRUE) != WAIT_IO_COMPLETION) {
        return 2861;
    }
    return callbacks == 4 && sequence == 1234 && callbackThread == expectedThread ? WIT_TEST_EXIT_CODE : 2862;
}

static bool basic()
{
    events[0] = event(CREATE_EVENT_INITIAL_SET);
    events[1] = event();
    if (!events[0] || !events[1]) {
        return false;
    }
    callbacks = sequence = 0;
    const DWORD self = GetCurrentThreadId();
    // A self-activation is delivered at the return of the activating call itself; a later non-alertable wait sees
    // nothing of it, and an alertable poll with no activation pending times out.
    if (!wit_apc_direct(callback, GetCurrentThread(), 1) || callbacks != 1 || sequence != 1 || callbackThread != self) {
        return false;
    }
    if (wit_wait_direct(1, events, FALSE, 0, FALSE) != WAIT_OBJECT_0 || callbacks != 1) {
        return false;
    }
    if (WaitForMultipleObjectsEx(1, events, FALSE, 0, TRUE) != WAIT_TIMEOUT || callbacks != 1) {
        return false;
    }
    // A callback that activates its own thread again: the second runs after the first continues, in order.
    callbacks = sequence = 0;
    requeue = true;
    if (!QueueUserAPC(callback, GetCurrentThread(), 1) || callbacks != 2 || sequence != 15 || callbackThread != self) {
        return false;
    }
    requeue = false;
    WitU64 arena = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    auto edge = (unsigned char *)(arena + 4088);
    *(HANDLE *)edge = events[0];
    if (!SetEvent(events[0]) ||
        WaitForMultipleObjectsEx(2, (HANDLE *)edge, FALSE, 0, TRUE) != WAIT_FAILED ||
        GetLastError() != ERROR_INVALID_ADDRESS ||
        callbacks != 2) {
        return false;
    }
    if (wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    HANDLE invalid[2] = {events[0], INVALID_HANDLE_VALUE};
    if (WaitForMultipleObjectsEx(2, invalid, FALSE, 0, TRUE) != WAIT_FAILED ||
        callbacks != 2 ||
        GetLastError() != ERROR_INVALID_HANDLE ||
        WaitForMultipleObjectsEx(1, events, FALSE, 0, FALSE) != WAIT_OBJECT_0) {
        return false;
    }
    if (!wit_set_direct(events[0]) ||
        WaitForMultipleObjectsEx(2, events, TRUE, 0, FALSE) != WAIT_TIMEOUT ||
        WaitForMultipleObjectsEx(1, events, FALSE, 0, FALSE) != WAIT_OBJECT_0) {
        return false;
    }
    if (!SetEvent(events[0]) ||
        !SetEvent(events[1]) ||
        WaitForMultipleObjectsEx(2, events, TRUE, 0, FALSE) != WAIT_OBJECT_0 ||
        WaitForMultipleObjectsEx(2, events, FALSE, 0, FALSE) != WAIT_TIMEOUT) {
        return false;
    }
    HANDLE aliases[2] = {events[0], events[0]};
    if (!SetEvent(events[0]) ||
        WaitForMultipleObjectsEx(2, aliases, TRUE, 0, FALSE) != WAIT_FAILED ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        WaitForMultipleObjectsEx(2, aliases, FALSE, 0, FALSE) != WAIT_OBJECT_0) {
        return false;
    }
    HANDLE waitOnly = wit_event_direct(nullptr, nullptr, 0, SYNCHRONIZE);
    if (!waitOnly || SetEvent(waitOnly) || GetLastError() != ERROR_ACCESS_DENIED || !CloseHandle(waitOnly)) {
        return false;
    }
    // The callback must be executable and not writable; the kernel validates it before it marks the thread.
    if (QueueUserAPC(nullptr, GetCurrentThread(), 0) ||
        GetLastError() != ERROR_INVALID_ADDRESS ||
        QueueUserAPC((PAPCFUNC)(uintptr_t)events, GetCurrentThread(), 0) ||
        GetLastError() != ERROR_INVALID_ADDRESS ||
        callbacks != 2) {
        return false;
    }
    WitU64 copied = 99;
    const void *binding = __imp_WaitForMultipleObjectsEx;
    if (wit_native_call(WIT_CALL_MONOTONIC_QUERY, (WitU64)&__imp_WaitForMultipleObjectsEx, 8, WIT_MONOTONIC_COUNTER,
            &copied) != WIT_STATUS_BAD_ADDRESS ||
        copied ||
        binding != __imp_WaitForMultipleObjectsEx) {
        return false;
    }
    return CloseHandle(events[0]) && CloseHandle(events[1]);
}

extern "C" WitU64 wit_test_object_wait(const WitUserStartup *startup, WitU64 mode)
{
    ((WitU64 *)WIT_GC_INFO_REPORT)[1] = 4294967296ULL;
    wit_native_security_initialize_system();
    wit_native_process_image_initialize(startup);
    const bool tls = mode == 66;
    if (tls) {
        wit_native_tls_initialize(startup);
    }
    if (tls) {
        errno = 137;
    }
    SetLastError(0x73512864);
    if (!basic()) {
        return 2810;
    }
    if (tls) {
        for (WitU64 kind = 0; kind < 5; ++kind) {
            events[0] = event();
            events[1] = event();
            if (!events[0] || !events[1]) {
                return 2811;
            }
            target = nullptr;
            phase = proceed = 0;
            callbacks = sequence = callbackThread = 0;
            WitU64 join, result;
            if (wit_native_thread_create(worker, kind, &join) != WIT_STATUS_OK) {
                return 2812;
            }
            while (!target) {
                Sleep(0);
            }
            HANDLE reference = target;
            if (kind != 4 && !parked(reference)) {
                return 2813;
            }
            if (kind == 0) {
                // THREAD_SET_CONTEXT carries the ACTIVATE right; query and get-context handles do not.
                HANDLE queryOnly = nullptr;
                if (!duplicate(reference, &queryOnly, THREAD_QUERY_INFORMATION, 0)) {
                    return 2814;
                }
                if (QueueUserAPC(callback, queryOnly, 9) ||
                    GetLastError() != ERROR_ACCESS_DENIED ||
                    !CloseHandle(queryOnly)) {
                    return 2815;
                }
                HANDLE getOnly = nullptr;
                if (!duplicate(reference, &getOnly, THREAD_GET_CONTEXT, 0) ||
                    QueueUserAPC(callback, getOnly, 9) ||
                    GetLastError() != ERROR_ACCESS_DENIED ||
                    !CloseHandle(getOnly) ||
                    !QueueUserAPC(callback, reference, 7)) {
                    return 2831;
                }
            } else if (kind == 1) {
                // The activation ends the parked non-alertable wait; the worker has not run yet, so its callback is
                // still to come, and the restarted wait consumes the event set afterwards.
                if (!QueueUserAPC(callback, reference, 7)) {
                    return 2816;
                }
                WitUserThreadInfo info;
                if (!snapshot(reference, info) || info.State == WIT_THREAD_STATE_WAITING || callbacks) {
                    return 2817;
                }
                if (!SetEvent(events[0]) ||
                    !reached(phase) ||
                    !parked(reference) ||
                    !QueueUserAPC(callback, reference, 7)) {
                    return 2818;
                }
            } else if (kind == 2) {
                const HANDLE captured = events[0];
                const HANDLE replacement = event();
                if (!replacement) {
                    return 2830;
                }
                // Kernel must retain its copied handle array while parked.
                events[0] = replacement;
                if (!SetEvent(captured)) {
                    return 2820;
                }
                WitUserThreadInfo info;
                if (!snapshot(reference, info) ||
                    info.State != WIT_THREAD_STATE_WAITING ||
                    WaitForMultipleObjectsEx(1, &captured, FALSE, 0, FALSE) != WAIT_OBJECT_0 ||
                    !SetEvent(captured) ||
                    !SetEvent(events[1]) ||
                    !QueueUserAPC(callback, reference, 7) ||
                    !CloseHandle(captured)) {
                    return 2821;
                }
                if (!reached(phase) || !parked(reference) || !QueueUserAPC(callback, reference, 7)) {
                    return 2832;
                }
            } else if (kind == 3) {
                if (!CloseHandle(events[0]) || !QueueUserAPC(callback, reference, 7)) {
                    return 2822;
                }
                events[0] = event();
                if (!events[0]) {
                    return 2823;
                }
                proceed = 1;
            } else {
                reached(phase);
                if (callbacks || !QueueUserAPC(callback, reference, 7)) {
                    return 2824;
                }
                proceed = 1;
                if (!parked(reference) || !QueueUserAPC(callback, reference, 7)) {
                    return 2833;
                }
            }
            if (wit_native_thread_join(join, &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE) {
                return 2825;
            }
            const WitU64 expected[] = {1, 2, 2, 1, 2};
            if (callbacks != expected[kind] ||
                WaitForMultipleObjectsEx(1, &reference, FALSE, 0, FALSE) != WAIT_OBJECT_0 ||
                QueueUserAPC(callback, reference, 8) ||
                GetLastError() != ERROR_INVALID_HANDLE) {
                return 2826;
            }
            HANDLE mixed[2] = {events[1], reference};
            if (WaitForMultipleObjectsEx(2, mixed, FALSE, 0, FALSE) != WAIT_OBJECT_0 + 1 ||
                WaitForMultipleObjectsEx(2, mixed, TRUE, 0, FALSE) != WAIT_TIMEOUT ||
                !SetEvent(events[1]) ||
                WaitForMultipleObjectsEx(2, mixed, TRUE, 0, FALSE) != WAIT_OBJECT_0) {
                return 2827;
            }
            if (!CloseHandle(reference) || !CloseHandle(events[0]) || !CloseHandle(events[1])) {
                return 2828;
            }
        }
    }
    if (tls) {
        // Four activations stay pending on a suspended thread, the fifth is refused, and the four are delivered in
        // order when the thread resumes.
        events[1] = event();
        if (!events[1]) {
            return 2851;
        }
        target = nullptr;
        callbacks = sequence = callbackThread = 0;
        WitU64 join, result;
        if (wit_native_thread_create(queued_worker, 0, &join) != WIT_STATUS_OK) {
            return 2852;
        }
        while (!target) {
            Sleep(0);
        }
        HANDLE reference = target;
        if (!parked(reference) || SuspendThread(reference) != 0) {
            return 2853;
        }
        for (ULONG_PTR i = 1; i <= 4; ++i) {
            if (!QueueUserAPC(callback, reference, i)) {
                return 2854;
            }
        }
        WitUserThreadInfo info;
        if (QueueUserAPC(callback, reference, 5) ||
            GetLastError() != ERROR_NOT_ENOUGH_MEMORY ||
            !snapshot(reference, info) ||
            info.State != WIT_THREAD_STATE_SUSPENDED ||
            callbacks) {
            return 2855;
        }
        if (ResumeThread(reference) != 1 ||
            wit_native_thread_join(join, &result) != WIT_STATUS_OK ||
            result != WIT_TEST_EXIT_CODE ||
            callbacks != 4) {
            return 2856;
        }
        if (!CloseHandle(reference) || !CloseHandle(events[1])) {
            return 2857;
        }
    }
    if (tls) {
        for (unsigned close = 0; close < 2; ++close) {
            events[0] = event();
            if (!events[0]) {
                return 2845;
            }
            target = nullptr;
            closeReference = close != 0;
            WitU64 joined, controller, result;
            if (wit_native_thread_create(reference_target, 0, &joined) != WIT_STATUS_OK) {
                return 2846;
            }
            while (!target) {
                Sleep(0);
            }
            HANDLE reference = target;
            if (!parked(reference) ||
                !duplicate(GetCurrentThread(), &mainReference) ||
                wit_native_thread_create(reference_controller, 0, &controller) != WIT_STATUS_OK) {
                return 2847;
            }
            const auto outcome = WaitForMultipleObjectsEx(1, &reference, FALSE, INFINITE, FALSE);
            if (outcome != (close ? WAIT_FAILED : WAIT_OBJECT_0) || (close && GetLastError() != ERROR_INVALID_HANDLE)) {
                return 2848;
            }
            if (wit_native_thread_join(joined, &result) != WIT_STATUS_OK ||
                result != WIT_TEST_EXIT_CODE ||
                wit_native_thread_join(controller, &result) != WIT_STATUS_OK ||
                result != WIT_TEST_EXIT_CODE) {
                return 2849;
            }
            if ((!close && !CloseHandle(reference)) || !CloseHandle(mainReference) || !CloseHandle(events[0])) {
                return 2850;
            }
        }
    }
    if (tls) {
        if (errno != 137) {
            return 2829;
        }
        wit_native_tls_leave();
    }
    return WIT_TEST_EXIT_CODE;
}
