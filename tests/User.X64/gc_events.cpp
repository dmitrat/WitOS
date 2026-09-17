#include "gcenv.witos.h"
#include "protocol.h"

static_assert(WIT_NATIVE_FAIL_FAST_EXIT == WIT_GC_TEST_FAIL_FAST_EXIT, "Fatal adapter exit contract");
static GCEvent* subject;
static GCEvent* acknowledgments[2];
static volatile WitU64 outcomes[2], attempts;
static volatile WitU32 test_gate;
static WitU64 protected_value;

static WitU64 call(WitU64 op, WitU64 a = 0, WitU64 b = 0, WitU64 c = 0, WitU64* result = nullptr)
{
    return wit_native_call(op, a, b, c, result);
}
static bool join(WitU64 handle)
{
    WitU64 code = 0;
    return call(WIT_CALL_THREAD_JOIN, handle, 0, 0, &code) == WIT_STATUS_OK && code == WIT_TEST_EXIT_CODE;
}
static bool start(void (*entry)(WitU64), WitU64 argument, WitU64* handle)
{
    return call(WIT_CALL_THREAD_CREATE, (uintptr_t)entry, argument, 0, handle) == WIT_STATUS_OK;
}
static void done(WitU64 code = WIT_TEST_EXIT_CODE)
{
    (void)call(WIT_CALL_THREAD_EXIT, code);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
static void waiter(WitU64 index)
{
    outcomes[index] = subject->Wait(INFINITE, index != 0);
    if (acknowledgments[index]) acknowledgments[index]->Set();
    done();
}
static void closer(WitU64)
{
    // Both legitimate overlap outcomes are accepted by the test: an old wait
    // fails, or a wait beginning after recreation consumes the new signal.
    subject->CloseEvent();
    if (!subject->CreateAutoEventNoThrow(true)) done(199);
    done();
}
static void contender(WitU64)
{
    while (!wit_native_try_lock(&test_gate)) {
        ++attempts;
        GCToOSInterface::YieldThread(1);
    }
    const bool correct = protected_value == 123;
    protected_value = 456;
    wit_native_unlock(&test_gate);
    done(correct ? WIT_TEST_EXIT_CODE : 199);
}

static WitU64 state()
{
    GCEvent automatic, manual;
    if (automatic.IsValid() || automatic.Wait(0, false) != WAIT_FAILED ||
        !automatic.CreateAutoEventNoThrow(true) || !automatic.IsValid() ||
        automatic.CreateManualEventNoThrow(false)) return 150;
    // A stored signal wins even with the largest finite timeout.
    if (automatic.Wait(INFINITE - 1, false) != WAIT_OBJECT_0 || automatic.Wait(0, false) != WAIT_TIMEOUT) return 151;
    automatic.Set(); automatic.Set();
    if (automatic.Wait(0, false) != WAIT_OBJECT_0 || automatic.Wait(0, false) != WAIT_TIMEOUT) return 152;
    automatic.Set(); automatic.Reset();
    if (automatic.Wait(0, false) != WAIT_TIMEOUT) return 153;
    GCEvent copied = automatic;
    if (copied.Wait(0, false) != WAIT_FAILED) return 154;
    automatic.CloseEvent();
    if (automatic.IsValid() || automatic.Wait(INFINITE, false) != WAIT_FAILED ||
        !manual.CreateOSManualEventNoThrow(false)) return 155;
    manual.Set();
    if (copied.Wait(0, false) != WAIT_FAILED || manual.Wait(0, false) != WAIT_OBJECT_0 ||
        manual.Wait(0, true) != WAIT_OBJECT_0) return 156;
    manual.Reset();
    if (manual.Wait(0, false) != WAIT_TIMEOUT) return 157;
    manual.CloseEvent();
    if (!automatic.CreateOSAutoEventNoThrow(false) || automatic.Wait(0, false) != WAIT_TIMEOUT) return 158;
    automatic.CloseEvent();
    if (!manual.CreateManualEventNoThrow(true) || manual.Wait(0, false) != WAIT_OBJECT_0) return 159;
    manual.CloseEvent();
    return WIT_TEST_EXIT_CODE;
}

static WitU64 capacity()
{
    GCEvent a, b, c, d, extra;
    GCEvent* events[] = { &a, &b, &c, &d };
    WitUserMemoryInfo before, after;
    WitU64 raw = 0;
    if (call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&before, sizeof(before), WIT_MEMORY_INFO_VERSION) != WIT_STATUS_OK ||
        call(WIT_CALL_EVENT_CREATE, 0, 0, 0, &raw) != WIT_STATUS_OK) return 160;
    for (size_t i = 0; i < 3; ++i) if (!events[i]->CreateAutoEventNoThrow(false)) return 161;
    if (d.CreateAutoEventNoThrow(false) || d.IsValid()) return 162;
    for (size_t i = 0; i < 3; ++i) events[i]->CloseEvent();
    if (call(WIT_CALL_CLOSE, raw) != WIT_STATUS_OK) return 163;
    for (size_t round = 0; round < 12; ++round) {
        for (size_t i = 0; i < 4; ++i) if (!events[i]->CreateManualEventNoThrow(true)) return 164;
        if (extra.CreateAutoEventNoThrow(false) || extra.IsValid()) return 165;
        for (size_t i = 0; i < 4; ++i) {
            if (events[i]->Wait(0, false) != WAIT_OBJECT_0) return 166;
            events[i]->CloseEvent();
            if (events[i]->IsValid()) return 167;
        }
    }
    if (call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&after, sizeof(after), WIT_MEMORY_INFO_VERSION) != WIT_STATUS_OK ||
        before.OwnedBytes != after.OwnedBytes || before.ReservedBytes != after.ReservedBytes ||
        before.PhysicalAvailableBytes != after.PhysicalAvailableBytes) return 168;
    return WIT_TEST_EXIT_CODE;
}

static WitU64 parallel(bool manual)
{
    GCEvent signal, ack0, ack1;
    WitU64 handles[2];
    subject = &signal;
    outcomes[0] = outcomes[1] = 999;
    acknowledgments[0] = &ack0;
    acknowledgments[1] = manual ? &ack1 : &ack0;
    if (!(manual ? signal.CreateManualEventNoThrow(false) : signal.CreateAutoEventNoThrow(false)) ||
        !ack0.CreateAutoEventNoThrow(false) || (manual && !ack1.CreateAutoEventNoThrow(false))) return 170;
    if (!start(waiter, 0, &handles[0]) || !start(waiter, 1, &handles[1])) return 171;
    signal.Set();
    if (ack0.Wait(INFINITE, false) != WAIT_OBJECT_0) return 172;
    if (manual) {
        if (ack1.Wait(INFINITE, false) != WAIT_OBJECT_0 || signal.Wait(0, false) != WAIT_OBJECT_0 ||
            signal.Wait(0, false) != WAIT_OBJECT_0) return 173;
        signal.Reset();
    } else {
        const size_t count = (outcomes[0] == WAIT_OBJECT_0 ? 1 : 0) + (outcomes[1] == WAIT_OBJECT_0 ? 1 : 0);
        if (count != 1 || signal.Wait(0, false) != WAIT_TIMEOUT) return 174;
        signal.Set();
        if (ack0.Wait(INFINITE, false) != WAIT_OBJECT_0) return 175;
    }
    if (!join(handles[0]) || !join(handles[1]) || outcomes[0] != WAIT_OBJECT_0 || outcomes[1] != WAIT_OBJECT_0 ||
        signal.Wait(0, false) != WAIT_TIMEOUT) return 176;
    signal.CloseEvent(); ack0.CloseEvent();
    if (manual) ack1.CloseEvent();
    return WIT_TEST_EXIT_CODE;
}

static WitU64 close_overlap()
{
    GCEvent signal;
    WitU64 handles[2];
    subject = &signal;
    outcomes[0] = 999;
    acknowledgments[0] = nullptr;
    if (!signal.CreateManualEventNoThrow(false) || !start(waiter, 0, &handles[0]) || !start(closer, 0, &handles[1])) return 180;
    if (!join(handles[0]) || !join(handles[1]) || !signal.IsValid()) return 181;
    if (outcomes[0] == WAIT_FAILED) {
        if (signal.Wait(0, false) != WAIT_OBJECT_0) return 182;
    } else if (outcomes[0] == WAIT_OBJECT_0) {
        if (signal.Wait(0, false) != WAIT_TIMEOUT) return 183;
    } else return 184;
    if (signal.Wait(0, false) != WAIT_TIMEOUT) return 185;
    signal.CloseEvent();
    return WIT_TEST_EXIT_CODE;
}

static void churn(WitU64)
{
    GCEvent event;
    for (size_t i = 0; i < 8; ++i) {
        if (!event.CreateAutoEventNoThrow(true) || event.Wait(0, false) != WAIT_OBJECT_0) done(199);
        GCToOSInterface::YieldThread(0);
        if (event.Wait(0, false) != WAIT_TIMEOUT) done(199);
        event.CloseEvent();
    }
    done();
}
static WitU64 contention()
{
    WitU64 handle;
    if (!wit_native_try_lock(&test_gate) || wit_native_try_lock(&test_gate)) return 190;
    protected_value = 123;
    attempts = 0;
    if (!start(contender, 0, &handle)) return 191;
    while (!attempts) GCToOSInterface::YieldThread(0);
    wit_native_unlock(&test_gate);
    if (!join(handle) || !wit_native_try_lock(&test_gate)) return 192;
    const bool correct = protected_value == 456;
    wit_native_unlock(&test_gate);
    if (!correct) return 193;
    WitU64 workers[2];
    if (!start(churn, 0, &workers[0]) || !start(churn, 0, &workers[1]) ||
        !join(workers[0]) || !join(workers[1])) return 197;
    return WIT_TEST_EXIT_CODE;
}

WitU64 wit_gc_events(WitU64 mode)
{
    if (mode == WIT_GC_TEST_EVENT_STATE) return state();
    if (mode == WIT_GC_TEST_EVENT_CAPACITY) return capacity();
    if (mode == WIT_GC_TEST_EVENT_MANUAL) return parallel(true);
    if (mode == WIT_GC_TEST_EVENT_AUTO) return parallel(false);
    if (mode == WIT_GC_TEST_EVENT_CLOSE) return close_overlap();
    if (mode == WIT_GC_TEST_EVENT_CONTENTION) return contention();
    if (mode == WIT_GC_TEST_EVENT_FAIL_FAST) {
        GCEvent signal;
        WitU64 handle;
        subject = &signal;
        acknowledgments[0] = nullptr;
        if (!signal.CreateAutoEventNoThrow(false) || !start(waiter, 0, &handle)) return 194;
        GCEvent copied = signal;
        *(volatile WitU64*)WIT_GC_INFO_REPORT = 0x475345564641494CULL;
        copied.Set(); // A void operation on a copied/non-owning event must terminate this component.
        return 195;
    }
    return 196;
}
