#include "gcenv.witos.h"
#include "../User/protocol.h"

using OS = GCToOSInterface;
static GCEvent *signal;

static WitU64 raw(WitU64 op, WitU64 a = 0, WitU64 b = 0, WitU64 c = 0, WitU64 *result = nullptr)
{
    return wit_native_call(op, a, b, c, result);
}

static WitU64 wait(WitU64 handle, WitU64 deadline, WitU64 reserved = 0)
{
    WitUserWaitRequest request = {WIT_WAIT_OBJECTS_VERSION, sizeof(request), (WitU64)&handle, 1, 0, deadline};
    return raw(WIT_CALL_OBJECT_WAIT, (WitU64)&request, sizeof(request), reserved);
}

static WitU64 now()
{
    return (WitU64)OS::QueryPerformanceCounter();
}

static WitU64 milliseconds(WitU64 counter, WitU64 frequency)
{
    return counter / frequency * 1000 + counter % frequency * 1000 / frequency;
}

static void signal_later(WitU64)
{
    OS::Sleep(2);
    signal->Set();
    (void)raw(WIT_CALL_THREAD_EXIT, WIT_TEST_EXIT_CODE);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

WitU64 wit_gc_time(const WitUserStartup *startup, WitU64 mode)
{
    const auto frequency = (WitU64)OS::QueryPerformanceFrequency();
    if (mode == WIT_GC_TEST_HPET_READ) {
        return *(volatile WitU64 *)(uintptr_t)((const WitUserTestConfig *)startup)->KernelProbe;
    }
    if (!frequency || frequency > 1000000000) {
        return 200;
    }
    if (mode == WIT_GC_TEST_CLOCK) {
        WitU64 direct = 0, previous = now();
        if (raw(WIT_CALL_MONOTONIC_FREQUENCY, 0, 0, 0, &direct) != WIT_STATUS_OK || direct != frequency) {
            return 201;
        }
        for (size_t i = 0; i < 128; ++i) {
            const auto value = now();
            if (value < previous) {
                return 202;
            }
            previous = value;
        }
        const auto before = now();
        const auto stamp = OS::GetLowPrecisionTimeStamp();
        const auto after = now();
        if (stamp < milliseconds(before, frequency) || stamp > milliseconds(after, frequency)) {
            return 203;
        }
        OS::Sleep(0);
        const auto begin = now();
        OS::Sleep(2);
        if (now() - begin < (frequency * 2 + 999) / 1000) {
            return 204;
        }
        if (raw(WIT_CALL_SLEEP_UNTIL, 0) != WIT_STATUS_OK ||
            raw(WIT_CALL_SLEEP_UNTIL, WIT_MONOTONIC_MAX + 1) != WIT_STATUS_INVALID_ARGUMENT ||
            raw(WIT_CALL_SLEEP_UNTIL, 0, 1) != WIT_STATUS_INVALID_ARGUMENT) {
            return 205;
        }
        return WIT_TEST_EXIT_CODE;
    }
    if (mode == WIT_GC_TEST_TIMED_WAIT) {
        GCEvent event;
        if (!event.CreateManualEventNoThrow(false)) {
            return 210;
        }
        auto begin = now();
        if (event.Wait(2, false) != WAIT_TIMEOUT || now() - begin < (frequency * 2 + 999) / 1000) {
            return 211;
        }
        event.CloseEvent();
        if (!event.CreateAutoEventNoThrow(false)) {
            return 212;
        }
        begin = now();
        if (event.Wait(1, true) != WAIT_TIMEOUT || now() - begin < (frequency + 999) / 1000) {
            return 213;
        }
        event.Set();
        if (event.Wait(INFINITE - 1, false) != WAIT_OBJECT_0 || event.Wait(0, false) != WAIT_TIMEOUT) {
            return 214;
        }
        event.CloseEvent();
        if (event.Wait(1, false) != WAIT_FAILED) {
            return 215;
        }
        return WIT_TEST_EXIT_CODE;
    }
    if (mode == WIT_GC_TEST_TIMED_SIGNAL) {
        GCEvent event;
        WitU64 thread = 0, code = 0;
        signal = &event;
        if (!event.CreateAutoEventNoThrow(false) ||
            wit_native_thread_start((uintptr_t)signal_later, 0, 0, &thread) != WIT_STATUS_OK) {
            return 220;
        }
        if (event.Wait(INFINITE - 1, false) != WAIT_OBJECT_0 ||
            wit_native_thread_join(thread, &code) != WIT_STATUS_OK ||
            code != WIT_TEST_EXIT_CODE ||
            event.Wait(0, false) != WAIT_TIMEOUT) {
            return 221;
        }
        event.CloseEvent();
        return WIT_TEST_EXIT_CODE;
    }
    if (mode == WIT_GC_TEST_TIME_ARITHMETIC) {
        if (wit_gc_deadline_at(0, 123, 10000000) != 0 ||
            wit_gc_deadline_at(INFINITE, 123, 10000000) != WIT_WAIT_INFINITE ||
            wit_gc_deadline_at(1, 123, 10000001) != 10124 ||
            wit_gc_deadline_at(1, WIT_MONOTONIC_MAX - 5, 10000000) != WIT_MONOTONIC_MAX ||
            wit_gc_deadline_at(INFINITE - 1, 0, 1000000000) != 4294967294000000ULL) {
            return 230;
        }
        WitU64 handle = 0;
        if (raw(WIT_CALL_EVENT_CREATE, WIT_EVENT_INITIAL_SIGNALED, 0, 0, &handle) != WIT_STATUS_OK ||
            wait(handle, WIT_MONOTONIC_MAX + 1) != WIT_STATUS_INVALID_ARGUMENT ||
            wait(handle, 0, 1) != WIT_STATUS_INVALID_ARGUMENT ||
            wait(handle, 0) != WIT_STATUS_OK ||
            wait(handle, 0) != WIT_STATUS_TIMED_OUT ||
            raw(WIT_CALL_CLOSE, handle) != WIT_STATUS_OK) {
            return 231;
        }
        return WIT_TEST_EXIT_CODE;
    }
    return 240;
}
