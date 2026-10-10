#include "user.h"
#include "witos/platform.h"

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

/* Resets a free thread slot for a new thread. */
static void reset_thread(WitUserThread *thread)
{
    thread->SuspendCount = 0;
    thread->Affinity = 1; /* The boot processor, the one online (K7.1). */
    wit_user_exception_clear(thread);
}

/* The first thread of a component the kernel builds (a fixture or the root task): its stack is the fixed window
 * [WIT_USER_STACK_BOTTOM, WIT_USER_STACK_TOP), mapped page by page, and it has no TLS base (K8.4b). Its private
 * identity is a handle-table entry user space never receives as a capability (closing it is BUSY); the stack is
 * reclaimed at its exit. A failure releases every page and the identity. */
WitU64 wit_user_prepare_thread(WitUserProcess *process, WitU64 entry, WitU64 argument)
{
    WitUserThread *thread = &process->Threads[0];
    WitU32 mapped = 0;
    reset_thread(thread);
    const WitU64 handle = wit_handle_grant(&process->Handles, WIT_HANDLE_THREAD_IDENTITY, 0);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    for (WitU64 page = WIT_USER_STACK_BOTTOM; page < WIT_USER_STACK_TOP; page += 4096) {
        if (!wit_user_space_map(&process->Space, page, 1, 0)) {
            while (mapped) {
                --mapped;
                require(wit_user_space_unmap_fixed(&process->Space, WIT_USER_STACK_BOTTOM + mapped * 4096ULL),
                    "First thread rollback lost a stack page");
            }
            require(
                wit_handle_close(&process->Handles, handle) == WIT_STATUS_OK, "First thread handle rollback failed");
            return WIT_STATUS_NO_MEMORY;
        }
        ++mapped;
    }
    thread->Handle = handle;
    thread->StackBottom = WIT_USER_STACK_BOTTOM;
    thread->StackTop = WIT_USER_STACK_TOP;
    thread->Tls = 0;
    thread->OwnsStack = 1;
    thread->ExitReservation = 0;
    thread->ExitClear = 0;
    thread->ExitEvent = 0;
    thread->AlternateBottom = 0;
    thread->AlternateTop = 0;
    thread->ExitCode = 0;
    thread->Context = wit_arch_frame_create(process->Slot, 0, entry, argument, WIT_USER_STACK_TOP);
    thread->WaitKind = WitWaitNone;
    thread->WaitHandle = 0;
    thread->WaitCount = 0;
    thread->WaitAll = 0;
    wit_user_activations_clear(thread);
    for (WitU32 w = 0; w < WIT_WAIT_ANY_CAPACITY; ++w) {
        thread->WaitHandles[w] = 0;
    }
    thread->Deadline = WIT_WAIT_INFINITE;
    thread->WaitOrder = 0;
    thread->State = WitThreadReady;
    ++process->ThreadCreates;
    return WIT_STATUS_OK;
}

/* The one form (version 2, K5.2a; version 3 names the process, K5.2c): a thread of the target process on a stack of
 * the target's with the TLS base asked; the kernel maps nothing for it. The stack pointer must be 16-byte aligned
 * inside a committed writable reservation of the target (a mapping the creator made into it), whose bounds become
 * the thread's; the entry is executable in the target; the TLS base is a user address or zero. The thread handle and
 * its record go to the caller, the thread's identity to the target. */
static WitU64 create_in(
    WitUserProcess *p, WitUserProcess *target, const WitThreadCreateRequest2 *request, WitU64 size, WitU64 *result)
{
    WitU64 base = 0, bytes = 0;
    if (request->Size != size || request->Reserved || (request->Flags & ~WIT_THREAD_START_SUSPENDED)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if ((request->StackPointer & 15) || request->TlsBase >= 0x0000800000000000ULL) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_space_physical(&target->Space, request->Entry, 0, 1)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    /* The reservation of the byte below the stack pointer: a stack's top may be another reservation's base. */
    if (!request->StackPointer ||
        !wit_user_space_reservation_bounds(&target->Space, request->StackPointer - 1, &base, &bytes) ||
        request->StackPointer <= base ||
        !wit_user_space_physical(&target->Space, request->StackPointer - 8, 1, 0)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    WitU32 index = 0;
    while (index < target->ThreadLimit && target->Threads[index].State != WitThreadEmpty) {
        ++index;
    }
    if (index == target->ThreadLimit) {
        ++target->ReferenceThreadCapacityFailures;
        return WIT_STATUS_NO_MEMORY;
    }
    WitUserThreadReference *reference = 0;
    for (WitU32 n = 0; n < p->Handles.Limit; ++n) {
        if (!p->ThreadReferences[n].Handle) {
            reference = &p->ThreadReferences[n];
            break;
        }
    }
    if (!reference ||
        wit_handles_free_count(&p->Handles) < (target == p ? 2U : 1U) ||
        !wit_handles_free_count(&target->Handles)) {
        return WIT_STATUS_NO_MEMORY;
    }
    WitUserThread *thread = &target->Threads[index];
    reset_thread(thread);
    const WitU64 handle = wit_handle_grant(&p->Handles, WIT_HANDLE_THREAD_REFERENCE, WIT_RIGHT_THREAD_ALL);
    const WitU64 identity = wit_handle_grant(&target->Handles, WIT_HANDLE_THREAD_IDENTITY, 0);
    require(handle != 0 && identity != 0, "Thread handles failed after the free slot check");
    WitArchFrame *context =
        wit_arch_frame_create_at(target->Slot, index, request->Entry, request->Argument, request->StackPointer);
    thread->Handle = identity;
    thread->StackBottom = base;
    thread->StackTop = base + bytes;
    thread->Tls = request->TlsBase;
    thread->OwnsStack = 0;
    thread->ExitReservation = 0;
    thread->ExitClear = 0;
    thread->ExitEvent = 0;
    thread->AlternateBottom = 0;
    thread->AlternateTop = 0;
    thread->ExitCode = 0;
    thread->Context = context;
    thread->WaitKind = WitWaitNone;
    thread->WaitHandle = 0;
    thread->WaitCount = 0;
    thread->WaitAll = 0;
    wit_user_activations_clear(thread);
    for (WitU32 w = 0; w < WIT_WAIT_ANY_CAPACITY; ++w) {
        thread->WaitHandles[w] = 0;
    }
    thread->Deadline = WIT_WAIT_INFINITE;
    thread->WaitOrder = 0;
    thread->SuspendCount = (request->Flags & WIT_THREAD_START_SUSPENDED) ? 1U : 0U;
    thread->State = WitThreadReady;
    ++target->ThreadCreates;
    reference->Handle = handle;
    reference->ThreadId = identity;
    reference->ExitCode = 0;
    reference->Rights = WIT_RIGHT_THREAD_ALL;
    reference->Exited = 0;
    *result = handle;
    return WIT_STATUS_OK;
}

/* THREAD_AFFINITY (RFC 0011 section 7.9, K7.1): the processors a thread may run on. Get needs QUERY, set AFFINITY;
 * the mask is validated whole (nonzero, within the processors present, with an online processor) before it changes,
 * and the boot processor stays the one online until phase P. */
WitU64 wit_user_thread_affinity(WitUserProcess *p, WitU64 handle, WitU64 address, WitU64 flags)
{
    WitUserThread *target;
    WitU64 mask = 0;
    if (flags != WIT_THREAD_AFFINITY_GET && flags != WIT_THREAD_AFFINITY_SET) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 status = wit_user_reference_target(
        p, handle, flags == WIT_THREAD_AFFINITY_SET ? WIT_RIGHT_AFFINITY : WIT_RIGHT_QUERY, &target);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (flags == WIT_THREAD_AFFINITY_GET) {
        if (!wit_user_buffer_writable(&p->Space, address, sizeof(mask))) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        mask = target->Affinity;
        require(
            wit_user_copy_to(&p->Space, address, (const WitU8 *)&mask, sizeof(mask)), "Validated mask output changed");
        return WIT_STATUS_OK;
    }
    if (!wit_user_copy_from(&p->Space, address, (WitU8 *)&mask, sizeof(mask))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    const WitU64 valid = wit_processors_affinity_status(mask);
    if (valid != WIT_STATUS_OK) {
        return valid;
    }
    target->Affinity = mask;
    return WIT_STATUS_OK;
}

int wit_user_thread_stack_range(const WitUserThread *t, WitU64 sp, WitU64 *bottom, WitU64 *top)
{
    if (sp >= t->StackBottom && sp <= t->StackTop) {
        *bottom = t->StackBottom;
        *top = t->StackTop;
        return 1;
    }
    if (t->AlternateTop && sp >= t->AlternateBottom && sp <= t->AlternateTop) {
        *bottom = t->AlternateBottom;
        *top = t->AlternateTop;
        return 1;
    }
    return 0;
}

/* THREAD_STACK_ALTERNATE (S3.1): validated whole before the thread's record changes. The range must be 16-byte
 * aligned, at least the callback frame's minimum, committed and writable in the caller's space (a mapping of the
 * image or a reservation alike) and apart from the thread's stack; a zero request clears it; a caller whose stack
 * pointer is inside the current alternate range cannot change it. */
WitU64 wit_user_thread_alternate_stack(
    WitUserProcess *p, const WitArchFrame *frame, WitU64 address, WitU64 size, WitU64 reserved)
{
    WitUserThread *t = &p->Threads[p->CurrentThread];
    WitThreadAlternateStackRequest request;
    if (reserved) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&p->Space, address, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_THREAD_ALTERNATE_STACK_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (size != sizeof(request) || request.Size != sizeof(request)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 sp = wit_arch_frame_sp(frame);
    if (t->AlternateTop && sp >= t->AlternateBottom && sp <= t->AlternateTop) {
        return WIT_STATUS_BUSY;
    }
    if (!request.Base && !request.Bytes) {
        t->AlternateBottom = 0;
        t->AlternateTop = 0;
        return WIT_STATUS_OK;
    }
    if ((request.Base & 15) ||
        (request.Bytes & 15) ||
        request.Bytes < WIT_EXCEPTION_STACK_MINIMUM ||
        request.Bytes > 0xFFFFFFFFULL ||
        request.Base + request.Bytes < request.Base) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request.Base < t->StackTop && request.Base + request.Bytes > t->StackBottom) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_buffer_writable(&p->Space, request.Base, (WitU32)request.Bytes)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    t->AlternateBottom = request.Base;
    t->AlternateTop = request.Base + request.Bytes;
    return WIT_STATUS_OK;
}

/* THREAD_SET_TLS: the raw TLS base of the current thread, applied at its next return to user mode. */
WitU64 wit_user_thread_set_tls(WitUserProcess *p, WitU64 base, WitU64 reserved0, WitU64 reserved1)
{
    if (reserved0 || reserved1 || base >= 0x0000800000000000ULL) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_arch_user_tls_settable()) {
        return WIT_STATUS_UNSUPPORTED;
    }
    p->Threads[p->CurrentThread].Tls = base;
    return WIT_STATUS_OK;
}

/* THREAD_CREATE: the one form, version 2 in the caller's process or version 3 in the process it names (version 1, the
 * kernel's stack and TLS, was retired at K8.4b). The request is validated as a whole and nothing is published before
 * the handle, the identity and the initial suspend state all exist. */
WitU64 wit_user_thread_create(WitUserProcess *p, WitU64 input, WitU64 size, WitU64 *result)
{
    *result = 0;
    if (size == sizeof(WitThreadCreateRequest3)) {
        /* Version 3 (K5.2c): the one form into the process the request names. */
        WitThreadCreateRequest3 request3;
        WitThreadCreateRequest2 form;
        WitUserProcess *target = 0;
        if (!wit_user_copy_from(&p->Space, input, (WitU8 *)&request3, sizeof(request3))) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        if (request3.Version != WIT_THREAD_CREATE_VERSION_3) {
            return WIT_STATUS_UNSUPPORTED;
        }
        if (request3.Size != sizeof(request3)) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        const WitU64 status = wit_user_process_target(p, request3.Process, WIT_RIGHT_MANAGE, &target);
        if (status != WIT_STATUS_OK) {
            return status;
        }
        form.Version = WIT_THREAD_CREATE_VERSION_2;
        form.Size = sizeof(request3);
        form.Entry = request3.Entry;
        form.Argument = request3.Argument;
        form.StackPointer = request3.StackPointer;
        form.TlsBase = request3.TlsBase;
        form.Flags = request3.Flags;
        form.Reserved = request3.Reserved;
        return create_in(p, target, &form, sizeof(request3), result);
    }
    if (size != sizeof(WitThreadCreateRequest2)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitThreadCreateRequest2 request;
    if (!wit_user_copy_from(&p->Space, input, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_THREAD_CREATE_VERSION_2) {
        return WIT_STATUS_UNSUPPORTED;
    }
    return create_in(p, p, &request, sizeof(request), result);
}
