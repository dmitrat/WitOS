#include "pal.witos.h"
#include "native_activation.witos.h"

static WitU64 deadline(DWORD milliseconds)
{
    if (!milliseconds) {
        return 0;
    }
    if (milliseconds == INFINITE) {
        return WIT_WAIT_INFINITE;
    }
    WitU64 now = 0, frequency = 0;
    if (wit_native_call(WIT_CALL_MONOTONIC_READ, 0, 0, 0, &now) != WIT_STATUS_OK ||
        now > WIT_MONOTONIC_MAX ||
        wit_native_call(WIT_CALL_MONOTONIC_FREQUENCY, 0, 0, 0, &frequency) != WIT_STATUS_OK ||
        frequency < 1000 ||
        frequency > 1000000000) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    const auto ticks = ((WitU64)milliseconds * frequency + 999) / 1000;
    return ticks > WIT_MONOTONIC_MAX - now ? WIT_MONOTONIC_MAX : now + ticks;
}

extern "C" HANDLE WINAPI wit_native_create_event_ex(
    LPSECURITY_ATTRIBUTES attributes, LPCWSTR name, DWORD flags, DWORD access)
{
    if (attributes ||
        name ||
        (flags & ~(CREATE_EVENT_MANUAL_RESET | CREATE_EVENT_INITIAL_SET)) ||
        (access & ~(MAXIMUM_ALLOWED | SYNCHRONIZE | EVENT_MODIFY_STATE))) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return nullptr;
    }
    WitU64 rights = 0, handle = 0;
    if (access & (MAXIMUM_ALLOWED | SYNCHRONIZE)) {
        rights |= WIT_EVENT_ACCESS_WAIT;
    }
    if (access & (MAXIMUM_ALLOWED | EVENT_MODIFY_STATE)) {
        rights |= WIT_EVENT_ACCESS_SIGNAL;
    }
    const auto nativeFlags = ((flags & CREATE_EVENT_MANUAL_RESET) ? WIT_EVENT_MANUAL_RESET : 0) |
        ((flags & CREATE_EVENT_INITIAL_SET) ? WIT_EVENT_INITIAL_SIGNALED : 0);
    if (!wit_pal_result(wit_native_call(WIT_CALL_EVENT_CREATE_RIGHTS, nativeFlags, rights, 0, &handle))) {
        return nullptr;
    }
    return (HANDLE)handle;
}

extern "C" BOOL WINAPI wit_native_set_event(HANDLE handle)
{
    return (BOOL)PalSetEvent(handle);
}

// An activation (a queued user APC of this binding) runs its callback through the process's fault callback at the
// target's next return to user mode and ends the wait the target is parked in with INTERRUPTED (RFC 0011 section
// 7.5). An alertable wait reports that as WAIT_IO_COMPLETION, the callback having run; a non-alertable wait restarts
// at its absolute deadline, as a libc restarts a wait a signal interrupted.
extern "C" DWORD WINAPI wit_native_wait_multiple(
    DWORD count, const HANDLE *handles, BOOL all, DWORD timeout, BOOL alertable)
{
    static_assert(sizeof(HANDLE) == sizeof(WitU64));
    if (!count || !handles) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return WAIT_FAILED;
    }
    if (count > WIT_WAIT_ANY_CAPACITY) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return WAIT_FAILED;
    }
    WitUserWaitRequest request = {WIT_WAIT_OBJECTS_VERSION, sizeof(request), (WitU64)handles, count,
        all ? WIT_WAIT_OBJECTS_ALL : 0U, deadline(timeout)};
    for (;;) {
        WitU64 index = 0;
        const auto status = wit_native_call(WIT_CALL_OBJECT_WAIT, (WitU64)&request, sizeof(request), 0, &index);
        if (status == WIT_STATUS_OK) {
            if (index >= count || (all && index)) {
                wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
            }
            return WAIT_OBJECT_0 + (DWORD)index;
        }
        if (status == WIT_STATUS_TIMED_OUT) {
            return WAIT_TIMEOUT;
        }
        if (status == WIT_STATUS_INTERRUPTED) {
            if (alertable) {
                return WAIT_IO_COMPLETION;
            }
            continue;
        }
        wit_pal_set_status(status);
        return WAIT_FAILED;
    }
}

// The kernel delivers an activation only to a process with a registered fault callback, so the dispatcher of this
// module is installed before the first activation; the dispatcher runs the callback and continues the context.
extern "C" DWORD WINAPI wit_native_queue_apc(PAPCFUNC callback, HANDLE thread, ULONG_PTR argument)
{
    const auto installed = wit_native_activation_install();
    if (installed != WIT_STATUS_OK) {
        wit_pal_set_status(installed);
        return 0;
    }
    return wit_pal_result(
               wit_native_call(WIT_CALL_THREAD_ACTIVATE, (WitU64)thread, (WitU64)callback, argument, nullptr))
        ? 1U
        : 0U;
}
