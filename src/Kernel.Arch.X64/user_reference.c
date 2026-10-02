#include "user.h"
#include "witos/platform.h"

void wit_user_references_initialize(WitUserProcess *p)
{
    for (WitU32 i = 0; i < WIT_RUNTIME_HANDLE_CAPACITY; ++i) {
        WitUserThreadReference *r = &p->ThreadReferences[i];
        r->Handle = 0;
        r->ThreadId = 0;
        r->ExitCode = 0;
        r->Rights = 0;
        r->Exited = 0;
    }
}

static WitUserThreadReference *lookup(WitUserProcess *p, WitU64 handle)
{
    for (WitU32 i = 0; i < p->Handles.Limit; ++i) {
        if (p->ThreadReferences[i].Handle == handle) {
            return &p->ThreadReferences[i];
        }
    }
    return 0;
}

WitU64 wit_user_reference_target(WitUserProcess *p, WitU64 handle, WitU32 rights, WitUserThread **target)
{
    *target = 0;
    if (handle == WIT_THREAD_REFERENCE_CURRENT) {
        *target = &p->Threads[p->CurrentThread];
        return WIT_STATUS_OK;
    }
    const WitU64 status = wit_handle_check(&p->Handles, handle, WIT_HANDLE_THREAD_REFERENCE, rights);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const WitUserThreadReference *r = lookup(p, handle);
    if (!r) {
        wit_panic("Missing target thread reference");
    }
    if (r->Exited) {
        return WIT_STATUS_CLOSED;
    }
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (p->Threads[i].State != WitThreadEmpty &&
            p->Threads[i].State != WitThreadExited &&
            p->Threads[i].Handle == r->ThreadId) {
            *target = &p->Threads[i];
            return WIT_STATUS_OK;
        }
    }
    wit_panic("Live reference has no target");
}

WitU64 wit_user_reference_signaled(WitUserProcess *p, WitU64 handle, int *signaled)
{
    *signaled = 0;
    const WitU64 status = wit_handle_check(&p->Handles, handle, WIT_HANDLE_THREAD_REFERENCE, WIT_THREAD_REFERENCE_WAIT);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const WitUserThreadReference *r = lookup(p, handle);
    if (!r) {
        wit_panic("Missing waited thread reference");
    }
    *signaled = r->Exited != 0;
    return WIT_STATUS_OK;
}

void wit_user_references_exit(WitUserProcess *p, WitU64 identity, WitU64 code)
{
    for (WitU32 i = 0; i < p->Handles.Limit; ++i) {
        WitUserThreadReference *r = &p->ThreadReferences[i];
        if (r->Handle && r->ThreadId == identity) {
            r->ExitCode = code;
            r->Exited = 1;
        }
    }
    wit_user_wait_objects_changed(p);
}

WitU64 wit_user_reference_duplicate(WitUserProcess *p, WitU64 source, WitU64 output, WitU64 requested)
{
    WitUserThreadReference snapshot;
    WitUserThreadReference *destination = 0;
    WitU64 status, handle;
    if (requested & ~(WitU64)WIT_THREAD_REFERENCE_ALL) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (source == WIT_THREAD_REFERENCE_CURRENT) {
        const WitUserThread *t = &p->Threads[p->CurrentThread];
        snapshot.ThreadId = t->Handle;
        snapshot.ExitCode = 0;
        snapshot.Exited = 0;
        snapshot.Rights = WIT_THREAD_REFERENCE_ALL;
    } else {
        status = wit_handle_check(&p->Handles, source, WIT_HANDLE_THREAD_REFERENCE, 0);
        if (status != WIT_STATUS_OK) {
            return status;
        }
        const WitUserThreadReference *original = lookup(p, source);
        if (!original) {
            wit_panic("Missing thread reference record");
        }
        snapshot = *original;
    }
    const WitU32 rights = requested ? (WitU32)requested : snapshot.Rights;
    if ((rights & snapshot.Rights) != rights) {
        return WIT_STATUS_DENIED;
    }
    if (!wit_user_buffer_writable(&p->Space, output, sizeof(handle))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    for (WitU32 i = 0; i < p->Handles.Limit; ++i) {
        if (!p->ThreadReferences[i].Handle) {
            destination = &p->ThreadReferences[i];
            break;
        }
    }
    if (!destination) {
        return WIT_STATUS_NO_MEMORY;
    }
    handle = wit_handle_grant(&p->Handles, WIT_HANDLE_THREAD_REFERENCE, rights);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    destination->Handle = handle;
    destination->ThreadId = snapshot.ThreadId;
    destination->ExitCode = snapshot.ExitCode;
    destination->Exited = snapshot.Exited;
    destination->Rights = rights;
    if (!wit_user_copy_to(&p->Space, output, (const WitU8 *)&handle, sizeof(handle))) {
        destination->Handle = 0;
        if (wit_handle_close(&p->Handles, handle) != WIT_STATUS_OK) {
            wit_panic("Thread reference rollback failed");
        }
        return WIT_STATUS_BAD_ADDRESS;
    }
    return WIT_STATUS_OK;
}

WitU64 wit_user_reference_query(WitUserProcess *p, WitU64 handle, WitU64 output, WitU64 size)
{
    WitThreadReferenceInfo info;
    if (size != sizeof(info)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 status =
        wit_handle_check(&p->Handles, handle, WIT_HANDLE_THREAD_REFERENCE, WIT_THREAD_REFERENCE_QUERY);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const WitUserThreadReference *reference = lookup(p, handle);
    if (!reference) {
        wit_panic("Missing queried thread reference");
    }
    info.Version = WIT_THREAD_REFERENCE_VERSION;
    info.Size = sizeof(info);
    info.ThreadId = reference->ThreadId;
    info.ExitCode = reference->ExitCode;
    info.Rights = reference->Rights;
    info.StackLow = 0;
    info.StackHigh = 0;
    info.SuspendCount = 0;
    info.Reserved = 0;
    info.State = reference->Exited ? WIT_THREAD_REFERENCE_EXITED : WIT_THREAD_REFERENCE_LIVE;
    if (!reference->Exited) {
        const WitUserThread *target = 0;
        for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
            if (p->Threads[i].State != WitThreadEmpty && p->Threads[i].Handle == reference->ThreadId) {
                target = &p->Threads[i];
                break;
            }
        }
        if (!target || target->State == WitThreadExited) {
            wit_panic("Live thread reference lost its target");
        }
        if (target->State == WitThreadWaiting) {
            info.State = WIT_THREAD_REFERENCE_WAITING;
        }
        info.SuspendCount = target->SuspendCount;
        if (target->SuspendCount) {
            info.State = WIT_THREAD_REFERENCE_SUSPENDED;
        }
        info.StackLow = target->StackBottom;
        info.StackHigh = target->StackTop;
    }
    return wit_user_copy_to(&p->Space, output, (const WitU8 *)&info, sizeof(info)) ? WIT_STATUS_OK
                                                                                   : WIT_STATUS_BAD_ADDRESS;
}

WitU64 wit_user_reference_close(WitUserProcess *p, WitU64 handle)
{
    const WitU64 status = wit_handle_check(&p->Handles, handle, WIT_HANDLE_THREAD_REFERENCE, 0);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    WitUserThreadReference *r = lookup(p, handle);
    if (!r) {
        wit_panic("Missing closed thread reference");
    }
    wit_user_wait_handle_closed(p, handle);
    const WitU64 result = wit_handle_close(&p->Handles, handle);
    if (result != WIT_STATUS_OK) {
        wit_panic("Thread reference close failed");
    }
    r->Handle = 0;
    r->ThreadId = 0;
    r->ExitCode = 0;
    r->Rights = 0;
    r->Exited = 0;
    return WIT_STATUS_OK;
}
