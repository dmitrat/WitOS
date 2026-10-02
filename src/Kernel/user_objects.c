#include "user.h"

/* All object validation/selection/consumption runs with IF disabled. */
WitU64 wit_user_objects_poll(
    WitUserProcess *p, const WitU64 *handles, WitU32 count, int all, int consume, WitU64 *winner)
{
    WitEvent *events[WIT_WAIT_ANY_CAPACITY];
    int ready[WIT_WAIT_ANY_CAPACITY];
    WitU32 first = count;
    *winner = 0;
    if (!count || count > WIT_WAIT_ANY_CAPACITY) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    for (WitU32 i = 0; i < count; ++i) {
        events[i] = 0;
        ready[i] = 0;
        WitU64 status = wit_event_get(&p->Events, &p->Handles, handles[i], WIT_RIGHT_WAIT, &events[i]);
        if (status == WIT_STATUS_WRONG_TYPE) {
            status = wit_user_reference_signaled(p, handles[i], &ready[i]);
        } else if (status == WIT_STATUS_OK) {
            ready[i] = events[i]->Signaled != 0;
        }
        if (status != WIT_STATUS_OK) {
            return status;
        }
        if (all) {
            for (WitU32 j = 0; j < i; ++j) {
                if (handles[j] == handles[i]) {
                    return WIT_STATUS_INVALID_ARGUMENT;
                }
            }
        }
        if (ready[i] && first == count) {
            first = i;
        }
    }
    if (all) {
        for (WitU32 i = 0; i < count; ++i) {
            if (!ready[i]) {
                return WIT_STATUS_TIMED_OUT;
            }
        }
    } else if (first == count) {
        return WIT_STATUS_TIMED_OUT;
    }
    if (consume) {
        if (all) {
            for (WitU32 i = 0; i < count; ++i) {
                if (events[i]) {
                    (void)wit_event_consume(events[i]);
                }
            }
        } else if (events[first]) {
            (void)wit_event_consume(events[first]);
        }
    }
    *winner = all ? 0 : first;
    return WIT_STATUS_OK;
}

WitU64 wit_user_object_wait(WitUserProcess *p, WitU64 address, WitU64 size, WitU64 reserved, WitU64 now, WitU64 *winner)
{
    WitUserWaitRequest request;
    WitU64 handles[WIT_WAIT_ANY_CAPACITY];
    WitUserThread *thread = &p->Threads[p->CurrentThread];
    *winner = 0;
    if (reserved || size != sizeof(request)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&p->Space, address, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_WAIT_OBJECTS_VERSION ||
        request.Size != sizeof(request) ||
        (request.Flags & ~(WIT_WAIT_OBJECTS_ALERTABLE | WIT_WAIT_OBJECTS_ALL))) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (!request.Count ||
        request.Count > WIT_WAIT_ANY_CAPACITY ||
        (request.Deadline > WIT_MONOTONIC_MAX && request.Deadline != WIT_WAIT_INFINITE)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&p->Space, request.Handles, (WitU8 *)handles, request.Count * sizeof(WitU64))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    const int all = (request.Flags & WIT_WAIT_OBJECTS_ALL) != 0;
    const WitU64 status = wit_user_objects_poll(p, handles, request.Count, all, 0, winner);
    if (status != WIT_STATUS_OK && status != WIT_STATUS_TIMED_OUT) {
        return status;
    }
    if ((request.Flags & WIT_WAIT_OBJECTS_ALERTABLE) && thread->ApcCount) {
        *winner = 0;
        return WIT_STATUS_APC_PENDING;
    }
    if (status == WIT_STATUS_OK) {
        return wit_user_objects_poll(p, handles, request.Count, all, 1, winner);
    }
    if (request.Deadline != WIT_WAIT_INFINITE && request.Deadline <= now) {
        ++p->WaitTimeouts;
        return WIT_STATUS_TIMED_OUT;
    }
    if (p->NextWaitOrder == ~0ULL) {
        return WIT_STATUS_NO_MEMORY;
    }
    for (WitU32 i = 0; i < WIT_WAIT_ANY_CAPACITY; ++i) {
        thread->WaitHandles[i] = i < request.Count ? handles[i] : 0;
    }
    thread->WaitCount = request.Count;
    thread->WaitHandle = 0;
    thread->WaitKind = WitWaitObjects;
    thread->WaitAll = all;
    thread->WaitAlertable = (request.Flags & WIT_WAIT_OBJECTS_ALERTABLE) != 0;
    thread->Deadline = request.Deadline;
    thread->MonotonicWait = 1;
    thread->WaitOrder = ++p->NextWaitOrder;
    thread->State = WitThreadWaiting;
    ++p->EventParks;
    return WIT_STATUS_OK;
}
