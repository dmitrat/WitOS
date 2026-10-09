#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <time.h>

/* The futex equivalent over kernel events (plan step S2, RFC 0011 section 9.3). musl waits on a word with
 * FUTEX_WAIT while it still holds a value and wakes the word's waiters with FUTEX_WAKE. The kernel waits on
 * events, not on words, so the library keeps the word: a waiter takes a slot with its own auto-reset event under
 * one lock, checks the word there and parks on the event; a wake finds the slots of that word under the same lock,
 * marks them woken and sets their events, and a requeue renames their word. A wake that comes between the check and
 * the park leaves the event's token in place, so the park returns at once: no wake is lost, and no waiter of another
 * word is woken. The one exception is the word the kernel's exit request clears (musl's thread list lock, thread.c):
 * the kernel sets one event after the thread is gone, so that word's waiters share one event and wake one another
 * on, as musl's list lock protocol does on Linux, where the kernel also wakes one. Seventeen events in all, within
 * the system layer's quota of thirty-two (K5.3). */

#define WAITERS 16U /* slots: as many as the kernel's threads of a process, each of which waits on one word */
#define FUTEX_WAIT 0
#define FUTEX_WAKE 1
#define FUTEX_REQUEUE 3
#define FUTEX_CMP_REQUEUE 4
#define FUTEX_WAIT_BITSET 9
#define FUTEX_WAKE_BITSET 10
#define FUTEX_PRIVATE 128
#define FUTEX_CLOCK_REALTIME 256

typedef struct Waiter {
    volatile int *Address; /* the word waited on; zero once woken */
    WitU64 Event; /* the slot's auto-reset event, created on first use and kept */
    int Busy; /* a thread holds the slot */
} Waiter;

static Waiter waiters[WAITERS];
static WitU64 exit_event; /* the one event of the exit word's waiters */
static volatile int lock;

/* A library lock is held with the thread's signals held (signal.c): a handler that interrupted the holder and
 * posted a semaphore would otherwise spin on the lock its own thread holds. */
void __wit_lock(volatile int *word)
{
    WitU64 result = 0;
    __wit_signal_hold_enter();
    while (__atomic_exchange_n(word, 1, __ATOMIC_ACQUIRE)) {
        wit_syscall(WIT_CALL_THREAD_YIELD, 0, 0, 0, &result); /* the holder runs on the one processor */
    }
}

void __wit_unlock(volatile int *word)
{
    __atomic_store_n(word, 0, __ATOMIC_RELEASE);
    __wit_signal_hold_leave();
}

static void acquire(void)
{
    __wit_lock(&lock);
}

static void release(void)
{
    __wit_unlock(&lock);
}

static WitU64 create_event(void)
{
    WitU64 handle = 0;
    return wit_syscall(WIT_CALL_EVENT_CREATE, 0, 0, 0, &handle) == WIT_STATUS_OK ? handle : 0;
}

/* The event the exit request sets: the one all waiters of the exit word park on. */
WitU64 __wit_futex_exit_event(void)
{
    acquire();
    if (!exit_event) {
        exit_event = create_event();
    }
    const WitU64 handle = exit_event;
    release();
    return handle;
}

static long monotonic(WitU64 *counts, WitU64 *frequency)
{
    if (wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, counts) != WIT_STATUS_OK ||
        wit_syscall(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, frequency) != WIT_STATUS_OK ||
        !*frequency) {
        return -EINVAL;
    }
    return 0;
}

static WitU64 counts_of(const struct timespec *ts, WitU64 frequency)
{
    return (WitU64)ts->tv_sec * frequency + (WitU64)ts->tv_nsec * frequency / 1000000000ULL;
}

/* The absolute monotonic deadline of a futex timeout: relative for FUTEX_WAIT, absolute for the bitset form, on the
 * real-time clock when asked (converted through the kernel's UTC). */
static long deadline_of(int operation, const struct timespec *timeout, WitU64 *deadline)
{
    WitU64 now = 0, frequency = 0;
    *deadline = WIT_WAIT_INFINITE;
    if (!timeout) {
        return 0;
    }
    if (timeout->tv_nsec < 0 || timeout->tv_nsec >= 1000000000L || timeout->tv_sec < 0) {
        return -EINVAL;
    }
    const long status = monotonic(&now, &frequency);
    if (status < 0) {
        return status;
    }
    const int absolute = (operation & 0x7F) == FUTEX_WAIT_BITSET;
    if (!absolute) {
        *deadline = now + counts_of(timeout, frequency);
        return 0;
    }
    if (operation & FUTEX_CLOCK_REALTIME) {
        WitU64 utc = 0;
        if (wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_UTC, 0, 0, &utc) != WIT_STATUS_OK) {
            return -EINVAL;
        }
        const WitU64 target = (WitU64)timeout->tv_sec * 1000000000ULL + (WitU64)timeout->tv_nsec;
        *deadline = target <= utc ? now : now + (target - utc) * frequency / 1000000000ULL;
        return 0;
    }
    *deadline = counts_of(timeout, frequency);
    return 0;
}

static WitU64 object_wait(WitU64 handle, WitU64 deadline)
{
    WitUserWaitRequest request;
    WitU64 result = 0;
    request.Version = WIT_WAIT_OBJECTS_VERSION;
    request.Size = sizeof(request);
    request.Handles = (WitU64)&handle;
    request.Count = 1;
    request.Flags = 0;
    request.Deadline = deadline;
    return wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&request, sizeof(request), 0, &result);
}

static long result_of(WitU64 status)
{
    if (status == WIT_STATUS_OK) {
        return 0;
    }
    return status == WIT_STATUS_TIMED_OUT ? -ETIMEDOUT : __wit_errno(status);
}

/* The exit word: its waiters share the event the kernel sets; the token of a set with nobody parked wakes the next
 * parker, and a woken waiter wakes the next one itself (musl's __tl_sync and __tl_unlock). */
/* A cancellation point about to park with its cancel word set does not park: musl cancels on the EINTR (thread.c). */
static int cancelled(void)
{
    return __wit_cancel_requested();
}

static long wait_exit_word(volatile int *address, int value, WitU64 deadline)
{
    const WitU64 handle = __wit_futex_exit_event();
    if (!handle) {
        return -ENOMEM;
    }
    if (*address != value) {
        return -EAGAIN;
    }
    if (cancelled()) {
        return -EINTR;
    }
    return result_of(object_wait(handle, deadline));
}

static long wait_slot(volatile int *address, int value, WitU64 deadline)
{
    WitU64 result = 0;
    Waiter *slot = 0;
    acquire();
    if (*address != value) {
        release();
        return -EAGAIN;
    }
    for (unsigned i = 0; i < WAITERS && !slot; ++i) {
        if (!waiters[i].Busy) {
            slot = &waiters[i];
        }
    }
    if (!slot || (!slot->Event && !(slot->Event = create_event()))) {
        release();
        return -ENOMEM;
    }
    if (cancelled()) {
        release();
        return -EINTR;
    }
    slot->Busy = 1;
    slot->Address = address;
    const WitU64 event = slot->Event;
    release();
    const WitU64 status = object_wait(event, deadline);
    acquire();
    const int woken = slot->Address == 0;
    slot->Address = 0;
    if (status != WIT_STATUS_OK) {
        /* A wake after the deadline left its token on the event: it goes before the slot serves another waiter. */
        wit_syscall(WIT_CALL_EVENT_RESET, event, 0, 0, &result);
    }
    slot->Busy = 0;
    release();
    return woken ? 0 : result_of(status);
}

static long wait(volatile int *address, int value, int operation, const struct timespec *timeout)
{
    WitU64 deadline;
    const long converted = deadline_of(operation, timeout, &deadline);
    if (converted < 0) {
        return converted;
    }
    return __wit_is_exit_word(address) ? wait_exit_word(address, value, deadline) : wait_slot(address, value, deadline);
}

/* Wakes up to count waiters of the word and moves up to requeue_count more to the second word (zero: none). */
static long wake(volatile int *address, int count, volatile int *requeue, int requeue_count)
{
    WitU64 result = 0;
    long woken = 0;
    int moved = 0;
    if (count <= 0 && (!requeue || requeue_count <= 0)) {
        return 0;
    }
    if (__wit_is_exit_word(address)) {
        const WitU64 handle = __wit_futex_exit_event();
        if (!handle) {
            return -ENOMEM;
        }
        for (; woken < count && woken < (long)WAITERS; ++woken) {
            if (wit_syscall(WIT_CALL_EVENT_SET, handle, 0, 0, &result) != WIT_STATUS_OK) {
                break;
            }
        }
        return woken;
    }
    acquire();
    for (unsigned i = 0; i < WAITERS; ++i) {
        Waiter *slot = &waiters[i];
        if (!slot->Busy || slot->Address != address) {
            continue;
        }
        if (woken < count) {
            slot->Address = 0;
            if (wit_syscall(WIT_CALL_EVENT_SET, slot->Event, 0, 0, &result) != WIT_STATUS_OK) {
                break;
            }
            ++woken;
        } else if (requeue && moved < requeue_count) {
            slot->Address = requeue;
            ++moved;
        }
    }
    release();
    return woken;
}

long __wit_futex(
    volatile int *address, int operation, int value, const struct timespec *timeout, volatile int *second, int third)
{
    switch (operation & 0x7F) {
    case FUTEX_WAIT:
    case FUTEX_WAIT_BITSET:
        return wait(address, value, operation, timeout);
    case FUTEX_WAKE:
    case FUTEX_WAKE_BITSET:
        return wake(address, value, 0, 0);
    case FUTEX_REQUEUE:
    case FUTEX_CMP_REQUEUE:
        if ((operation & 0x7F) == FUTEX_CMP_REQUEUE && *address != third) {
            return -EAGAIN;
        }
        return wake(address, value, second, (int)(WitU64)timeout); /* the requeue count travels in the timeout slot */
    default:
        return -ENOSYS;
    }
}
