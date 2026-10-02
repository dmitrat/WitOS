#include "common.h"
#include "gcenv.h"
#include <minipal/mutex.h>
#include "event.h"
#include "regdisplay.h"
#include "StackFrameIterator.h"
#include "thread.h"
#include "threadstore.h"
#include "threadstore.inl"
#include "thread.inl"
#include "RestrictedCallouts.h"
#include "tls.h"
#include "protocol.h"
#include "witos/handles.h"

extern volatile uint32_t *p_tls_index;
extern volatile uint32_t SECTIONREL__tls_CurrentThread;
extern "C" uint32_t _tls_index;
void InitializeGCEventLock();
static RuntimeInstance *expected;
static Thread *main_record;

static bool query(WitUserThreadInfo &info)
{
    return wit_native_call(WIT_CALL_THREAD_QUERY, (uintptr_t)&info, sizeof(info), WIT_THREAD_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK;
}

static bool snapshot(WitUserMemoryInfo &info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&info, sizeof(info), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK;
}

static bool equal(const WitUserMemoryInfo &a, const WitUserMemoryInfo &b)
{
    return a.OwnedBytes == b.OwnedBytes &&
        a.ReservedBytes == b.ReservedBytes &&
        a.DynamicCommittedBytes == b.DynamicCommittedBytes &&
        a.ReservationCount == b.ReservationCount &&
        a.PrivatePageTableBytes == b.PrivatePageTableBytes;
}

static bool local_record()
{
    WitUserThreadInfo info;
    if (!query(info) ||
        !info.CompilerTls ||
        p_tls_index != &_tls_index ||
        *p_tls_index != 0 ||
        ThreadStore::GetCurrentThreadIfAvailable() != nullptr) {
        return false;
    }
    const uintptr_t first = info.CompilerTls + WIT_COMPILER_TLS_DATA_OFFSET;
    const uintptr_t address = (uintptr_t)ThreadStore::RawGetCurrentThread();
    return address >= first &&
        address - first == SECTIONREL__tls_CurrentThread &&
        address - first <= 4096 - WIT_COMPILER_TLS_DATA_OFFSET - sizeof(RuntimeThreadLocals);
}

static WitU64 worker(WitU64)
{
    if (GetRuntimeInstance() != expected ||
        !expected->GetThreadStore() ||
        !local_record() ||
        ThreadStore::RawGetCurrentThread() == main_record) {
        return 1721;
    }
    for (size_t i = 0; i < 3; ++i) {
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK || !local_record()) {
            return 1722;
        }
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" bool wit_test_runtime_instance()
{
    // This profile links standalonegc-disabled: its event-lock method is an
    // upstream no-op; the built-in workstation collector is still required.
    InitializeGCEventLock();
    if (!RestrictedCallouts::Initialize() || GetRuntimeInstance() != nullptr) {
        return false;
    }
    HANDLE module = PalGetModuleHandleFromPointer((void *)&wit_test_runtime_instance);
    if (!module) {
        return false;
    }
    void *held[128];
    size_t n = 0;
    for (; n < 128; ++n) {
        held[n] = ::operator new(1, std::nothrow);
        if (!held[n]) {
            break;
        }
    }
    WitUserMemoryInfo before, after;
    if (n < 2 ||
        !snapshot(before) ||
        RuntimeInstance::Initialize(module) ||
        GetRuntimeInstance() ||
        p_tls_index ||
        SECTIONREL__tls_CurrentThread ||
        !snapshot(after) ||
        !equal(before, after)) {
        return false;
    }
    // Only the RuntimeInstance allocation can succeed; ThreadStore allocation
    // must fail and the NewHolder must reclaim the unpublished instance.
    ::operator delete(held[--n]);
    if (!snapshot(before) ||
        RuntimeInstance::Initialize(module) ||
        GetRuntimeInstance() ||
        p_tls_index ||
        SECTIONREL__tls_CurrentThread ||
        !snapshot(after) ||
        !equal(before, after)) {
        return false;
    }
    while (n) {
        ::operator delete(held[--n]);
    }
    WitUserThreadInfo info;
    if (!query(info)) {
        return false;
    }
    auto raw = (WitU64 *)(uintptr_t)info.RawTls;
    const WitU64 saved_self = raw[0], saved_id = raw[1];
    raw[0] = raw[1] = 0;
    const bool initialized = RuntimeInstance::Initialize(module);
    raw[0] = saved_self;
    raw[1] = saved_id;
    expected = GetRuntimeInstance();
    if (!initialized ||
        !expected ||
        expected->GetPalInstance() != module ||
        !expected->GetThreadStore() ||
        !local_record()) {
        return false;
    }
    main_record = ThreadStore::RawGetCurrentThread();
    WitU64 handles[3], code;
    for (size_t i = 0; i < 3; ++i) {
        if (wit_native_thread_create(worker, 0, &handles[i]) != WIT_STATUS_OK) {
            return false;
        }
    }
    for (size_t i = 0; i < 3; ++i) {
        if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &code) != WIT_STATUS_OK ||
            code != WIT_TEST_EXIT_CODE) {
            return false;
        }
    }
    return GetRuntimeInstance() == expected && local_record();
}

extern "C" void wit_test_runtime_missing_tls()
{
    ThreadStore::SaveCurrentThreadOffsetForDAC();
}

static WitU64 record_ids[3], prior_ids[3];
static volatile WitU64 record_ready[3], record_release;

static bool construct_record()
{
    WitUserThreadInfo info;
    if (!query(info) || !local_record()) {
        return false;
    }
    Thread *record = ThreadStore::RawGetCurrentThread();
    if (record->IsInitialized() || record->IsGCSpecial()) {
        return false;
    }
    // Neither caller-writable FS hints nor compiler TLS supply kernel identity.
    auto raw = (WitU64 *)(uintptr_t)info.RawTls;
    const WitU64 old_self = raw[0], old_id = raw[1];
    raw[0] = raw[1] = 0;
    SetLastError(0x12348765);
    record->SetGCSpecial(); // Actual upstream public route to private Construct.
    raw[0] = old_self;
    raw[1] = old_id;
    const HANDLE capability = record->GetOSThreadHandle();
    if (!capability || capability == INVALID_HANDLE_VALUE || capability == GetCurrentThread()) {
        return false;
    }
    WitThreadReferenceInfo reference;
    if (wit_native_call(WIT_CALL_THREAD_REFERENCE_QUERY, (WitU64)capability, (WitU64)&reference, sizeof(reference),
            nullptr) != WIT_STATUS_OK ||
        reference.ThreadId != info.ThreadId ||
        reference.Rights != WIT_THREAD_REFERENCE_ALL) {
        return false;
    }
    for (unsigned i = 0; i < 3; ++i) {
        void *low = nullptr, *high = nullptr;
        record->GetStackBounds(&low, &high);
        if (!record->IsInitialized() ||
            !record->IsGCSpecial() ||
            ThreadStore::GetCurrentThreadIfAvailable() != record ||
            record->GetPalThreadIdForLogging() != info.NativeId ||
            record->GetOSThreadHandle() != capability ||
            (uintptr_t)low != info.StackLow ||
            (uintptr_t)high != info.StackHigh ||
            !record->IsWithinStackBounds(low) ||
            record->IsWithinStackBounds(high) ||
            !record->IsWithinStackBounds(&info) ||
            record->IsCurrentThreadInCooperativeMode() ||
            GetLastError() != 0x12348765) {
            return false;
        }
        const auto bytes = (const unsigned char *)record->GetEEAllocContext();
        for (size_t j = 0; j < sizeof(ee_alloc_context); ++j) {
            if (bytes[j]) {
                return false;
            }
        }
        record->SetGCSpecial();
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
            return false;
        }
    }
    return true;
}

static WitU64 record_worker(WitU64 index)
{
    if (index >= 3 || !construct_record()) {
        wit_native_fail_fast(1731);
    }
    record_ids[index] = ThreadStore::RawGetCurrentThread()->GetPalThreadIdForLogging();
    record_ready[index] = 1;
    while (!record_release) {
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
            return 1732;
        }
    }
    // Construct-only fixture: explicitly release its owned capability. Actual
    // runtime attachment uses the unchanged Thread::Destroy cleanup instead.
    if (!CloseHandle(ThreadStore::RawGetCurrentThread()->GetOSThreadHandle())) {
        return 1733;
    }
    return WIT_TEST_EXIT_CODE;
}

static WitU64 record_exhaustion_worker(WitU64)
{
    if (!local_record()) {
        return 1734;
    }
    HANDLE held[WIT_HANDLE_CAPACITY];
    unsigned count = 0;
    while (count < WIT_HANDLE_CAPACITY &&
        DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &held[count], 0, FALSE,
            DUPLICATE_SAME_ACCESS)) {
        ++count;
    }
    if (!count || count == WIT_HANDLE_CAPACITY || GetLastError() != ERROR_NOT_ENOUGH_MEMORY) {
        return 1735;
    }
    Thread *record = ThreadStore::RawGetCurrentThread();
    record->SetGCSpecial();
    if (!record->IsInitialized() ||
        !record->IsGCSpecial() ||
        record->GetOSThreadHandle() != INVALID_HANDLE_VALUE ||
        GetLastError() != ERROR_NOT_ENOUGH_MEMORY) {
        return 1736;
    }
    for (unsigned i = 0; i < count; ++i) {
        if (!CloseHandle(held[i])) {
            return 1737;
        }
    }
    record->SetGCSpecial(); // Initialization cannot silently replace the absent capability later.
    return record->GetOSThreadHandle() == INVALID_HANDLE_VALUE ? WIT_TEST_EXIT_CODE : 1738;
}

extern "C" bool wit_test_runtime_thread_record()
{
    // The earlier RuntimeInstance probe leaves all records unattached. This
    // workload initializes real GC-special records without publishing a list
    // entry or a GC allocation context. No attach/detach implementation is faked.
    if (!construct_record()) {
        return false;
    }
    WitUserMemoryInfo before, after;
    if (!snapshot(before)) {
        return false;
    }
    for (unsigned round = 0; round < 4; ++round) {
        WitU64 handles[3], result;
        record_release = 0;
        for (unsigned i = 0; i < 3; ++i) {
            record_ids[i] = record_ready[i] = 0;
            if (wit_native_thread_create(record_worker, i, &handles[i]) != WIT_STATUS_OK) {
                return false;
            }
        }
        while (!record_ready[0] || !record_ready[1] || !record_ready[2]) {
            if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
                return false;
            }
        }
        for (unsigned i = 0; i < 3; ++i) {
            if (!record_ids[i] || record_ids[i] == ThreadStore::RawGetCurrentThread()->GetPalThreadIdForLogging()) {
                return false;
            }
            for (unsigned j = 0; j < 3; ++j) {
                if (record_ids[i] == prior_ids[j] || (i != j && record_ids[i] == record_ids[j])) {
                    return false;
                }
            }
        }
        record_release = 1;
        for (unsigned i = 0; i < 3; ++i) {
            if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK ||
                result != WIT_TEST_EXIT_CODE) {
                return false;
            }
            prior_ids[i] = record_ids[i];
        }
        if (!snapshot(after) || !equal(before, after)) {
            return false;
        }
    }
    WitU64 exhaustion, result;
    if (wit_native_thread_create(record_exhaustion_worker, 0, &exhaustion) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_JOIN, exhaustion, 0, 0, &result) != WIT_STATUS_OK ||
        result != WIT_TEST_EXIT_CODE ||
        !snapshot(after) ||
        !equal(before, after)) {
        return false;
    }
    return CloseHandle(ThreadStore::RawGetCurrentThread()->GetOSThreadHandle()) != FALSE;
}

static minipal_xoshiro128pp *main_random;
static WitU64 random_addresses[3];
static volatile WitU64 random_ready[3], random_release;

static WitU64 random_worker(WitU64 index)
{
    auto state = &ee_alloc_context::t_random.random_state;
    if (index >= 3 || state == main_random || !(state->s[0] | state->s[1] | state->s[2] | state->s[3])) {
        wit_native_fail_fast(1760);
    }
    random_addresses[index] = (WitU64)state;
    const uint32_t marker = (uint32_t)(index + 19);
    state->s[0] = marker;
    random_ready[index] = 1;
    while (!random_release) {
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK || state->s[0] != marker) {
            wit_native_fail_fast(1761);
        }
    }
    // A later reused thread must run the real constructor over fresh storage.
    for (unsigned i = 0; i < 4; ++i) {
        state->s[i] = 0;
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" bool wit_test_runtime_random_tls()
{
    main_random = &ee_alloc_context::t_random.random_state;
    if (!(main_random->s[0] | main_random->s[1] | main_random->s[2] | main_random->s[3])) {
        return false;
    }
    uint32_t saved[4];
    for (unsigned i = 0; i < 4; ++i) {
        saved[i] = main_random->s[i];
    }
    for (unsigned round = 0; round < 2; ++round) {
        WitU64 handles[3], result;
        random_release = 0;
        for (WitU64 i = 0; i < 3; ++i) {
            random_ready[i] = 0;
            if (wit_native_thread_create(random_worker, i, &handles[i]) != WIT_STATUS_OK) {
                return false;
            }
        }
        while (!random_ready[0] || !random_ready[1] || !random_ready[2]) {
            if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
                return false;
            }
        }
        if (random_addresses[0] == random_addresses[1] ||
            random_addresses[0] == random_addresses[2] ||
            random_addresses[1] == random_addresses[2]) {
            return false;
        }
        random_release = 1;
        for (unsigned i = 0; i < 3; ++i) {
            if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK ||
                result != WIT_TEST_EXIT_CODE) {
                return false;
            }
        }
        for (unsigned i = 0; i < 4; ++i) {
            if (main_random->s[i] != saved[i]) {
                return false;
            }
        }
    }
    return true;
}
