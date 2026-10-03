#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "../User/protocol.h"
#include <errno.h>
extern "C" LPVOID WINAPI wit_memory_direct_alloc(LPVOID, SIZE_T, DWORD, DWORD);
extern "C" BOOL WINAPI wit_memory_direct_free(LPVOID, SIZE_T, DWORD);
extern "C" const void *const __imp_VirtualAlloc;

static bool info(WitUserMemoryInfo &value)
{
    WitU64 bytes = 0;
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (WitU64)&value, sizeof(value), WIT_MEMORY_INFO_VERSION, &bytes) ==
        WIT_STATUS_OK &&
        bytes == sizeof(value);
}

static bool run_cases(bool tls)
{
    const DWORD saved = GetLastError();
    const int saved_errno = tls ? errno : 0;
    WitUserMemoryInfo before, reserved, after;
    if (!info(before)) {
        return false;
    }
    auto p = (unsigned char *)VirtualAlloc(nullptr, 8192, MEM_RESERVE, PAGE_READWRITE);
    if (!p ||
        ((uintptr_t)p & 65535) ||
        GetLastError() != saved ||
        !info(reserved) ||
        reserved.OwnedBytes != before.OwnedBytes ||
        reserved.ReservedBytes != before.ReservedBytes + 8192) {
        return false;
    }
    // The actual FrozenObjectHeapManager path reserves, incrementally commits,
    // then releases the exact reservation base with size zero.
    if (wit_memory_direct_alloc(p + 1, 4096, MEM_COMMIT, PAGE_READWRITE) != p) {
        return false;
    }
    for (unsigned i = 0; i < 8192; ++i) {
        if (p[i]) {
            return false;
        }
    }
    p[0] = 17;
    p[8191] = 29;
    if (VirtualAlloc(p, 8192, MEM_COMMIT, PAGE_READONLY) != p || p[0] != 17 || p[8191] != 29) {
        return false;
    }
    // Recommit of existing pages preserves protection: a later kernel write works.
    WitU64 copied = 0;
    if (wit_native_call(WIT_CALL_MONOTONIC_QUERY, (WitU64)p, 8, WIT_MONOTONIC_HZ, &copied) != WIT_STATUS_OK ||
        copied != 8) {
        return false;
    }
    if (VirtualAlloc(p + 1, 4096, MEM_RESET, PAGE_READONLY) != p) {
        return false;
    }
    for (unsigned i = 0; i < 8192; ++i) {
        if (p[i]) {
            return false;
        }
    }
    p[0] = 7;
    p[8191] = 9;
    if (!wit_memory_direct_free(p + 1, 4096, MEM_DECOMMIT) || VirtualAlloc(p, 8192, MEM_COMMIT, PAGE_READWRITE) != p) {
        return false;
    }
    for (unsigned i = 0; i < 8192; ++i) {
        if (p[i]) {
            return false;
        }
    }
    if (VirtualFree(p, 4096, MEM_RELEASE) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        VirtualFree(p + 4096, 0, MEM_RELEASE)) {
        return false;
    }
    SetLastError(saved);
    if (!VirtualFree(p, 0, MEM_RELEASE) ||
        GetLastError() != saved ||
        !info(after) ||
        after.OwnedBytes != before.OwnedBytes ||
        after.ReservedBytes != before.ReservedBytes ||
        after.ReservationCount != before.ReservationCount) {
        return false;
    }
    p = (unsigned char *)wit_memory_direct_alloc(nullptr, 4096, MEM_COMMIT, PAGE_READWRITE);
    if (!p || !VirtualFree(p, 0, MEM_RELEASE)) {
        return false;
    }
    if (VirtualAlloc(nullptr, 0, MEM_RESERVE, PAGE_READWRITE) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        VirtualAlloc(nullptr, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE) ||
        GetLastError() != ERROR_NOT_SUPPORTED ||
        VirtualAlloc((void *)WIT_GC_INFO_REPORT, 4096, MEM_RESERVE, PAGE_READWRITE) ||
        GetLastError() != ERROR_NOT_SUPPORTED ||
        VirtualAlloc((void *)(~0ULL - 15), 32, MEM_COMMIT, PAGE_READWRITE) ||
        GetLastError() != ERROR_INVALID_PARAMETER) {
        return false;
    }
    if (VirtualAlloc(nullptr, 4 * 1024 * 1024, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE) ||
        GetLastError() != ERROR_NOT_ENOUGH_MEMORY ||
        !info(after) ||
        after.OwnedBytes != before.OwnedBytes ||
        after.ReservedBytes != before.ReservedBytes ||
        after.ReservationCount != before.ReservationCount) {
        return false;
    }
    const void *slot = __imp_VirtualAlloc;
    if (VirtualAlloc((void *)&__imp_VirtualAlloc, 8, MEM_COMMIT, PAGE_READWRITE) || slot != __imp_VirtualAlloc) {
        return false;
    }
    p = (unsigned char *)VirtualAlloc(nullptr, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_NOACCESS);
    if (!p || !info(after) || after.DynamicCommittedBytes != before.DynamicCommittedBytes + 4096) {
        return false;
    }
    if (VirtualAlloc(p, 4096, MEM_COMMIT, PAGE_READWRITE) != p ||
        wit_native_call(WIT_CALL_MONOTONIC_QUERY, (WitU64)p, 8, WIT_MONOTONIC_HZ, &copied) != WIT_STATUS_BAD_ADDRESS ||
        copied ||
        !VirtualFree(p, 0, MEM_RELEASE)) {
        return false;
    }
    SetLastError(saved);
    return (!tls || errno == saved_errno) &&
        info(after) &&
        after.OwnedBytes == before.OwnedBytes &&
        after.ReservedBytes == before.ReservedBytes;
}

static WitU64 worker(WitU64 index)
{
    SetLastError(DWORD(2700 + index));
    errno = int(2800 + index);
    // Each worker allocates only its own reservation; concurrent snapshots are
    // intentionally avoided since sibling allocations legitimately change totals.
    auto p = (unsigned char *)VirtualAlloc(nullptr, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!p) {
        return 2501;
    }
    p[0] = (unsigned char)index;
    if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK ||
        p[0] != index ||
        !VirtualFree(p, 0, MEM_RELEASE) ||
        GetLastError() != 2700 + index ||
        errno != 2800 + index) {
        return 2502;
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" WitU64 wit_test_memory(const WitUserStartup *startup, WitU64 mode)
{
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[1] = 536870912;
    wit_native_security_initialize_system();
    wit_native_process_image_initialize(startup);
    const bool tls = mode == 60;
    if (tls) {
        wit_native_tls_initialize(startup);
    }
    SetLastError(0x17263548);
    if (tls) {
        errno = 101;
    }
    if (!run_cases(tls)) {
        return 2503;
    }
    if (tls) {
        WitU64 handles[3], result;
        for (WitU64 i = 0; i < 3; ++i) {
            if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) {
                return 2504;
            }
        }
        for (unsigned i = 0; i < 3; ++i) {
            if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK ||
                result != WIT_TEST_EXIT_CODE) {
                return 2505;
            }
        }
        wit_native_tls_leave();
    }
    return GetLastError() == 0x17263548 && (!tls || errno == 101) ? WIT_TEST_EXIT_CODE : 2506;
}
