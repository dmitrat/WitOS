#include "pal.witos.h"

static WitU64 deadline(uint32_t milliseconds)
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
    const WitU64 ticks = ((WitU64)milliseconds * frequency + 999) / 1000;
    return ticks > WIT_MONOTONIC_MAX - now ? WIT_MONOTONIC_MAX : now + ticks;
}

HANDLE PalCreateLowMemoryResourceNotification()
{
    WitU64 handle = 0;
    if (!wit_pal_result(wit_native_call(WIT_CALL_MEMORY_PRESSURE_EVENT, 0, 0, 0, &handle))) {
        return nullptr;
    }
    return (HANDLE)(uintptr_t)handle;
}

HANDLE PalCreateEventW(LPSECURITY_ATTRIBUTES attributes, UInt32_BOOL manual, UInt32_BOOL signaled, LPCWSTR name)
{
    if (attributes || name) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return nullptr;
    } // No named objects or Win32 inheritance/security descriptors.
    WitU64 handle = 0;
    const WitU64 flags = (manual ? WIT_EVENT_MANUAL_RESET : 0) | (signaled ? WIT_EVENT_INITIAL_SIGNALED : 0);
    if (!wit_pal_result(wit_native_call(WIT_CALL_EVENT_CREATE, flags, 0, 0, &handle))) {
        return nullptr;
    }
    return (HANDLE)(uintptr_t)handle;
}

UInt32_BOOL PalSetEvent(HANDLE handle)
{
    return wit_pal_result(wit_native_call(WIT_CALL_EVENT_SET, (uintptr_t)handle, 0, 0, nullptr));
}

UInt32_BOOL PalResetEvent(HANDLE handle)
{
    return wit_pal_result(wit_native_call(WIT_CALL_EVENT_RESET, (uintptr_t)handle, 0, 0, nullptr));
}

uint32_t PalWaitForSingleObjectEx(HANDLE handle, uint32_t milliseconds, UInt32_BOOL alertable)
{
    if (alertable) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return WAIT_FAILED;
    } // No APC/reentrant wait machinery exists yet.
    const WitU64 status =
        wit_native_call(WIT_CALL_EVENT_WAIT_UNTIL, (uintptr_t)handle, deadline(milliseconds), 0, nullptr);
    if (status == WIT_STATUS_OK) {
        return WAIT_OBJECT_0;
    }
    if (status == WIT_STATUS_TIMED_OUT) {
        return WAIT_TIMEOUT;
    }
    wit_pal_set_status(status);
    return WAIT_FAILED;
}

uint32_t PalCompatibleWaitAny(
    UInt32_BOOL alertable, uint32_t timeout, uint32_t count, HANDLE *handles, UInt32_BOOL reentrant)
{
    static_assert(sizeof(HANDLE) == sizeof(WitU64));
    if (alertable || reentrant || count > WIT_WAIT_ANY_CAPACITY) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return WAIT_FAILED;
    }
    if (!count || !handles) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return WAIT_FAILED;
    }
    WitU64 index = 0;
    const WitU64 status =
        wit_native_call(WIT_CALL_EVENT_WAIT_ANY_UNTIL, (uintptr_t)handles, count, deadline(timeout), &index);
    if (status == WIT_STATUS_OK) {
        if (index >= count) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        return WAIT_OBJECT_0 + (uint32_t)index;
    }
    if (status == WIT_STATUS_TIMED_OUT) {
        return WAIT_TIMEOUT;
    }
    wit_pal_set_status(status);
    return WAIT_FAILED;
}

UInt32_BOOL PalCloseHandle(HANDLE handle)
{
    return wit_pal_result(wit_native_call(WIT_CALL_CLOSE, (uintptr_t)handle, 0, 0, nullptr));
}

UInt32_BOOL PalSwitchToThread()
{
    WitU64 switched = 0;
    if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, &switched) != WIT_STATUS_OK || switched > 1) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return (UInt32_BOOL)switched;
}

void PalSleep(uint32_t milliseconds)
{
    if (!milliseconds) {
        (void)PalSwitchToThread();
        return;
    }
    if (wit_native_call(WIT_CALL_SLEEP_UNTIL, deadline(milliseconds), 0, 0, nullptr) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
}
