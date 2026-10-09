#include "user.h"
#include "witos/platform.h"

/* Waits of one process: the one object wait, sleeps and their monotonic deadlines. Every check, park and completion
 * runs with interrupts disabled; the architecture scheduler owns the waiters' frames. */

void wit_user_wait_complete(WitUserThread *thread, WitU64 status, WitU64 index)
{
    wit_arch_frame_set_result(thread->Context, status, index);
    thread->WaitKind = WitWaitNone;
    thread->WaitHandle = 0;
    thread->WaitCount = 0;
    thread->WaitAll = 0;
    for (WitU32 i = 0; i < WIT_WAIT_ANY_CAPACITY; ++i) {
        thread->WaitHandles[i] = 0;
    }
    thread->Deadline = WIT_WAIT_INFINITE;
    thread->State = WitThreadReady;
}

/* An activation ends the wait or sleep its target is parked in (RFC 0011 section 7.5): the target is made Ready with
 * INTERRUPTED, which it sees after the handler continues the interrupted context. A completed wait is never
 * overwritten; a running or ready target has nothing to interrupt. */
void wit_user_wait_interrupt(WitUserProcess *process, WitUserThread *thread)
{
    if (thread->State != WitThreadWaiting || (thread->WaitKind != WitWaitObjects && thread->WaitKind != WitWaitSleep)) {
        return;
    }
    ++process->WaitInterruptions;
    wit_user_wait_complete(thread, WIT_STATUS_INTERRUPTED, 0);
}

/* Expired deadlines complete before any later signal or close, so a timeout is never overwritten. */
void wit_user_wait_expire(WitUserProcess *process, WitU64 now)
{
    for (WitU32 i = 0; i < WIT_PROCESS_THREAD_CAPACITY; ++i) {
        WitUserThread *thread = &process->Threads[i];
        if (thread->State != WitThreadWaiting ||
            (thread->WaitKind != WitWaitObjects && thread->WaitKind != WitWaitSleep) ||
            thread->Deadline == WIT_WAIT_INFINITE ||
            thread->Deadline > now) {
            continue;
        }
        if (thread->WaitKind == WitWaitObjects) {
            ++process->WaitTimeouts;
            wit_user_wait_complete(thread, WIT_STATUS_TIMED_OUT, 0);
        } else {
            wit_user_wait_complete(thread, WIT_STATUS_OK, 0);
        }
    }
}

static int valid_deadline(WitU64 deadline)
{
    return deadline <= WIT_MONOTONIC_MAX || deadline == WIT_WAIT_INFINITE;
}

WitU64 wit_user_sleep_until(WitUserProcess *process, WitU64 deadline, WitU64 now)
{
    WitUserThread *thread = &process->Threads[process->CurrentThread];
    if (!valid_deadline(deadline)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (deadline <= now) {
        return WIT_STATUS_OK;
    }
    thread->WaitKind = WitWaitSleep;
    thread->WaitHandle = 0;
    thread->WaitCount = 0;
    thread->WaitAll = 0;
    for (WitU32 i = 0; i < WIT_WAIT_ANY_CAPACITY; ++i) {
        thread->WaitHandles[i] = 0;
    }
    thread->Deadline = deadline;
    thread->State = WitThreadWaiting;
    return WIT_STATUS_OK;
}

/* A wait set with at least one event or endpoint is accounted as an object wait; a set of thread handles alone is a
 * join. */
static int waits_on_event(WitUserProcess *process, const WitU64 *handles, WitU32 count)
{
    for (WitU32 i = 0; i < count; ++i) {
        if (wit_handle_check(&process->Handles, handles[i], WIT_HANDLE_EVENT, 0) == WIT_STATUS_OK ||
            wit_handle_check(&process->Handles, handles[i], WIT_HANDLE_CHANNEL_ENDPOINT, 0) == WIT_STATUS_OK) {
            return 1;
        }
    }
    return 0;
}

/* The one wait, after its request was copied and validated: consume a ready object now, time out at a past deadline,
 * or park the current thread on the copied handles. */
WitU64 wit_user_wait_objects(
    WitUserProcess *process, const WitU64 *handles, WitU32 count, int all, WitU64 deadline, WitU64 now, WitU64 *winner)
{
    WitUserThread *thread = &process->Threads[process->CurrentThread];
    *winner = 0;
    if (!count || count > WIT_WAIT_ANY_CAPACITY || !valid_deadline(deadline)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 status = wit_user_objects_poll(process, handles, count, all, 1, winner);
    if (status != WIT_STATUS_TIMED_OUT) {
        return status;
    }
    if (deadline != WIT_WAIT_INFINITE && deadline <= now) {
        ++process->WaitTimeouts;
        return WIT_STATUS_TIMED_OUT;
    }
    if (process->NextWaitOrder == ~0ULL) {
        return WIT_STATUS_NO_MEMORY;
    }
    for (WitU32 i = 0; i < WIT_WAIT_ANY_CAPACITY; ++i) {
        thread->WaitHandles[i] = i < count ? handles[i] : 0;
    }
    thread->WaitCount = count;
    thread->WaitHandle = 0;
    thread->WaitKind = WitWaitObjects;
    thread->WaitAll = all;
    thread->Deadline = deadline;
    thread->WaitOrder = ++process->NextWaitOrder;
    thread->State = WitThreadWaiting;
    if (waits_on_event(process, handles, count)) {
        ++process->EventParks;
    } else {
        ++process->ThreadWaitParks;
    }
    return WIT_STATUS_OK;
}

static int waits_on(const WitUserThread *thread, WitU64 handle)
{
    if (thread->State != WitThreadWaiting || thread->WaitKind != WitWaitObjects) {
        return 0;
    }
    for (WitU32 i = 0; i < thread->WaitCount; ++i) {
        if (thread->WaitHandles[i] == handle) {
            return 1;
        }
    }
    return 0;
}

/* Completes parked waits in their arrival order while an object they wait for is ready. */
void wit_user_wait_objects_changed(WitUserProcess *process)
{
    for (;;) {
        WitUserThread *first = 0;
        for (WitU32 i = 0; i < WIT_PROCESS_THREAD_CAPACITY; ++i) {
            WitUserThread *thread = &process->Threads[i];
            WitU64 winner = 0;
            if (thread->State != WitThreadWaiting || thread->WaitKind != WitWaitObjects) {
                continue;
            }
            const WitU64 status =
                wit_user_objects_poll(process, thread->WaitHandles, thread->WaitCount, thread->WaitAll, 0, &winner);
            if (status != WIT_STATUS_OK && status != WIT_STATUS_TIMED_OUT) {
                wit_panic("Parked wait lost a validated object");
            }
            if (status == WIT_STATUS_OK && (!first || thread->WaitOrder < first->WaitOrder)) {
                first = thread;
            }
        }
        if (!first) {
            break;
        }
        WitU64 winner = 0;
        if (wit_user_objects_poll(process, first->WaitHandles, first->WaitCount, first->WaitAll, 1, &winner) !=
            WIT_STATUS_OK) {
            wit_panic("Ready object wait changed under serialization");
        }
        if (waits_on_event(process, first->WaitHandles, first->WaitCount)) {
            ++process->EventWakes;
        } else {
            ++process->ThreadWaitWakes;
        }
        wit_user_wait_complete(first, WIT_STATUS_OK, winner);
    }
}

static void signal(WitUserProcess *process, WitEvent *event)
{
    event->Signaled = 1;
    wit_user_wait_objects_changed(process);
}

/* A kernel-side signal of an event by its object number: an interrupt binding's (K3.2). */
void wit_user_event_signal_object(WitUserProcess *process, WitU64 object)
{
    WitEvent *event = wit_event_lookup(&process->Events, object);
    if (!event) {
        wit_panic("Signalled event object does not exist");
    }
    signal(process, event);
}

void wit_user_wait_handle_closed(WitUserProcess *process, WitU64 handle)
{
    for (WitU32 i = 0; i < WIT_PROCESS_THREAD_CAPACITY; ++i) {
        if (waits_on(&process->Threads[i], handle)) {
            wit_user_wait_complete(&process->Threads[i], WIT_STATUS_CLOSED, 0);
            ++process->WaitCloses;
        }
    }
}

WitU64 wit_user_event_set(WitUserProcess *process, WitU64 handle)
{
    WitEvent *event;
    const WitU64 status = wit_event_get(&process->Events, &process->Handles, handle, WIT_RIGHT_SIGNAL, &event);
    if (status == WIT_STATUS_OK) {
        signal(process, event);
    }
    return status;
}

WitU64 wit_user_event_notify(WitUserProcess *process, WitU64 handle, int signaled)
{
    WitEvent *event;
    const WitU64 status = wit_event_get(&process->Events, &process->Handles, handle, WIT_RIGHT_WAIT, &event);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!event->ManualReset) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (signaled) {
        signal(process, event);
    } else {
        event->Signaled = 0;
    }
    return WIT_STATUS_OK;
}

WitU64 wit_user_event_reset(WitUserProcess *process, WitU64 handle)
{
    WitEvent *event;
    const WitU64 status = wit_event_get(&process->Events, &process->Handles, handle, WIT_RIGHT_SIGNAL, &event);
    if (status == WIT_STATUS_OK) {
        event->Signaled = 0;
    }
    return status;
}

WitU64 wit_user_event_close(WitUserProcess *process, WitU64 handle)
{
    WitEvent *event;
    const WitU64 status = wit_event_get(&process->Events, &process->Handles, handle, 0, &event);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    wit_user_wait_handle_closed(process, handle);
    return wit_event_remove(&process->Events, &process->Handles, handle);
}
