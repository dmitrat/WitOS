#include "pal.witos.h"
#include "protocol.h"

static HANDLE events[3], captured[2];
static volatile WitU64 done[2], outcomes[2], errors[2];
static WitU64 mode, step;

static void check(bool value)
{
    ++step;
    if (!value) {
        ((WitU64 *)WIT_GC_INFO_REPORT)[2] = step;
        wit_native_fail_fast(1750 + step);
    }
}

static void yield()
{
    check(PalSwitchToThread() <= 1);
}

static void let_workers_park()
{
    WitU64 now;
    check(wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &now) == WIT_STATUS_OK);
    check(wit_native_call(WIT_CALL_THREAD_SLEEP, now + 2, 0, 0, nullptr) == WIT_STATUS_OK);
}

static void worker(WitU64 index)
{
    HANDLE local[] = {events[0], events[1]};
    SetLastError((DWORD)(0x9000 + index));
    outcomes[index] = mode == 56 && index == 0
        ? PalWaitForSingleObjectEx(events[1], INFINITE, FALSE)
        : PalCompatibleWaitAny(FALSE, mode == 54 ? 1 : INFINITE, mode == 57 ? 1 : 2,
              mode == 55       ? captured
                  : mode == 57 ? &local[1]
                               : local,
              FALSE);
    errors[index] = GetLastError();
    done[index] = 1;
    (void)wit_native_call(WIT_CALL_THREAD_EXIT, WIT_TEST_EXIT_CODE, 0, 0, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static void basic()
{
    HANDLE list[] = {events[0], events[1], events[2], nullptr};
    SetLastError(0xABCDEF12);
    check(PalSetEvent(events[1]) && PalSetEvent(events[2]));
    check(PalCompatibleWaitAny(FALSE, 0, 3, list, FALSE) == WAIT_OBJECT_0 + 1);
    check(PalCompatibleWaitAny(FALSE, 0, 3, list, FALSE) == WAIT_OBJECT_0 + 2);
    check(PalCompatibleWaitAny(FALSE, 0, 3, list, FALSE) == WAIT_OBJECT_0 + 2); // Manual remains set.
    check(PalResetEvent(events[2]));
    check(PalCompatibleWaitAny(FALSE, 0, 3, list, FALSE) == WAIT_TIMEOUT && GetLastError() == 0xABCDEF12);
    check(PalSetEvent(events[0]) && PalSetEvent(events[1]));
    check(PalCompatibleWaitAny(FALSE, 0, 3, list, FALSE) == WAIT_OBJECT_0);
    check(PalCompatibleWaitAny(FALSE, 0, 3, list, FALSE) == WAIT_OBJECT_0 + 1);
    check(PalCompatibleWaitAny(FALSE, 0, 1, list, FALSE) == WAIT_TIMEOUT);
    check(PalSetEvent(events[0]));
    HANDLE duplicate[] = {events[0], events[0]};
    HANDLE invalid[] = {events[0], (HANDLE)(uintptr_t)PalGetCurrentOSThreadId()};
    check(PalCompatibleWaitAny(FALSE, 0, 2, duplicate, FALSE) == WAIT_FAILED &&
        GetLastError() == ERROR_INVALID_PARAMETER);
    check(PalCompatibleWaitAny(FALSE, 0, 2, invalid, FALSE) == WAIT_FAILED && GetLastError() == ERROR_INVALID_HANDLE);
    check(PalCompatibleWaitAny(FALSE, 0, 0, list, FALSE) == WAIT_FAILED && GetLastError() == ERROR_INVALID_PARAMETER);
    check(
        PalCompatibleWaitAny(FALSE, 0, 1, nullptr, FALSE) == WAIT_FAILED && GetLastError() == ERROR_INVALID_PARAMETER);
    check(PalCompatibleWaitAny(FALSE, 0, 5, list, FALSE) == WAIT_FAILED && GetLastError() == ERROR_NOT_SUPPORTED);
    check(PalCompatibleWaitAny(TRUE, 0, 1, list, FALSE) == WAIT_FAILED && GetLastError() == ERROR_NOT_SUPPORTED);
    check(PalCompatibleWaitAny(FALSE, 0, 1, list, TRUE) == WAIT_FAILED && GetLastError() == ERROR_NOT_SUPPORTED);
    WitU64 index = 99;
    check(wit_native_call(WIT_CALL_EVENT_WAIT_ANY_UNTIL, (uintptr_t)list, 1, WIT_MONOTONIC_MAX + 1, &index) ==
            WIT_STATUS_INVALID_ARGUMENT &&
        !index);
    auto buffer = (uint8_t *)PalVirtualAlloc(8192, PAGE_READWRITE);
    check(buffer != nullptr);
    auto tail = (HANDLE *)(buffer + 4096 - sizeof(HANDLE));
    *tail = events[0];
    check(wit_native_call(WIT_CALL_MEMORY_DECOMMIT, (uintptr_t)(buffer + 4096), 4096, 0, nullptr) == WIT_STATUS_OK);
    check(PalVirtualProtect(buffer, 4096, PAGE_READONLY));
    check(PalCompatibleWaitAny(FALSE, 0, 2, tail, FALSE) == WAIT_FAILED && GetLastError() == ERROR_INVALID_ADDRESS);
    check(PalCompatibleWaitAny(FALSE, 0, 1, tail, FALSE) == WAIT_OBJECT_0); // No rejected call consumed A.
    PalVirtualFree(buffer, 8192);
    list[3] = PalCreateEventW(nullptr, FALSE, TRUE, nullptr);
    check(list[3] && PalCompatibleWaitAny(FALSE, 0, 4, list, FALSE) == WAIT_OBJECT_0 + 3);
    check(PalCloseHandle(list[3]));
}

WitU64 wit_pal_wait_any(const WitUserStartup *startup)
{
    mode = ((const WitUserTestConfig *)startup)->Mode;
    ((WitU64 *)WIT_GC_INFO_REPORT)[0] = mode;
    events[0] = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
    events[1] = PalCreateEventW(nullptr, mode == 52, FALSE, nullptr);
    events[2] = PalCreateEventW(nullptr, mode == 50, FALSE, nullptr);
    check(events[0] && events[1] && events[2]);
    if (mode == 50) {
        basic();
    } else {
        const WitU32 count = mode >= 54 && mode != 56 ? 1 : 2;
        WitU64 handles[2], code;
        captured[0] = events[0];
        captured[1] = events[1];
        for (WitU32 i = 0; i < count; ++i) {
            check(wit_native_call(WIT_CALL_THREAD_CREATE, (uintptr_t)worker, i, 0, &handles[i]) == WIT_STATUS_OK);
            if (mode == 56) {
                let_workers_park();
            }
        }
        if (mode != 56) {
            let_workers_park();
        }
        if (mode == 51 || mode == 56) {
            check(PalSetEvent(events[1]));
            while (!done[0] && !done[1]) {
                yield();
            }
            for (WitU32 i = 0; i < 3; ++i) {
                yield();
            }
            check(done[0] + done[1] == 1);
            if (mode == 56) {
                check(done[0] && !done[1]);
            }
            check(PalSetEvent(events[1]));
        } else if (mode == 52 || mode == 53) {
            if (mode == 52) {
                check(PalSetEvent(events[1]));
            }
            const HANDLE old = events[1];
            check(PalCloseHandle(old));
            events[1] = PalCreateEventW(nullptr, FALSE, TRUE, nullptr);
            check(events[1] && events[1] != old && PalSetEvent(events[0]));
        } else if (mode == 54 || mode == 57) {
            if (mode == 54) {
                while (!done[0]) {
                    yield();
                }
            }
            check(PalSetEvent(events[1]));
        } else {
            captured[1] = events[2]; // Kernel must retain the original B handle.
            check(PalSetEvent(events[2]));
            for (WitU32 i = 0; i < 3; ++i) {
                yield();
            }
            check(!done[0]);
            check(PalSetEvent(events[1]));
        }
        for (WitU32 i = 0; i < count; ++i) {
            check(wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &code) == WIT_STATUS_OK &&
                code == WIT_TEST_EXIT_CODE);
            check(done[i] &&
                outcomes[i] ==
                    (mode == 53                                      ? WAIT_FAILED
                            : mode == 54                             ? WAIT_TIMEOUT
                            : (mode == 57 || (mode == 56 && i == 0)) ? WAIT_OBJECT_0
                                                                     : WAIT_OBJECT_0 + 1));
            check(errors[i] == (mode == 53 ? ERROR_INVALID_HANDLE : 0x9000 + i));
        }
        if (mode == 52 || mode == 53) {
            check(PalWaitForSingleObjectEx(events[0], 0, FALSE) == WAIT_OBJECT_0);
        }
        if (mode == 52 || mode == 53 || mode == 54) {
            check(PalWaitForSingleObjectEx(events[1], 0, FALSE) == WAIT_OBJECT_0);
        }
        if (mode == 55) {
            check(PalWaitForSingleObjectEx(events[2], 0, FALSE) == WAIT_OBJECT_0);
        }
    }
    for (WitU32 i = 0; i < 3; ++i) {
        check(PalCloseHandle(events[i]));
    }
    ((WitU64 *)WIT_GC_INFO_REPORT)[1] = 1;
    return WIT_TEST_EXIT_CODE;
}
