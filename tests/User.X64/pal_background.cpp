#include "pal.witos.h"
#include "tls.h"
#include "protocol.h"
#include <new>

static WitU64 mode, root_id;
static volatile WitU64 ids[3], started[3], dtor_started[3], dtor_done[3], cleanup_seen[3];
static HANDLE release_event;
static __declspec(thread) WitU64 index_value = 3;
static __declspec(thread) WitU64 constructions;
class BackgroundLocal {
public:
    BackgroundLocal() noexcept : m_data(::operator new(16, std::nothrow))
    {
        if (!m_data) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        *(WitU64*)m_data = 0x123456;
        ++constructions;
        if (mode == 5 && PalGetCurrentOSThreadId() != root_id) {
            *(WitU64*)WIT_GC_INFO_REPORT = mode;
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
    ~BackgroundLocal() noexcept
    {
        if (*(WitU64*)m_data != 0x123456) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        if (index_value < 3) {
            dtor_started[index_value] = 1;
            if (mode <= 2) while (!cleanup_seen[index_value]) (void)PalSwitchToThread();
            else (void)PalSwitchToThread();
            if (mode == 4) { *(WitU64*)WIT_GC_INFO_REPORT = mode; *(volatile WitU64*)0 = 1; }
        }
        ::operator delete(m_data);
        if (index_value < 3) {
            dtor_done[index_value] = 1;
            if (mode == 6) ((WitU64*)WIT_GC_INFO_REPORT)[2] = 1;
        } else ((WitU64*)WIT_GC_INFO_REPORT)[1] = 1;
    }
private:
    void* m_data;
};
static thread_local BackgroundLocal local;
static bool snapshot(WitUserMemoryInfo* info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)info, sizeof(*info), WIT_MEMORY_INFO_VERSION, nullptr) == WIT_STATUS_OK;
}
static bool same(const WitUserMemoryInfo& a, const WitUserMemoryInfo& b)
{
    return a.OwnedBytes == b.OwnedBytes && a.PrivatePageTableBytes == b.PrivatePageTableBytes &&
        a.DynamicCommittedBytes == b.DynamicCommittedBytes && a.ReservedBytes == b.ReservedBytes &&
        a.ReservationCount == b.ReservationCount && a.PhysicalAvailableBytes == b.PhysicalAvailableBytes;
}
static uint32_t callback(void* context)
{
    const auto index = (WitU64)(uintptr_t)context;
    if (index >= 3 || constructions != 1 || index_value != 3) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    index_value = index;
    ids[index] = PalGetCurrentOSThreadId();
    WitU64 result = 99;
    if (!ids[index] || ids[index] == root_id ||
        wit_native_call(WIT_CALL_THREAD_JOIN, ids[index], 0, 0, &result) != WIT_STATUS_DENIED || result ||
        wit_native_call(WIT_CALL_CLOSE, ids[index], 0, 0, nullptr) != WIT_STATUS_BUSY) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    started[index] = 1;
    if (mode == 3) { *(WitU64*)WIT_GC_INFO_REPORT = mode; *(volatile WitU64*)0 = 1; }
    if (release_event && PalWaitForSingleObjectEx(release_event, INFINITE, FALSE) != WAIT_OBJECT_0)
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    if (index == 1) wit_native_thread_exit(73); // Explicit exit must also clean TLS and auto-reap.
    return 73;
}
static bool launch(unsigned kind, void* context)
{
    if (kind == 0) return PalStartBackgroundGCThread(callback, context);
    if (kind == 1) return PalStartFinalizerThread(callback, context);
    return PalStartEventPipeHelperThread(callback, context);
}
static bool wait_for_reap(unsigned index)
{
    bool saw_cleanup = false;
    while (!ids[index]) (void)PalSwitchToThread();
    for (;;) {
        const auto status = wit_native_call(WIT_CALL_CLOSE, ids[index], 0, 0, nullptr);
        if (status == WIT_STATUS_BAD_HANDLE) break;
        if (status != WIT_STATUS_BUSY) return false; // The parent must never reap a detached worker.
        if (dtor_started[index] && !dtor_done[index]) { saw_cleanup = true; cleanup_seen[index] = 1; }
        (void)PalSwitchToThread();
    }
    WitU64 result = 99;
    return saw_cleanup && dtor_done[index] &&
        wit_native_call(WIT_CALL_THREAD_JOIN, ids[index], 0, 0, &result) == WIT_STATUS_BAD_HANDLE && !result;
}
static void clear(unsigned index)
{
    ids[index] = started[index] = dtor_started[index] = dtor_done[index] = cleanup_seen[index] = 0;
}
extern "C" void wit_background_configure(const WitUserStartup* startup)
{
    mode = ((const WitUserTestConfig*)startup)->Mode;
    root_id = PalGetCurrentOSThreadId();
    *(WitU64*)WIT_GC_INFO_REPORT = mode;
}
extern "C" WitU64 wit_background_program(const WitUserStartup* startup)
{
    (void)startup;
    WitUserMemoryInfo baseline, after;
    if (constructions != 1 || !snapshot(&baseline)) return 1300;
    if (mode >= 3) {
        if (!launch(0, nullptr)) return 1301;
        if (mode == 6) wit_native_thread_exit(WIT_TEST_EXIT_CODE); // Last detached worker must finish the component safely.
        for (;;) (void)PalSwitchToThread();
    }
    WitU64 result = 99;
    if (wit_native_call(WIT_CALL_THREAD_CREATE, (uintptr_t)callback, 0, 2, &result) != WIT_STATUS_INVALID_ARGUMENT || result ||
        wit_native_call(WIT_CALL_THREAD_CREATE, (uintptr_t)callback, 0, 1ULL << 32, &result) != WIT_STATUS_INVALID_ARGUMENT || result ||
        wit_native_call(WIT_CALL_THREAD_CREATE, 0, 0, WIT_THREAD_DETACHED, &result) != WIT_STATUS_BAD_ADDRESS || result ||
        PalStartBackgroundGCThread(nullptr, nullptr) ||
        PalStartFinalizerThread((BackgroundCallback)startup, nullptr)) return 1302;
    if (mode == 0) {
        WitU64 previous = 0;
        for (unsigned i = 0; i < 12; ++i) {
            clear(0);
            if (!launch(i % 3, nullptr) || !wait_for_reap(0) || ids[0] == previous || !snapshot(&after) || !same(baseline, after)) return 1310;
            previous = ids[0];
        }
    } else if (mode == 1) {
        release_event = PalCreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!release_event) return 1320;
        for (unsigned i = 0; i < 3; ++i) if (!launch(i, (void*)(uintptr_t)i)) return 1321;
        while (!started[0] || !started[1] || !started[2]) (void)PalSwitchToThread();
        for (unsigned i = 0; i < 12; ++i) if (launch(i % 3, nullptr)) return 1322;
        if (!PalSetEvent(release_event)) return 1323;
        // Observe every destructor while the three callbacks yield from cleanup.
        bool seen[3] = {};
        for (;;) {
            unsigned reaped = 0;
            for (unsigned i = 0; i < 3; ++i) {
                const auto status = wit_native_call(WIT_CALL_CLOSE, ids[i], 0, 0, nullptr);
                if (status == WIT_STATUS_BAD_HANDLE) { ++reaped; continue; }
                if (status != WIT_STATUS_BUSY) return 1324;
                if (dtor_started[i] && !dtor_done[i]) { seen[i] = true; cleanup_seen[i] = 1; }
            }
            if (reaped == 3) break;
            (void)PalSwitchToThread();
        }
        for (unsigned i = 0; i < 3; ++i) if (!seen[i] || !dtor_done[i]) return 1325;
        if (!PalCloseHandle(release_event)) return 1326;
        release_event = nullptr;
        clear(0);
        if (!launch(0, nullptr) || !wait_for_reap(0)) return 1327;
    } else if (mode == 2) {
        for (WitU64 remaining = 0; remaining < 6; ++remaining) {
            WitU64 arena = 0, pages = 0;
            if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 128 * 4096, 4096, 0, &arena) != WIT_STATUS_OK) return 1330;
            while (wit_native_call(WIT_CALL_MEMORY_COMMIT, arena + pages * 4096, 4096, 3, nullptr) == WIT_STATUS_OK) ++pages;
            if (pages <= remaining || (remaining && wit_native_call(WIT_CALL_MEMORY_DECOMMIT,
                arena + (pages - remaining) * 4096, remaining * 4096, 0, nullptr) != WIT_STATUS_OK)) return 1331;
            WitUserMemoryInfo held;
            if (!snapshot(&held)) return 1332;
            for (unsigned i = 0; i < 3; ++i) if (launch(i, nullptr)) return 1333;
            if (started[0] || !snapshot(&after) || !same(held, after) ||
                wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) return 1334;
        }
        clear(0);
        if (!launch(0, nullptr) || !wait_for_reap(0)) return 1335;
    }
    return snapshot(&after) && same(baseline, after) ? WIT_TEST_EXIT_CODE : 1399;
}
extern "C" WitU64 wit_background_finish(WitU64 result)
{
    return result == WIT_TEST_EXIT_CODE && ((WitU64*)WIT_GC_INFO_REPORT)[1] == 1 ? result : 1398;
}
