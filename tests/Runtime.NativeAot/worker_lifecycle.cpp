// Full-runtime acceptance fixture. All attachment and cleanup are upstream calls.
#include "common.h"
#include "gcenv.h"
#include "gcenv.ee.h"
#include "regdisplay.h"
#include "StackFrameIterator.h"
#include "thread.h"
#include "threadstore.h"
#include "threadstore.inl"
#include "thread.inl"
#include "pal.witos.h"
#include "tls.h"
#include "native_process.h"
#include "hijack_evidence.witos.h"
#include "NativeContext.h"
#include <errno.h>
#include <stdlib.h>

extern Thread *g_pFinalizerThread;

namespace {
using Callback = int (*)(int);

struct State {
    Callback callback;
    HANDLE ready;
    HANDLE release;
    HANDLE observer;
    HANDLE runtimeHandle;
    Thread *thread;
    uint64_t deadBefore;
    uintptr_t unused;
    Object *tail;
    int value;
    bool controlled;
    bool exitGate;
    bool tlsPaused;
    bool contextResetByGc;
    bool cleaned;
    bool tlsCleaned;
};

struct TlsTrace {
    State *state = nullptr;
    ~TlsTrace();
};

thread_local TlsTrace trace;

void require(bool ok, unsigned line)
{
    if (ok) {
        return;
    }
    char text[] = "[RUNTIME-WORKER-FAIL] line 0000\n";
    for (unsigned i = 0; i < 4; ++i) {
        text[sizeof("[RUNTIME-WORKER-FAIL] line ") - 1 + 3 - i] = (char)('0' + line % 10);
        line /= 10;
    }
    (void)wit_native_call(WIT_CALL_WRITE, wit_native_process_console(), (WitU64)text, sizeof(text) - 1, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

#define check(value) require((value), __LINE__)

TlsTrace::~TlsTrace()
{
    if (!state) {
        return;
    }
    if (state->exitGate) {
        state->tlsPaused = true;
        check(SetEvent(state->ready) != 0);
        check(PalWaitForSingleObjectEx(state->release, 10000, FALSE) == WAIT_OBJECT_0);
        auto alloc = state->thread->GetAllocContext();
        check(!alloc->alloc_ptr && !alloc->alloc_limit);
        state->contextResetByGc = true;
    }
    state->tlsCleaned = true;
}

unsigned listed(Thread *wanted)
{
    GetThreadStore()->LockThreadStore();
    unsigned found = 0;
    {
        ThreadStore::Iterator iterator;
        while (auto t = iterator.GetNext()) {
            if (t == wanted) {
                ++found;
            }
        }
    }
    GetThreadStore()->UnlockThreadStore();
    return found;
}

void cleanup(void *context)
{
    auto &state = *(State *)context;
    check(state.tlsCleaned);
    check(state.thread == ThreadStore::RawGetCurrentThread() && state.thread->IsDetached());
    check(!listed(state.thread));
    // Upstream FixAllocContext(false) converts the unused tail to a free object;
    // unlike GC-time fixing it need not zero alloc_ptr/alloc_limit.
    auto alloc = state.thread->GetAllocContext();
    // Shutdown may execute managed callbacks after the worker body. They consume
    // part of the same allocation context before upstream Detach fixes its tail.
    if (!state.exitGate) {
        check(alloc->alloc_ptr >= (uint8_t *)state.tail && alloc->alloc_limit > alloc->alloc_ptr);
        check(alloc->alloc_limit == (uint8_t *)state.tail + state.unused);
    } else {
        check(state.tlsPaused && state.contextResetByGc);
        check(
            (!alloc->alloc_ptr && !alloc->alloc_limit) || (alloc->alloc_ptr && alloc->alloc_limit > alloc->alloc_ptr));
    }
    const auto unused = alloc->alloc_ptr ? (uintptr_t)(alloc->alloc_limit - alloc->alloc_ptr) : 0;
    check(Thread::GetDeadThreadsNonAllocBytes() == state.deadBefore + unused);
    if (alloc->alloc_ptr) {
        check(((Object *)alloc->alloc_ptr)->RawGetMethodTable() == GCToEEInterface::GetFreeObjectMethodTable());
    }
    WitUserThreadInfo info = {};
    check(wit_native_thread_query((WitU64)state.runtimeHandle, &info) == WIT_STATUS_BAD_HANDLE);
    state.cleaned = true;
}

WitU64 worker(WitU64 argument)
{
    auto &state = *(State *)argument;
    trace.state = &state;
    check(!state.tlsCleaned);
    auto thread = ThreadStore::RawGetCurrentThread();
    check(!thread->IsInitialized()); // Reused kernel slot must have fresh runtime TLS.
    ThreadStore::AttachCurrentThread();
    state.thread = thread;
    check(thread->IsInitialized() && !thread->IsDetached() && listed(thread) == 1);
    state.runtimeHandle = thread->GetOSThreadHandle();
    check(state.runtimeHandle != INVALID_HANDLE_VALUE);
    ThreadStore::AttachCurrentThread(); // Repeat attachment must not register twice.
    check(thread->GetOSThreadHandle() == state.runtimeHandle && listed(thread) == 1);
    check(wit_native_thread_on_cleanup(cleanup, &state) == WIT_STATUS_OK);
    check(wit_native_thread_on_cleanup(cleanup, &state) == WIT_STATUS_BUSY);
    check(wit_native_thread_on_exit(cleanup, &state) == WIT_STATUS_BUSY); // Must retain the real runtime notification.
    check(DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &state.observer, 0, FALSE,
              DUPLICATE_SAME_ACCESS) != 0);
    check(SetEvent(state.ready) != 0);
    if (state.release) {
        check(PalWaitForSingleObjectEx(state.release, 10000, FALSE) == WAIT_OBJECT_0);
    }
    WitStackLeaseInfo selfLease = {};
    if (state.value == 218) {
        check(wit_native_call(WIT_CALL_STACK_LEASE_ACQUIRE, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&selfLease,
                  sizeof(selfLease), nullptr) == WIT_STATUS_OK);
    }
    check(state.callback(state.value) == state.value);
    if (selfLease.Token) {
        check(wit_native_call(WIT_CALL_STACK_LEASE_RELEASE, selfLease.Token, 0, 0, nullptr) == WIT_STATUS_OK);
    }
    if (state.controlled) {
        check(SetEvent(state.ready) != 0);
        check(PalWaitForSingleObjectEx(state.release, 10000, FALSE) == WAIT_OBJECT_0);
    }
    check(!thread->IsCurrentThreadInCooperativeMode());
    auto alloc = thread->GetAllocContext();
    check(alloc->alloc_ptr && alloc->alloc_limit > alloc->alloc_ptr);
    state.unused = (uintptr_t)(alloc->alloc_limit - alloc->alloc_ptr);
    state.tail = (Object *)alloc->alloc_ptr;
    check(state.tail->RawGetMethodTable() != GCToEEInterface::GetFreeObjectMethodTable());
    state.deadBefore = Thread::GetDeadThreadsNonAllocBytes();
    return 42; // Native lifecycle invokes real RuntimeThreadShutdown before cleanup.
}

void service_guard(State &state)
{
    WitUserThreadInfo info = {};
    bool parked = false;
    for (unsigned attempt = 0; attempt < 64; ++attempt) {
        check(wit_native_thread_query((WitU64)state.observer, &info) == WIT_STATUS_OK);
        if (info.State == WIT_THREAD_STATE_WAITING) {
            parked = true;
            break;
        }
        (void)PalSwitchToThread();
    }
    check(parked && info.SuspendCount == 0);
    check(SuspendThread(state.runtimeHandle) == 0);
    NATIVE_CONTEXT before = {}, after = {};
    before.ctx.ContextFlags = after.ctx.ContextFlags =
        CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS | CONTEXT_EXCEPTION_REQUEST;
    check(PalGetCompleteThreadContext(state.runtimeHandle, &before));
    check((before.ctx.ContextFlags & CONTEXT_SERVICE_ACTIVE) != 0);
    const auto first = wit_pal_hijack_evidence();
    GetThreadStore()->LockThreadStore();
    const DWORD error = GetLastError();
    const int savedErrno = errno;
    SetLastError(0x735149);
    errno = 127;
    PalHijack(state.thread); // Must refuse the active syscall frame, with balanced nested suspension.
    check(GetLastError() == 0x735149 && errno == 127);
    errno = savedErrno;
    SetLastError(error);
    GetThreadStore()->UnlockThreadStore();
    const auto last = wit_pal_hijack_evidence();
    check(last.Attempts == first.Attempts + 1 && last.UnsafeSnapshots == first.UnsafeSnapshots + 1);
    check(last.Redirects == first.Redirects && last.ReturnHijacks == first.ReturnHijacks);
    check(PalGetCompleteThreadContext(state.runtimeHandle, &after));
    check(memcmp(&before.ctx, &after.ctx, sizeof(before.ctx)) == 0);
    check(wit_native_thread_query((WitU64)state.observer, &info) == WIT_STATUS_OK && info.SuspendCount == 1);
    check(ResumeThread(state.runtimeHandle) == 1);
}

void report_hijack(const WitHijackEvidence &value)
{
    char text[] = "[RUNTIME] hijack attempts/redirects/returns/unsafe: "
                  "0000000000000000/0000000000000000/0000000000000000/0000000000000000\n";
    const WitU64 fields[] = {value.Attempts, value.Redirects, value.ReturnHijacks, value.UnsafeSnapshots};
    const char hex[] = "0123456789ABCDEF";
    for (unsigned field = 0; field < 4; ++field) {
        for (unsigned i = 0; i < 16; ++i) {
            text[sizeof("[RUNTIME] hijack attempts/redirects/returns/unsafe: ") - 1 + field * 17 + i] =
                hex[(fields[field] >> (60 - i * 4)) & 15];
        }
    }
    WitU64 written = 0;
    check(wit_native_call(WIT_CALL_WRITE, wit_native_process_console(), (WitU64)text, sizeof(text) - 1, &written) ==
            WIT_STATUS_OK &&
        written == sizeof(text) - 1);
}

void finish(State &state, WitU64 join)
{
    // Waiting on the independent reference proves entry to the exit syscall,
    // not just execution of our callback. It works for detached workers too.
    check(WaitForMultipleObjectsEx(1, &state.observer, FALSE, 10000, FALSE) == WAIT_OBJECT_0);
    check(state.cleaned);
    WitUserThreadInfo info = {};
    check(wit_native_thread_query((WitU64)state.observer, &info) == WIT_STATUS_OK);
    check(info.State == WIT_THREAD_STATE_EXITED && info.ExitCode == 42);
    if (join) {
        WitU64 code = 0;
        check(wit_native_thread_join(join, &code) == WIT_STATUS_OK && code == 42);
        check(wit_native_call(WIT_CALL_CLOSE, join, 0, 0, nullptr) ==
            WIT_STATUS_BAD_HANDLE); // Join consumes its capability.
    }
    check(CloseHandle(state.observer) != 0 && CloseHandle(state.ready) != 0);
    if (state.release) {
        check(CloseHandle(state.release) != 0);
    }
}
}

static int managed_lifecycle_audit()
{
    Thread *current = ThreadStore::GetCurrentThread();
    check(
        current && g_pFinalizerThread && current != g_pFinalizerThread && !current->IsCurrentThreadInCooperativeMode());
    GetThreadStore()->LockThreadStore();
    unsigned total = 0, main = 0, finalizer = 0;
    bool valid = true;
    {
        ThreadStore::Iterator iterator;
        while (auto thread = iterator.GetNext()) {
            ++total;
            main += thread == current;
            finalizer += thread == g_pFinalizerThread;
            valid = valid && thread->IsInitialized() && !thread->IsDetached();
        }
    }
    GetThreadStore()->UnlockThreadStore();
    check(valid && total == 2 && main == 1 && finalizer == 1);
    const char text[] = "[RUNTIME] managed lifecycle audit passed: main+finalizer\n";
    WitU64 written = 0;
    check(wit_native_call(WIT_CALL_WRITE, wit_native_process_console(), (WitU64)text, sizeof(text) - 1, &written) ==
            WIT_STATUS_OK &&
        written == sizeof(text) - 1);
    return 42;
}

extern "C" int wit_runtime_worker_acceptance(Callback callback)
{
    if (!callback) {
        return managed_lifecycle_audit();
    }
    check(wit_native_tls_code_pointer((WitU64)callback) != 0);
    check(!ThreadStore::GetCurrentThread()->IsCurrentThreadInCooperativeMode());
    check(wit_native_call(WIT_CALL_THREAD_EXIT, 0xBAD, 1, 0, nullptr) == WIT_STATUS_INVALID_ARGUMENT);
    /* The third argument is the exit request since S2.1 (ABI v65): an unreadable one is BAD_ADDRESS. */
    check(wit_native_call(WIT_CALL_THREAD_EXIT, 0xBAD, 0, 1, nullptr) == WIT_STATUS_BAD_ADDRESS);
    State held = {};
    held.callback = callback;
    held.value = 199;
    held.ready = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
    held.release = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
    check(held.ready && held.release);
    WitU64 heldJoin = 0;
    check(wit_native_thread_create(worker, (WitU64)&held, &heldJoin) == WIT_STATUS_OK);
    check(PalWaitForSingleObjectEx(held.ready, 10000, FALSE) == WAIT_OBJECT_0);
    service_guard(held);
    for (int round = 0; round < 8; ++round) {
        State state = {};
        state.callback = callback;
        state.value = 200 + round;
        state.ready = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
        check(state.ready != nullptr);
        if (round == 0) {
            state.release = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
            check(state.release != nullptr);
        }
        WitU64 join = 0;
        check((round & 1 ? wit_native_thread_create_detached(worker, (WitU64)&state)
                         : wit_native_thread_create(worker, (WitU64)&state, &join)) == WIT_STATUS_OK);
        check(PalWaitForSingleObjectEx(state.ready, 10000, FALSE) == WAIT_OBJECT_0);
        check(state.observer != nullptr);
        if (round == 0) {
            WitU64 rejected = 99;
            check(wit_native_thread_create(worker, (WitU64)&state, &rejected) == WIT_STATUS_NO_MEMORY && !rejected);
            check(wit_native_thread_create_detached(worker, (WitU64)&state) == WIT_STATUS_NO_MEMORY);
            check(SetEvent(state.release) != 0);
        }
        finish(state, join);
        if (round == 0) {
            check(SetEvent(held.release) != 0);
            finish(held, heldJoin);
        }
    }
    // Phase 1 owns an actual current-stack lease. Context replacement is
    // correctly denied by the kernel, so upstream must use its return fallback.
    for (unsigned phase = 0; phase < 2; ++phase) {
        const auto before = wit_pal_hijack_evidence();
        State spinner = {}, collector = {};
        spinner.callback = callback;
        spinner.value = phase ? 218 : 208;
        spinner.controlled = true;
        collector.callback = callback;
        collector.value = phase ? 219 : 209;
        collector.controlled = true;
        State *concurrent[] = {&spinner, &collector};
        for (auto state : concurrent) {
            state->ready = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
            state->release = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
            check(state->ready && state->release);
        }
        WitU64 collectorJoin = 0;
        check(wit_native_thread_create_detached(worker, (WitU64)&spinner) == WIT_STATUS_OK);
        check(PalWaitForSingleObjectEx(spinner.ready, 10000, FALSE) == WAIT_OBJECT_0);
        check(wit_native_thread_create(worker, (WitU64)&collector, &collectorJoin) == WIT_STATUS_OK);
        check(PalWaitForSingleObjectEx(collector.ready, 10000, FALSE) == WAIT_OBJECT_0);
        check(SetEvent(spinner.release) != 0 && SetEvent(collector.release) != 0);
        check(PalWaitForSingleObjectEx(spinner.ready, 10000, FALSE) == WAIT_OBJECT_0);
        check(PalWaitForSingleObjectEx(collector.ready, 10000, FALSE) == WAIT_OBJECT_0);
        // Serialize shutdown observers after concurrent managed execution.
        check(SetEvent(spinner.release) != 0);
        finish(spinner, 0);
        check(SetEvent(collector.release) != 0);
        finish(collector, collectorJoin);
        const auto after = wit_pal_hijack_evidence();
        check(after.Attempts > before.Attempts);
        if (phase == 0) {
            check(after.Redirects > before.Redirects);
        } else {
            check(after.ReturnHijacks > before.ReturnHijacks && after.Redirects == before.Redirects);
        }
    }
    report_hijack(wit_pal_hijack_evidence());
    State exiting = {}, exitCollector = {};
    exiting.callback = callback;
    exiting.value = 210;
    exiting.exitGate = true;
    exitCollector.callback = callback;
    exitCollector.value = 211;
    exitCollector.controlled = true;
    State *exitStates[] = {&exiting, &exitCollector};
    for (auto state : exitStates) {
        state->ready = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
        state->release = PalCreateEventW(nullptr, FALSE, FALSE, nullptr);
        check(state->ready && state->release);
    }
    check(wit_native_thread_create_detached(worker, (WitU64)&exiting) == WIT_STATUS_OK);
    check(PalWaitForSingleObjectEx(exiting.ready, 10000, FALSE) == WAIT_OBJECT_0);
    check(SetEvent(exiting.release) != 0); // Let it return from managed code and start TLS destruction.
    check(PalWaitForSingleObjectEx(exiting.ready, 10000, FALSE) == WAIT_OBJECT_0);
    check(exiting.tlsPaused && !exiting.tlsCleaned && listed(exiting.thread) == 1);
    WitU64 exitCollectorJoin = 0;
    check(wit_native_thread_create(worker, (WitU64)&exitCollector, &exitCollectorJoin) == WIT_STATUS_OK);
    check(PalWaitForSingleObjectEx(exitCollector.ready, 10000, FALSE) == WAIT_OBJECT_0);
    check(SetEvent(exitCollector.release) != 0);
    check(PalWaitForSingleObjectEx(exitCollector.ready, 10000, FALSE) == WAIT_OBJECT_0);
    check(SetEvent(exiting.release) != 0);
    finish(exiting, 0);
    check(exiting.contextResetByGc);
    check(SetEvent(exitCollector.release) != 0);
    finish(exitCollector, exitCollectorJoin);

    WitUserMemoryInfo memory = {};
    check(wit_native_call(WIT_CALL_MEMORY_QUERY, (WitU64)&memory, sizeof(memory), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK);
    check(memory.OwnedBytes <= memory.OwnedLimitBytes && memory.DynamicCommittedBytes <= memory.OwnedBytes);
    check(callback(212) == 212); // Three real managed allocation failures and recoveries.
    check(wit_native_call(WIT_CALL_MEMORY_QUERY, (WitU64)&memory, sizeof(memory), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK);
    check(memory.OwnedBytes <= memory.OwnedLimitBytes &&
        memory.DynamicCommittedBytes <= memory.OwnedBytes &&
        memory.ReservationCount <= memory.ReservationCapacity);
    check(memory.DynamicCommittedBytes + 8 * memory.PageSize < 2 * 1024 * 1024);
    const WitU64 remaining = memory.OwnedLimitBytes - memory.OwnedBytes;
    check(remaining > 8 * memory.PageSize);
    const WitU64 pressureBytes = remaining - 8 * memory.PageSize;
    WitU64 pressure = 0;
    check(wit_native_call(WIT_CALL_MEMORY_RESERVE, pressureBytes, 2 * 1024 * 1024, 0, &pressure) == WIT_STATUS_OK);
    check(wit_native_call(WIT_CALL_MEMORY_COMMIT, pressure, pressureBytes, WIT_MEMORY_NONE, nullptr) == WIT_STATUS_OK);
    check(callback(213) == 213);
    check(wit_native_call(WIT_CALL_MEMORY_RELEASE, pressure, 0, 0, nullptr) == WIT_STATUS_OK);
    check(callback(214) == 214);
    const char oom[] = "[RUNTIME] managed OOM recovery passed: 3 hard-limit + 1 backing-pressure\n";
    WitU64 oomWritten = 0;
    check(wit_native_call(WIT_CALL_WRITE, wit_native_process_console(), (WitU64)oom, sizeof(oom) - 1, &oomWritten) ==
            WIT_STATUS_OK &&
        oomWritten == sizeof(oom) - 1);
    const char eh[] = "[RUNTIME] managed EH workers passed: 2 (filters/rethrow/nested-finally/native-release)\n";
    check(wit_native_call(WIT_CALL_WRITE, wit_native_process_console(), (WitU64)eh, sizeof(eh) - 1, nullptr) ==
        WIT_STATUS_OK);
    const char message[] =
        "[RUNTIME] worker attach/detach/reuse/rollback and foreign GC/hijack/service-guard/exit-GC passed\n";
    WitU64 written = 0;
    check(wit_native_call(WIT_CALL_WRITE, wit_native_process_console(), (WitU64)message, sizeof(message) - 1,
              &written) == WIT_STATUS_OK &&
        written == sizeof(message) - 1);
    return 42;
}

extern "C" [[noreturn]] void wit_runtime_invalid_instruction();

extern "C" int wit_runtime_native_fault(int (*callback)(int))
{
    (void)callback;
    const char text[] = "[RUNTIME] native fault probe entered\n";
    check(wit_native_call(WIT_CALL_WRITE, wit_native_process_console(), (WitU64)text, sizeof(text) - 1, nullptr) ==
        WIT_STATUS_OK);
    wit_runtime_invalid_instruction();
}

static void unexpected_stack_exit()
{
    const char text[] = "[RUNTIME] unexpected stack-atexit cleanup\n";
    (void)wit_native_call(WIT_CALL_WRITE, wit_native_process_console(), (WitU64)text, sizeof(text) - 1, nullptr);
}

extern "C" int wit_runtime_stack_overflow(int (*callback)(int))
{
    check(wit_native_tls_code_pointer((WitU64)callback) != 0);
    check(atexit(unexpected_stack_exit) == 0);
    const char text[] = "[RUNTIME] managed stack overflow probe entered\n";
    check(wit_native_call(WIT_CALL_WRITE, wit_native_process_console(), (WitU64)text, sizeof(text) - 1, nullptr) ==
        WIT_STATUS_OK);
    (void)callback(217); // Real managed recursion must hit the fixed stack guard.
    return -1; // A returned probe is a failure, never a synthesized overflow.
}
