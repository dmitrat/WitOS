#include "user.h"
#include "witos/platform.h"

/* Thread handles. A record per handle keeps the thread's identity and, after its exit, the exit code; the thread's
 * own stack and TLS are reclaimed at its exit regardless of the handles. */

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

static WitUserThread *live_target(WitUserProcess *p, WitU64 identity)
{
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (p->Threads[i].State != WitThreadEmpty &&
            p->Threads[i].State != WitThreadExited &&
            p->Threads[i].Handle == identity) {
            return &p->Threads[i];
        }
    }
    return 0;
}

WitU64 wit_user_reference_describe(
    WitUserProcess *p, WitU64 handle, WitU32 rights, const WitUserThreadReference **reference)
{
    *reference = 0;
    const WitU64 status = wit_handle_check(&p->Handles, handle, WIT_HANDLE_THREAD_REFERENCE, rights);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const WitUserThreadReference *r = lookup(p, handle);
    if (!r) {
        wit_panic("Missing thread reference record");
    }
    *reference = r;
    return WIT_STATUS_OK;
}

WitU64 wit_user_reference_target(WitUserProcess *p, WitU64 handle, WitU32 rights, WitUserThread **target)
{
    *target = 0;
    if (handle == WIT_THREAD_SELF) {
        *target = &p->Threads[p->CurrentThread];
        return WIT_STATUS_OK;
    }
    const WitUserThreadReference *r;
    const WitU64 status = wit_user_reference_describe(p, handle, rights, &r);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (r->Exited) {
        return WIT_STATUS_CLOSED;
    }
    *target = live_target(p, r->ThreadId);
    if (!*target) {
        wit_panic("Live reference has no target");
    }
    return WIT_STATUS_OK;
}

WitU64 wit_user_reference_signaled(WitUserProcess *p, WitU64 handle, int *signaled)
{
    const WitUserThreadReference *r;
    *signaled = 0;
    const WitU64 status = wit_user_reference_describe(p, handle, WIT_RIGHT_WAIT, &r);
    if (status != WIT_STATUS_OK) {
        return status;
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

/* THREAD_QUERY: one snapshot of the thread a handle or WIT_THREAD_SELF names. The caller's Version and Size select
 * the record; the whole destination is validated before the snapshot is taken and copied. */
WitU64 wit_user_thread_query(WitUserProcess *p, WitU64 handle, WitU64 address, WitU64 size)
{
    WitUserThreadInfo info;
    WitU32 header[2];
    const WitUserThread *target = 0;
    const WitUserThreadReference *reference = 0;
    if (size != sizeof(info)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_buffer_writable(&p->Space, address, sizeof(info)) ||
        !wit_user_copy_from(&p->Space, address, (WitU8 *)header, sizeof(header))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (header[0] != WIT_THREAD_INFO_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (header[1] != sizeof(info)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    for (WitU32 i = 0; i < sizeof(info); ++i) {
        ((WitU8 *)&info)[i] = 0;
    }
    info.Version = WIT_THREAD_INFO_VERSION;
    info.Size = sizeof(info);
    info.ProcessId = p->Id;
    info.ProcessorCount = WIT_USER_PROCESSOR_COUNT; // The supported backend brings up one processor.
    if (handle == WIT_THREAD_SELF) {
        target = &p->Threads[p->CurrentThread];
        info.Rights = WIT_RIGHT_THREAD_ALL;
    } else {
        /* The QUERY right, or a context right: a context carries the same prefix, and a thread that may have its
         * context set must be able to build one from the record. */
        const WitU64 status = wit_user_reference_describe(p, handle, 0, &reference);
        if (status != WIT_STATUS_OK) {
            return status;
        }
        if (!(reference->Rights & (WIT_RIGHT_QUERY | WIT_RIGHT_GET_CONTEXT | WIT_RIGHT_SET_CONTEXT))) {
            return WIT_STATUS_DENIED;
        }
        info.Rights = reference->Rights;
        if (reference->Exited) {
            info.ThreadId = reference->ThreadId;
            info.ExitCode = reference->ExitCode;
            info.State = WIT_THREAD_STATE_EXITED;
        } else {
            target = live_target(p, reference->ThreadId);
            if (!target) {
                wit_panic("Live reference has no target");
            }
        }
    }
    if (target) {
        info.ThreadId = target->Handle;
        info.StackLow = target->StackBottom;
        info.StackHigh = target->StackTop;
        info.RawTls = target->Tls;
        info.CompilerTls = p->TlsBytes ? target->CompilerTls : 0;
        info.CompilerTlsHeader = target->CompilerTls;
        info.NativeId = target->NativeId;
        info.SuspendCount = target->SuspendCount;
        info.ContextFlags = wit_user_context_flags(target);
        info.State = target->SuspendCount       ? WIT_THREAD_STATE_SUSPENDED
            : target->State == WitThreadRunning ? WIT_THREAD_STATE_RUNNING
            : target->State == WitThreadWaiting ? WIT_THREAD_STATE_WAITING
                                                : WIT_THREAD_STATE_READY;
    }
    // IF remains clear through snapshot and whole-buffer validation/copy.
    if (!wit_user_copy_to(&p->Space, address, (const WitU8 *)&info, sizeof(info))) {
        wit_panic("Validated thread query destination changed");
    }
    return WIT_STATUS_OK;
}

static WitU64 duplicate_thread(WitUserProcess *p, WitU64 source, WitU64 output, WitU64 requested)
{
    WitUserThreadReference snapshot;
    WitUserThreadReference *destination = 0;
    WitU64 handle;
    if (requested & ~(WitU64)WIT_RIGHT_THREAD_ALL) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (source == WIT_THREAD_SELF) {
        const WitUserThread *t = &p->Threads[p->CurrentThread];
        snapshot.ThreadId = t->Handle;
        snapshot.ExitCode = 0;
        snapshot.Exited = 0;
        snapshot.Rights = WIT_RIGHT_THREAD_ALL;
    } else {
        const WitUserThreadReference *original;
        const WitU64 status = wit_user_reference_describe(p, source, 0, &original);
        if (status != WIT_STATUS_OK) {
            return status;
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

static WitU64 duplicate_event(WitUserProcess *p, WitU64 source, WitU64 output, WitU64 requested)
{
    WitU64 handle = 0;
    if (requested & ~(WitU64)(WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL)) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (!wit_user_buffer_writable(&p->Space, output, sizeof(handle))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    const WitU64 status = wit_event_duplicate(&p->Events, &p->Handles, source, (WitU32)requested, &handle);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_user_copy_to(&p->Space, output, (const WitU8 *)&handle, sizeof(handle))) {
        if (wit_event_remove(&p->Events, &p->Handles, handle) != WIT_STATUS_OK) {
            wit_panic("Event handle rollback failed");
        }
        return WIT_STATUS_BAD_ADDRESS;
    }
    return WIT_STATUS_OK;
}

/* HANDLE_DUPLICATE: a thread handle (or WIT_THREAD_SELF) or an event handle, with the same or fewer rights. */
WitU64 wit_user_handle_duplicate(WitUserProcess *p, WitU64 source, WitU64 output, WitU64 requested)
{
    if (source == WIT_THREAD_SELF ||
        wit_handle_check(&p->Handles, source, WIT_HANDLE_THREAD_REFERENCE, 0) != WIT_STATUS_WRONG_TYPE) {
        return duplicate_thread(p, source, output, requested);
    }
    return duplicate_event(p, source, output, requested);
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
