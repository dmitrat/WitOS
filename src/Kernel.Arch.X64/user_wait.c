#include "user.h"

static void wake(WitUserThread *thread, WitU64 status)
{
    thread->Context->Rax = status;
    thread->Context->Rdx = 0;
    thread->WaitKind = WitWaitNone;
    thread->WaitHandle = 0;
    thread->Deadline = WIT_WAIT_INFINITE;
    thread->MonotonicWait = 0;
    thread->State = WitThreadReady;
}

static void expire(WitUserProcess *process, WitU64 now, WitU32 monotonic)
{
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        WitUserThread *thread = &process->Threads[i];
        if (thread->State != WitThreadWaiting || thread->MonotonicWait != monotonic ||
            (thread->WaitKind != WitWaitEvent && thread->WaitKind != WitWaitSleep) ||
            thread->Deadline == WIT_WAIT_INFINITE || thread->Deadline > now) continue;
        if (thread->WaitKind == WitWaitEvent) {
            ++process->WaitTimeouts;
            wake(thread, WIT_STATUS_TIMED_OUT);
        } else wake(thread, WIT_STATUS_OK);
    }
}

void wit_user_wait_expire(WitUserProcess *process, WitU64 now) { expire(process, now, 0); }
void wit_user_wait_expire_time(WitUserProcess *process, WitU64 now) { expire(process, now, 1); }

static WitU64 sleep_at(WitUserProcess *process, WitU64 deadline, WitU64 now, WitU32 monotonic)
{
    WitUserThread *thread = &process->Threads[process->CurrentThread];
    if (!monotonic && deadline == WIT_WAIT_INFINITE) return WIT_STATUS_INVALID_ARGUMENT;
    if (deadline <= now) return WIT_STATUS_OK;
    thread->WaitKind = WitWaitSleep;
    thread->WaitHandle = 0;
    thread->Deadline = deadline;
    thread->MonotonicWait = monotonic;
    thread->State = WitThreadWaiting;
    return WIT_STATUS_OK;
}

static WitU64 event_at(WitUserProcess *process, WitU64 handle, WitU64 deadline, WitU64 now, WitU32 monotonic)
{
    WitEvent *event;
    WitUserThread *thread = &process->Threads[process->CurrentThread];
    WitU64 status = wit_event_get(&process->Events, &process->Handles, handle, WIT_RIGHT_WAIT, &event);
    if (status != WIT_STATUS_OK) return status;
    /* A stored signal wins even for a poll/past deadline. */
    if (wit_event_consume(event)) return WIT_STATUS_OK;
    if (deadline != WIT_WAIT_INFINITE && deadline <= now) {
        ++process->WaitTimeouts;
        return WIT_STATUS_TIMED_OUT;
    }
    if (process->NextWaitOrder == ~0ULL) return WIT_STATUS_NO_MEMORY;
    thread->WaitKind = WitWaitEvent;
    thread->WaitHandle = handle;
    thread->Deadline = deadline;
    thread->MonotonicWait = monotonic;
    thread->WaitOrder = ++process->NextWaitOrder;
    thread->State = WitThreadWaiting;
    ++process->EventParks;
    return WIT_STATUS_OK;
}

WitU64 wit_user_sleep(WitUserProcess *process, WitU64 deadline, WitU64 now)
{
    return sleep_at(process, deadline, now, 0);
}
WitU64 wit_user_event_wait(WitUserProcess *process, WitU64 handle, WitU64 deadline, WitU64 now)
{
    return event_at(process, handle, deadline, now, 0);
}
static int valid_deadline(WitU64 deadline)
{
    return deadline <= WIT_MONOTONIC_MAX || deadline == WIT_WAIT_INFINITE;
}
WitU64 wit_user_sleep_until(WitUserProcess *process, WitU64 deadline, WitU64 now)
{
    if (!valid_deadline(deadline)) return WIT_STATUS_INVALID_ARGUMENT;
    return sleep_at(process, deadline, now, 1);
}
WitU64 wit_user_event_wait_until(WitUserProcess *process, WitU64 handle, WitU64 deadline, WitU64 now)
{
    if (!valid_deadline(deadline)) return WIT_STATUS_INVALID_ARGUMENT;
    return event_at(process, handle, deadline, now, 1);
}

WitU64 wit_user_event_set(WitUserProcess *process, WitU64 handle)
{
    WitEvent *event;
    WitU64 status = wit_event_get(&process->Events, &process->Handles, handle, WIT_RIGHT_SIGNAL, &event);
    if (status != WIT_STATUS_OK) return status;
    event->Signaled = 1;
    for (;;) {
        WitUserThread *first = 0;
        for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
            WitUserThread *thread = &process->Threads[i];
            if (thread->State == WitThreadWaiting && thread->WaitKind == WitWaitEvent &&
                thread->WaitHandle == handle && (!first || thread->WaitOrder < first->WaitOrder))
                first = thread;
        }
        if (!first) break;
        (void)wit_event_consume(event);
        wake(first, WIT_STATUS_OK);
        ++process->EventWakes;
        if (!event->ManualReset) break;
    }
    return WIT_STATUS_OK;
}

WitU64 wit_user_event_reset(WitUserProcess *process, WitU64 handle)
{
    WitEvent *event;
    const WitU64 status = wit_event_get(&process->Events, &process->Handles, handle, WIT_RIGHT_SIGNAL, &event);
    if (status == WIT_STATUS_OK) event->Signaled = 0;
    return status;
}

WitU64 wit_user_event_close(WitUserProcess *process, WitU64 handle)
{
    WitEvent *event;
    const WitU64 status = wit_event_get(&process->Events, &process->Handles, handle, 0, &event);
    if (status != WIT_STATUS_OK) return status;
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        WitUserThread *thread = &process->Threads[i];
        if (thread->State == WitThreadWaiting && thread->WaitKind == WitWaitEvent &&
            thread->WaitHandle == handle) {
            wake(thread, WIT_STATUS_CLOSED);
            ++process->WaitCloses;
        }
    }
    return wit_event_remove(&process->Events, &process->Handles, handle);
}
