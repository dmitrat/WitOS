#include "gcenv.witos.h"
#include "Crst.h"
#include "protocol.h"

static minipal_mutex *shared;
static volatile WitU64 phase, entered, inside;
static WitU64 value, thread_ids[2];

static WitU64 call(WitU64 op, WitU64 a = 0, WitU64 b = 0, WitU64 c = 0, WitU64 *result = nullptr)
{
    return wit_native_call(op, a, b, c, result);
}

static WitU64 identity()
{
    WitU64 id = 0;
    if (call(WIT_CALL_THREAD_CURRENT, 0, 0, 0, &id) != WIT_STATUS_OK) {
        return 0;
    }
    return id;
}

static bool spawn(void (*entry)(WitU64), WitU64 argument, WitU64 *handle)
{
    return call(WIT_CALL_THREAD_CREATE, (uintptr_t)entry, argument, 0, handle) == WIT_STATUS_OK;
}

static bool join(WitU64 handle)
{
    WitU64 code = 0;
    return call(WIT_CALL_THREAD_JOIN, handle, 0, 0, &code) == WIT_STATUS_OK && code == WIT_TEST_EXIT_CODE;
}

static void done(WitU64 code = WIT_TEST_EXIT_CODE)
{
    (void)call(WIT_CALL_THREAD_EXIT, code);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static void identify(WitU64 index)
{
    thread_ids[index] = identity();
    done(thread_ids[index] ? WIT_TEST_EXIT_CODE : 399);
}

static void waiter(WitU64)
{
    phase = 1;
    minipal_mutex_enter(shared);
    entered = 1;
    const bool correct = value == 456;
    minipal_mutex_leave(shared);
    done(correct ? WIT_TEST_EXIT_CODE : 399);
}

static void stress_worker(WitU64)
{
    for (size_t i = 0; i < 8; ++i) {
        minipal_mutex_enter(shared);
        minipal_mutex_enter(shared);
        if (inside) {
            done(399);
        }
        inside = 1;
        const auto saved = value;
        GCToOSInterface::YieldThread(0);
        if (inside != 1 || value != saved) {
            done(399);
        }
        value = saved + 1;
        inside = 0;
        minipal_mutex_leave(shared);
        minipal_mutex_leave(shared);
    }
    done();
}

static void wrong_owner(WitU64)
{
    minipal_mutex_leave(shared);
    done(399);
}

static void exhausted_waiter(WitU64)
{
    minipal_mutex_enter(shared);
    done(399);
}

static bool all_events_available()
{
    WitU64 events[4];
    for (size_t i = 0; i < 4; ++i) {
        if (call(WIT_CALL_EVENT_CREATE, 0, 0, 0, &events[i]) != WIT_STATUS_OK) {
            return false;
        }
    }
    for (size_t i = 0; i < 4; ++i) {
        if (call(WIT_CALL_CLOSE, events[i]) != WIT_STATUS_OK) {
            return false;
        }
    }
    return true;
}

WitU64 wit_gc_mutex(const WitUserStartup *startup, WitU64 mode)
{
    if (mode == WIT_GC_TEST_THREAD_ID) {
        const auto self = identity();
        WitU64 handle, invalid = 9;
        auto tls = (volatile WitU64 *)(uintptr_t)((const WitUserTestConfig *)startup)->KernelProbe;
        if (!self || tls[WIT_TLS_HANDLE_OFFSET / 8] != self) {
            return 300;
        }
        tls[WIT_TLS_HANDLE_OFFSET / 8] = 0;
        if (identity() != self) {
            return 301;
        }
        tls[WIT_TLS_HANDLE_OFFSET / 8] = self;
        for (size_t i = 0; i < 32; ++i) {
            if (identity() != self) {
                return 302;
            }
        }
        if (call(WIT_CALL_THREAD_CURRENT, 1, 0, 0, &invalid) != WIT_STATUS_INVALID_ARGUMENT ||
            invalid ||
            call(WIT_CALL_CLOSE, self) != WIT_STATUS_BUSY ||
            !all_events_available()) {
            return 303;
        }
        for (size_t i = 0; i < 2; ++i) {
            if (!spawn(identify, i, &handle) || !join(handle) || thread_ids[i] != handle || handle == self) {
                return 304;
            }
        }
        if (thread_ids[0] == thread_ids[1] || identity() != self) {
            return 305;
        }
        *(WitU64 *)WIT_GC_INFO_REPORT = self;
        return WIT_TEST_EXIT_CODE;
    }
    if (mode == WIT_GC_TEST_MUTEX_BASIC) {
        minipal_mutex lock;
        if (minipal_mutex_init(nullptr) || !minipal_mutex_init(&lock) || minipal_mutex_init(&lock)) {
            return 310;
        }
        minipal_mutex_enter(&lock);
        minipal_mutex_enter(&lock);
        minipal_mutex_leave(&lock);
        minipal_mutex_leave(&lock);
        {
            minipal::MutexHolder a(lock);
            {
                minipal::MutexHolder b(lock);
            }
        }
        minipal_mutex_destroy(&lock);
        if (!minipal_mutex_init(&lock)) {
            return 311;
        }
        minipal_mutex_destroy(&lock);
        CLRCriticalSection section;
        if (!section.Initialize()) {
            return 312;
        }
        section.Enter();
        section.Enter();
        section.Leave();
        section.Leave();
        section.Destroy();
        return all_events_available() ? WIT_TEST_EXIT_CODE : 313;
    }
    if (mode == WIT_GC_TEST_MUTEX_BLOCKING || mode == WIT_GC_TEST_MUTEX_STRESS) {
        minipal_mutex lock;
        WitU64 workers[2];
        shared = &lock;
        if (!minipal_mutex_init(&lock)) {
            return 320;
        }
        value = 123;
        phase = entered = inside = 0;
        if (mode == WIT_GC_TEST_MUTEX_BLOCKING) {
            minipal_mutex_enter(&lock);
            minipal_mutex_enter(&lock);
            if (!spawn(waiter, 0, &workers[0])) {
                return 321;
            }
            while (!phase) {
                GCToOSInterface::YieldThread(0);
            }
            minipal_mutex_leave(&lock); // Still owned once: contender cannot enter.
            for (size_t i = 0; i < 8; ++i) {
                GCToOSInterface::YieldThread(0);
                if (entered) {
                    return 322;
                }
            }
            value = 456;
            minipal_mutex_leave(&lock);
            if (!join(workers[0]) || !entered) {
                return 323;
            }
        } else {
            value = 0;
            if (!spawn(stress_worker, 0, &workers[0]) ||
                !spawn(stress_worker, 1, &workers[1]) ||
                !join(workers[0]) ||
                !join(workers[1]) ||
                value != 16 ||
                inside) {
                return 324;
            }
        }
        minipal_mutex_destroy(&lock);
        return all_events_available() ? WIT_TEST_EXIT_CODE : 325;
    }
    if (mode == WIT_GC_TEST_MUTEX_CAPACITY || mode == WIT_GC_TEST_CRST_INIT_FAIL) {
        minipal_mutex locks[17];
        WitUserMemoryInfo before, after;
        if (call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&before, sizeof(before), WIT_MEMORY_INFO_VERSION) != WIT_STATUS_OK) {
            return 330;
        }
        for (size_t round = 0; round < 3; ++round) {
            for (size_t i = 0; i < 16; ++i) {
                if (!minipal_mutex_init(&locks[i])) {
                    return 331;
                }
            }
            if (minipal_mutex_init(&locks[16])) {
                return 332;
            }
            if (mode == WIT_GC_TEST_CRST_INIT_FAIL) {
                CrstStatic extra;
                *(WitU64 *)WIT_GC_INFO_REPORT = mode;
                (void)extra.InitNoThrow(CrstThreadStore);
                return 333;
            }
            for (size_t i = 0; i < 16; ++i) {
                minipal_mutex_enter(&locks[i]);
                minipal_mutex_leave(&locks[i]);
                minipal_mutex_destroy(&locks[i]);
            }
        }
        if (call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&after, sizeof(after), WIT_MEMORY_INFO_VERSION) != WIT_STATUS_OK ||
            before.OwnedBytes != after.OwnedBytes ||
            before.PhysicalAvailableBytes != after.PhysicalAvailableBytes ||
            before.ReservedBytes != after.ReservedBytes ||
            !all_events_available()) {
            return 334;
        }
        return WIT_TEST_EXIT_CODE;
    }
    if (mode == WIT_GC_TEST_CRST) {
        CrstStatic lock;
        if (!lock.InitNoThrow(CrstGcEvent)) {
            return 340;
        }
        {
            CrstHolder first(&lock);
            CrstHolderWithState second(&lock, false);
            second.Acquire();
            second.Acquire();
            second.Release();
            second.Release();
            second.Acquire();
        }
        lock.Destroy();
        Crst constructed(CrstThreadStore, CRST_REENTRANCY);
        constructed.Enter();
        constructed.Enter();
        constructed.Leave();
        constructed.Leave();
        constructed.Destroy();
        return WIT_TEST_EXIT_CODE;
    }
    if (mode >= WIT_GC_TEST_MUTEX_OWNER_FAIL && mode <= WIT_GC_TEST_MUTEX_EVENT_FAIL) {
        minipal_mutex lock;
        WitU64 worker;
        shared = &lock;
        if (!minipal_mutex_init(&lock)) {
            return 350;
        }
        *(WitU64 *)WIT_GC_INFO_REPORT = mode;
        if (mode == WIT_GC_TEST_MUTEX_COPY_FAIL) {
            minipal_mutex copy = lock;
            minipal_mutex_enter(&copy);
            return 351;
        }
        minipal_mutex_enter(&lock);
        if (mode == WIT_GC_TEST_MUTEX_DESTROY_FAIL) {
            minipal_mutex_destroy(&lock);
            return 352;
        }
        if (mode == WIT_GC_TEST_MUTEX_EVENT_FAIL) {
            WitU64 event;
            for (size_t i = 0; i < 4; ++i) {
                if (call(WIT_CALL_EVENT_CREATE, 0, 0, 0, &event) != WIT_STATUS_OK) {
                    return 353;
                }
            }
            if (!spawn(exhausted_waiter, 0, &worker)) {
                return 354;
            }
        } else if (!spawn(wrong_owner, 0, &worker)) {
            return 355;
        }
        (void)join(worker);
        return 356;
    }
    return 399;
}
