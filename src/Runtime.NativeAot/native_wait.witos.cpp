#include "pal.witos.h"

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
        (all ? WIT_WAIT_OBJECTS_ALL : 0U) | (alertable ? WIT_WAIT_OBJECTS_ALERTABLE : 0U), deadline(timeout)};
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
    if (status == WIT_STATUS_APC_PENDING) {
        if (!alertable) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        bool delivered = false;
        for (;;) {
            WitUserApc apc;
            WitU64 copied = 0;
            const auto next = wit_native_call(WIT_CALL_APC_DEQUEUE, (WitU64)&apc, sizeof(apc), 0, &copied);
            if (next == WIT_STATUS_TIMED_OUT && delivered && !copied) {
                break;
            }
            if (next != WIT_STATUS_OK || copied != sizeof(apc) || !apc.Callback) {
                wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
            }
            // FIFO callbacks execute with no kernel gate held. They may queue
            // more work or enter another alertable wait; normal user preemption
            // and the component CPU budget still apply during this dispatch.
            delivered = true;
            ((PAPCFUNC)(uintptr_t)apc.Callback)((ULONG_PTR)apc.Argument);
        }
        return WAIT_IO_COMPLETION;
    }
    wit_pal_set_status(status);
    return WAIT_FAILED;
}

extern "C" DWORD WINAPI wit_native_queue_apc(PAPCFUNC callback, HANDLE thread, ULONG_PTR argument)
{
    return wit_pal_result(wit_native_call(WIT_CALL_APC_QUEUE, (WitU64)thread, (WitU64)callback, argument, nullptr))
        ? 1U
        : 0U;
}
