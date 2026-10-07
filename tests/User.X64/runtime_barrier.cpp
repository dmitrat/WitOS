#include "common.h"
#include "gcenv.h"
#include "pal.witos.h"
#include "tls.h"
#include "../User/protocol.h"
#include <errno.h>

static volatile WitU64 values[3];

static bool snapshot(WitUserMemoryInfo &info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&info, sizeof(info), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK;
}

extern "C" bool wit_test_barrier_early()
{
    SetLastError(0x11223344);
    GCToOSInterface::FlushProcessWriteBuffers();
    PalFlushProcessWriteBuffers();
    if (GetLastError() != 0x11223344) {
        return false;
    }
    for (WitU32 i = 0; i < 3; ++i) {
        WitU64 result = 99;
        if (wit_native_call(WIT_CALL_PROCESS_WRITE_BARRIER, i == 0, i == 1, i == 2, &result) !=
                WIT_STATUS_INVALID_ARGUMENT ||
            result ||
            GetLastError() != 0x11223344) {
            return false;
        }
    }
    return true;
}

static WitU64 worker(WitU64 index)
{
    SetLastError((DWORD)(100 + index));
    errno = (int)(200 + index);
    for (WitU64 i = 0; i < 8; ++i) {
        values[index] = index * 100 + i;
        GCToOSInterface::FlushProcessWriteBuffers();
        PalFlushProcessWriteBuffers();
        const size_t cache = GCToOSInterface::GetCacheSizePerLogicalCpu(true);
        if (!cache || GCToOSInterface::GetCacheSizePerLogicalCpu(false) != cache) {
            return 1741;
        }
        if (GetLastError() != 100 + index ||
            errno != 200 + index ||
            values[index] != index * 100 + i ||
            wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
            return 1740;
        }
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" bool wit_test_barrier_threads()
{
    WitUserMemoryInfo before, after;
    WitU64 handles[3], code;
    if (!snapshot(before)) {
        return false;
    }
    for (WitU64 i = 0; i < 3; ++i) {
        if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) {
            return false;
        }
    }
    for (WitU64 i = 0; i < 3; ++i) {
        if (wit_native_thread_join(handles[i], &code) != WIT_STATUS_OK ||
            code != WIT_TEST_EXIT_CODE ||
            values[i] != i * 100 + 7) {
            return false;
        }
    }
    return snapshot(after) &&
        before.OwnedBytes == after.OwnedBytes &&
        before.ReservedBytes == after.ReservedBytes &&
        before.DynamicCommittedBytes == after.DynamicCommittedBytes &&
        before.PrivatePageTableBytes == after.PrivatePageTableBytes &&
        before.ReservationCount == after.ReservationCount &&
        GetLastError() == 0x11223344;
}
