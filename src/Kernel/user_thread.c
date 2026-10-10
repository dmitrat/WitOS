#include "user.h"
#include "witos/platform.h"

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitU64 next_native_id = 1;

static int take_native_id(WitU64 *next, WitU32 *output)
{
    if (!*next || *next > 0xFFFFFFFFULL) {
        return 0;
    }
    *output = (WitU32)*next;
    ++*next;
    return 1;
}

#if defined(WITOS_SELFTEST)
/* Native ID exhaustion check; take_native_id is private to this file. */
void wit_user_native_id_self_test(void)
{
    WitU64 cursor = 1;
    WitU32 value = 0;
    require(take_native_id(&cursor, &value) && value == 1 && cursor == 2, "Native ID initial allocation failed");
    cursor = 0xFFFFFFFFULL;
    require(take_native_id(&cursor, &value) && value == 0xFFFFFFFFU && cursor == 0x100000000ULL,
        "Native ID final allocation failed");
    value = 17;
    require(
        !take_native_id(&cursor, &value) && value == 17 && cursor == 0x100000000ULL, "Native ID wrapped on exhaustion");
    cursor = 0;
    require(!take_native_id(&cursor, &value) && value == 17, "Native ID accepted zero cursor");
    wit_console_write("[TEST-PASS] User.NativeThreadIdExhaustion\n");
}
#endif

/* Resets a free thread slot for a new thread. Fails when native thread identifiers are exhausted. */
static WitU64 reset_thread(WitUserThread *thread)
{
    thread->NativeId = 0;
    thread->SuspendCount = 0;
    thread->Affinity = 1; /* The boot processor, the one online (K7.1). */
    wit_user_exception_clear(thread);
    if (next_native_id > 0xFFFFFFFFULL) {
        return WIT_STATUS_NO_MEMORY;
    }
    thread->CompilerTls = 0;
    return WIT_STATUS_OK;
}

/* Pages of one thread under construction, for rollback. */
typedef struct ThreadPages {
    WitU64 Bottom, Top, Tls;
    WitU32 Mapped;
    int RawMapped;
} ThreadPages;

/* Maps the stack and the raw TLS page. */
static int map_thread(WitUserProcess *process, ThreadPages *pages)
{
    for (WitU64 page = pages->Bottom; page < pages->Top; page += 4096) {
        if (!wit_user_space_map(&process->Space, page, 1, 0)) {
            return 0;
        }
        ++pages->Mapped;
    }
    if (!wit_user_space_map(&process->Space, pages->Tls, 1, 0)) {
        return 0;
    }
    pages->RawMapped = 1;
    return 1;
}

static void unmap_thread(WitUserProcess *process, const ThreadPages *pages, WitU64 handle)
{
    if (pages->RawMapped) {
        require(wit_user_space_unmap_fixed(&process->Space, pages->Tls), "Raw TLS rollback failed");
    }
    for (WitU32 mapped = pages->Mapped; mapped;) {
        --mapped;
        require(wit_user_space_unmap_fixed(&process->Space, pages->Bottom + mapped * 4096ULL),
            "Thread creation rollback lost a stack page");
    }
    require(wit_handle_close(&process->Handles, handle) == WIT_STATUS_OK, "Thread handle rollback failed");
}

/* Fills the raw TLS block, creates the initial frame and makes the thread Ready. */
static void start_thread(WitUserProcess *process, WitU32 index, WitUserThread *thread, const ThreadPages *pages,
    WitU64 handle, WitU64 entry, WitU64 argument)
{
    WitU64 *tls = (WitU64 *)wit_user_space_physical(&process->Space, pages->Tls, 1, 0);
    tls[0] = pages->Tls;
    tls[1] = handle;
    tls[2] = argument;
    WitArchFrame *context = wit_arch_frame_create(process->Slot, index, entry, argument, pages->Top);
    require(take_native_id(&next_native_id, &thread->NativeId), "Serialized native ID allocation failed");
    thread->Handle = handle;
    thread->StackBottom = pages->Bottom;
    thread->StackTop = pages->Top;
    thread->Tls = pages->Tls;
    thread->OwnsStack = 1;
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
    thread->State = WitThreadReady;
    ++process->ThreadCreates;
}

/* Maps and starts a thread in a free slot. Its private identity is a handle-table entry user space never receives as
 * a capability (closing it is BUSY); the thread's stack and TLS are reclaimed at its exit. */
WitU64 wit_user_prepare_thread(WitUserProcess *process, WitU32 index, WitU64 entry, WitU64 argument)
{
    WitUserThread *thread = &process->Threads[index];
    ThreadPages pages = {WIT_USER_STACK_BOTTOM + index * WIT_USER_THREAD_STRIDE,
        WIT_USER_STACK_TOP + index * WIT_USER_THREAD_STRIDE, WIT_USER_TLS + index * WIT_USER_THREAD_STRIDE, 0, 0};
    const WitU64 reset = reset_thread(thread);
    if (reset != WIT_STATUS_OK) {
        return reset;
    }
    const WitU64 handle = wit_handle_grant(&process->Handles, WIT_HANDLE_THREAD_IDENTITY, 0);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    if (!map_thread(process, &pages)) {
        unmap_thread(process, &pages, handle);
        return WIT_STATUS_NO_MEMORY;
    }
    start_thread(process, index, thread, &pages, handle, entry, argument);
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
    const WitU64 reset = reset_thread(thread);
    if (reset != WIT_STATUS_OK) {
        return reset;
    }
    const WitU64 handle = wit_handle_grant(&p->Handles, WIT_HANDLE_THREAD_REFERENCE, WIT_RIGHT_THREAD_ALL);
    const WitU64 identity = wit_handle_grant(&target->Handles, WIT_HANDLE_THREAD_IDENTITY, 0);
    require(handle != 0 && identity != 0, "Thread handles failed after the free slot check");
    WitArchFrame *context =
        wit_arch_frame_create_at(target->Slot, index, request->Entry, request->Argument, request->StackPointer);
    require(take_native_id(&next_native_id, &thread->NativeId), "Serialized native ID allocation failed");
    thread->Handle = identity;
    thread->StackBottom = base;
    thread->StackTop = base + bytes;
    thread->Tls = request->TlsBase;
    thread->CompilerTls = 0;
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

/* THREAD_CREATE: version 1 maps the kernel's stack and TLS (the frozen line), version 2 is the one form. The request
 * is validated as a whole, the thread handle is reserved first, and nothing is published before the identity,
 * stacks, TLS and initial suspend state all exist. */
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
    if (size != sizeof(WitThreadCreateRequest)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitThreadCreateRequest request;
    if (!wit_user_copy_from(&p->Space, input, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version == WIT_THREAD_CREATE_VERSION_2) {
        return create_in(p, p, (const WitThreadCreateRequest2 *)&request, sizeof(request), result);
    }
    if (request.Version != WIT_THREAD_CREATE_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Size != sizeof(request) || request.Reserved || (request.Flags & ~WIT_THREAD_START_SUSPENDED)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request.StackBytes > WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (!wit_user_space_physical(&p->Space, request.Entry, 0, 1) ||
        (request.NativeIdOutput && !wit_user_buffer_writable(&p->Space, request.NativeIdOutput, sizeof(WitU32)))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    /* A version 1 thread's stack and TLS sit in the window of its index (user_layout.h), which holds the first
     * WIT_USER_THREAD_CAPACITY threads of any process. */
    WitU32 index = 0;
    while (index < WIT_USER_THREAD_CAPACITY && p->Threads[index].State != WitThreadEmpty) {
        ++index;
    }
    if (index == WIT_USER_THREAD_CAPACITY) {
        ++p->ReferenceThreadCapacityFailures;
        return WIT_STATUS_NO_MEMORY;
    }
    WitUserThreadReference *reference = 0;
    for (WitU32 n = 0; n < p->Handles.Limit; ++n) {
        if (!p->ThreadReferences[n].Handle) {
            reference = &p->ThreadReferences[n];
            break;
        }
    }
    if (!reference) {
        return WIT_STATUS_NO_MEMORY;
    }
    const WitU64 handle = wit_handle_grant(&p->Handles, WIT_HANDLE_THREAD_REFERENCE, WIT_RIGHT_THREAD_ALL);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    const WitU64 status = wit_user_prepare_thread(p, index, request.Entry, request.Argument);
    if (status != WIT_STATUS_OK) {
        if (wit_handle_close(&p->Handles, handle) != WIT_STATUS_OK) {
            wit_panic("Thread creation rollback failed");
        }
        return status;
    }
    WitUserThread *thread = &p->Threads[index];
    thread->SuspendCount = (request.Flags & WIT_THREAD_START_SUSPENDED) ? 1U : 0U;
    reference->Handle = handle;
    reference->ThreadId = thread->Handle;
    reference->ExitCode = 0;
    reference->Rights = WIT_RIGHT_THREAD_ALL;
    reference->Exited = 0;
    // The syscall is serialized with IF clear. Allocation did not remove or
    // change the prevalidated destination's existing mapping.
    if (request.NativeIdOutput &&
        !wit_user_copy_to(
            &p->Space, request.NativeIdOutput, (const WitU8 *)&thread->NativeId, sizeof(thread->NativeId))) {
        wit_panic("Validated thread native ID output changed");
    }
    *result = handle;
    return WIT_STATUS_OK;
}
