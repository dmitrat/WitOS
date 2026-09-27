#include "common.h"
#include <minipal/mutex.h>
#include "CachedInterfaceDispatchPal.h"
#include "event.h"
#include "regdisplay.h"
#include "StackFrameIterator.h"
#include "thread.h"
#include "threadstore.h"
#include "threadstore.inl"
#include "RestrictedCallouts.h"
#include "tls.h"
#include "protocol.h"

extern volatile uint32_t* p_tls_index;
extern volatile uint32_t SECTIONREL__tls_CurrentThread;
extern "C" uint32_t _tls_index;
void InitializeGCEventLock();
static RuntimeInstance* expected;
static Thread* main_record;
static bool query(WitUserThreadInfo& info)
{
    return wit_native_call(WIT_CALL_THREAD_QUERY, (uintptr_t)&info, sizeof(info), WIT_THREAD_INFO_VERSION, nullptr) == WIT_STATUS_OK;
}
static bool snapshot(WitUserMemoryInfo& info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&info, sizeof(info), WIT_MEMORY_INFO_VERSION, nullptr) == WIT_STATUS_OK;
}
static bool equal(const WitUserMemoryInfo& a, const WitUserMemoryInfo& b)
{
    return a.OwnedBytes == b.OwnedBytes && a.ReservedBytes == b.ReservedBytes &&
        a.DynamicCommittedBytes == b.DynamicCommittedBytes && a.ReservationCount == b.ReservationCount &&
        a.PrivatePageTableBytes == b.PrivatePageTableBytes;
}
static bool local_record()
{
    WitUserThreadInfo info;
    if (!query(info) || !info.CompilerTls || p_tls_index != &_tls_index || *p_tls_index != 0 ||
        ThreadStore::GetCurrentThreadIfAvailable() != nullptr) return false;
    const uintptr_t first = info.CompilerTls + WIT_COMPILER_TLS_DATA_OFFSET;
    const uintptr_t address = (uintptr_t)ThreadStore::RawGetCurrentThread();
    return address >= first && address - first == SECTIONREL__tls_CurrentThread &&
        address - first <= 4096 - WIT_COMPILER_TLS_DATA_OFFSET - sizeof(RuntimeThreadLocals);
}
static WitU64 worker(WitU64)
{
    if (GetRuntimeInstance() != expected || !expected->GetThreadStore() || !local_record() ||
        ThreadStore::RawGetCurrentThread() == main_record) return 1721;
    for (size_t i = 0; i < 3; ++i)
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK || !local_record()) return 1722;
    return WIT_TEST_EXIT_CODE;
}
extern "C" bool wit_test_runtime_instance()
{
    // This profile links standalonegc-disabled: its event-lock method is an
    // upstream no-op; the built-in workstation collector is still required.
    InitializeGCEventLock();
    if (!RestrictedCallouts::Initialize() || GetRuntimeInstance() != nullptr) return false;
    HANDLE module = PalGetModuleHandleFromPointer((void*)&wit_test_runtime_instance);
    if (!module) return false;
    void* held[128]; size_t n = 0;
    for (; n < 128; ++n) { held[n] = ::operator new(1, std::nothrow); if (!held[n]) break; }
    WitUserMemoryInfo before, after;
    if (n < 2 || !snapshot(before) || RuntimeInstance::Initialize(module) || GetRuntimeInstance() || p_tls_index || SECTIONREL__tls_CurrentThread ||
        !snapshot(after) || !equal(before, after)) return false;
    // Only the RuntimeInstance allocation can succeed; ThreadStore allocation
    // must fail and the NewHolder must reclaim the unpublished instance.
    ::operator delete(held[--n]);
    if (!snapshot(before) || RuntimeInstance::Initialize(module) || GetRuntimeInstance() || p_tls_index || SECTIONREL__tls_CurrentThread ||
        !snapshot(after) || !equal(before, after)) return false;
    while (n) ::operator delete(held[--n]);
    WitUserThreadInfo info;
    if (!query(info)) return false;
    auto raw = (WitU64*)(uintptr_t)info.RawTls;
    const WitU64 saved_self = raw[0], saved_id = raw[1];
    raw[0] = raw[1] = 0;
    const bool initialized = RuntimeInstance::Initialize(module);
    raw[0] = saved_self; raw[1] = saved_id;
    expected = GetRuntimeInstance();
    if (!initialized || !expected || expected->GetPalInstance() != module || !expected->GetThreadStore() || !local_record()) return false;
    main_record = ThreadStore::RawGetCurrentThread();
    WitU64 handles[3], code;
    for (size_t i = 0; i < 3; ++i)
        if (wit_native_thread_create(worker, 0, &handles[i]) != WIT_STATUS_OK) return false;
    for (size_t i = 0; i < 3; ++i)
        if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &code) != WIT_STATUS_OK || code != WIT_TEST_EXIT_CODE) return false;
    return GetRuntimeInstance() == expected && local_record();
}
extern "C" void wit_test_runtime_missing_tls()
{
    ThreadStore::SaveCurrentThreadOffsetForDAC();
}
