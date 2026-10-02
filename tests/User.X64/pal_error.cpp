#include "pal.witos.h"
#include "protocol.h"

extern "C" const void *const __imp_GetLastError;
static WitU64 identities[3], ownerships[3];

static bool preserved(WitU32 value)
{
    return (WitU32)PalGetLastError() == value && GetLastError() == value && wit_native_error_get() == value;
}

static bool snapshot(WitUserMemoryInfo *info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)info, sizeof(*info), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK;
}

static void worker(WitU64 index)
{
    WitU64 start = 0, now = 0;
    WitU64 result = WIT_TEST_EXIT_CODE;
    const WitU32 value = (WitU32)(0xF0000010 + index);
    if (!preserved(0)) {
        result = 1401;
    }
    WitUserThreadInfo info;
    if (wit_native_call(WIT_CALL_THREAD_QUERY, (uintptr_t)&info, sizeof(info), WIT_THREAD_INFO_VERSION, nullptr) !=
        WIT_STATUS_OK) {
        result = 1405;
    } else {
        ownerships[index] = info.ThreadId;
        identities[index] = PalGetCurrentOSThreadId();
        if (identities[index] != info.NativeId) {
            result = 1406;
        }
    }
    PalSetLastError((int)value);
    if (index < 2) {
        if (wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &start) != WIT_STATUS_OK) {
            result = 1402;
        }
        do {
            if (!preserved(value) || wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &now) != WIT_STATUS_OK) {
                result = 1403;
            }
        } while (now - start < 2);
    }
    PalSleep(1);
    if (!preserved(value)) {
        result = 1404;
    }
    (void)wit_native_call(WIT_CALL_THREAD_EXIT, result, 0, 0, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static WitU64 state()
{
    if (!preserved(0)) {
        return 1410;
    }
    WitUserThreadInfo info;
    if (wit_native_call(WIT_CALL_THREAD_QUERY, (uintptr_t)&info, sizeof(info), WIT_THREAD_INFO_VERSION, nullptr) !=
        WIT_STATUS_OK) {
        return 1411;
    }
    auto raw = (volatile WitU32 *)(uintptr_t)info.RawTls;
    const WitU64 self = *(volatile WitU64 *)(uintptr_t)info.RawTls;
    const WitU64 handle = *(volatile WitU64 *)(uintptr_t)(info.RawTls + 8);
    const WitU64 argument = *(volatile WitU64 *)(uintptr_t)(info.RawTls + 16);
    raw[WIT_TLS_LAST_ERROR_OFFSET / 4 + 1] = 0xA1B2C3D4;
    const WitU32 values[] = {0, 1, 0x80000000, 0xFFFFFFFF, 0xC0000123};
    for (auto value : values) {
        wit_native_error_set(value);
        if (!preserved(value) || raw[WIT_TLS_LAST_ERROR_OFFSET / 4] != value) {
            return 1412;
        }
        SetLastError(value ^ 0x12345678);
        if (!preserved(value ^ 0x12345678)) {
            return 1413;
        }
        PalSetLastError((int)value);
        if (!preserved(value) || raw[WIT_TLS_LAST_ERROR_OFFSET / 4 + 1] != 0xA1B2C3D4) {
            return 1414;
        }
    }
    if (*(volatile WitU64 *)(uintptr_t)info.RawTls != self ||
        *(volatile WitU64 *)(uintptr_t)(info.RawTls + 8) != handle ||
        *(volatile WitU64 *)(uintptr_t)(info.RawTls + 16) != argument ||
        PalGetCurrentOSThreadId() != info.NativeId) {
        return 1415;
    }
    raw[WIT_TLS_LAST_ERROR_OFFSET / 4 + 1] = 0;
    PalSetLastError(0x76543210);
    WitU64 handles[2], code;
    for (WitU64 i = 0; i < 2; ++i) {
        if (wit_native_call(WIT_CALL_THREAD_CREATE, (uintptr_t)worker, i, 0, &handles[i]) != WIT_STATUS_OK) {
            return 1416;
        }
    }
    for (unsigned i = 0; i < 2; ++i) {
        if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &code) != WIT_STATUS_OK ||
            code != WIT_TEST_EXIT_CODE ||
            ownerships[i] != handles[i] ||
            !identities[i] ||
            identities[i] == info.NativeId ||
            !preserved(0x76543210)) {
            return 1417;
        }
    }
    if (wit_native_call(WIT_CALL_THREAD_CREATE, (uintptr_t)worker, 2, 0, &handles[0]) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_JOIN, handles[0], 0, 0, &code) != WIT_STATUS_OK ||
        code != WIT_TEST_EXIT_CODE ||
        identities[0] >= identities[2] ||
        identities[0] == identities[1] ||
        !preserved(0x76543210)) {
        return 1418;
    }
    PalSleep(1);
    return preserved(0x76543210) ? WIT_TEST_EXIT_CODE : 1419;
}

static WitU64 failures()
{
    const WitU32 keep = 0xFEDCBA98;
    PalSetLastError((int)keep);
    if (PalVirtualAlloc(0, PAGE_READWRITE) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        PalVirtualAlloc(UINTPTR_MAX, PAGE_READWRITE) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        PalVirtualAlloc(4096, PAGE_EXECUTE_READ) ||
        GetLastError() != ERROR_NOT_SUPPORTED) {
        return 1420;
    }
    WitUserMemoryInfo before, after;
    if (!snapshot(&before) ||
        PalVirtualAlloc(128 * 4096, PAGE_READWRITE) ||
        GetLastError() != ERROR_NOT_ENOUGH_MEMORY ||
        !snapshot(&after) ||
        before.ReservedBytes != after.ReservedBytes ||
        before.OwnedBytes != after.OwnedBytes) {
        return 1421;
    }
    PalSetLastError((int)keep);
    auto p = PalVirtualAlloc(4096, PAGE_READWRITE);
    if (!p || !preserved(keep) || !PalVirtualProtect(p, 4096, PAGE_READONLY) || !preserved(keep)) {
        return 1422;
    }
    if (PalVirtualProtect(p, 0, PAGE_READONLY) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        PalVirtualProtect((char *)p + 4096, 4096, PAGE_READWRITE) ||
        GetLastError() != ERROR_INVALID_ADDRESS) {
        return 1423;
    }
    PalSetLastError((int)keep);
    PalVirtualFree(p, 0);
    if (!preserved(keep) || PalCreateEventW(nullptr, FALSE, FALSE, L"named") || GetLastError() != ERROR_NOT_SUPPORTED) {
        return 1424;
    }
    PalSetLastError((int)keep);
    auto event = PalCreateEventW(nullptr, FALSE, TRUE, nullptr);
    if (!event ||
        !preserved(keep) ||
        PalWaitForSingleObjectEx(event, 0, TRUE) != WAIT_FAILED ||
        GetLastError() != ERROR_NOT_SUPPORTED ||
        PalWaitForSingleObjectEx(event, 0, FALSE) != WAIT_OBJECT_0 ||
        GetLastError() != ERROR_NOT_SUPPORTED) {
        return 1425;
    }
    PalSetLastError((int)keep);
    if (PalWaitForSingleObjectEx(event, 0, FALSE) != WAIT_TIMEOUT ||
        !preserved(keep) ||
        PalWaitForSingleObjectEx(event, 1, FALSE) != WAIT_TIMEOUT ||
        !preserved(keep) ||
        !PalSetEvent(event) ||
        !PalResetEvent(event) ||
        !PalCloseHandle(event) ||
        !preserved(keep)) {
        return 1426;
    }
    if (PalCloseHandle(event) ||
        GetLastError() != ERROR_INVALID_HANDLE ||
        PalWaitForSingleObjectEx(event, 0, FALSE) != WAIT_FAILED ||
        GetLastError() != ERROR_INVALID_HANDLE ||
        PalSetEvent(INVALID_HANDLE_VALUE) ||
        GetLastError() != ERROR_INVALID_HANDLE) {
        return 1427;
    }
    WitU64 id = PalGetCurrentOSThreadId();
    if (PalCloseHandle((HANDLE)(uintptr_t)id) || GetLastError() != ERROR_INVALID_HANDLE) {
        return 1428;
    }
    WitU64 ownership = 0;
    if (wit_native_call(WIT_CALL_THREAD_CURRENT, 0, 0, 0, &ownership) != WIT_STATUS_OK ||
        PalCloseHandle((HANDLE)(uintptr_t)ownership) ||
        GetLastError() != ERROR_BUSY) {
        return 1428;
    }
    void *low = (void *)1;
    if (PalGetMaximumStackBounds(&low, nullptr) || GetLastError() != ERROR_INVALID_PARAMETER || low != (void *)1) {
        return 1429;
    }
    HANDLE events[4];
    for (size_t i = 0; i < 4; ++i) {
        if (!(events[i] = PalCreateEventW(nullptr, FALSE, FALSE, nullptr))) {
            return 1430;
        }
    }
    if (PalCreateEventW(nullptr, FALSE, FALSE, nullptr) || GetLastError() != ERROR_NOT_ENOUGH_MEMORY) {
        return 1431;
    }
    for (auto value : events) {
        if (!PalCloseHandle(value) || GetLastError() != ERROR_NOT_ENOUGH_MEMORY) {
            return 1432;
        }
    }
    return WIT_TEST_EXIT_CODE;
}

WitU64 wit_pal_error(const WitUserStartup *startup)
{
    const auto mode = ((const WitUserTestConfig *)startup)->Mode;
    if (mode == 32) {
        *(WitU64 *)WIT_GC_INFO_REPORT = (WitU64)&__imp_GetLastError;
        *(volatile WitU64 *)&__imp_GetLastError = 0; // The local import binding must be immutable.
        return 1440;
    }
    return mode == 30 ? state() : failures();
}
