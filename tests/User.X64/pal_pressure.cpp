#include "pal.witos.h"
#include "protocol.h"

static HANDLE pressure[2], ordinary;
static WitU64 arena, committed, mode, step;
static volatile WitU64 outcome, error;

static void check(bool value)
{
    ++step;
    if (!value) {
        ((WitU64 *)WIT_GC_INFO_REPORT)[2] = step;
        wit_native_fail_fast(1800 + step);
    }
}

static WitU64 available()
{
    WitUserMemoryInfo info;
    check(wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&info, sizeof(info), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK);
    const WitU64 physical = info.PhysicalAvailableBytes / 4096;
    const WitU64 quota = (info.OwnedLimitBytes - info.OwnedBytes) / 4096;
    if (mode == 63) {
        check(physical < quota); // This run is limited by real RAM, not quota.
    }
    return physical < quota ? physical : quota;
}

static void commit_to(WitU64 remaining)
{
    while (available() > remaining) {
        check(committed < 128);
        check(wit_native_call(WIT_CALL_MEMORY_COMMIT, arena + committed * 4096, 4096,
                  WIT_MEMORY_READ | WIT_MEMORY_WRITE, nullptr) == WIT_STATUS_OK);
        ++committed;
    }
    check(available() == remaining);
}

static void recover_to(WitU64 remaining)
{
    while (available() < remaining) {
        check(committed != 0);
        --committed;
        check(wit_native_call(WIT_CALL_MEMORY_DECOMMIT, arena + committed * 4096, 4096, 0, nullptr) == WIT_STATUS_OK);
    }
}

static void reserve()
{
    check(wit_native_call(WIT_CALL_MEMORY_RESERVE, 128 * 4096, 4096, 0, &arena) == WIT_STATUS_OK);
}

static void release()
{
    check(wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) == WIT_STATUS_OK);
    committed = arena = 0;
}

static void worker(WitU64)
{
    HANDLE list[] = {ordinary, pressure[0]};
    SetLastError(0x12344321);
    outcome = PalCompatibleWaitAny(FALSE, INFINITE, 2, list, FALSE);
    error = GetLastError();
    (void)wit_native_call(WIT_CALL_THREAD_EXIT, WIT_TEST_EXIT_CODE, 0, 0, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static WitU64 start_worker()
{
    WitU64 handle, ticks;
    check(wit_native_call(WIT_CALL_THREAD_CREATE, (uintptr_t)worker, 0, 0, &handle) == WIT_STATUS_OK);
    check(wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &ticks) == WIT_STATUS_OK);
    check(wit_native_call(WIT_CALL_THREAD_SLEEP, ticks + 2, 0, 0, nullptr) == WIT_STATUS_OK);
    return handle;
}

static void join(WitU64 handle, uint32_t expected, uint32_t expected_error)
{
    WitU64 code;
    check(wit_native_call(WIT_CALL_THREAD_JOIN, handle, 0, 0, &code) == WIT_STATUS_OK && code == WIT_TEST_EXIT_CODE);
    check(outcome == expected && error == expected_error);
}

WitU64 wit_pal_pressure(const WitUserStartup *startup)
{
    mode = ((const WitUserTestConfig *)startup)->Mode;
    ((WitU64 *)WIT_GC_INFO_REPORT)[0] = mode;
    for (WitU32 i = 0; i < 3; ++i) {
        WitU64 result = 99;
        check(wit_native_call(WIT_CALL_MEMORY_PRESSURE_EVENT, i == 0, i == 1, i == 2, &result) ==
                WIT_STATUS_INVALID_ARGUMENT &&
            !result);
    }
    check(available() >= 32);
    if (mode == 62) {
        HANDLE handles[4];
        for (auto &handle : handles) {
            handle = PalCreateLowMemoryResourceNotification();
            check(handle != nullptr);
        }
        check(!PalCreateLowMemoryResourceNotification() && GetLastError() == ERROR_NOT_ENOUGH_MEMORY);
        const HANDLE old = handles[1];
        check(PalCloseHandle(old));
        handles[1] = PalCreateLowMemoryResourceNotification();
        check(handles[1] && handles[1] != old);
        check(PalWaitForSingleObjectEx(old, 0, FALSE) == WAIT_FAILED && GetLastError() == ERROR_INVALID_HANDLE);
        for (const auto handle : handles) {
            check(PalCloseHandle(handle));
        }
    } else {
        pressure[0] = PalCreateLowMemoryResourceNotification();
        check(pressure[0] != nullptr);
        check(!PalSetEvent(pressure[0]) && GetLastError() == ERROR_ACCESS_DENIED);
        check(!PalResetEvent(pressure[0]) && GetLastError() == ERROR_ACCESS_DENIED);
        SetLastError(0xABCDE123);
        check(PalWaitForSingleObjectEx(pressure[0], 0, FALSE) == WAIT_TIMEOUT && GetLastError() == 0xABCDE123);
        const WitU64 before = available();
        reserve();
        check(available() == before);
        if (mode == 61) {
            ordinary = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
            check(ordinary != nullptr);
            const WitU64 worker_handle = start_worker();
            commit_to(16);
            join(worker_handle, WAIT_OBJECT_0 + 1, 0x12344321);
            commit_to(16);
        } else {
            commit_to(17);
            check(PalWaitForSingleObjectEx(pressure[0], 0, FALSE) == WAIT_TIMEOUT);
            check(wit_native_call(WIT_CALL_MEMORY_COMMIT, arena + committed * 4096, 33 * 4096,
                      WIT_MEMORY_READ | WIT_MEMORY_WRITE, nullptr) == WIT_STATUS_NO_MEMORY);
            check(available() == 17 && PalWaitForSingleObjectEx(pressure[0], 0, FALSE) == WAIT_TIMEOUT);
            commit_to(16);
        }
        check(PalWaitForSingleObjectEx(pressure[0], 0, FALSE) == WAIT_OBJECT_0);
        check(!PalResetEvent(pressure[0]) && GetLastError() == ERROR_ACCESS_DENIED);
        check(PalWaitForSingleObjectEx(pressure[0], 0, FALSE) == WAIT_OBJECT_0);
        pressure[1] = PalCreateLowMemoryResourceNotification();
        check(pressure[1] && PalWaitForSingleObjectEx(pressure[1], 0, FALSE) == WAIT_OBJECT_0);
        check(PalVirtualProtect((void *)(uintptr_t)arena, committed * 4096, PAGE_NOACCESS));
        check(wit_native_call(WIT_CALL_MEMORY_RESET, arena, committed * 4096, 0, nullptr) == WIT_STATUS_OK);
        check(available() == 16 && PalWaitForSingleObjectEx(pressure[0], 0, FALSE) == WAIT_OBJECT_0);
        if (mode == 61) {
            const HANDLE old = pressure[1];
            check(PalCloseHandle(old));
            pressure[1] = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
            check(pressure[1] && pressure[1] != old && PalWaitForSingleObjectEx(pressure[1], 0, FALSE) == WAIT_TIMEOUT);
            check(PalSetEvent(pressure[1]) && PalWaitForSingleObjectEx(pressure[1], 0, FALSE) == WAIT_OBJECT_0);
        }
        recover_to(24);
        check(PalWaitForSingleObjectEx(pressure[0], 0, FALSE) == WAIT_OBJECT_0);
        recover_to(32);
        check(PalWaitForSingleObjectEx(pressure[0], 0, FALSE) == WAIT_TIMEOUT);
        release();
        if (mode == 61) {
            const WitU64 worker_handle = start_worker();
            const HANDLE old = pressure[0];
            check(PalCloseHandle(old));
            pressure[0] = PalCreateLowMemoryResourceNotification();
            check(pressure[0] && pressure[0] != old);
            join(worker_handle, WAIT_FAILED, ERROR_INVALID_HANDLE);
            check(PalWaitForSingleObjectEx(pressure[0], 0, FALSE) == WAIT_TIMEOUT && PalCloseHandle(ordinary));
        }
        check(PalCloseHandle(pressure[0]) && PalCloseHandle(pressure[1]));
    }
    ((WitU64 *)WIT_GC_INFO_REPORT)[1] = 1;
    return WIT_TEST_EXIT_CODE;
}
