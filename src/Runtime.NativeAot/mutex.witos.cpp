#include <minipal/mutex.h>
extern "C" {
#include "bootstrap.h"
}

/* Preserve the upstream object declaration. The object address, not Windows
 * CRITICAL_SECTION fields, identifies a private bounded registry entry. */
struct MutexState {
    minipal_mutex *Object;
    WitU64 Owner;
    WitU64 Event;
    WitU32 Depth;
    WitU32 Waiters; // Includes a signaled waiter until it reacquires the gate.
};

static MutexState slots[16];
static volatile WitU32 gate;

static WIT_NORETURN void fatal()
{
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static void lock()
{
    wit_native_lock(&gate);
}

static void unlock()
{
    wit_native_unlock(&gate);
}

static MutexState *find(minipal_mutex *mutex)
{
    if (mutex) {
        for (size_t i = 0; i < 16; ++i) {
            if (slots[i].Object == mutex) {
                return &slots[i];
            }
        }
    }
    return nullptr;
}

static void clear(minipal_mutex *mutex)
{
    for (size_t i = 0; i < sizeof(*mutex); ++i) {
        ((volatile unsigned char *)mutex)[i] = 0;
    }
}

extern "C" bool minipal_mutex_init(minipal_mutex *mutex)
{
    if (!mutex) {
        return false;
    }
    lock();
    if (find(mutex)) {
        unlock();
        return false;
    }
    for (size_t i = 0; i < 16; ++i) {
        if (slots[i].Object) {
            continue;
        }
        clear(mutex);
        slots[i].Owner = slots[i].Event = 0;
        slots[i].Depth = slots[i].Waiters = 0;
        slots[i].Object = mutex;
        unlock();
        return true;
    }
    unlock();
    return false;
}

extern "C" void minipal_mutex_destroy(minipal_mutex *mutex)
{
    lock();
    MutexState *state = find(mutex);
    if (!state || state->Owner || state->Depth || state->Waiters) {
        fatal();
    }
    if (state->Event && wit_native_call(WIT_CALL_CLOSE, state->Event, 0, 0, nullptr) != WIT_STATUS_OK) {
        fatal();
    }
    state->Event = 0;
    state->Object = nullptr;
    clear(mutex);
    unlock();
}

extern "C" void minipal_mutex_enter(minipal_mutex *mutex)
{
    const WitU64 self = wit_native_thread_identity();
    lock();
    for (;;) {
        MutexState *state = find(mutex);
        if (!state) {
            fatal();
        }
        if (!state->Owner) {
            state->Owner = self;
            state->Depth = 1;
            unlock();
            return;
        }
        if (state->Owner == self) {
            if (state->Depth == 0xFFFFFFFFU) {
                fatal();
            }
            ++state->Depth;
            unlock();
            return;
        }
        if (!state->Event) {
            WitU64 event = 0;
            // Auto reset retains a wake even if release happens before parking.
            if (wit_native_call(WIT_CALL_EVENT_CREATE, 0, 0, 0, &event) != WIT_STATUS_OK) {
                fatal();
            }
            state->Event = event;
        }
        if (state->Waiters == 0xFFFFFFFFU) {
            fatal();
        }
        ++state->Waiters;
        const WitU64 event = state->Event;
        unlock();
        const WitU64 status = wit_native_call(WIT_CALL_EVENT_WAIT_UNTIL, event, WIT_WAIT_INFINITE, 0, nullptr);
        if (status != WIT_STATUS_OK) {
            fatal();
        }
        lock();
        state = find(mutex);
        if (!state || state->Event != event || !state->Waiters) {
            fatal();
        }
        --state->Waiters;
        // Keep the gate through retry/acquisition. Destroy cannot reuse this
        // entry between decrementing Waiters and re-registering or owning it.
    }
}

extern "C" void minipal_mutex_leave(minipal_mutex *mutex)
{
    const WitU64 self = wit_native_thread_identity();
    lock();
    MutexState *state = find(mutex);
    if (!state || state->Owner != self || !state->Depth) {
        fatal();
    }
    if (--state->Depth == 0) {
        state->Owner = 0;
        if (state->Waiters && wit_native_call(WIT_CALL_EVENT_SET, state->Event, 0, 0, nullptr) != WIT_STATUS_OK) {
            fatal();
        }
    }
    unlock();
}
