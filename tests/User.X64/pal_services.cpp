#include "pal.witos.h"
#include "../User/protocol.h"

static HANDLE shared;
static uint32_t expected_wait;
static volatile WitU64 arrived[2], completed[2], hold_workers;

static bool snapshot(WitUserMemoryInfo *info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)info, sizeof(*info), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK;
}

static bool same(const WitUserMemoryInfo &a, const WitUserMemoryInfo &b)
{
    return a.OwnedBytes == b.OwnedBytes &&
        a.PrivatePageTableBytes == b.PrivatePageTableBytes &&
        a.DynamicCommittedBytes == b.DynamicCommittedBytes &&
        a.ReservedBytes == b.ReservedBytes &&
        a.ReservationCount == b.ReservationCount &&
        a.PhysicalAvailableBytes == b.PhysicalAvailableBytes;
}

static bool zero(const void *p, size_t size)
{
    for (size_t i = 0; i < size; ++i) {
        if (((const volatile unsigned char *)p)[i]) {
            return false;
        }
    }
    return true;
}

static WitU64 now()
{
    WitU64 value = 0;
    if (wit_native_call(WIT_CALL_MONOTONIC_READ, 0, 0, 0, &value) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return value;
}

static void worker(WitU64 index)
{
    arrived[index] = 1;
    while (hold_workers) {
        (void)PalSwitchToThread();
    }
    arrived[index] = 2;
    const auto result = PalWaitForSingleObjectEx(shared, INFINITE, FALSE);
    completed[index] = 1;
    (void)wit_native_call(WIT_CALL_THREAD_EXIT, result == expected_wait ? WIT_TEST_EXIT_CODE : 1201, 0, 0, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static bool spawn(WitU64 index, WitU64 *handle)
{
    return wit_native_call(WIT_CALL_THREAD_CREATE, (uintptr_t)worker, index, 0, handle) == WIT_STATUS_OK;
}

static bool join(WitU64 handle)
{
    WitU64 code = 0;
    return wit_native_call(WIT_CALL_THREAD_JOIN, handle, 0, 0, &code) == WIT_STATUS_OK && code == WIT_TEST_EXIT_CODE;
}

static WitU64 memory()
{
    const uint32_t invalid[] = {0, PAGE_EXECUTE, PAGE_EXECUTE_READWRITE, PAGE_WRITECOPY, PAGE_READWRITE | PAGE_GUARD};
    for (auto flags : invalid) {
        if (PalVirtualAlloc(4096, flags)) {
            return 1210;
        }
    }
    if (PalVirtualAlloc(0, PAGE_READWRITE) || PalVirtualAlloc(UINTPTR_MAX, PAGE_READWRITE)) {
        return 1211;
    }
    auto p = (unsigned char *)PalVirtualAlloc(4097, PAGE_READWRITE);
    if (!p || ((uintptr_t)p & 65535) || !zero(p, 8192)) {
        return 1212;
    }
    p[0] = 0xA1;
    p[8191] = 0xB2;
    WitUserMemoryInfo before, after;
    if (!snapshot(&before) ||
        !PalVirtualProtect(p + 1, 4096, PAGE_READONLY) ||
        !PalVirtualProtect(p, 8192, PAGE_NOACCESS) ||
        !PalVirtualProtect(p, 8192, PAGE_READWRITE) ||
        p[0] != 0xA1 ||
        p[8191] != 0xB2 ||
        !snapshot(&after) ||
        !same(before, after)) {
        return 1213;
    }
    if (PalVirtualProtect(p, 0, PAGE_READONLY) ||
        PalVirtualProtect(nullptr, 1, PAGE_READONLY) ||
        PalVirtualProtect((void *)(UINTPTR_MAX - 7), 16, PAGE_READONLY) ||
        PalVirtualProtect((void *)(UINTPTR_MAX - 4095), 1, PAGE_READONLY) ||
        PalVirtualProtect(&before, sizeof(before), PAGE_READWRITE) ||
        PalVirtualProtect(p, 1, PAGE_EXECUTE_READ) ||
        PalVirtualProtect(p + 4096, 8192, PAGE_READONLY)) {
        return 1214;
    }
    p[4096] = 0xC3;
    if (p[0] != 0xA1 || p[8191] != 0xB2 || !snapshot(&after) || !same(before, after)) {
        return 1215;
    }
    PalVirtualFree(p, 1); // Windows PAL ignores size when releasing an allocation.
    p = (unsigned char *)PalVirtualAlloc(4096, PAGE_READONLY);
    if (!p || !zero(p, 4096)) {
        return 1216;
    }
    PalVirtualFree(p, 0);
    p = (unsigned char *)PalVirtualAlloc(4096, PAGE_NOACCESS);
    if (!p || !PalVirtualProtect(p, 4096, PAGE_READWRITE) || !zero(p, 4096)) {
        return 1217;
    }
    PalVirtualFree(p, 4096);
    return WIT_TEST_EXIT_CODE;
}

static WitU64 rollback()
{
    WitUserMemoryInfo before, after;
    if (!snapshot(&before) ||
        PalVirtualAlloc(128 * 4096, PAGE_READWRITE) ||
        !snapshot(&after) ||
        !same(before, after)) {
        return 1220;
    }
    void *values[8];
    for (size_t i = 0; i < 8; ++i) {
        values[i] = PalVirtualAlloc(4096, PAGE_READWRITE);
        if (!values[i]) {
            return 1221;
        }
        *(volatile unsigned char *)values[i] = (unsigned char)(i + 1);
    }
    if (!snapshot(&before) || PalVirtualAlloc(1, PAGE_READWRITE) || !snapshot(&after) || !same(before, after)) {
        return 1222;
    }
    for (size_t i = 0; i < 8; ++i) {
        if (*(volatile unsigned char *)values[i] != i + 1) {
            return 1223;
        }
        PalVirtualFree(values[i], 0);
    }
    auto p = PalVirtualAlloc(1, PAGE_READWRITE);
    if (!p || !zero(p, 4096)) {
        return 1224;
    }
    PalVirtualFree(p, 0);
    return WIT_TEST_EXIT_CODE;
}

static WitU64 events(const WitUserStartup *startup)
{
    if (PalCreateEventW((LPSECURITY_ATTRIBUTES)1, FALSE, FALSE, nullptr) ||
        PalCreateEventW(nullptr, FALSE, FALSE, L"named")) {
        return 1230;
    }
    auto automatic = PalCreateEventW(nullptr, FALSE, TRUE, nullptr);
    if (!automatic ||
        PalWaitForSingleObjectEx(automatic, 0, TRUE) != WAIT_FAILED ||
        PalWaitForSingleObjectEx(automatic, 0, FALSE) != WAIT_OBJECT_0 ||
        PalWaitForSingleObjectEx(automatic, 0, FALSE) != WAIT_TIMEOUT ||
        !PalSetEvent(automatic) ||
        !PalResetEvent(automatic) ||
        PalWaitForSingleObjectEx(automatic, 0, FALSE) != WAIT_TIMEOUT ||
        !PalCloseHandle(automatic)) {
        return 1231;
    }
    auto manual = PalCreateEventW(nullptr, 2, 7, nullptr); // BOOL uses nonzero truth.
    if (!manual ||
        PalWaitForSingleObjectEx(manual, 0, FALSE) != WAIT_OBJECT_0 ||
        PalWaitForSingleObjectEx(manual, 0, FALSE) != WAIT_OBJECT_0 ||
        !PalResetEvent(manual) ||
        PalWaitForSingleObjectEx(manual, 0, FALSE) != WAIT_TIMEOUT ||
        !PalCloseHandle(manual)) {
        return 1232;
    }
    const HANDLE invalid[] = {nullptr, INVALID_HANDLE_VALUE, automatic, (HANDLE)(uintptr_t)startup->ConsoleHandle};
    for (auto h : invalid) {
        if (PalSetEvent(h) || PalResetEvent(h) || PalWaitForSingleObjectEx(h, 0, FALSE) != WAIT_FAILED) {
            return 1233;
        }
    }
    if (PalCloseHandle(automatic) || PalCloseHandle(INVALID_HANDLE_VALUE)) {
        return 1234;
    }
    HANDLE handles[4];
    for (size_t round = 0; round < 4; ++round) {
        for (size_t i = 0; i < 4; ++i) {
            if (!(handles[i] = PalCreateEventW(nullptr, FALSE, FALSE, nullptr))) {
                return 1235;
            }
        }
        if (PalCreateEventW(nullptr, FALSE, FALSE, nullptr)) {
            return 1236;
        }
        for (auto h : handles) {
            if (!PalCloseHandle(h)) {
                return 1237;
            }
        }
    }
    return WIT_TEST_EXIT_CODE;
}

static WitU64 handoff()
{
    expected_wait = WAIT_OBJECT_0;
    for (size_t manual = 0; manual < 2; ++manual) {
        arrived[0] = arrived[1] = completed[0] = completed[1] = 0;
        hold_workers = 1;
        WitU64 handles[2];
        shared = PalCreateEventW(nullptr, (UInt32_BOOL)manual, FALSE, nullptr);
        if (!shared || !spawn(0, &handles[0]) || !spawn(1, &handles[1])) {
            return 1240;
        }
        while (!arrived[0] || !arrived[1]) {
            (void)PalSwitchToThread();
        }
        // Both children remain runnable until released, so this yield must switch.
        if (!PalSwitchToThread()) {
            return 1241;
        }
        hold_workers = 0;
        while (arrived[0] != 2 || arrived[1] != 2) {
            (void)PalSwitchToThread();
        }
        if (!PalSetEvent(shared)) {
            return 1241;
        }
        if (!manual) {
            for (size_t i = 0; i < 4; ++i) {
                (void)PalSwitchToThread();
            }
            if (completed[0] + completed[1] != 1 || !PalSetEvent(shared)) {
                return 1242;
            }
        }
        if (!join(handles[0]) || !join(handles[1]) || !completed[0] || !completed[1] || !PalCloseHandle(shared)) {
            return 1243;
        }
    }
    return PalSwitchToThread() ? 1244 : WIT_TEST_EXIT_CODE;
}

static WitU64 timed()
{
    WitU64 frequency = 0;
    if (wit_native_call(WIT_CALL_MONOTONIC_FREQUENCY, 0, 0, 0, &frequency) != WIT_STATUS_OK ||
        !frequency ||
        PalSwitchToThread()) {
        return 1250;
    }
    PalSleep(0);
    WitU64 start = now();
    PalSleep(1);
    if (now() - start < (frequency + 999) / 1000) {
        return 1251;
    }
    shared = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!shared) {
        return 1252;
    }
    start = now();
    if (PalWaitForSingleObjectEx(shared, 1, FALSE) != WAIT_TIMEOUT ||
        now() - start < (frequency + 999) / 1000 ||
        !PalSetEvent(shared) ||
        PalWaitForSingleObjectEx(shared, INFINITE - 1, FALSE) != WAIT_OBJECT_0 ||
        !PalCloseHandle(shared)) {
        return 1253;
    }
    return WIT_TEST_EXIT_CODE;
}

static WitU64 closed_wait()
{
    WitU64 handle;
    arrived[0] = completed[0] = 0;
    expected_wait = WAIT_FAILED;
    shared = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!shared || !spawn(0, &handle)) {
        return 1260;
    }
    while (!arrived[0]) {
        (void)PalSwitchToThread();
    }
    (void)PalSwitchToThread();
    const auto old = shared;
    if (!PalCloseHandle(old)) {
        return 1261;
    }
    shared = PalCreateEventW(nullptr, FALSE, TRUE, nullptr);
    if (!shared ||
        old == shared ||
        !join(handle) ||
        !completed[0] ||
        PalWaitForSingleObjectEx(old, 0, FALSE) != WAIT_FAILED ||
        PalSetEvent(old) ||
        PalCloseHandle(old) ||
        PalWaitForSingleObjectEx(shared, 0, FALSE) != WAIT_OBJECT_0 ||
        !PalCloseHandle(shared)) {
        return 1262;
    }
    return WIT_TEST_EXIT_CODE;
}

WitU64 wit_pal_services(const WitUserStartup *startup)
{
    const auto mode = ((const WitUserTestConfig *)startup)->Mode;
    WitUserMemoryInfo before, after;
    if (!snapshot(&before)) {
        return 1200;
    }
    WitU64 result = 1299;
    if (mode == 10) {
        result = memory();
    }
    if (mode == 11) {
        result = rollback();
    }
    if (mode == 12) {
        result = events(startup);
    }
    if (mode == 13) {
        result = handoff();
    }
    if (mode == 14) {
        result = timed();
    }
    if (mode == 15) {
        result = closed_wait();
    }
    if (mode >= 16 && mode <= 20) {
        auto p = (unsigned char *)PalVirtualAlloc(4096, mode == 17 ? PAGE_NOACCESS : PAGE_READWRITE);
        if (!p) {
            return 1270;
        }
        if (mode == 16) {
            if (!PalVirtualProtect(p + 1, 1, PAGE_READONLY)) {
                return 1271;
            }
            *(volatile unsigned char *)p = 1;
        }
        if (mode == 17) {
            return *(volatile unsigned char *)p;
        }
        if (mode == 18) {
            p[0] = 0xC3;
            ((void (*)())p)();
        }
        if (mode == 19) {
            PalVirtualFree(p, 0);
            return *(volatile unsigned char *)p;
        }
        *(WitU64 *)WIT_GC_INFO_REPORT = mode;
        PalVirtualFree(p + 1, 0);
        return 1272;
    }
    return result != WIT_TEST_EXIT_CODE ? result
                                        : (snapshot(&after) && same(before, after) ? WIT_TEST_EXIT_CODE : 1298);
}
